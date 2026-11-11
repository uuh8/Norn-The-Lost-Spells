using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using Norn.Business.Content;
using Norn.Business.Infrastructure;
using MySqlConnector;

namespace Norn.Business.Features.Inventory;

/// <summary>角色行锁串行化同一资产聚合的写入；事务不跨网络回合，更不会跨整场副本。</summary>
public sealed class InventoryService(OnlineDatabase database, ContentManifestValidator content)
{
    // Whole Profile还包括仓库/legacy平面项；这是控制面有限预算，不是把旧8槽扩大成新背包。
    private const int MaximumProfileItems = OnlineOwnedAssetSnapshot.MaximumItems + OnlineInventoryAreas.BackpackCapacity + OnlineInventoryAreas.WarehouseCapacity;
    private const int MaximumComponentRows = MaximumProfileItems * 8;

    public async Task<OnlineProfileReply> GetProfileAsync(string player, CancellationToken ct, string? practiceSessionScope = null)
    {
        await using var connection = await database.OpenAsync(ct);
        // 首次Profile兼容可能恢复历史无包账号。先锁角色，再建立RR一致视图，
        // 与入局/整备使用相同聚合锁，避免两次并发GET各发一只或读到旧版混合快照。
        // 正常已有包账号只读；这个短事务不跨HTTP请求，更不占整场副本。
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        uint? version = await connection.QuerySingleOrDefaultAsync<uint?>(new CommandDefinition(
            "SELECT Version FROM Characters WHERE CharacterId=@Player FOR UPDATE", new { Player = player }, tx, cancellationToken: ct));
        if (version == null) throw new OnlineFailure(404, "ProfileNotFound");
        // 活动成员读不可变Entry的私有深副本，当前DS变化不从持久旧行反推；明确锁标志，不能重授。
        // 跨Owner个人结算可能重排另一在局成员的SQL父链，该成员仍须得到完整可信冻结视图。
        var frozen = await connection.QuerySingleOrDefaultAsync<FrozenProfileRow>(new CommandDefinition("""
            SELECT l.RunId,m.EntrySnapshot FROM AssetLeases l
            JOIN RunMembers m ON m.RunId=l.RunId AND m.CharacterId=l.CharacterId
            JOIN Runs r ON r.RunId=l.RunId AND r.RoomEpoch=l.RoomEpoch
            WHERE l.CharacterId=@Player AND m.State IN ('Reserved','Admitted')
            """, new { Player=player }, tx, cancellationToken:ct));
        frozen ??= await connection.QuerySingleOrDefaultAsync<FrozenProfileRow>(new CommandDefinition("""
            SELECT m.RunId,m.EntrySnapshot FROM RunMembers m JOIN Runs r ON r.RunId=m.RunId
            WHERE m.CharacterId=@Player AND m.State<>'Exited'
              AND EXISTS (SELECT 1 FROM RunItemCustody c WHERE c.RunId=m.RunId AND c.OriginalOwnerCharacterId=@Player AND c.State='Active')
            ORDER BY r.CreatedAtUtc DESC LIMIT 1
            """, new { Player=player }, tx, cancellationToken:ct));
        OnlineProfileReply reply;
        if (frozen != null)
        {
            reply = JsonSerializer.Deserialize<OnlineProfileReply>(frozen.EntrySnapshot,ContentManifestValidator.Json)
                ?? throw new OnlineFailure(409,"RunAssetsPendingRecovery");
            if (reply.PlayerId!=player) throw new OnlineFailure(409,"RunAssetsPendingRecovery");
            reply.AssetsLocked=true;reply.ActiveRunId=frozen.RunId;
        }
        else
        {
            // 不完整租约不是可补包资格；未决托管另由ReadSnapshot原门拒绝。
            // 冻结分支始终返回原Entry，即使持久旧行缺包也绝不在局补资产。
            if (await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM AssetLeases WHERE CharacterId=@Player", new { Player = player }, tx, cancellationToken: ct)) != 0)
                throw new OnlineFailure(409, "RunAssetsPendingRecovery");
            reply = await ReadSnapshotAsync(connection, tx, player, ct, practiceSessionScope);
            reply = await EnsureEntryBackpackAsync(connection, tx, reply, null, ct, practiceSessionScope);
        }
        await tx.CommitAsync(ct); return reply;
    }

    // 入局复用同一份只读映射，但连接/Transaction由RunEntry持有，不能另开RR视图而读到锁定前的数据。
    internal async Task<OnlineProfileReply> ReadSnapshotAsync(MySqlConnection connection, MySqlTransaction tx, string player, CancellationToken ct, string? practiceSessionScope = null)
    {
        // 没有最终收据的托管不能因旧Fail释放租约就重新用于新Run。只保持现场，不选崩溃回滚政策。
        int pending = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM RunItemCustody c JOIN RunMembers m ON m.RunId=c.RunId AND m.CharacterId=c.OriginalOwnerCharacterId
            WHERE c.OriginalOwnerCharacterId=@Player AND c.State='Active' AND m.State<>'Exited'
              AND NOT EXISTS (SELECT 1 FROM AssetLeases l WHERE l.CharacterId=@Player)
            """,new{Player=player},tx,cancellationToken:ct));
        if(pending!=0)throw new OnlineFailure(409,"RunAssetsPendingRecovery");
        var row = await connection.QuerySingleOrDefaultAsync<ProfileRow>(new CommandDefinition("""
            SELECT c.CharacterId,a.Username,c.Version,c.SafeHub,w.Balance,e.SelectedWandInstanceId,
              (e.CharacterId IS NOT NULL) AS HasCombatLoadout FROM Characters c
            JOIN Accounts a ON a.AccountId=c.AccountId JOIN Wallets w ON w.CharacterId=c.CharacterId
            LEFT JOIN CharacterCombatLoadouts e ON e.CharacterId=c.CharacterId
            WHERE c.CharacterId=@Player
            """, new { Player = player }, tx, cancellationToken: ct)) ?? throw new OnlineFailure(404, "ProfileNotFound");
        var items = await connection.QueryAsync<ItemRow>(new CommandDefinition("""
            SELECT i.InstanceId,i.ContentId,i.Revision,i.RemainingCharges,i.HasUnlimitedCharges,i.WandStatsMode,
              l.Area,l.Slot,l.LocationKind,l.ParentInstanceId,l.GridX,l.GridY,l.QuarterTurns FROM Items i
            LEFT JOIN ItemLocations l ON l.InstanceId=i.InstanceId AND l.OwnerCharacterId=i.OwnerCharacterId
            WHERE i.OwnerCharacterId=@Player
              AND NOT EXISTS (SELECT 1 FROM RunItemCustody rc WHERE rc.InstanceId=i.InstanceId AND rc.State IN ('Active','Retired'))
              AND NOT EXISTS (SELECT 1 FROM AccountItemRetirements ar WHERE ar.InstanceId=i.InstanceId)
            ORDER BY l.LocationKind,l.Slot,i.InstanceId LIMIT @ReadLimit
            """, new { Player = player, ReadLimit = MaximumProfileItems + 1 }, tx, cancellationToken: ct));
        ItemRow[] rows = items.ToArray();
        if (rows.Length > MaximumProfileItems) throw new OnlineFailure(409, "InventoryCapacityExceeded");
        if (rows.Any(i => i.LocationKind == null)) throw new OnlineFailure(409, "LocationMissing");

        // State读取不按物品Kind过滤：非容器夹带State必须交给完整业务规则拒绝，不能在SELECT中悄悄“修好”。
        // LEFT JOIN保留零组分State行，明确空容器与缺失State不能合并。查询同属原RR/RunEntry事务。
        var stateRows = (await connection.QueryAsync<ContainerStateRow>(new CommandDefinition("""
            SELECT s.InstanceId,s.SchemaVersion,c.MaterialKey,c.Quantity FROM ItemContainerStates s
            JOIN Items i ON i.InstanceId=s.InstanceId
            LEFT JOIN ItemContainerComponents c ON c.InstanceId=s.InstanceId
            WHERE i.OwnerCharacterId=@Player
              AND NOT EXISTS (SELECT 1 FROM RunItemCustody rc WHERE rc.InstanceId=i.InstanceId AND rc.State IN ('Active','Retired'))
              AND NOT EXISTS (SELECT 1 FROM AccountItemRetirements ar WHERE ar.InstanceId=i.InstanceId)
            ORDER BY s.InstanceId,c.MaterialKey LIMIT @ReadLimit
            """, new { Player = player, ReadLimit = MaximumComponentRows + 1 }, tx, cancellationToken: ct))).ToArray();
        if (stateRows.Length > MaximumComponentRows) throw new OnlineFailure(409, "InventoryCapacityExceeded");
        var states = new Dictionary<string, ContainerStateBuilder>(StringComparer.Ordinal);
        foreach (var part in stateRows)
        {
            if (!states.TryGetValue(part.InstanceId, out var state))
            {
                state = new ContainerStateBuilder(part.SchemaVersion);
                states.Add(part.InstanceId, state);
            }
            if (state.SchemaVersion != part.SchemaVersion || part.MaterialKey.HasValue != part.Quantity.HasValue)
                throw new OnlineFailure(409, "InvalidCombatContainer");
            if (part.MaterialKey.HasValue)
            {
                if (state.Components.Count >= 8) throw new OnlineFailure(409, "InvalidCombatContainer");
                state.Components.Add(new OnlineOwnedMaterialAmountSnapshot { MaterialKey = part.MaterialKey.Value, Quantity = part.Quantity!.Value });
            }
        }
        // 固体每实例只有一行真实GMU，读取与容器同属当前RR/RunEntry事务；不按Kind过滤，
        // 否则被错误挂在卡/法杖/容器上的状态会在查询中消失，伪装成合法资产。
        var materialRows = (await connection.QueryAsync<MaterialStateRow>(new CommandDefinition("""
            SELECT s.InstanceId,s.SchemaVersion,s.MaterialKey,s.Quantity FROM ItemMaterialStates s
            JOIN Items i ON i.InstanceId=s.InstanceId
            WHERE i.OwnerCharacterId=@Player
              AND NOT EXISTS (SELECT 1 FROM RunItemCustody rc WHERE rc.InstanceId=i.InstanceId AND rc.State IN ('Active','Retired'))
              AND NOT EXISTS (SELECT 1 FROM AccountItemRetirements ar WHERE ar.InstanceId=i.InstanceId)
            ORDER BY s.InstanceId LIMIT @ReadLimit
            """, new { Player = player, ReadLimit = MaximumProfileItems + 1 }, tx, cancellationToken: ct))).ToArray();
        if (materialRows.Length > MaximumProfileItems) throw new OnlineFailure(409, "InventoryCapacityExceeded");
        var materials = new Dictionary<string, OnlineOwnedMaterialStateSnapshot>(materialRows.Length, StringComparer.Ordinal);
        foreach (var state in materialRows)
            if (!materials.TryAdd(state.InstanceId, new OnlineOwnedMaterialStateSnapshot
                { SchemaVersion = state.SchemaVersion, MaterialKey = state.MaterialKey, Quantity = state.Quantity }))
                throw new OnlineFailure(409, "InvalidCombatMaterial");
        // 可选设置仍按Owner实际持有行读取，不按Spell过滤以掩盖Kind错误；没有行就是没有设置。
        // ContainerId只是历史来源引用，不能JOIN来源Items，否则已失效设置会从Profile/冻结中悄悄消失。
        var attachmentRows = (await connection.QueryAsync<AttachmentStateRow>(new CommandDefinition("""
            SELECT s.InstanceId,s.SchemaVersion,s.ContainerId,s.Dose,s.Enabled FROM ItemSpellAttachmentStates s
            JOIN Items i ON i.InstanceId=s.InstanceId
            WHERE i.OwnerCharacterId=@Player
              AND NOT EXISTS (SELECT 1 FROM RunItemCustody rc WHERE rc.InstanceId=i.InstanceId AND rc.State IN ('Active','Retired'))
              AND NOT EXISTS (SELECT 1 FROM AccountItemRetirements ar WHERE ar.InstanceId=i.InstanceId)
            ORDER BY s.InstanceId LIMIT @ReadLimit
            """, new { Player = player, ReadLimit = MaximumProfileItems + 1 }, tx, cancellationToken: ct))).ToArray();
        if (attachmentRows.Length > MaximumProfileItems) throw new OnlineFailure(409, "InventoryCapacityExceeded");
        var attachments = new Dictionary<string, OnlineOwnedSpellAttachmentStateSnapshot>(attachmentRows.Length, StringComparer.Ordinal);
        foreach (var state in attachmentRows)
            if (!attachments.TryAdd(state.InstanceId, new OnlineOwnedSpellAttachmentStateSnapshot
                { SchemaVersion = state.SchemaVersion, ContainerId = state.ContainerId, Dose = state.Dose, Enabled = state.Enabled }))
                throw new OnlineFailure(409, "InvalidCombatAttachment");
        var wandStates = await PersistentWandStateStore.ReadAsync(connection, tx, player, MaximumProfileItems, content, ct);
        foreach (var item in rows)
        {
            var definition = content.RequireItem(item.ContentId);
            materials.TryGetValue(item.InstanceId, out var material);
            attachments.TryGetValue(item.InstanceId, out var attachment);
            // 包括仓库里的真实持有物；没有state必须拒绝整份Profile，不能先把壳发给HTTP再等入局报错。
            OwnedCombatAssetRules.RequireMaterialState(material, definition);
            OwnedCombatAssetRules.RequireAttachmentState(attachment, definition);
            if (definition.Kind == 4 && (states.ContainsKey(item.InstanceId) || item.RemainingCharges != 0 || item.HasUnlimitedCharges))
                throw new OnlineFailure(409, "InvalidCombatAsset");
            if (attachment != null && states.ContainsKey(item.InstanceId)) throw new OnlineFailure(409, "InvalidCombatAsset");
            // 模式是持久事实：旧模板保持0，实例roll必须显式1且有完整状态；仓库同样检查，不能靠不投影而掩盖坏行。
            bool hasWandState = wandStates.ContainsKey(item.InstanceId);
            if (item.WandStatsMode is < 0 or > 1 || item.WandStatsMode == 1 && (definition.Kind != 1 || !hasWandState)
                || item.WandStatsMode == 0 && hasWandState)
                throw new OnlineFailure(409, "InvalidPersistentWandState");
            if (definition.Kind == 1)
            {
                var persisted = new OnlineOwnedItemSnapshot { InstanceId = item.InstanceId, ContentId = item.ContentId,
                    RemainingCharges = item.RemainingCharges, HasUnlimitedCharges = item.HasUnlimitedCharges,
                    UseTemplateWandStats = item.WandStatsMode == 0, WandStats = PersistentWandStateStore.Copy(item.InstanceId, wandStates),
                    ContainerState = CopyState(item.InstanceId, states), MaterialState = material, AttachmentState = attachment };
                OwnedCombatAssetRules.RequireCheckpointItemState(persisted, definition, content);
            }
        }
        // 免费包的身份来自同一数据库视图内的可信来源行，不能按ContentId猜测，
        // 否则玩家正常获得的同款包也会被错误绑定。复合Owner连接保留来源与持有者边界。
        var entryBoundBackpacks = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT g.InstanceId FROM EntryBackpackGrants g
            JOIN Items i ON i.InstanceId=g.InstanceId AND i.OwnerCharacterId=g.CharacterId
            WHERE g.CharacterId=@Player
            """, new { Player = player }, tx, cancellationToken: ct))).ToHashSet(StringComparer.Ordinal);
        // 仓库与携带物共用上面同一RR事务已校验的持久模式/roll；按实例ID复制独立DTO，
        // 不从ContentId重Roll、不转换旧模板，也不让Profile.Items借用CombatAssets的可变数组。
        OnlineItemReply[] view = rows.Select(i => new OnlineItemReply { InstanceId = i.InstanceId, ContentId = i.ContentId,
            DisplayName = content.RequireItem(i.ContentId).DisplayName, Area = Area(i), Slot = i.LocationKind==5?-1:i.Slot, Revision = i.Revision,
            LocationKind = i.LocationKind!.Value, ParentInstanceId = i.ParentInstanceId ?? "", X = i.GridX, Y = i.GridY, QuarterTurns = i.QuarterTurns,
            RemainingCharges = i.RemainingCharges, HasUnlimitedCharges = i.HasUnlimitedCharges,
            UseTemplateWandStats = content.RequireItem(i.ContentId).Kind == 1 && i.WandStatsMode == 0,
            WandStats = PersistentWandStateStore.Copy(i.InstanceId, wandStates),
            ContainerState = CopyState(i.InstanceId, states), MaterialState = CopyMaterialState(i.InstanceId, materials),
            AttachmentState = CopyAttachmentState(i.InstanceId, attachments), EntryBoundBackpack = entryBoundBackpacks.Contains(i.InstanceId) }).ToArray();
        // 地面只存位置/原实例引用。状态和子卡仍来自同一RR物品视图；缺姿态不静默隐藏物品。
        var groundRows=(await connection.QueryAsync<HubWorldRow>(new CommandDefinition(
            "SELECT InstanceId,WorldOwnerId,SceneKey,WorldSceneKey,PoseJson FROM HubWorldItems WHERE OwnerCharacterId=@Player ORDER BY InstanceId LIMIT @Limit",
            new{Player=player,Limit=MaximumProfileItems+1},tx,cancellationToken:ct))).ToArray();
        if(groundRows.Length>MaximumProfileItems||groundRows.Length!=rows.Count(i=>i.LocationKind==5))throw new OnlineFailure(409,"InvalidHubWorldItem");
        var world=groundRows.Select(g=>
        {
            if(!rows.Any(i=>i.InstanceId==g.InstanceId&&i.LocationKind==5))throw new OnlineFailure(409,"InvalidHubWorldItem");
            var pose=JsonSerializer.Deserialize<OnlineHubWorldItemPose>(g.PoseJson,ContentManifestValidator.Json);
            AccountInventoryMoves.RequireWorldPose(pose);
            if(pose!.SceneKey!=g.SceneKey||pose.WorldSceneKey!=g.WorldSceneKey||!IsId(g.WorldOwnerId))throw new OnlineFailure(409,"InvalidHubWorldItem");
            return new OnlineHubWorldItemReply{InstanceId=g.InstanceId,WorldOwnerId=g.WorldOwnerId,Pose=pose};
        }).ToArray();
        OnlineOwnedAssetSnapshot? combat = null;
        if (row.HasCombatLoadout)
        {
            // 只按真实位置归属决定是否携带，不按Kind筛掉坏行。仓库及其安装/包内子链留在业务库存；
            // 缺父、循环或未知位置则保留给冻结规则报错，不能被过滤成另一份“合法”的资产快照。
            ItemRow[] carried = CarriedRows(rows);
            if (carried.Length > OnlineOwnedAssetSnapshot.MaximumItems) throw new OnlineFailure(409, "InventoryCapacityExceeded");
            combat = new OnlineOwnedAssetSnapshot { SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, PlayerId = player, InventoryId = player,
                InventoryVersion = row.Version, Gold = row.Balance, CurrencyVersion = 1,
                GameplayConfigHash = content.GameplayConfigHash, SelectedWandInstanceId = row.SelectedWandInstanceId ?? "",
                Items = carried.Select(i => new OnlineOwnedItemSnapshot
                { InstanceId = i.InstanceId, ContentId = i.ContentId, ParentInstanceId = i.ParentInstanceId ?? "", Revision = i.Revision,
                    LocationKind = i.LocationKind!.Value, Slot = i.Slot, X = i.GridX, Y = i.GridY, QuarterTurns = i.QuarterTurns,
                    RemainingCharges = i.RemainingCharges, HasUnlimitedCharges = i.HasUnlimitedCharges,
                    UseTemplateWandStats = content.RequireItem(i.ContentId).Kind == 1 && i.WandStatsMode == 0,
                    WandStats = PersistentWandStateStore.Copy(i.InstanceId, wandStates), ContainerState = CopyState(i.InstanceId, states),
                    MaterialState = CopyMaterialState(i.InstanceId, materials), AttachmentState = CopyAttachmentState(i.InstanceId, attachments),
                    EntryBoundBackpack = entryBoundBackpacks.Contains(i.InstanceId) }).ToArray() };
        }
        return new OnlineProfileReply { PlayerId = row.CharacterId, Username = row.Username, Version = row.Version, Gold = row.Balance,
            SafeHub = row.SafeHub, BackpackSlots = OnlineInventoryAreas.BackpackCapacity, WarehouseSlots = OnlineInventoryAreas.WarehouseCapacity,
            ContentManifestHash = content.Manifest.Hash, Items = view, WorldItems=world, CombatAssets = combat,
            PracticeSessionScope = practiceSessionScope ?? "", PracticeContinuityJson = await AccountPracticeContinuityStore.ReadAsync(
                connection, tx, player, practiceSessionScope, content.GameplayConfigHash, ct) };
    }

    /// <summary>
    /// 注册唯一事务中的初始包。新角色还没有可对外观察的旧版本，故保持初始Version=1；
    /// 唯一grant与原Owner复合FK共同保护真实来源，不把普通同款包按ContentId绑定。
    /// 只能由AccountService在账户/钱包/杖卡完整后调用，失败随整个注册回滚。
    /// </summary>
    internal Task GrantRegistrationBackpackAsync(MySqlConnection connection, MySqlTransaction tx,
        string player, CancellationToken ct) => InsertMinimumBackpackAsync(connection, tx, player, null, ct);

    /// <summary>
    /// 入局调用方已锁角色、核对Ready/租约；Profile兼容调用方先锁角色并排除在局/未决托管。
    /// run为null明确表示AccountBaseline，非空保留真实RunEntry来源。只在无装备包时补齐，
    /// 任何写入与来源/版本/完整读回同事务提交；不暴露为客户端领取端点。
    /// </summary>
    internal async Task<OnlineProfileReply> EnsureEntryBackpackAsync(MySqlConnection connection, MySqlTransaction tx,
        OnlineProfileReply profile, string? run, CancellationToken ct, string? practiceSessionScope = null)
    {
        // 任何合法装备包均原样保留，不能按ContentId只保留基础包而覆盖玩家自己的大包。
        var equipped = profile.Items.Where(i => i.LocationKind == 4).ToArray();
        if (equipped.Length != 0)
        {
            if (equipped.Length != 1 || content.RequireItem(equipped[0].ContentId).Kind != 2
                || equipped[0].ParentInstanceId.Length != 0 || equipped[0].Slot != 0
                || equipped[0].X != 0 || equipped[0].Y != 0 || equipped[0].QuarterTurns != 0)
                throw new OnlineFailure(409, "EntryBackpackDispositionRequired");
            return profile;
        }
        string? existing = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT InstanceId FROM EntryBackpackGrants WHERE CharacterId=@Player", new { Player = profile.PlayerId }, tx, cancellationToken: ct));
        // 已支持同Owner整备换包/存包；兼容既有唯一保底grant，只复用能确认仍在本人仓库的原包。
        // 缺失、未知位置或在局托管保留现场拒绝，不自动清理、跨Owner取回或再发第二只。
        if (profile.Version == uint.MaxValue) throw new OnlineFailure(409, "ProfileVersionConflict");
        var definition = content.RequireEntryBackpack();
        if (existing != null)
        {
            // 同Owner整备可把保底包暂存仓库；下次入局复用唯一可信grant的原ID，不能再造一只。
            // 缺失/未知位置或仍在别局托管时保留现场拒绝，不自动回收、转资产或补发。
            var owned = profile.Items.SingleOrDefault(i => i.InstanceId == existing);
            if (owned == null || !owned.EntryBoundBackpack || owned.LocationKind != 0 || owned.Area != "Warehouse"
                || owned.ContentId != definition.ContentId) throw new OnlineFailure(409, "EntryBackpackDispositionRequired");
            if (owned.Revision == uint.MaxValue) throw new OnlineFailure(409, "InventoryRevisionExhausted");
            int restored = await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE ItemLocations SET Area=NULL,CombatLocationKind=4,ParentInstanceId=NULL,Slot=0,GridX=0,GridY=0,QuarterTurns=0
                WHERE InstanceId=@Item AND OwnerCharacterId=@Player AND Area='Warehouse';
                """, new { Item=existing, Player=profile.PlayerId }, tx, cancellationToken:ct));
            if (restored != 1) throw new OnlineFailure(409, "EntryBackpackDispositionRequired");
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE Items SET Revision=Revision+1 WHERE InstanceId=@Item AND OwnerCharacterId=@Player;
                UPDATE Characters SET Version=Version+1 WHERE CharacterId=@Player AND Version=@Version
                """, new { Item=existing, Player=profile.PlayerId, Version=profile.Version }, tx, cancellationToken:ct));
            return await ReadSnapshotAsync(connection, tx, profile.PlayerId, ct, practiceSessionScope);
        }
        await InsertMinimumBackpackAsync(connection, tx, profile.PlayerId, run, ct);
        if (await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Characters SET Version=Version+1 WHERE CharacterId=@Player AND Version=@Version",
            new { Player = profile.PlayerId, Version = profile.Version }, tx, cancellationToken: ct)) != 1)
            throw new OnlineFailure(409, "ProfileVersionConflict");
        // 从数据库重新构造同一事务里的真实快照，不把临时DTO追加视为已持有资产。
        // 不改钱包、原物品Revision/数量/位置；完整读取或后续冻结失败会回滚整笔补包。
        return await ReadSnapshotAsync(connection, tx, profile.PlayerId, ct, practiceSessionScope);
    }

    /// <summary>共用实际创建步骤；null来源只表示账号保底，不关联虚构Run。调用方负责聚合锁和版本。</summary>
    private async Task InsertMinimumBackpackAsync(MySqlConnection connection, MySqlTransaction tx,
        string player, string? run, CancellationToken ct)
    {
        var definition = content.RequireEntryBackpack();
        string item = Guid.NewGuid().ToString("N");
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges)
              VALUES(@Item,@Player,@Content,1,0,FALSE);
            INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind,ParentInstanceId)
              VALUES(@Item,@Player,NULL,0,4,NULL);
            INSERT INTO EntryBackpackGrants(CharacterId,InstanceId,GrantedRunId,GrantedAtUtc)
              VALUES(@Player,@Item,@Run,UTC_TIMESTAMP(6))
            """, new { Item = item, Player = player, Content = definition.ContentId, Run = run }, tx, cancellationToken: ct));
    }

    /// <summary>
    /// 只由RunEntry的新Run分支调用：调用方已锁角色并验证原资产，发瓶与RunMembers冻结/租约同事务。
    /// 每个新Run都可发一只；同Run重试在RunEntry直接返回原预留，Ticket重连不会进入此方法。
    /// </summary>
    internal async Task<OnlineProfileReply> GrantEntryContainerAsync(MySqlConnection connection, MySqlTransaction tx,
        OnlineProfileReply profile, CancellationToken ct, string? practiceSessionScope = null)
    {
        // 既有领域/数据库约束都是7个道具装备位（Kind8，Slot0..6）。选首个空位，
        // 满栏就明确跳过，不替换旧物、不塞进背包，也不留下以后重连领取的pending奖励。
        int slot = 0;
        for (; slot < 7; slot++)
        {
            bool occupied = false;
            foreach (var owned in profile.Items)
                if (owned.LocationKind == 8 && owned.Slot == slot) { occupied = true; break; }
            if (!occupied) break;
        }
        if (slot == 7) return profile;
        if (profile.Version == uint.MaxValue) throw new OnlineFailure(409, "ProfileVersionConflict");
        var definition = content.RequireEntryContainer();
        string item = Guid.NewGuid().ToString("N");
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges)
              VALUES(@Item,@Player,@Content,1,0,FALSE);
            INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind,ParentInstanceId)
              VALUES(@Item,@Player,NULL,@Slot,8,NULL);
            INSERT INTO ItemContainerStates(InstanceId,SchemaVersion) VALUES(@Item,1);
            UPDATE Characters SET Version=Version+1 WHERE CharacterId=@Player AND Version=@Version
            """, new { Item = item, Player = profile.PlayerId, Content = definition.ContentId, Slot = slot, Version = profile.Version }, tx, cancellationToken: ct));
        // 显式State且零组分才是真实空容器；不按容量补满、不生成任何Material组分。
        // 重新读取真实持有行，后续完整冻结失败会连同全队奖励、版本和Run一起回滚。
        return await ReadSnapshotAsync(connection, tx, profile.PlayerId, ct, practiceSessionScope);
    }

    internal void FreezeCombatAssets(OnlineProfileReply profile, string run, ulong actor)
    {
        var assets = profile.CombatAssets;
        if (assets == null) throw new OnlineFailure(409, "CombatAssetsUnavailable");
        // legacy平面Backpack仍可在Profile查看/按旧Move整备，但没有真实Bag/XY，不能凭Slot制造入局格子。
        if (profile.Items.Any(i => i.Area == OnlineInventoryAreas.Backpack))
            throw new OnlineFailure(409, "LegacyBackpackLayoutUnsupported");
        assets.RunId = run; assets.ActorId = actor; assets.InventoryId = run + "/" + profile.PlayerId;
        OwnedCombatAssetRules.RequirePersisted(assets, profile.PlayerId, content.GameplayConfigHash, content);
    }

    private static ItemRow[] CarriedRows(ItemRow[] rows)
    {
        var ids = rows.ToDictionary(i => i.InstanceId, StringComparer.Ordinal);
        var carried = new Dictionary<string, bool>(rows.Length, StringComparer.Ordinal);
        foreach (var first in rows)
        {
            if (carried.ContainsKey(first.InstanceId)) continue;
            var path = new List<ItemRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = first;
            bool include;
            while (true)
            {
                if (carried.TryGetValue(current.InstanceId, out include)) break;
                if (!seen.Add(current.InstanceId)) { include = true; break; }
                path.Add(current);
                if (current.LocationKind is 0 or 5) { include = false; break; }
                if (current.LocationKind is not (1 or 2) || current.Area != null) { include = true; break; }
                if (!ids.TryGetValue(current.ParentInstanceId ?? "", out var parent)) { include = true; break; }
                current = parent;
            }
            foreach (var item in path) carried[item.InstanceId] = include;
        }
        return rows.Where(i => carried[i.InstanceId]).ToArray();
    }

    private static OnlineOwnedContainerStateSnapshot? CopyState(string id, Dictionary<string, ContainerStateBuilder> states)
    {
        if (!states.TryGetValue(id, out var state)) return null;
        // Profile和Owned各有独立值数组，调用方修改展示DTO不能反向改变准备冻结的组分。
        return new OnlineOwnedContainerStateSnapshot { SchemaVersion = state.SchemaVersion,
            Components = state.Components.Select(p => new OnlineOwnedMaterialAmountSnapshot { MaterialKey = p.MaterialKey, Quantity = p.Quantity }).ToArray() };
    }

    private static OnlineOwnedMaterialStateSnapshot? CopyMaterialState(string id, Dictionary<string, OnlineOwnedMaterialStateSnapshot> materials)
    {
        if (!materials.TryGetValue(id, out var state)) return null;
        // Profile和Owned分别复制，展示DTO后续变化不能改写待冻结量；全程保持ulong，不经过浮点/有符号中间值。
        return new OnlineOwnedMaterialStateSnapshot { SchemaVersion = state.SchemaVersion, MaterialKey = state.MaterialKey, Quantity = state.Quantity };
    }

    private static OnlineOwnedSpellAttachmentStateSnapshot? CopyAttachmentState(string id, Dictionary<string, OnlineOwnedSpellAttachmentStateSnapshot> attachments)
    {
        if (!attachments.TryGetValue(id, out var state)) return null;
        // 两份可变DTO分别复制，Enabled=false也保真，不把导入等同于一次清除配置命令或材料发放。
        return new OnlineOwnedSpellAttachmentStateSnapshot
        { SchemaVersion = state.SchemaVersion, ContainerId = state.ContainerId, Dose = state.Dose, Enabled = state.Enabled };
    }

    public async Task<InventoryMoveReply> TryApplyInventoryAsync(string player, InventoryMoveRequest request, CancellationToken ct)
    {
        // 新账号结构Move复用原HTTP/收据表，旧0分支的请求语义与摘要保持原样。
        if (request.OperationKind is >=1 and <=5) return await AccountInventoryMoves.ApplyAsync(database, content, this, player, request, ct);
        if (request.OperationKind != 0) throw new OnlineFailure(400, "InvalidMove");
        if (!IsId(request.CommandId) || !IsId(request.InstanceId) || request.ExpectedVersion == 0 ||
            !(request.Destination == OnlineInventoryAreas.Backpack || request.Destination == OnlineInventoryAreas.Warehouse) || request.Slot < 0 ||
            request.Slot >= (request.Destination == OnlineInventoryAreas.Backpack ? OnlineInventoryAreas.BackpackCapacity : OnlineInventoryAreas.WarehouseCapacity))
            throw new OnlineFailure(400, "InvalidMove");
        string canonical = FormattableString.Invariant($"move-v1|{request.ExpectedVersion}|{request.InstanceId}|{request.Destination}|{request.Slot}");
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        await using var connection = await database.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        uint? version = await connection.QuerySingleOrDefaultAsync<uint?>(new CommandDefinition("SELECT Version FROM Characters WHERE CharacterId=@Player FOR UPDATE", new { Player = player }, tx, cancellationToken: ct));
        if (version == null) throw new OnlineFailure(404, "ProfileNotFound");
        // 收据先于版本校验：成功回包丢失后，携带旧版本重试仍要得到第一次的结果。
        var receipt = await connection.QuerySingleOrDefaultAsync<ReceiptRow>(new CommandDefinition("SELECT RequestHash,ResultJson FROM InventoryCommands WHERE CharacterId=@Player AND CommandId=@Command", new { Player = player, Command = request.CommandId }, tx, cancellationToken: ct));
        if (receipt != null)
        {
            if (receipt.RequestHash != hash) throw new OnlineFailure(409, "CommandConflict");
            return JsonSerializer.Deserialize<InventoryMoveReply>(receipt.ResultJson, ContentManifestValidator.Json)!;
        }
        // 先返回已提交的收据，再阻止新写入；重放成功结果不会再次改资产。入局与移动共享角色行锁。
        if (await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM AssetLeases WHERE CharacterId=@Player", new { Player = player }, tx, cancellationToken: ct)) > 0)
            throw new OnlineFailure(409, "AssetsLeased");
        if (version != request.ExpectedVersion || version == uint.MaxValue) throw new OnlineFailure(409, "ProfileVersionConflict");
        if((await ReadRetiredIdsAsync(connection,tx,new[]{request.InstanceId},ct)).Count!=0)throw new OnlineFailure(404,"ItemNotOwned");
        string? id = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT ContentId FROM Items WHERE InstanceId=@Instance AND OwnerCharacterId=@Player", new { Instance = request.InstanceId, Player = player }, tx, cancellationToken: ct));
        if (id == null) throw new OnlineFailure(404, "ItemNotOwned");
        if(await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM RunItemCustody WHERE InstanceId=@Instance AND State IN ('Active','Retired')",
            new{Instance=request.InstanceId},tx,cancellationToken:ct))!=0)throw new OnlineFailure(409,"AssetsLeased");
        content.RequireItem(id);
        // 来源只由业务入场事务写入。即使未来扩展旧Move的位置，也不能凭客户端说“普通包”解除绑定。
        if (await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM EntryBackpackGrants WHERE CharacterId=@Player AND InstanceId=@Instance",
            new { Player = player, Instance = request.InstanceId }, tx, cancellationToken: ct)) > 0)
            throw new OnlineFailure(409, "EntryBackpackBound");
        // 整备 Move 只搬平面背包/仓库项；装着程序的 Wand 不得通过旧端点脱离装备闭包。
        var source = await connection.QuerySingleOrDefaultAsync<MoveLocationRow>(new CommandDefinition("SELECT LocationKind,Area FROM ItemLocations WHERE InstanceId=@Instance AND OwnerCharacterId=@Player", new { Instance = request.InstanceId, Player = player }, tx, cancellationToken: ct));
        if (source == null) throw new OnlineFailure(409, "LocationMissing");
        // 同为编号1的真实包内位置Area=NULL；旧端点只处理Area明确的legacy平面位置，不能先写坏父链再靠SQL报503。
        if (source.Area == null || source.LocationKind is not (0 or 1)) throw new OnlineFailure(409, "EquippedItemMoveUnsupported");
        if (await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM ItemLocations WHERE OwnerCharacterId=@Player AND Area=@Area AND Slot=@Slot", new { Player = player, Area = request.Destination, request.Slot }, tx, cancellationToken: ct)) > 0)
            throw new OnlineFailure(409, "SlotOccupied");
        int moved = await connection.ExecuteAsync(new CommandDefinition("UPDATE ItemLocations SET Area=@Area,Slot=@Slot WHERE InstanceId=@Instance AND OwnerCharacterId=@Player", new { Player = player, Area = request.Destination, request.Slot, Instance = request.InstanceId }, tx, cancellationToken: ct));
        if (moved != 1) throw new OnlineFailure(409, "LocationMissing");
        uint committed = version.Value + 1;
        await connection.ExecuteAsync(new CommandDefinition("UPDATE Items SET Revision=Revision+1 WHERE InstanceId=@Instance; UPDATE Characters SET Version=@Version WHERE CharacterId=@Player", new { Instance = request.InstanceId, Player = player, Version = committed }, tx, cancellationToken: ct));
        var reply = new InventoryMoveReply { CommandId = request.CommandId, CommittedVersion = committed, InstanceId = request.InstanceId, Destination = request.Destination, Slot = request.Slot };
        await connection.ExecuteAsync(new CommandDefinition("INSERT INTO InventoryCommands VALUES(@Player,@Command,@Hash,@Json,UTC_TIMESTAMP(6))", new { Player = player, Command = request.CommandId, Hash = hash, Json = JsonSerializer.Serialize(reply, ContentManifestValidator.Json) }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return reply;
    }

    private static bool IsId(string? id) => id != null && Guid.TryParseExact(id, "N", out var value) && value.ToString("N") == id;
    /// <summary>跨Run/账号候选共用永久身份栅栏；只读已退休ID，256一批，不把状态快照当新资产。</summary>
    internal static async Task<HashSet<string>> ReadRetiredIdsAsync(MySqlConnection db,MySqlTransaction tx,IEnumerable<string> candidates,CancellationToken ct)
    {
        var ids=candidates.Distinct(StringComparer.Ordinal).ToArray();
        if(ids.Length>32768)throw new OnlineFailure(409,"InventoryCapacityExceeded");
        var result=new HashSet<string>(StringComparer.Ordinal);
        foreach(var batch in ids.Chunk(256))
            foreach(string id in await db.QueryAsync<string>(new CommandDefinition("SELECT InstanceId FROM AccountItemRetirements WHERE InstanceId IN @Ids",new{Ids=batch},tx,cancellationToken:ct)))result.Add(id);
        return result;
    }
    private static string Area(ItemRow item) => item.LocationKind == 3 ? "EquippedWand" : item.LocationKind == 2 ? "WandSlot" : item.Area ?? "";
    private sealed class ProfileRow { public string CharacterId { get; set; } = ""; public string Username { get; set; } = ""; public uint Version { get; set; } public string SafeHub { get; set; } = ""; public long Balance { get; set; } public string? SelectedWandInstanceId { get; set; } public bool HasCombatLoadout {get;set;} }
    private sealed class FrozenProfileRow { public string RunId {get;set;}="";public string EntrySnapshot {get;set;}=""; }
    private sealed class HubWorldRow {public string InstanceId{get;set;}="";public string WorldOwnerId{get;set;}="";public string SceneKey{get;set;}="";public string WorldSceneKey{get;set;}="";public string PoseJson{get;set;}="";}
    private sealed class ItemRow { public string InstanceId { get; set; } = ""; public string ContentId { get; set; } = ""; public uint Revision { get; set; } public string? Area { get; set; } public int Slot { get; set; } public int? LocationKind { get; set; } public string? ParentInstanceId { get; set; } public int GridX { get; set; } public int GridY { get; set; } public int QuarterTurns { get; set; } public int RemainingCharges { get; set; } public bool HasUnlimitedCharges { get; set; } public int WandStatsMode { get; set; } }
    private sealed class MoveLocationRow { public int LocationKind { get; set; } public string? Area { get; set; } }
    private sealed class ContainerStateRow { public string InstanceId { get; set; } = ""; public uint SchemaVersion { get; set; } public byte? MaterialKey { get; set; } public ulong? Quantity { get; set; } }
    private sealed class MaterialStateRow { public string InstanceId { get; set; } = ""; public uint SchemaVersion { get; set; } public byte MaterialKey { get; set; } public ulong Quantity { get; set; } }
    private sealed class AttachmentStateRow { public string InstanceId { get; set; } = ""; public uint SchemaVersion { get; set; } public string ContainerId { get; set; } = ""; public ulong Dose { get; set; } public bool Enabled { get; set; } }
    private sealed class ContainerStateBuilder(uint schemaVersion)
    {
        public uint SchemaVersion { get; } = schemaVersion;
        public List<OnlineOwnedMaterialAmountSnapshot> Components { get; } = new(8);
    }
    private sealed class ReceiptRow { public string RequestHash { get; set; } = ""; public string ResultJson { get; set; } = ""; }
}
