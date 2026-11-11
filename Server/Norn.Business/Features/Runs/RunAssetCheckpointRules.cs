using Game.NetworkProtocol;
using Norn.Business.Content;
using Norn.Business.Features.Inventory;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Runs;

/// <summary>
/// 由真实实例认证与受保护数据库读构造的业务事实，不反序列化客户端/DS正文来填这些权限。
/// 纯规则不持有连接或发放资产；事务仍须在同一Gate/角色锁中重新核对这些事实并保存收据。
/// </summary>
public sealed record RunAssetCheckpointContext
{
    public string InstanceId { get; init; } = "";
    public string RunId { get; init; } = "";
    public uint RoomEpoch { get; init; }
    public bool InstanceLeaseValid { get; init; }
    public ulong LastSequence { get; init; }
    public OnlineRunAssetCheckpoint? PreviousCheckpoint { get; init; }
    public IReadOnlyList<RunAssetCheckpointMemberContext> Members { get; init; } = [];
    public IReadOnlyDictionary<string, RunAssetCheckpointKnownItem> KnownItems { get; init; }
        = new Dictionary<string, RunAssetCheckpointKnownItem>(StringComparer.Ordinal);
    // 仅需查询本次候选ID：全局已有但不属本Run的实例，以及历史墓碑/已持久退出资产都拒绝接管。
    // 旧物品从Items删除不等于成为新ID；调用方必须同时查询历史证据，不能只查询仍存活的Items行。
    public IReadOnlySet<string> ForbiddenExistingIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> ExitedPlayerIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    // 只读历史身份由业务服已提交的个人退出收据证明，仅供累计损耗事实继续归档。
    // 不把这些身份加入Members，也不恢复Ticket、资源写入或资产租约。
    public IReadOnlyDictionary<string, ulong> ExitedActors { get; init; }
        = new Dictionary<string, ulong>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, OnlineOwnedMaterialAmountSnapshot[]> FinalDeathLosses { get; init; }
        = new Dictionary<string, OnlineOwnedMaterialAmountSnapshot[]>(StringComparer.Ordinal);
    // 新提交必须包含已经确认的最终死亡损耗；历史Head读取不补写未来才发生的事实。
    public bool RequireCurrentExitLosses { get; init; }
    // 仅显式终态控制入口在验证全部原成员最终收据后设置；不是DS正文中的自报权限。
    // 普通在局保存仍必须有活动成员，历史schema1/2/3也没有空成员特例。
    public bool TerminalCapture { get; init; }
    // 只有业务确认Home本人最终收据后才成立。此时携带/地面均已移交账号，
    // terminal只能保留由该收据精确证明的退休子集，不能再发活动资产或新生身份。
    public bool HomeAssetsHandedOff { get; init; }
    public IReadOnlyDictionary<string, OnlineRunAssetCheckpointRetired> ReceiptedHomeRetirements { get; init; }
        = new Dictionary<string, OnlineRunAssetCheckpointRetired>(StringComparer.Ordinal);
    // 来自可信DS候选与事务内全局不存在查询；不假定项目已有逐Item预注册网络API。
    public IReadOnlySet<string> NewItemIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

public sealed record RunAssetCheckpointMemberContext(string PlayerId, ulong ActorId, uint ConnectionEpoch,
    uint InventoryVersion, bool HasLease, bool NeverAdmitted = false, OnlineOwnedAssetSnapshot? EntryAssets = null);

/// <summary>前次已提交的真实物品状态；Owner用于验证原归属仍属本Run，不能拿Entry重新授予。</summary>
public sealed record RunAssetCheckpointKnownItem(string OwnerPlayerId, OnlineOwnedItemSnapshot Item);

/// <summary>纯检查点门：只检查实例身份、完整分区与既有物品状态，不执行退出或数据库写入。</summary>
public static class RunAssetCheckpointRules
{
    public static void Require(OnlineRunAssetCheckpoint? checkpoint, RunAssetCheckpointContext trusted,
        ContentManifestValidator content)
    {
        RequireHeader(checkpoint, trusted, content);
        // 上面先拒绝超预算数组，再创建索引。控制面只在离散提交时分配；这些集合不进入Unity热路径。
        var value = checkpoint!;
        bool complete = OnlineRunAssetCheckpoint.HasWorldFacts(value.SchemaVersion);
        var members = RequireTrustedMembers(trusted, complete);
        var seenMembers = new HashSet<string>(StringComparer.Ordinal);
        var seenItems = new HashSet<string>(StringComparer.Ordinal);
        int activeCount = 0;
        foreach (var member in value.Members)
        {
            if (member == null || !IsId(member.PlayerId) || !seenMembers.Add(member.PlayerId)
                || !members.TryGetValue(member.PlayerId, out var expected) || member.ActorId != expected.ActorId
                || member.ConnectionEpoch != expected.ConnectionEpoch || trusted.ExitedPlayerIds.Contains(member.PlayerId))
                throw Failure("RunCheckpointMemberMismatch");
            var assets = member.Assets;
            if (assets == null || assets.RunId != value.RunId || assets.PlayerId != member.PlayerId
                || assets.ActorId != member.ActorId || assets.InventoryId != value.RunId + "/" + member.PlayerId
                || assets.InventoryVersion < expected.InventoryVersion)
                throw Failure("RunCheckpointMemberMismatch");
            // 父子闭包、装备位置、稀疏形状/堆叠和真实组分统一走原冻结规则，不另维护退出版库存规则。
            if (!OnlineRunAssetCheckpoint.HasCurrencyFacts(value.SchemaVersion)
                && assets.SchemaVersion == OnlineOwnedAssetSnapshot.PreCurrencySchemaVersion)
                OwnedCombatAssetRules.RequirePreCurrencyCheckpoint(assets, member.PlayerId, value.GameplayConfigHash, content);
            else OwnedCombatAssetRules.RequireCheckpoint(assets, member.PlayerId, value.GameplayConfigHash, content);
            foreach (var item in assets.Items)
            {
                RequireLiveItem(item, member.PlayerId, trusted, members, seenItems, content);
                CountActive(ref activeCount);
            }
        }
        if (seenMembers.Count != members.Count) throw Failure("RunCheckpointMemberMismatch");

        var worldOwners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ground in value.GroundItems)
        {
            if (ground == null || !IsId(ground.WorldOwnerId) || !worldOwners.Add(ground.WorldOwnerId)
                || ground.Item == null || ground.Children == null || ground.Children.Length > 32)
                throw Failure("RunCheckpointGroundInvalid");
            var root = ground.Item;
            if (root.LocationKind != 5 || root.ParentInstanceId != "" || root.Slot != -1
                || root.X != 0 || root.Y != 0 || root.QuarterTurns != 0 || root.EntryBoundBackpack)
                throw Failure("RunCheckpointGroundInvalid");
            RequireLiveItem(root, "", trusted, members, seenItems, content);
            CountActive(ref activeCount);
            var definition = content.RequireItem(root.ContentId);
            int capacity = definition.Kind == 1 && !root.UseTemplateWandStats
                ? root.WandStats!.ProgramCapacity : definition.WandProgramCapacity;
            // Children只表达原地面杖的安装卡，不扩展成任意背包/递归对象图。
            // 数据合同能保真杖卡，并不授权新增任何掉落/丢地动作。
            if (ground.Children.Length != 0 && definition.Kind != 1)
                throw Failure("RunCheckpointGroundInvalid");
            var slots = new HashSet<int>();
            foreach (var child in ground.Children)
            {
                if (child == null || child.LocationKind != 2 || child.ParentInstanceId != root.InstanceId
                    || child.Slot < 0 || child.Slot >= 32 || child.Slot >= capacity
                    || child.X != 0 || child.Y != 0 || child.QuarterTurns != 0 || !slots.Add(child.Slot)
                    || string.IsNullOrEmpty(child.ContentId) || content.RequireItem(child.ContentId).Kind != 0)
                    throw Failure("RunCheckpointGroundInvalid");
                RequireLiveItem(child, "", trusted, members, seenItems, content);
                CountActive(ref activeCount);
            }
        }

