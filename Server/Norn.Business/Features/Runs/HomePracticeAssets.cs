using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using MySqlConnector;
using Norn.Business.Content;
using Norn.Business.Features.Inventory;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Runs;

/// <summary>
/// 个人Home的资产边界。地面是账号在home.map1的原身份，不是携带背包，也不是副本末人丢弃。
/// 本类只由已认证的入局/检查点/最终退出事务调用；不增加客户端资产写入口，不读取旧本地档。
/// </summary>
internal static class HomePracticeAssets
{
    internal const int MaximumItems = OnlineOwnedAssetSnapshot.MaximumItems + OnlineInventoryAreas.BackpackCapacity + OnlineInventoryAreas.WarehouseCapacity;

    internal static OnlineOwnedItemSnapshot[] EntryGround(OnlineProfileReply profile, ContentManifestValidator content)
    {
        var account = AccountInventoryMoves.AccountSnapshot(profile, content);
        OwnedCombatAssetRules.RequireAccount(account, profile.PlayerId, content.GameplayConfigHash, content);
        var items = account.Items.ToDictionary(i => i.InstanceId, StringComparer.Ordinal);
        var result = new List<OnlineOwnedItemSnapshot>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var world in profile.WorldItems.Where(w => w.Pose?.WorldSceneKey == "home.map1"))
        {
            AccountInventoryMoves.RequireWorldPose(world.Pose);
            if (world.Pose.SceneKey != "home.map1" || !Id(world.WorldOwnerId)
                || !items.TryGetValue(world.InstanceId, out var root) || root.LocationKind != 5 || root.EntryBoundBackpack
                || !seen.Add(root.InstanceId)) throw Invalid();
            result.Add(root);
            foreach (var child in account.Items.Where(i => i.ParentInstanceId == root.InstanceId))
            {
                // 原Ground wire只承载杖的安装卡；不能把任意父链/仓库或另一地面的卡藏进Children。
                if (content.RequireItem(root.ContentId).Kind != 1 || child.LocationKind != 2 || !seen.Add(child.InstanceId)) throw Invalid();
                result.Add(child);
            }
        }
        return result.ToArray();
    }

    internal static OnlineRunMemberExitRequest KeepCandidate(OnlineRunMemberExitRequest source, ContentManifestValidator content)
    {
        var keep = JsonSerializer.Deserialize<OnlineRunMemberExitRequest>(JsonSerializer.Serialize(source, ContentManifestValidator.Json), ContentManifestValidator.Json)!;
        keep.Assets.Items = keep.Assets.Items.Concat(keep.PracticeGroundItems.SelectMany(g => g.Children.Prepend(g.Item))).ToArray();
        // Account门保留原GMU/roll/容量/父链规则，并允许位置5；携带门仍单独校验，不能向CombatAssets塞地面。
        OwnedCombatAssetRules.RequireAccount(keep.Assets, keep.PlayerId, content.GameplayConfigHash, content);
        return keep;
    }

    internal static async Task SeedEntryAsync(MySqlConnection db, MySqlTransaction tx, OnlineProfileReply profile,
        string run, ContentManifestValidator content, CancellationToken ct)
    {
        var all = profile.CombatAssets.Items.Concat(EntryGround(profile, content)).ToArray();
        if (all.Length > MaximumItems || all.Select(i => i.InstanceId).Distinct(StringComparer.Ordinal).Count() != all.Length) throw Invalid();
        // 冻结时立即托管整个Home闭包，避免首次Journal误把数据库已有地面当作伪造新掉落。
        foreach (var item in all) await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO RunItemCustody(RunId,InstanceId,ContentId,OriginalOwnerCharacterId,State,Revision)
            VALUES(@Run,@Id,@Content,@Player,'Active',@Revision)
            """, new { Run = run, Id = item.InstanceId, Content = item.ContentId, Player = profile.PlayerId, item.Revision }, tx, cancellationToken: ct));
    }

    /// <summary>
    /// 仅释放没有任何局中持久事实的入场seed：此时账号SQL资产从未被改写。
    /// 一旦有Journal/个人收据，失败恢复必须保现场，绝不能借启动取消重授旧Entry。
    /// </summary>
    internal static Task ReleaseUnstartedSeedAsync(MySqlConnection db, MySqlTransaction tx, string run, CancellationToken ct)
        => db.ExecuteAsync(new CommandDefinition("""
            DELETE c FROM RunItemCustody c WHERE c.RunId=@Run AND c.State='Active'
              AND NOT EXISTS(SELECT 1 FROM RunAssetCheckpointHeads h WHERE h.RunId=@Run)
              AND NOT EXISTS(SELECT 1 FROM RunMemberExitReceipts e WHERE e.RunId=@Run)
              AND NOT EXISTS(SELECT 1 FROM RunMembers m WHERE m.RunId=@Run AND m.ConnectionEpoch<>0)
            """, new { Run = run }, tx, cancellationToken: ct));

    internal static async Task PrepareWorldReplacementAsync(MySqlConnection db, MySqlTransaction tx,
        OnlineRunMemberExitRequest value, string payloadHash, uint committed, CancellationToken ct)
    {
        // 先清旧Home位置，再安装新carried/ground。反过来会把本次刚拾起的旧地面杖及其卡删掉。
        // Hub有独立Scene键，整个过程不能删除它的姿态/实例/安装卡。
        string[] old = (await db.QueryAsync<string>(new CommandDefinition(
            "SELECT InstanceId FROM HubWorldItems WHERE OwnerCharacterId=@Player AND WorldSceneKey='home.map1'",
            new { Player = value.PlayerId }, tx, cancellationToken: ct))).ToArray();
        foreach (var batch in old.Chunk(256)) await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM ItemLocations WHERE OwnerCharacterId=@Player AND (InstanceId IN @Ids OR ParentInstanceId IN @Ids)",
            new { Player = value.PlayerId, Ids = batch }, tx, cancellationToken: ct));
        await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM HubWorldItems WHERE OwnerCharacterId=@Player AND WorldSceneKey='home.map1'", new { Player = value.PlayerId }, tx, cancellationToken: ct));
        // 013姿态外键要求真实命令来源。记录本次可信Home最终提交，不伪造客户端Drop，不改已应用迁移。
        string commandHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("Norn.HomePracticeExit.v1:" + payloadHash)));
        if (await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM InventoryCommands WHERE CharacterId=@Player AND CommandId=@Id",
            new { Player = value.PlayerId, Id = value.SettlementId }, tx, cancellationToken: ct)) != 0) throw new OnlineFailure(409, "CommandConflict");
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO InventoryCommands(CharacterId,CommandId,RequestHash,ResultJson,CommittedAtUtc)
            VALUES(@Player,@Id,@Hash,@Result,UTC_TIMESTAMP(6))
            """, new { Player = value.PlayerId, Id = value.SettlementId, Hash = commandHash,
                Result = JsonSerializer.Serialize(new { Kind = "HomePracticeExit", value.SettlementId, PayloadHash = payloadHash, CommittedVersion = committed }, ContentManifestValidator.Json) }, tx, cancellationToken: ct));
    }

    internal static async Task InstallWorldAsync(MySqlConnection db, MySqlTransaction tx, OnlineRunMemberExitRequest value, CancellationToken ct)
    {
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ground in value.PracticeGroundItems)
        {
            string owner = Id(ground.WorldOwnerId) ? ground.WorldOwnerId : Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes("Norn.HomeGround.v1:" + value.RunId + ":" + ground.WorldOwnerId)))[..32];
            if (!owners.Add(owner) || await db.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM HubWorldItems WHERE WorldOwnerId=@Owner OR InstanceId=@Id",
                new { Owner = owner, Id = ground.Item.InstanceId }, tx, cancellationToken: ct)) != 0)
                throw new OnlineFailure(409, "RunMemberExitOwnershipConflict");
            var pose = new OnlineHubWorldItemPose { SceneKey = "home.map1", WorldSceneKey = "home.map1", PoseKind = 2,
                PositionX = ground.Position.X, PositionY = ground.Position.Y, PositionZ = ground.Position.Z,
                RotationX = ground.Rotation.X, RotationY = ground.Rotation.Y, RotationZ = ground.Rotation.Z, RotationW = ground.Rotation.W };
            AccountInventoryMoves.RequireWorldPose(pose);
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO HubWorldItems(InstanceId,OwnerCharacterId,WorldOwnerId,SceneKey,WorldSceneKey,PoseJson,DropCommandId,RecordedAtUtc)
                VALUES(@Id,@Player,@Owner,'home.map1','home.map1',@Pose,@Command,UTC_TIMESTAMP(6))
                """, new { Id = ground.Item.InstanceId, Player = value.PlayerId, Owner = owner,
                    Pose = JsonSerializer.Serialize(pose, ContentManifestValidator.Json), Command = value.SettlementId }, tx, cancellationToken: ct));
        }
    }

    private static bool Id(string? value) => value != null && Guid.TryParseExact(value, "N", out var id) && id.ToString("N") == value;
    private static OnlineFailure Invalid() => new(409, "InvalidPracticeGround");
}
