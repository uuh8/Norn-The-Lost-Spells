using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Game.NetworkProtocol;
using MySqlConnector;
using Norn.Business.Content;
using Norn.Business.Features.Rooms;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Runs;

/// <summary>独立检查点Journal；不更新原库存、钱包、成员终态或租约，也不承担ESC退出策略。</summary>
public sealed class RunAssetCheckpointService(RoomTransactions transactions, RoomInstanceService instances,
    ContentManifestValidator content)
{
    // 显式传输技术上限，不承诺所有未来/极限布局都可落库；超限须保留DS现场并明确失败。
    public const int MaximumPayloadBytes = OnlineRunAssetCheckpoint.MaximumPayloadBytes;
    private static readonly JsonSerializerOptions CompleteJson = CompleteOptions();
    private static readonly JsonSerializerOptions HistoricalJson = HistoricalOptions(ContentManifestValidator.Json);
    private static readonly JsonSerializerOptions HistoricalCompleteJson = HistoricalOptions(CompleteJson);

    public Task AuthorizeAsync(string instance, string secret, uint epoch, CancellationToken ct)
        => transactions.MutateAsync(async (db, tx) =>
        { _ = await instances.RequireCheckpointAuthorityAsync(db, tx, instance, secret, epoch, true, ct); return true; }, ct);

    public Task<OnlineRunAssetCheckpointReceipt> SaveAsync(string instance, string secret, uint epoch,
        OnlineRunAssetCheckpoint request, CancellationToken ct)
        => SaveCoreAsync(instance, secret, epoch, request, false, ct);

    /// <summary>原Journal的终态入口；空Members必须由全部真实个人收据证明，不接受正文自报。</summary>
    public Task<OnlineRunAssetCheckpointReceipt> SaveTerminalAsync(string instance, string secret, uint epoch,
        OnlineRunAssetCheckpoint request, CancellationToken ct)
        => SaveCoreAsync(instance, secret, epoch, request, true, ct);

    private Task<OnlineRunAssetCheckpointReceipt> SaveCoreAsync(string instance, string secret, uint epoch,
        OnlineRunAssetCheckpoint request, bool terminal, CancellationToken ct)
        => transactions.MutateAsync(async (db, tx) =>
        {
            // 再验一次，覆盖HTTP读正文期间租约/Run已经变化的竞争窗口；事务不跨网络等待。
            var authority = await instances.RequireCheckpointAuthorityAsync(db, tx, instance, secret, epoch, true, ct);
            if (terminal) await RequireTerminalMembersAsync(db, tx, authority, ct);
            if (request == null || request.InstanceId != authority.InstanceId || request.RunId != authority.RunId
                || request.RoomEpoch != epoch || (terminal ? request.SchemaVersion != OnlineRunAssetCheckpoint.CompleteSchemaVersion
                    || request.Members == null || request.Members.Length != 0 : request.Members == null || request.Members.Length == 0))
                throw Failure("RunCheckpointIdentityMismatch");

            // 先复制值再排序/校验，调用者不能在等待SQL时通过可变数组改写已计算Hash的正文。
            string raw;
            try { raw = JsonSerializer.Serialize(request, ContentManifestValidator.Json); }
            catch (Exception error) when (error is JsonException or ArgumentException) { throw Failure("RunCheckpointCompleteInvalid"); }
            int rawBytes = Encoding.UTF8.GetByteCount(raw);
            RequirePayloadBudget(rawBytes, ulong.MaxValue);
            var frozen = JsonSerializer.Deserialize<OnlineRunAssetCheckpoint>(raw, ContentManifestValidator.Json)!;
            RunCheckpointCompleteRules.RequireShape(frozen);
            var candidateIds = CandidateIds(frozen);
            if (!IsId(frozen.CheckpointId)) throw Failure("RunCheckpointIdentityMismatch");
            Normalize(frozen);
            string json = CanonicalJson(frozen);
            int bytes = Encoding.UTF8.GetByteCount(json);
            string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            var previous = await Receipt(db, tx, authority.RunId, frozen.CheckpointId, ct);
            if (previous != null)
            {
                if (previous.PayloadHash != hash) throw Failure("RunCheckpointIdempotencyConflict");
                return Reply(previous); // 不重写头/身份账本，也不重复保存一份大JSON。
            }

            var head = await db.QuerySingleOrDefaultAsync<HeadRow>(new CommandDefinition(
                "SELECT * FROM RunAssetCheckpointHeads WHERE RunId=@Run FOR UPDATE", new { Run = authority.RunId }, tx, cancellationToken: ct));
            if (head != null && (head.InstanceId != instance || head.RoomEpoch != epoch)) throw Failure("RunCheckpointIdentityMismatch");
            if (frozen.Sequence == 0 || head != null && frozen.Sequence <= head.Sequence) throw Failure("RunCheckpointReplay");
            var trusted = await Context(db, tx, authority, head, candidateIds, frozen.SchemaVersion, ct, terminal);
            if (terminal) trusted = trusted with { TerminalCapture = true };
            RunAssetCheckpointRules.Require(frozen, trusted, content);
            // MySQL packet限制由运行环境决定。只查询、不SET GLOBAL；保守预留文本参数转义和命令开销。
            ulong packet = await db.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT @@max_allowed_packet", transaction: tx, cancellationToken: ct));
            RequirePayloadBudget(bytes, packet);
            int identityCount = await db.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM RunAssetCheckpointIdentities WHERE RunId=@Run", new { Run = authority.RunId }, tx, cancellationToken: ct));
            int missing = await CountUnrecorded(db, tx, authority.RunId, candidateIds, ct);
            // 墓碑不能为新掉落腾空间而删除；单Run历史身份同样有明确上限，超限保持现场而非假成功。
            if ((long)identityCount + missing > OnlineRunAssetCheckpoint.MaximumActiveItems) throw Failure("RunCheckpointIdentityBudgetExceeded");

            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO RunAssetCheckpointReceipts(RunId,CheckpointId,InstanceId,RoomEpoch,Sequence,PayloadHash,RecordedAtUtc)
                VALUES(@Run,@Checkpoint,@Instance,@Epoch,@Sequence,@Hash,UTC_TIMESTAMP(6))
                """, new { Run = authority.RunId, Checkpoint = frozen.CheckpointId, Instance = instance, Epoch = epoch,
                    frozen.Sequence, Hash = hash }, tx, cancellationToken: ct));
            // 分开INSERT/UPDATE，让大JSON在一条命令中只出现一次，不能因重复参数把packet预算翻倍。
            string writeHead = head == null ? """
                INSERT INTO RunAssetCheckpointHeads(RunId,InstanceId,RoomEpoch,Sequence,CheckpointId,PayloadHash,PayloadJson,PayloadBytes,UpdatedAtUtc)
                VALUES(@Run,@Instance,@Epoch,@Sequence,@Checkpoint,@Hash,@Json,@Bytes,UTC_TIMESTAMP(6))
                """ : """
                UPDATE RunAssetCheckpointHeads SET InstanceId=@Instance,RoomEpoch=@Epoch,Sequence=@Sequence,
                  CheckpointId=@Checkpoint,PayloadHash=@Hash,PayloadJson=@Json,PayloadBytes=@Bytes,UpdatedAtUtc=UTC_TIMESTAMP(6)
                WHERE RunId=@Run
                """;
            await db.ExecuteAsync(new CommandDefinition(writeHead, new { Run = authority.RunId, Checkpoint = frozen.CheckpointId,
                Instance = instance, Epoch = epoch, frozen.Sequence, Hash = hash, Json = json, Bytes = bytes }, tx, cancellationToken: ct));
            var identities = new List<IdentityWrite>(candidateIds.Count);
            foreach (var member in frozen.Members)
                foreach (var item in member.Assets.Items) identities.Add(new(item.InstanceId,
                    item.ContentId, item.Revision, false, member.PlayerId));
            foreach (var ground in frozen.GroundItems)
            {
                identities.Add(new(ground.Item.InstanceId, ground.Item.ContentId, ground.Item.Revision, false, null));
                foreach (var child in ground.Children)
                    identities.Add(new(child.InstanceId, child.ContentId, child.Revision, false, null));
            }
            if (OnlineRunAssetCheckpoint.HasWorldFacts(frozen.SchemaVersion))
                foreach (var flight in frozen.Flights)
                    identities.Add(new(flight.Item.InstanceId, flight.Item.ContentId, flight.Item.Revision, false, flight.PlayerId));
            foreach (var retired in frozen.RetiredItems)
                identities.Add(new(retired.InstanceId, retired.ContentId, retired.Revision, true, null));
            await RecordIdentities(db, tx, authority.RunId, frozen.Sequence, identities, ct);
            // 大快照不能在租约处理过程中到期后仍提交“新的权威检查点”；失败会回滚刚写入的整个Journal。
            _ = await instances.RequireCheckpointAuthorityAsync(db, tx, instance, secret, epoch, true, ct);
            // 此处有意不触碰Items/ItemLocations/Wallets/RunMembers/AssetLeases；退出结算仍是独立后续能力。
            return new OnlineRunAssetCheckpointReceipt { CheckpointId = frozen.CheckpointId, InstanceId = instance,
                RunId = authority.RunId, RoomEpoch = epoch, Sequence = frozen.Sequence, PayloadHash = hash };
        }, ct);

    public Task<OnlineRunAssetCheckpointReceipt> FindAsync(string instance, string secret, uint epoch,
        string checkpoint, CancellationToken ct)
        => transactions.MutateAsync(async (db, tx) =>
        {
            RoomTransactions.RequireId(checkpoint);
            var authority = await instances.RequireCheckpointAuthorityAsync(db, tx, instance, secret, epoch, false, ct);
            var receipt = await Receipt(db, tx, authority.RunId, checkpoint, ct) ?? throw new OnlineFailure(404, "RunCheckpointNotFound");
            if (receipt.InstanceId != instance || receipt.RoomEpoch != epoch) throw Failure("RunCheckpointIdentityMismatch");
            return Reply(receipt);
        }, ct);

    public Task<OnlineRunAssetCheckpointHeadReply> ReadHeadAsync(string instance, string secret, uint epoch, CancellationToken ct)
        => transactions.MutateAsync(async (db, tx) =>
        {
            var authority = await instances.RequireCheckpointAuthorityAsync(db, tx, instance, secret, epoch, false, ct);
            var head = await db.QuerySingleOrDefaultAsync<HeadRow>(new CommandDefinition(
                "SELECT * FROM RunAssetCheckpointHeads WHERE RunId=@Run", new { Run = authority.RunId }, tx, cancellationToken: ct))
                ?? throw new OnlineFailure(404, "RunCheckpointNotFound");
            var receipt = await Receipt(db, tx, authority.RunId, head.CheckpointId, ct) ?? throw Failure("RunCheckpointHeadInvalid");
            if (head.InstanceId != instance || head.RoomEpoch != epoch || receipt.InstanceId != instance || receipt.RoomEpoch != epoch
                || receipt.RunId != head.RunId || receipt.Sequence != head.Sequence || receipt.PayloadHash != head.PayloadHash)
                throw Failure("RunCheckpointHeadInvalid");
            var value = DecodeHead(head);
            // 查询的是已提交历史事实，不拿当前连接Epoch/租约去改写历史。身份仍来自真实实例与RunMembers。
            var entries = (await db.QueryAsync<EntryValidationRow>(new CommandDefinition(
                "SELECT CharacterId,EntrySnapshot FROM RunMembers WHERE RunId=@Run", new { Run = authority.RunId }, tx, cancellationToken: ct)))
                .ToDictionary(e => e.CharacterId, StringComparer.Ordinal);
            var members = new List<RunAssetCheckpointMemberContext>();
            var known = new Dictionary<string, RunAssetCheckpointKnownItem>(StringComparer.Ordinal);
            foreach (var member in value.Members)
            {
                if (!entries.TryGetValue(member.PlayerId, out var record)) throw Failure("RunCheckpointHeadInvalid");
                var entry = JsonSerializer.Deserialize<OnlineProfileReply>(record.EntrySnapshot, ContentManifestValidator.Json)?.CombatAssets
                    ?? throw Failure("RunCheckpointHeadInvalid");
                if (entry.ActorId != member.ActorId || entry.RunId != authority.RunId) throw Failure("RunCheckpointHeadInvalid");
                members.Add(new(member.PlayerId, member.ActorId, member.ConnectionEpoch, entry.InventoryVersion, true, member.NeverAdmitted, entry));
                foreach (var item in member.Assets.Items) known.TryAdd(item.InstanceId, new(member.PlayerId, item));
            }
            foreach (var ground in value.GroundItems)
            {
                known.TryAdd(ground.Item.InstanceId, new("", ground.Item));
                foreach (var child in ground.Children) known.TryAdd(child.InstanceId, new("", child));
            }
            if (OnlineRunAssetCheckpoint.HasWorldFacts(value.SchemaVersion))
                foreach (var flight in value.Flights) known.TryAdd(flight.Item.InstanceId, new(flight.PlayerId, flight.Item));
            foreach (var retired in value.RetiredItems) known.TryAdd(retired.InstanceId, new("", new OnlineOwnedItemSnapshot
            { InstanceId = retired.InstanceId, ContentId = retired.ContentId, Revision = retired.Revision }));
            var exitedFacts = await ReadExitedFacts(db, tx, authority, ct);
            bool terminal = value.SchemaVersion == OnlineRunAssetCheckpoint.CompleteSchemaVersion && value.Members.Length == 0;
            if (terminal) await RequireTerminalMembersAsync(db, tx, authority, ct);
            var trusted = new RunAssetCheckpointContext { InstanceId = instance, RunId = authority.RunId, RoomEpoch = epoch,
                InstanceLeaseValid = true, Members = members, KnownItems = known,
                TerminalCapture = terminal, ExitedPlayerIds = terminal ? entries.Keys.ToHashSet(StringComparer.Ordinal) : new HashSet<string>(),
                ExitedActors = exitedFacts.Actors, FinalDeathLosses = exitedFacts.DeathLosses,
                HomeAssetsHandedOff = terminal && exitedFacts.HomeExitVerified,
                ReceiptedHomeRetirements = exitedFacts.HomeRetirements };
            // 这里的校验Context只用于读回完整值，不传给写入/续租入口；不存在资产授予或Authority安装。
            RunAssetCheckpointRules.Require(value, trusted, content);
            return new OnlineRunAssetCheckpointHeadReply { Receipt = Reply(receipt), Checkpoint = value,
                IsComplete = value.SchemaVersion == OnlineRunAssetCheckpoint.CompleteSchemaVersion };
        }, ct);

    /// <summary>
    /// 输出仍走上方同一身份/Head完整校验，区别仅在历史DTO的JSON形状。
    /// 新C#字段的零默认值不是旧记录的金币事实；GET不能把它们重新写进历史检查点正文。
    /// </summary>
    public async Task<string> ReadHeadJsonAsync(string instance, string secret, uint epoch, CancellationToken ct)
    {
        var head = await ReadHeadAsync(instance, secret, epoch, ct);
        string receipt = JsonSerializer.Serialize(head.Receipt, ContentManifestValidator.Json);
        return "{\"Receipt\":" + receipt + ",\"Checkpoint\":" + CanonicalJson(head.Checkpoint)
            + ",\"IsComplete\":" + (head.IsComplete ? "true" : "false") + "}";
    }

    internal static void RequirePayloadBudget(int bytes, ulong packetBytes)
    {
        if (bytes < 1 || bytes > MaximumPayloadBytes) throw new OnlineFailure(413, "RunCheckpointPayloadTooLarge");
        // Connector文本参数会转义JSON引号/反斜杠；用2倍UTF8字节+8KiB命令余量保守预检。
        // 不是全部极限合法背包的支持承诺；不足就拒绝并让调用方保留DS现场。
        if ((ulong)bytes * 2 + 8192 > packetBytes) throw new OnlineFailure(413, "RunCheckpointStoragePacketLimit");
    }

    private async Task<RunAssetCheckpointContext> Context(MySqlConnection db, MySqlTransaction tx,
        RoomInstanceService.InstanceRow authority, HeadRow? head, HashSet<string> candidateIds, uint schema, CancellationToken ct,
        bool terminal = false)
    {
        bool home = (await RoomTransactions.Party(db, tx, authority.RoomId, ct)).DungeonId == "home";
        var members = (await db.QueryAsync<MemberRow>(new CommandDefinition("""
            SELECT m.CharacterId,m.InventoryVersion,m.EntrySnapshot,m.State,m.ConnectionEpoch,p.Seat,
              l.RunId AS LeaseRun,l.RoomEpoch AS LeaseEpoch FROM RunMembers m
            LEFT JOIN PartyMembers p ON p.CharacterId=m.CharacterId AND p.RoomId=@Room
            LEFT JOIN AssetLeases l ON l.CharacterId=m.CharacterId
            WHERE m.RunId=@Run ORDER BY m.CharacterId
            """, new { Run = authority.RunId, Room = authority.RoomId }, tx, cancellationToken: ct))).ToArray();
        if (members.Length is < 1 or > 4) throw Failure("RunCheckpointMemberMismatch");
        var active = new List<RunAssetCheckpointMemberContext>(members.Length);
        var exited = new HashSet<string>(StringComparer.Ordinal);
        var known = new Dictionary<string, RunAssetCheckpointKnownItem>(StringComparer.Ordinal);
        var entryIds = new HashSet<string>(StringComparer.Ordinal);
        var custody = (await db.QueryAsync<CheckpointCustodyRow>(new CommandDefinition(
            "SELECT InstanceId,ContentId,OriginalOwnerCharacterId,State FROM RunItemCustody WHERE RunId=@Run FOR UPDATE",
            new { Run = authority.RunId }, tx, cancellationToken: ct))).ToDictionary(x => x.InstanceId, StringComparer.Ordinal);
        OnlineRunAssetCheckpoint? previous = head == null ? null : DecodeHead(head);
        if (previous != null && OnlineRunAssetCheckpoint.HasWorldFacts(previous.SchemaVersion) && schema < previous.SchemaVersion)
            throw Failure("RunCheckpointSchemaDowngrade");
        foreach (var member in members)
        {
            // 房间Gate之后按CharacterId稳定顺序锁实际角色行，与库存/RunEntry的锁顺序一致。
            uint persistentVersion = await db.ExecuteScalarAsync<uint>(new CommandDefinition("SELECT Version FROM Characters WHERE CharacterId=@Player FOR UPDATE",
                new { Player = member.CharacterId }, tx, cancellationToken: ct));
            if (member.State is not ("Reserved" or "Admitted"))
            {
                exited.Add(member.CharacterId);
                if (previous == null && member.State == "Exited")
                {
                    // 个人退出不强依赖全局Head。首次Journal只从immutable Entry恢复仍由本Run
                    // Active托管的原物身份，Owner置空由当前DS事实重新确定；已Settled物品不回灌。
                    var oldProfile = JsonSerializer.Deserialize<OnlineProfileReply>(member.EntrySnapshot, ContentManifestValidator.Json);
                    var oldEntry = oldProfile?.CombatAssets;
                    if (oldEntry == null || oldEntry.PlayerId != member.CharacterId || oldEntry.RunId != authority.RunId)
                        throw Failure("RunCheckpointEntryUnavailable");
                    var oldItems = home ? oldEntry.Items.Concat(HomePracticeAssets.EntryGround(oldProfile!, content)) : oldEntry.Items;
                    foreach (var item in oldItems)
                        if (custody.TryGetValue(item.InstanceId, out var held) && held.State == "Active"
                            && held.OriginalOwnerCharacterId == member.CharacterId && held.ContentId == item.ContentId
                            && !known.TryAdd(item.InstanceId, new("", item)))
                            throw Failure("RunCheckpointOwnershipConflict");
                }
                continue;
            }
            // DS内InventoryVersion可以随本局操作推进，但持久角色仍须是入局租约冻结的那一代。
            // 两个版本不是同一时钟，不能把DS版本写回角色行来“修正”外部漂移。
            if (persistentVersion != member.InventoryVersion) throw Failure("RunCheckpointProfileVersionConflict");
            bool never = member.State == "Reserved" && member.ConnectionEpoch == 0;
            if (member.Seat == null || member.LeaseRun != authority.RunId || member.LeaseEpoch != authority.RoomEpoch
                || (member.ConnectionEpoch == 0 ? !never || !OnlineRunAssetCheckpoint.HasWorldFacts(schema) : member.State != "Admitted"))
                throw Failure("RunCheckpointMemberMismatch");
            var profile = JsonSerializer.Deserialize<OnlineProfileReply>(member.EntrySnapshot, ContentManifestValidator.Json);
            var entry = profile?.CombatAssets;
            if (entry == null) throw Failure("RunCheckpointEntryUnavailable");
            // Home地面来自immutable Entry的完整账号视图，属于已知原资产；仍不属于携带Snapshot。
            var entryItems = home ? entry.Items.Concat(HomePracticeAssets.EntryGround(profile!, content)) : entry.Items;
            foreach (var item in entryItems) entryIds.Add(item.InstanceId);
            var priorMember = previous?.Members.SingleOrDefault(m => m.PlayerId == member.CharacterId);
            active.Add(new(member.CharacterId, (ulong)member.Seat.Value + 1, member.ConnectionEpoch,
                priorMember?.Assets.InventoryVersion ?? member.InventoryVersion, true, never, entry));
            if (previous == null)
            {
                // Entry只初始化第一份已知身份/上界，不作为待保存的当前状态；当前正文必须由DS Workspace捕获。
                foreach (var item in entryItems)
                    if (!known.TryAdd(item.InstanceId, new(item.LocationKind == 5 || home && entry.Items.All(i => i.InstanceId != item.InstanceId) ? "" : member.CharacterId, item)))
                        throw Failure("RunCheckpointOwnershipConflict");
            }
        }
        if (previous != null)
        {
            foreach (var member in previous.Members)
                foreach (var item in member.Assets.Items)
                    if (!known.TryAdd(item.InstanceId, new(member.PlayerId, item))) throw Failure("RunCheckpointOwnershipConflict");
            foreach (var ground in previous.GroundItems)
            {
                if (!known.TryAdd(ground.Item.InstanceId, new("", ground.Item))) throw Failure("RunCheckpointOwnershipConflict");
                foreach (var item in ground.Children)
                    if (!known.TryAdd(item.InstanceId, new("", item))) throw Failure("RunCheckpointOwnershipConflict");
            }
            if (OnlineRunAssetCheckpoint.HasWorldFacts(previous.SchemaVersion))
                foreach (var flight in previous.Flights)
                    if (!known.TryAdd(flight.Item.InstanceId, new(flight.PlayerId, flight.Item))) throw Failure("RunCheckpointOwnershipConflict");
        }
        // 个人退出已原子提交的物品不再属于运行中的世界。仍托管在本局的旧Owner物品
        // 可以继续留地面或被队友拾取，但不可用已退出玩家的旧Entry再次授予。
        foreach (string id in known.Keys.ToArray())
        {
            if (custody.TryGetValue(id, out var held) && held.State is "Settled" or "Retired") known.Remove(id);
            else if (exited.Contains(known[id].OwnerPlayerId)) known[id] = new("", known[id].Item);
        }
        var forbidden = new HashSet<string>(StringComparer.Ordinal);
        var fresh = new HashSet<string>(StringComparer.Ordinal);
        // 整理耗尽的原身份永久退休：即使DS提交旧Entry或伪新ID，也不能跨Run重新授予。
        forbidden.UnionWith(await Norn.Business.Features.Inventory.InventoryService.ReadRetiredIdsAsync(db, tx, candidateIds, ct));
        var leasedPlayers = active.Select(m => m.PlayerId).ToHashSet(StringComparer.Ordinal);
        // 256ID一批，避免合法大检查点生成超长IN/巨量驱动参数；批次仍在同一短SQL事务/Gate内。
        foreach (string[] batch in candidateIds.Chunk(256))
        {
            var existing = (await db.QueryAsync<ExistingItemRow>(new CommandDefinition(
                "SELECT InstanceId,OwnerCharacterId,ContentId FROM Items WHERE InstanceId IN @Ids",
                new { Ids = batch }, tx, cancellationToken: ct))).ToDictionary(x => x.InstanceId, StringComparer.Ordinal);
            var ledger = (await db.QueryAsync<IdentityRow>(new CommandDefinition(
                "SELECT InstanceId,RunId,IsRetired FROM RunAssetCheckpointIdentities WHERE InstanceId IN @Ids FOR UPDATE",
                new { Ids = batch }, tx, cancellationToken: ct))).ToLookup(x => x.InstanceId, StringComparer.Ordinal);
            var foreignCustody = (await db.QueryAsync<string>(new CommandDefinition(
                "SELECT InstanceId FROM RunItemCustody WHERE InstanceId IN @Ids AND (State='Retired' OR (RunId<>@Run AND State='Active'))",
                new { Run = authority.RunId, Ids = batch }, tx, cancellationToken: ct))).ToHashSet(StringComparer.Ordinal);
            foreach (string id in batch)
            {
                // 同Run原Owner A→当前DS Owner B是合法转交，A/B都必须仍在已核实的同Run租约内。
                // 不能只因Entry曾出现过该ID，就忽略主库实际Owner/Content已经漂移到外人的事实。
                custody.TryGetValue(id, out var held);
                bool activeHeld = held?.State == "Active";
                bool invalidPersistent = existing.TryGetValue(id, out var persistent)
                    && (!known.TryGetValue(id, out var original)
                        || !leasedPlayers.Contains(persistent.OwnerCharacterId)
                            && !(activeHeld && held!.OriginalOwnerCharacterId == persistent.OwnerCharacterId)
                        || persistent.ContentId != original.Item.ContentId);
                var currentRecord = ledger[id].SingleOrDefault(x => x.RunId == authority.RunId);
                bool forbiddenHistory = currentRecord != null && (currentRecord.IsRetired || !known.ContainsKey(id))
                    || ledger[id].Any(x => x.RunId != authority.RunId) && !entryIds.Contains(id) && !activeHeld;
                if (held?.State is "Settled" or "Retired" || foreignCustody.Contains(id)
                    || forbiddenHistory || invalidPersistent) forbidden.Add(id);
                else if (!known.ContainsKey(id)) fresh.Add(id);
            }
        }
        var exitedFacts = await ReadExitedFacts(db, tx, authority, ct);
        return new RunAssetCheckpointContext { InstanceId = authority.InstanceId, RunId = authority.RunId,
            RoomEpoch = authority.RoomEpoch, InstanceLeaseValid = true, LastSequence = head?.Sequence ?? 0,
            Members = active, KnownItems = known, ExitedPlayerIds = exited, ForbiddenExistingIds = forbidden, NewItemIds = fresh,
            PreviousCheckpoint = previous, ExitedActors = exitedFacts.Actors, FinalDeathLosses = exitedFacts.DeathLosses,
            RequireCurrentExitLosses = true, HomeAssetsHandedOff = terminal && exitedFacts.HomeExitVerified,
            ReceiptedHomeRetirements = exitedFacts.HomeRetirements };
    }

    private static async Task<VerifiedExitedFacts> ReadExitedFacts(MySqlConnection db, MySqlTransaction tx,
        RoomInstanceService.InstanceRow authority, CancellationToken ct)
    {
        // 不能从提交者上报的Members或旧Entry猜测退出身份。已提交收据与RunMember终态
        // 共同证明原Actor；它只是历史署名，不是当前在局资源资格。保存与Head读回使用同一事实源。
        var rows = (await db.QueryAsync<ExitedActorRow>(new CommandDefinition("""
            SELECT e.SettlementId,e.CharacterId,e.RequestHash,e.ResultJson,m.ConnectionEpoch
            FROM RunMemberExitReceipts e JOIN RunMembers m ON m.RunId=e.RunId AND m.CharacterId=e.CharacterId
            WHERE e.RunId=@Run AND e.InstanceId=@Instance AND e.RoomEpoch=@Epoch AND m.State='Exited'
            """, new { Run=authority.RunId, Instance=authority.InstanceId, Epoch=authority.RoomEpoch }, tx, cancellationToken:ct))).ToArray();
        if (rows.Length > OnlineRunAssetCheckpoint.MaximumMembers) throw Failure("RunCheckpointMemberMismatch");
        var actors = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var deathLosses = new Dictionary<string, OnlineOwnedMaterialAmountSnapshot[]>(StringComparer.Ordinal);
        bool homeRun = (await RoomTransactions.Party(db, tx, authority.RoomId, ct)).DungeonId == "home";
        bool homeExitVerified = false;
        var homeRetirements = new Dictionary<string, OnlineRunAssetCheckpointRetired>(StringComparer.Ordinal);
        var seen = new HashSet<ulong>();
        foreach (var row in rows)
        {
            OnlineRunMemberExitReceipt? receipt;
            try { receipt = JsonSerializer.Deserialize<OnlineRunMemberExitReceipt>(row.ResultJson, ContentManifestValidator.Json); }
            catch (JsonException) { throw Failure("RunCheckpointMemberMismatch"); }
            if (receipt == null || receipt.SchemaVersion is not (OnlineRunMemberExitRequest.LegacySchemaVersion or OnlineRunMemberExitRequest.LegacyFinalDeathSchemaVersion or OnlineRunMemberExitRequest.CurrentSchemaVersion) || !receipt.Exited
                || receipt.SettlementId != row.SettlementId || receipt.PlayerId != row.CharacterId
                || receipt.RunId != authority.RunId || receipt.InstanceId != authority.InstanceId || receipt.RoomEpoch != authority.RoomEpoch
                || receipt.ActorId is < 1 or > 4 || receipt.ConnectionEpoch == 0 || receipt.ConnectionEpoch != row.ConnectionEpoch
                || receipt.CommittedVersion == 0 || receipt.PayloadHash != row.RequestHash
                || !actors.TryAdd(row.CharacterId, receipt.ActorId) || !seen.Add(receipt.ActorId)) throw Failure("RunCheckpointMemberMismatch");
            if (receipt.SchemaVersion == OnlineRunMemberExitRequest.LegacySchemaVersion)
            {
                if (receipt.Reason != OnlineRunMemberExitReason.None || receipt.FinalRetiredItems != null || receipt.FinalDeathLostMaterials != null)
                    throw Failure("RunCheckpointMemberMismatch");
            }
            else
            {
                if (receipt.Reason is not (OnlineRunMemberExitReason.VoluntaryExit or OnlineRunMemberExitReason.FinalDeath)
                    || receipt.FinalRetiredItems == null || receipt.FinalDeathLostMaterials == null || receipt.FinalDeathLostMaterials.Length > 8)
                    throw Failure("RunCheckpointMemberMismatch");
                ushort keys = 0;
                foreach (var part in receipt.FinalDeathLostMaterials)
                {
                    if (part == null || part.MaterialKey is < 1 or > 8 || part.Quantity == 0 || (keys & (1 << part.MaterialKey)) != 0)
                        throw Failure("RunCheckpointMemberMismatch");
                    keys |= (ushort)(1 << part.MaterialKey);
                }
                if (receipt.Reason == OnlineRunMemberExitReason.FinalDeath) deathLosses.Add(row.CharacterId, receipt.FinalDeathLostMaterials);
                else
                {
                    bool home = receipt.SchemaVersion == OnlineRunMemberExitRequest.CurrentSchemaVersion && homeRun
                        && Norn.Business.Features.Inventory.AccountPracticeContinuityStore.IsScope(receipt.PracticeSessionScope);
                    if ((!home && receipt.FinalRetiredItems.Length != 0) || receipt.FinalDeathLostMaterials.Length != 0)
                        throw Failure("RunCheckpointMemberMismatch");
                    if (home)
                    {
                        if (!Norn.Business.Features.Inventory.AccountPracticeContinuityStore.IsScope(receipt.PracticeContinuityHash)
                            || receipt.SuccessfulExtraction || receipt.CollectedGmu != 0 || receipt.DefeatedEnemies != 0
                            || receipt.FinalRetiredItems.Length > HomePracticeAssets.MaximumItems)
                            throw Failure("RunCheckpointMemberMismatch");
                        homeExitVerified = true;
                        foreach (var fact in receipt.FinalRetiredItems)
                            if (fact == null || !IsId(fact.InstanceId) || string.IsNullOrEmpty(fact.ContentId) || fact.ContentId.Length > 128
                                || fact.Revision == 0 || fact.Reason is not (OnlineRunAssetRetiredReason.Consumed or OnlineRunAssetRetiredReason.Merged or OnlineRunAssetRetiredReason.Destroyed)
                                || !homeRetirements.TryAdd(fact.InstanceId, fact)) throw Failure("RunCheckpointMemberMismatch");
                    }
                }
            }
        }
        // 收据是终止的来源，当前custody仍须匹配同Run/Content/Revision。即使后来账号再次整备，
        // 也不会把旧Home的墓碑解释成新Run的身份或可重新授予的物品。
        foreach (var batch in homeRetirements.Keys.Chunk(256))
        {
            var held = (await db.QueryAsync<HomeRetiredProofRow>(new CommandDefinition(
                "SELECT InstanceId,ContentId,Revision,State FROM RunItemCustody WHERE RunId=@Run AND InstanceId IN @Ids",
                new { Run = authority.RunId, Ids = batch }, tx, cancellationToken: ct))).ToDictionary(item => item.InstanceId, StringComparer.Ordinal);
            foreach (string id in batch)
                if (!held.TryGetValue(id, out var record) || record.State != "Retired"
                    || record.ContentId != homeRetirements[id].ContentId || record.Revision != homeRetirements[id].Revision)
                    throw Failure("RunCheckpointMemberMismatch");
        }
        return new(actors, deathLosses, homeRun && homeExitVerified && actors.Count == 1, homeRetirements);
    }
    private sealed record VerifiedExitedFacts(Dictionary<string, ulong> Actors, Dictionary<string, OnlineOwnedMaterialAmountSnapshot[]> DeathLosses,
        bool HomeExitVerified, Dictionary<string, OnlineRunAssetCheckpointRetired> HomeRetirements);

    /// <summary>
    /// 原Run成员必须全部有匹配实例/Epoch/Connection的最终收据；RolledBack、断线或HP=0均不能替代。
    /// 同Gate内同时确认旧成员lease/票/房间席位已经由个人事务解除，不负责替玩家清理这些记录。
    /// </summary>
    internal static async Task RequireTerminalMembersAsync(MySqlConnection db, MySqlTransaction tx,
        RoomInstanceService.InstanceRow authority, CancellationToken ct)
    {
        var members = (await db.QueryAsync<MemberRow>(new CommandDefinition(
            "SELECT CharacterId,State FROM RunMembers WHERE RunId=@Run", new { Run = authority.RunId }, tx, cancellationToken: ct))).ToArray();
        if (members.Length is < 1 or > OnlineRunAssetCheckpoint.MaximumMembers || members.Any(m => m.State != "Exited"))
            throw Failure("InstanceCompletionMembersPending");
        var facts = await ReadExitedFacts(db, tx, authority, ct);
        if (facts.Actors.Count != members.Length || members.Any(m => !facts.Actors.ContainsKey(m.CharacterId)))
            throw Failure("InstanceCompletionMembersPending");
        int outstanding = await db.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT (SELECT COUNT(*) FROM AssetLeases WHERE RunId=@Run)
              +(SELECT COUNT(*) FROM JoinTickets WHERE InstanceId=@Instance)
              +(SELECT COUNT(*) FROM PartyMembers WHERE RoomId=@Room)
            """, new { Run = authority.RunId, Instance = authority.InstanceId, Room = authority.RoomId }, tx, cancellationToken: ct));
        if (outstanding != 0) throw Failure("InstanceCompletionMembersPending");
    }

    /// <summary>
    /// Complete只确认既有终态Head，不改已经交还个人的背包/钱包。
    /// 用户已确认：全员真实离局后的未拾物随实例消失。先证明剩余托管与最终世界闭包精确一致，
    /// 再与Completed同事务退休；不能凭空连接、缺Owner或任意未知托管身份提前销毁。
    /// </summary>
    internal static async Task<string[]> RequireTerminalHeadAsync(MySqlConnection db, MySqlTransaction tx,
        RoomInstanceService.InstanceRow authority, InstanceCompleteRequest request, CancellationToken ct)
    {
        var head = await db.QuerySingleOrDefaultAsync<HeadRow>(new CommandDefinition(
            "SELECT * FROM RunAssetCheckpointHeads WHERE RunId=@Run FOR UPDATE", new { Run = authority.RunId }, tx, cancellationToken: ct));
        if (head == null || head.InstanceId != authority.InstanceId || head.RoomEpoch != authority.RoomEpoch
            || head.CheckpointId != request.CheckpointId || head.Sequence != request.Sequence || head.PayloadHash != request.PayloadHash)
            throw Failure("InstanceCompletionCheckpointPending");
        var receipt = await Receipt(db, tx, authority.RunId, request.CheckpointId, ct);
        if (receipt == null || receipt.InstanceId != head.InstanceId || receipt.RoomEpoch != head.RoomEpoch
            || receipt.Sequence != head.Sequence || receipt.PayloadHash != head.PayloadHash)
            throw Failure("InstanceCompletionCheckpointPending");
        var value = DecodeHead(head);
        var custody = (await db.QueryAsync<CheckpointCustodyRow>(new CommandDefinition(
            "SELECT InstanceId,State FROM RunItemCustody WHERE RunId=@Run FOR UPDATE",
            new { Run = authority.RunId }, tx, cancellationToken: ct))).ToArray();
        return RunCheckpointCompleteRules.RequireTerminalDiscard(value,
            custody.Where(c => c.State == "Active").Select(c => c.InstanceId).ToHashSet(StringComparer.Ordinal),
            custody.Where(c => c.State == "Retired").Select(c => c.InstanceId).ToHashSet(StringComparer.Ordinal),
            authority.State == "Completed");
    }

    private static HashSet<string> CandidateIds(OnlineRunAssetCheckpoint value)
    {
        if (value.Members == null || value.Members.Length > 4
            || value.Members.Length == 0 && value.SchemaVersion != OnlineRunAssetCheckpoint.CompleteSchemaVersion || value.GroundItems == null
            || value.GroundItems.Length > OnlineRunAssetCheckpoint.MaximumGroundItems || value.RetiredItems == null
            || value.RetiredItems.Length > OnlineRunAssetCheckpoint.MaximumRetiredItems)
            throw Failure("RunCheckpointBoundsInvalid");
        var ids = new HashSet<string>(StringComparer.Ordinal); int live = 0;
        void Add(string? id, bool active)
        {
            if (!IsId(id) || active && ++live > OnlineRunAssetCheckpoint.MaximumActiveItems) throw Failure("RunCheckpointBoundsInvalid");
            ids.Add(id!); // 重复身份留给完整Rules拒绝，不以HashSet去重代替合法性。
        }
        foreach (var member in value.Members)
        {
            if (member?.Assets?.Items == null || member.Assets.Items.Length > OnlineOwnedAssetSnapshot.MaximumItems) throw Failure("RunCheckpointBoundsInvalid");
            foreach (var item in member.Assets.Items) Add(item?.InstanceId, true);
        }
        foreach (var ground in value.GroundItems)
        {
            if (ground?.Children == null || ground.Children.Length > 32) throw Failure("RunCheckpointBoundsInvalid");
            Add(ground.Item?.InstanceId, true);
            foreach (var child in ground.Children) Add(child?.InstanceId, true);
        }
        if (OnlineRunAssetCheckpoint.HasWorldFacts(value.SchemaVersion))
            foreach (var flight in value.Flights) Add(flight.Item.InstanceId, true);
        foreach (var retired in value.RetiredItems) Add(retired?.InstanceId, false);
        return ids;
    }

    private static void Normalize(OnlineRunAssetCheckpoint value)
    {
        Array.Sort(value.Members, (a, b) => StringComparer.Ordinal.Compare(a.PlayerId, b.PlayerId));
        foreach (var member in value.Members) Array.Sort(member.Assets.Items, ItemOrder);
        Array.Sort(value.GroundItems, (a, b) => ItemOrder(a.Item, b.Item));
        foreach (var ground in value.GroundItems) Array.Sort(ground.Children, ItemOrder);
        Array.Sort(value.RetiredItems, (a, b) => StringComparer.Ordinal.Compare(a.InstanceId, b.InstanceId));
        if (value.SchemaVersion == OnlineRunAssetCheckpoint.CompleteSchemaVersion)
            Array.Sort(value.Coins, (a, b) => a.Id.CompareTo(b.Id));
        foreach (var member in value.Members) foreach (var item in member.Assets.Items) SortComponents(item);
        foreach (var ground in value.GroundItems)
        { SortComponents(ground.Item); foreach (var item in ground.Children) SortComponents(item); }
        if (!OnlineRunAssetCheckpoint.HasWorldFacts(value.SchemaVersion)) return;
        foreach (var member in value.Members)
        {
            Array.Sort(member.Wands, (a, b) => StringComparer.Ordinal.Compare(a.InstanceId, b.InstanceId));
            Array.Sort(member.SpellUses, (a, b) => StringComparer.Ordinal.Compare(a.InstanceId, b.InstanceId));
        }
        Array.Sort(value.World.Chunks, (a, b) =>
        {
            int c = a.Domain.CompareTo(b.Domain); if (c != 0) return c;
            c = a.X.CompareTo(b.X); if (c != 0) return c;
            c = a.Y.CompareTo(b.Y); return c != 0 ? c : a.Z.CompareTo(b.Z);
        });
        Array.Sort(value.Flights, (a, b) => StringComparer.Ordinal.Compare(a.FlightId, b.FlightId));
        foreach (var flight in value.Flights) SortComponents(flight.Item);
        Array.Sort(value.MaterialOutputs, (a, b) => StringComparer.Ordinal.Compare(a.OutputId, b.OutputId));
        foreach (var output in value.MaterialOutputs) Array.Sort(output.Remaining, (a, b) => a.MaterialKey.CompareTo(b.MaterialKey));
        Array.Sort(value.LostMaterials, (a, b) =>
        { int c = StringComparer.Ordinal.Compare(a.PlayerId, b.PlayerId); if (c != 0) return c;
            c = StringComparer.Ordinal.Compare(a.Source, b.Source); return c != 0 ? c : a.MaterialKey.CompareTo(b.MaterialKey); });
    }

    private static string CanonicalJson(OnlineRunAssetCheckpoint value)
    {
        if (value.SchemaVersion == OnlineRunAssetCheckpoint.CurrentSchemaVersion)
        {
            // 字段顺序和形状严格保留V1历史Hash。新DTO的null/0字段不能破坏已经提交的同ID收据。
            return JsonSerializer.Serialize(new { value.SchemaVersion, value.CheckpointId, value.InstanceId, value.RunId,
                value.GameplayConfigHash, value.RoomEpoch, value.ServerTick, value.Sequence,
                Members = value.Members.Select(m => new { m.PlayerId, m.ActorId, m.ConnectionEpoch, m.Assets }).ToArray(),
                GroundItems = value.GroundItems.Select(g => new { g.WorldOwnerId, g.Item, g.Children }).ToArray(), value.RetiredItems }, HistoricalJson);
        }
        if (value.SchemaVersion == OnlineRunAssetCheckpoint.LegacyCompleteSchemaVersion
            || value.SchemaVersion == OnlineRunAssetCheckpoint.LegacyCurrencyCompleteSchemaVersion)
        {
            // 明确列出Coin字段出现前的原声明顺序；不能给已提交V2/V3正文追加Coins:null破坏原Hash。
            // V2仍用原Owned2字段适配，V3保留当时已存在的Gold/CurrencyVersion，绝不吞币或补默认。
            return JsonSerializer.Serialize(new { value.SchemaVersion, value.CheckpointId, value.InstanceId, value.RunId,
                value.GameplayConfigHash, value.RoomEpoch, value.ServerTick, value.Sequence,
                value.Members, value.GroundItems, value.RetiredItems, value.World, value.Flights, value.MaterialOutputs, value.LostMaterials },
                value.SchemaVersion == OnlineRunAssetCheckpoint.LegacyCompleteSchemaVersion ? HistoricalCompleteJson : CompleteJson);
        }
        return JsonSerializer.Serialize(value, CompleteJson);
    }

    private static OnlineRunAssetCheckpoint DecodeHead(HeadRow head)
    {
        if (head.PayloadBytes is < 1 or > MaximumPayloadBytes || string.IsNullOrEmpty(head.PayloadJson)
            || Encoding.UTF8.GetByteCount(head.PayloadJson) > MaximumPayloadBytes) throw Failure("RunCheckpointHeadInvalid");
        try
        {
            var value = JsonSerializer.Deserialize<OnlineRunAssetCheckpoint>(head.PayloadJson, ContentManifestValidator.Json)
                ?? throw Failure("RunCheckpointHeadInvalid");
            RunCheckpointCompleteRules.RequireShape(value); _ = CandidateIds(value); Normalize(value);
            string canonical = CanonicalJson(value);
            if (value.InstanceId != head.InstanceId || value.RunId != head.RunId || value.RoomEpoch != head.RoomEpoch
                || value.Sequence != head.Sequence || value.CheckpointId != head.CheckpointId
                || Encoding.UTF8.GetByteCount(canonical) != head.PayloadBytes
                || Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))) != head.PayloadHash) throw Failure("RunCheckpointHeadInvalid");
            return value;
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { throw Failure("RunCheckpointHeadInvalid"); }
    }

    private static JsonSerializerOptions CompleteOptions()
    {
        var options = new JsonSerializerOptions(ContentManifestValidator.Json);
        options.Converters.Add(new CompleteFloatConverter()); return options;
    }
    private static JsonSerializerOptions HistoricalOptions(JsonSerializerOptions original)
    {
        var options = new JsonSerializerOptions(original);
        options.Converters.Add(new PreCurrencyOwnedConverter(original)); return options;
    }
    /// <summary>
    /// 按旧Owned2原声明顺序序列化，保留schema1/2已提交的正文Hash与已有请求幂等语义。
    /// 仅省略当时不存在的币字段，不修改任何旧DTO、资产、数据库记录或数量。
    /// Owned3即使位于legacy外层，也保留自己显式的币字段，不能被历史适配器吞掉。
    /// </summary>
    private sealed class PreCurrencyOwnedConverter(JsonSerializerOptions original) : JsonConverter<OnlineOwnedAssetSnapshot>
    {
        public override OnlineOwnedAssetSnapshot? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => JsonSerializer.Deserialize<OnlineOwnedAssetSnapshot>(ref reader, original);
        public override void Write(Utf8JsonWriter writer, OnlineOwnedAssetSnapshot value, JsonSerializerOptions options)
        {
            if (value.SchemaVersion != OnlineOwnedAssetSnapshot.PreCurrencySchemaVersion)
            { JsonSerializer.Serialize(writer, value, original); return; }
            JsonSerializer.Serialize(writer, new { value.SchemaVersion, value.RunId, value.PlayerId, value.InventoryId,
                value.GameplayConfigHash, value.SelectedWandInstanceId, value.ActorId, value.InventoryVersion, value.Items }, original);
        }
    }
    private sealed class CompleteFloatConverter : JsonConverter<float>
    {
        public override float Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetSingle();
        // SQL JSON可能将数值-0重写为0；两者数值相同。仅V2规范Hash归一化数值零，World原uint bits完全不改。
        public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options) => writer.WriteNumberValue(value == 0 ? 0 : value);
    }
    private static int ItemOrder(OnlineOwnedItemSnapshot a, OnlineOwnedItemSnapshot b) => StringComparer.Ordinal.Compare(a.InstanceId, b.InstanceId);
    private static void SortComponents(OnlineOwnedItemSnapshot item)
    {
        if (item.ContainerState?.Components is { } values && values.All(v => v != null))
            Array.Sort(values, (a, b) => a.MaterialKey.CompareTo(b.MaterialKey));
    }
    private static Task<ReceiptRow?> Receipt(MySqlConnection db, MySqlTransaction tx, string run, string checkpoint, CancellationToken ct)
        => db.QuerySingleOrDefaultAsync<ReceiptRow>(new CommandDefinition(
            "SELECT * FROM RunAssetCheckpointReceipts WHERE RunId=@Run AND CheckpointId=@Checkpoint", new { Run = run, Checkpoint = checkpoint }, tx, cancellationToken: ct));
    private static OnlineRunAssetCheckpointReceipt Reply(ReceiptRow row) => new()
    { CheckpointId = row.CheckpointId, InstanceId = row.InstanceId, RunId = row.RunId, RoomEpoch = row.RoomEpoch, Sequence = row.Sequence, PayloadHash = row.PayloadHash };
    private static async Task<int> CountUnrecorded(MySqlConnection db, MySqlTransaction tx, string run, HashSet<string> ids, CancellationToken ct)
    {
        int recorded = 0;
        foreach (string[] batch in ids.Chunk(256)) recorded += await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM RunAssetCheckpointIdentities WHERE RunId=@Run AND InstanceId IN @Ids", new { Run = run, Ids = batch }, tx, cancellationToken: ct));
        return ids.Count - recorded;
    }
    private static async Task RecordIdentities(MySqlConnection db, MySqlTransaction tx, string run, ulong sequence,
        List<IdentityWrite> identities, CancellationToken ct)
    {
        // 一条命令最多256行，避免最大32768项变成32768次往返；全部值仍走驱动参数而非拼接内容。
        foreach (IdentityWrite[] batch in identities.Chunk(256))
        {
            var sql = new StringBuilder("INSERT INTO RunAssetCheckpointIdentities(InstanceId,RunId,ContentId,Revision,IsRetired,OwnerPlayerId,LastSequence) VALUES ");
            var args = new DynamicParameters(); args.Add("Run", run); args.Add("Sequence", sequence);
            for (int i = 0; i < batch.Length; i++)
            {
                if (i != 0) sql.Append(',');
                sql.Append("(@Id").Append(i).Append(",@Run,@Content").Append(i).Append(",@Revision").Append(i)
                    .Append(",@Retired").Append(i).Append(",@Owner").Append(i).Append(",@Sequence)");
                args.Add("Id" + i, batch[i].Id); args.Add("Content" + i, batch[i].ContentId);
                args.Add("Revision" + i, batch[i].Revision); args.Add("Retired" + i, batch[i].Retired); args.Add("Owner" + i, batch[i].Owner);
            }
            // 固定MySQL8.4支持行别名；只更新已校验的状态，不修改原身份的Run或Content。
            sql.Append(" AS incoming ON DUPLICATE KEY UPDATE Revision=incoming.Revision,IsRetired=incoming.IsRetired,OwnerPlayerId=incoming.OwnerPlayerId,LastSequence=incoming.LastSequence");
            await db.ExecuteAsync(new CommandDefinition(sql.ToString(), args, tx, cancellationToken: ct));
            string[] retired = batch.Where(x => x.Retired).Select(x => x.Id).ToArray();
            if (retired.Length != 0)
                await db.ExecuteAsync(new CommandDefinition(
                    "UPDATE RunItemCustody SET State='Retired' WHERE RunId=@Run AND State='Active' AND InstanceId IN @Ids",
                    new { Run = run, Ids = retired }, tx, cancellationToken: ct));
        }
    }
    private static bool IsId(string? value) => value != null && Guid.TryParseExact(value, "N", out var id) && id.ToString("N") == value;
    private static OnlineFailure Failure(string code) => new(409, code);
    private sealed class MemberRow
    {
        public string CharacterId { get; set; } = "";
        public uint InventoryVersion { get; set; }
        public string EntrySnapshot { get; set; } = "";
        public string State { get; set; } = "";
        public uint ConnectionEpoch { get; set; }
        public int? Seat { get; set; }
        public string? LeaseRun { get; set; }
        public uint? LeaseEpoch { get; set; }
    }
    private sealed class HeadRow
    {
        public string RunId { get; set; } = "";
        public string InstanceId { get; set; } = "";
        public string CheckpointId { get; set; } = "";
        public string PayloadHash { get; set; } = "";
        public int PayloadBytes { get; set; }
        public uint RoomEpoch { get; set; }
        public ulong Sequence { get; set; }
        public string PayloadJson { get; set; } = "";
    }
    private sealed class ReceiptRow
    {
        public string RunId { get; set; } = "";
        public string CheckpointId { get; set; } = "";
        public string InstanceId { get; set; } = "";
        public string PayloadHash { get; set; } = "";
        public uint RoomEpoch { get; set; }
        public ulong Sequence { get; set; }
    }
    private sealed class IdentityRow
    {
        public string InstanceId { get; set; } = "";
        public string RunId { get; set; } = "";
        public bool IsRetired { get; set; }
    }
    private sealed class CheckpointCustodyRow
    {
        public string InstanceId { get; set; } = "";
        public string ContentId { get; set; } = "";
        public string? OriginalOwnerCharacterId { get; set; }
        public string State { get; set; } = "";
    }
    private sealed class HomeRetiredProofRow
    {
        public string InstanceId { get; set; } = "";
        public string ContentId { get; set; } = "";
        public string State { get; set; } = "";
        public uint Revision { get; set; }
    }
    private sealed class EntryValidationRow
    {
        public string CharacterId { get; set; } = "";
        public string EntrySnapshot { get; set; } = "";
    }
    private sealed class ExitedActorRow
    {
        public string SettlementId { get; set; } = "";
        public string CharacterId { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string ResultJson { get; set; } = "";
        public uint ConnectionEpoch { get; set; }
    }
    private sealed class ExistingItemRow
    {
        public string InstanceId { get; set; } = "";
        public string OwnerCharacterId { get; set; } = "";
        public string ContentId { get; set; } = "";
    }
    private sealed record IdentityWrite(string Id, string ContentId, uint Revision, bool Retired, string? Owner);
}