        if (complete)
        {
            foreach (var flight in value.Flights)
            {
                RequireLiveItem(flight.Item, flight.PlayerId, trusted, members, seenItems, content);
                CountActive(ref activeCount);
            }
            RunCheckpointCompleteRules.Require(value, trusted, content);
        }

        foreach (var retired in value.RetiredItems)
        {
            if (retired == null || !IsId(retired.InstanceId) || string.IsNullOrEmpty(retired.ContentId)
                || retired.Revision == 0 || retired.Reason is not (OnlineRunAssetRetiredReason.Consumed
                    or OnlineRunAssetRetiredReason.Merged or OnlineRunAssetRetiredReason.Destroyed))
                throw Failure("RunCheckpointRetirementInvalid");
            if (trusted.HomeAssetsHandedOff)
            {
                // 个人Exit可能先于周期Head。原DS Journal不能被清空来制造稳定；
                // 只允许同一最终收据中逐字段相同的墓碑继续归档，不为live物品放宽Forbidden门。
                if (!seenItems.Add(retired.InstanceId)
                    || !trusted.ReceiptedHomeRetirements.TryGetValue(retired.InstanceId, out var proof)
                    || proof.ContentId != retired.ContentId || proof.Revision != retired.Revision || proof.Reason != retired.Reason)
                    throw Failure("RunCheckpointOwnershipConflict");
            }
            else RequireIdentity(retired.InstanceId, retired.ContentId, retired.Revision, trusted, members, seenItems);
            _ = content.RequireItem(retired.ContentId);
            if (trusted.KnownItems.TryGetValue(retired.InstanceId, out var known) && known.Item.EntryBoundBackpack)
                throw Failure("RunCheckpointOwnershipConflict");
        }

        // 单一分区证明：不靠“当前Owner没有这件”默默推定消耗，原ID必须有明确去向。
        // NewItemIds也必须全部出现，避免上游遗漏了已登记的新杖卡/合并源，形成不完整检查点。
        foreach (string id in trusted.KnownItems.Keys)
            if (!seenItems.Contains(id)) throw Failure("RunCheckpointItemMissing");
        foreach (string id in trusted.NewItemIds)
            if (!seenItems.Contains(id)) throw Failure("RunCheckpointItemMissing");
    }

    private static void RequireHeader(OnlineRunAssetCheckpoint? checkpoint, RunAssetCheckpointContext trusted,
        ContentManifestValidator content)
    {
        if (checkpoint == null || trusted == null || content == null
            || checkpoint.SchemaVersion is not (OnlineRunAssetCheckpoint.CurrentSchemaVersion
                or OnlineRunAssetCheckpoint.LegacyCompleteSchemaVersion or OnlineRunAssetCheckpoint.LegacyCurrencyCompleteSchemaVersion
                or OnlineRunAssetCheckpoint.CompleteSchemaVersion)
            || !IsId(checkpoint.CheckpointId) || !IsId(checkpoint.InstanceId) || !IsId(checkpoint.RunId)
            || checkpoint.InstanceId != trusted.InstanceId || checkpoint.RunId != trusted.RunId
            || checkpoint.RoomEpoch == 0 || checkpoint.RoomEpoch != trusted.RoomEpoch || !trusted.InstanceLeaseValid
            || !IsHash(checkpoint.GameplayConfigHash) || checkpoint.GameplayConfigHash != content.GameplayConfigHash)
            throw Failure("RunCheckpointIdentityMismatch");
        // 同ID重试应由事务持久收据先返回旧结果。新的校验只接受更高Sequence，坏候选不推进任何水位。
        // ServerTick只是来源事实；此处不把uint数值大小当作跨回绕时钟或另加未确认的回绕政策。
        if (checkpoint.Sequence == 0 || checkpoint.Sequence <= trusted.LastSequence)
            throw Failure("RunCheckpointReplay");
        bool terminal = trusted.TerminalCapture && checkpoint.SchemaVersion == OnlineRunAssetCheckpoint.CompleteSchemaVersion;
        if (checkpoint.Members == null || checkpoint.Members.Length > OnlineRunAssetCheckpoint.MaximumMembers
            || (terminal ? checkpoint.Members.Length != 0 : checkpoint.Members.Length < 1)
            || checkpoint.GroundItems == null || checkpoint.GroundItems.Length > OnlineRunAssetCheckpoint.MaximumGroundItems
            || checkpoint.RetiredItems == null || checkpoint.RetiredItems.Length > OnlineRunAssetCheckpoint.MaximumRetiredItems
            || trusted.Members == null || trusted.Members.Count > OnlineRunAssetCheckpoint.MaximumMembers
            || (terminal ? trusted.Members.Count != 0 : trusted.Members.Count < 1)
            || trusted.KnownItems == null || trusted.KnownItems.Count > OnlineRunAssetCheckpoint.MaximumActiveItems
            || trusted.NewItemIds == null || trusted.NewItemIds.Count > OnlineRunAssetCheckpoint.MaximumActiveItems
            || trusted.ForbiddenExistingIds == null || trusted.ExitedPlayerIds == null
            || trusted.ExitedActors == null || trusted.ExitedActors.Count > OnlineRunAssetCheckpoint.MaximumMembers
            || trusted.FinalDeathLosses == null || trusted.FinalDeathLosses.Count > OnlineRunAssetCheckpoint.MaximumMembers
            || trusted.ReceiptedHomeRetirements == null || trusted.ReceiptedHomeRetirements.Count > HomePracticeAssets.MaximumItems)
            throw Failure("RunCheckpointBoundsInvalid");
        if (trusted.HomeAssetsHandedOff && (!terminal || checkpoint.GroundItems.Length != 0))
            throw Failure("RunCheckpointOwnershipConflict");
        if (terminal && (trusted.ExitedActors.Count < 1 || trusted.ExitedActors.Count != trusted.ExitedPlayerIds.Count
            || trusted.ExitedActors.Any(p => !trusted.ExitedPlayerIds.Contains(p.Key))))
            throw Failure("RunCheckpointMemberMismatch");
        RunCheckpointCompleteRules.RequireShape(checkpoint);
    }

    private static Dictionary<string, RunAssetCheckpointMemberContext> RequireTrustedMembers(RunAssetCheckpointContext trusted, bool complete)
    {
        var members = new Dictionary<string, RunAssetCheckpointMemberContext>(trusted.Members.Count, StringComparer.Ordinal);
        var actors = new HashSet<ulong>();
        foreach (var member in trusted.Members)
        {
            if (member == null || !IsId(member.PlayerId) || member.ActorId is < 1 or > 4
                || (member.ConnectionEpoch == 0 ? !complete || !member.NeverAdmitted || member.EntryAssets == null : member.NeverAdmitted)
                || member.InventoryVersion == 0 || !member.HasLease
                || trusted.ExitedPlayerIds.Contains(member.PlayerId) || !actors.Add(member.ActorId)
                || !members.TryAdd(member.PlayerId, member))
                throw Failure("RunCheckpointMemberMismatch");
        }
        return members;
    }

    private static void RequireLiveItem(OnlineOwnedItemSnapshot item, string currentPlayer,
        RunAssetCheckpointContext trusted, Dictionary<string, RunAssetCheckpointMemberContext> members,
        HashSet<string> seenItems, ContentManifestValidator content)
    {
        if (item == null || item.RemainingCharges < 0) throw Failure("RunCheckpointItemInvalid");
        RequireIdentity(item.InstanceId, item.ContentId, item.Revision, trusted, members, seenItems);
        OwnedCombatAssetRules.RequireCheckpointItemState(item, content.RequireItem(item.ContentId), content);
        if (trusted.KnownItems.TryGetValue(item.InstanceId, out var known))
        {
            // 实物ID/Content/是否无限及保底来源不可改写。有限卡在同Run只允许保持或减少，
            // 材料量则可因合法采集/倾倒/合并变化，不能误用“不增”假装世界材料总守恒。
            if (item.HasUnlimitedCharges != known.Item.HasUnlimitedCharges
                || !known.Item.HasUnlimitedCharges && item.RemainingCharges > known.Item.RemainingCharges
                || !SameWandState(item, known.Item)
                || item.EntryBoundBackpack != known.Item.EntryBoundBackpack
                || known.Item.EntryBoundBackpack && (currentPlayer != known.OwnerPlayerId || item.LocationKind != 4))
                throw Failure("RunCheckpointStateRollback");
        }
        else if (item.EntryBoundBackpack)
        {
            // 新保底由业务RunEntry原子发放，不从DS候选声明新的受保护来源。
            throw Failure("RunCheckpointOwnershipConflict");
        }
    }

    private static void RequireIdentity(string id, string contentId, uint revision, RunAssetCheckpointContext trusted,
        Dictionary<string, RunAssetCheckpointMemberContext> members, HashSet<string> seenItems)
    {
        if (!IsId(id) || string.IsNullOrEmpty(contentId) || revision == 0 || !seenItems.Add(id)
            || trusted.ForbiddenExistingIds.Contains(id))
            throw Failure("RunCheckpointOwnershipConflict");
        bool knownId = trusted.KnownItems.TryGetValue(id, out var known);
        if (knownId == trusted.NewItemIds.Contains(id)) throw Failure("RunCheckpointOwnershipConflict");
        if (!knownId) return;
        if (known == null || known.Item == null || known.Item.InstanceId != id || known.Item.ContentId != contentId
            || revision < known.Item.Revision || known.OwnerPlayerId == null
            || known.OwnerPlayerId.Length != 0 && (!members.ContainsKey(known.OwnerPlayerId)
                || trusted.ExitedPlayerIds.Contains(known.OwnerPlayerId)))
            throw Failure("RunCheckpointStateRollback");
    }

    private static void CountActive(ref int count)
    {
        if (++count > OnlineRunAssetCheckpoint.MaximumActiveItems) throw Failure("RunCheckpointBoundsInvalid");
    }

    /// <summary>Roll是物品固定身份的一部分；位置、选择、Mana或次数变化不能借机重Roll或切回模板。</summary>
    private static bool SameWandState(OnlineOwnedItemSnapshot a, OnlineOwnedItemSnapshot b)
    {
        if (a.UseTemplateWandStats != b.UseTemplateWandStats) return false;
        var x = a.WandStats; var y = b.WandStats;
        if (x == null || y == null) return x == null && y == null;
        return x.MaxMana == y.MaxMana && x.ManaRegenPerSecond == y.ManaRegenPerSecond
            && x.BaseCastDelay == y.BaseCastDelay && x.BaseReloadTime == y.BaseReloadTime
            && x.BaseSpreadDegrees == y.BaseSpreadDegrees && x.ProgramCapacity == y.ProgramCapacity
            && x.BaseDraws == y.BaseDraws && x.Shuffle == y.Shuffle
            && x.AlwaysCastContentIds != null && y.AlwaysCastContentIds != null
            && x.AlwaysCastContentIds.SequenceEqual(y.AlwaysCastContentIds, StringComparer.Ordinal);
    }

    private static bool IsId(string? value)
        => value != null && Guid.TryParseExact(value, "N", out var parsed) && parsed.ToString("N") == value;
    private static bool IsHash(string? value)
        => value != null && value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static OnlineFailure Failure(string code) => new(409, code);
}
