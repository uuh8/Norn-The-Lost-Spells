using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Game.NetworkProtocol;
using Norn.Business.Content;
using Norn.Business.Features.Inventory;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Runs;

/// <summary>
/// 个人ESC候选的纯值门，不连接数据库/授予归属/解除租约。先复制固定正文，再求规范摘要，
/// 未来事务在认证后可先按摘要查既成收据；新提交才继续验证当前实例/member/lease/版本。
/// Schema1/2规范正文逐字保留；Schema3追加和平连续性/地面与可信成功事实。
/// </summary>
internal static class RunMemberExitRules
{
    internal static OnlineRunMemberExitRequest Freeze(OnlineRunMemberExitRequest request, out string json, out string hash)
    {
        if (request == null) throw Invalid();
        string raw;
        try { raw = JsonSerializer.Serialize(request, ContentManifestValidator.Json); }
        catch (Exception error) when (error is JsonException or ArgumentException) { throw Invalid(); }
        bool oldSchema = request.SchemaVersion is OnlineRunMemberExitRequest.LegacySchemaVersion or OnlineRunMemberExitRequest.LegacyFinalDeathSchemaVersion;
        if (!oldSchema && Encoding.UTF8.GetByteCount(raw) > OnlineRunMemberExitRequest.MaximumPayloadBytes)
            throw new OnlineFailure(413, "RunMemberExitPayloadTooLarge");
        var copy = JsonSerializer.Deserialize<OnlineRunMemberExitRequest>(raw, ContentManifestValidator.Json) ?? throw Invalid();
        bool legacy=copy.SchemaVersion==OnlineRunMemberExitRequest.LegacySchemaVersion;
        bool legacyFinal=copy.SchemaVersion==OnlineRunMemberExitRequest.LegacyFinalDeathSchemaVersion;
        bool current=copy.SchemaVersion==OnlineRunMemberExitRequest.CurrentSchemaVersion;
        if ((!legacy && !legacyFinal && !current)
            || !Id(copy.SettlementId) || !Id(copy.InstanceId) || !Id(copy.RunId) || !Id(copy.PlayerId)
            || copy.RoomEpoch == 0 || copy.ConnectionEpoch == 0 || copy.ExpectedPersistentVersion == 0
            || copy.ActorId is < 1 or > 4 || copy.Assets?.Items == null
            || copy.Assets.Items.Length > OnlineOwnedAssetSnapshot.MaximumItems
            || copy.Assets.Items.Any(i => i == null || i.ContainerState?.Components?.Length > 8)
            || (legacy ? copy.Reason!=OnlineRunMemberExitReason.None||copy.FinalRetiredItems!=null||copy.FinalDeathLostMaterials!=null
                : copy.Reason is not (OnlineRunMemberExitReason.VoluntaryExit or OnlineRunMemberExitReason.FinalDeath)
                    ||copy.FinalRetiredItems==null||copy.FinalRetiredItems.Length>(current && copy.PracticeSessionScope is { Length: > 0 }
                        ? HomePracticeAssets.MaximumItems : OnlineOwnedAssetSnapshot.MaximumItems)
                    ||copy.FinalDeathLostMaterials==null||copy.FinalDeathLostMaterials.Length>8
                    ||copy.FinalRetiredItems.Any(i=>i==null)||copy.FinalDeathLostMaterials.Any(i=>i==null))) throw Invalid();
        if (!current)
        {
            if (copy.SuccessfulExtraction || copy.CollectedGmu != 0 || copy.DefeatedEnemies != 0
                || copy.PracticeSessionScope != "" || copy.PracticeContinuityJson != "" || copy.PracticeContinuityHash != ""
                || copy.PracticeGroundItems != null) throw Invalid();
        }
        else RequirePracticeMetadata(copy);
        // 排序只发生在私有深副本。安装卡的语义顺序来自Slot，有序AlwaysCast数组不能为了Hash重排。
        Array.Sort(copy.Assets.Items, (a, b) => StringComparer.Ordinal.Compare(a.InstanceId, b.InstanceId));
        foreach (var item in copy.Assets.Items)
            if (item.ContainerState?.Components is { } parts)
            {
                if (parts.Any(p => p == null)) throw Invalid();
                Array.Sort(parts, (a, b) => a.MaterialKey.CompareTo(b.MaterialKey));
            }
        if(!legacy)
        {
            var retired=copy.FinalRetiredItems??throw Invalid();
            var losses=copy.FinalDeathLostMaterials??throw Invalid();
            Array.Sort(retired,(a,b)=>StringComparer.Ordinal.Compare(a.InstanceId,b.InstanceId));
            Array.Sort(losses,(a,b)=>a.MaterialKey.CompareTo(b.MaterialKey));
        }
        // V1/V2已提交正文分别沿原字段/顺序生成。追加DTO字段、默认false/0/null都不能改变历史Hash。
        json = legacy ? JsonSerializer.Serialize(new {copy.SchemaVersion,copy.SettlementId,copy.InstanceId,copy.RunId,copy.PlayerId,
            copy.RoomEpoch,copy.ConnectionEpoch,copy.ExpectedPersistentVersion,copy.ActorId,copy.Assets},ContentManifestValidator.Json)
            : legacyFinal ? JsonSerializer.Serialize(new { copy.SchemaVersion, copy.SettlementId, copy.InstanceId, copy.RunId, copy.PlayerId,
                copy.RoomEpoch, copy.ConnectionEpoch, copy.ExpectedPersistentVersion, copy.ActorId, copy.Assets,
                copy.Reason, copy.FinalRetiredItems, copy.FinalDeathLostMaterials }, ContentManifestValidator.Json)
            : JsonSerializer.Serialize(copy, ContentManifestValidator.Json);
        // 旧格式按真正旧Canonical核预算，不能因新DTO补了空字段就把边界内的历史正文误拒绝。
        if (Encoding.UTF8.GetByteCount(json) > OnlineRunMemberExitRequest.MaximumPayloadBytes)
            throw new OnlineFailure(413, "RunMemberExitPayloadTooLarge");
        hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return copy;
    }

    /// <summary>
    /// 这里只核纯值、精确闭包和空间标量；Home/单人/Entry scope须在已认证业务事务中再核。
    /// 普通成功出局可以没有个人击杀/收集，此时仍成功返回但经济hook不加进度。
    /// </summary>
    private static void RequirePracticeMetadata(OnlineRunMemberExitRequest value)
    {
        if (value.PracticeSessionScope == null || value.PracticeContinuityJson == null || value.PracticeContinuityHash == null
            || value.PracticeGroundItems == null || value.PracticeGroundItems.Length > OnlineOwnedAssetSnapshot.MaximumItems + OnlineInventoryAreas.BackpackCapacity + OnlineInventoryAreas.WarehouseCapacity
            || !value.SuccessfulExtraction && (value.CollectedGmu != 0 || value.DefeatedEnemies != 0)
            || value.SuccessfulExtraction && value.Reason != OnlineRunMemberExitReason.VoluntaryExit) throw Invalid();
        bool practice = value.PracticeSessionScope.Length != 0 || value.PracticeContinuityJson.Length != 0 || value.PracticeContinuityHash.Length != 0;
        if (practice)
        {
            if (!AccountPracticeContinuityStore.IsScope(value.PracticeSessionScope) || value.SuccessfulExtraction) throw Invalid();
            AccountPracticeContinuityStore.Validate(value.PracticeContinuityJson, value.PracticeContinuityHash, value.PlayerId, value.Assets.GameplayConfigHash);
        }
        else if (value.PracticeGroundItems.Length != 0) throw Invalid();
        if (value.Reason == OnlineRunMemberExitReason.FinalDeath && (practice || value.PracticeGroundItems.Length != 0)) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.Assets.Items) if (!seen.Add(item.InstanceId)) throw Invalid();
        var owners = new HashSet<string>(StringComparer.Ordinal);
        int count = value.Assets.Items.Length;
        foreach (var ground in value.PracticeGroundItems)
        {
            if (ground == null || string.IsNullOrWhiteSpace(ground.WorldOwnerId) || ground.WorldOwnerId.Length > 128
                || ground.WorldOwnerId != ground.WorldOwnerId.Trim() || ground.WorldOwnerId.Contains('\0') || !owners.Add(ground.WorldOwnerId)
                || ground.Item == null || ground.Children == null || ground.Children.Length > 32 || ground.Position == null || ground.Rotation == null
                || !float.IsFinite(ground.Position.X) || !float.IsFinite(ground.Position.Y) || !float.IsFinite(ground.Position.Z)
                || !float.IsFinite(ground.Rotation.X) || !float.IsFinite(ground.Rotation.Y) || !float.IsFinite(ground.Rotation.Z) || !float.IsFinite(ground.Rotation.W)
                || Math.Abs((double)ground.Rotation.X * ground.Rotation.X + (double)ground.Rotation.Y * ground.Rotation.Y
                    + (double)ground.Rotation.Z * ground.Rotation.Z + (double)ground.Rotation.W * ground.Rotation.W - 1) > .01) throw Invalid();
            var root = ground.Item;
            if (!Id(root.InstanceId) || !seen.Add(root.InstanceId) || root.EntryBoundBackpack || root.LocationKind != 5
                || root.ParentInstanceId != "" || root.Slot != -1 || root.X != 0 || root.Y != 0 || root.QuarterTurns != 0) throw Invalid();
            count++; var slots = new HashSet<int>();
            foreach (var child in ground.Children)
            {
                if (child == null || !Id(child.InstanceId) || !seen.Add(child.InstanceId) || child.EntryBoundBackpack
                    || child.LocationKind != 2 || child.ParentInstanceId != root.InstanceId || child.Slot is < 0 or >= 32
                    || child.X != 0 || child.Y != 0 || child.QuarterTurns != 0 || !slots.Add(child.Slot)) throw Invalid();
                count++;
            }
            if (count > OnlineOwnedAssetSnapshot.MaximumItems + OnlineInventoryAreas.BackpackCapacity + OnlineInventoryAreas.WarehouseCapacity) throw Invalid();
            Array.Sort(ground.Children, (a, b) => StringComparer.Ordinal.Compare(a.InstanceId, b.InstanceId));
            foreach (var item in ground.Children.Prepend(root))
                if (item.ContainerState?.Components is { } parts)
                {
                    if (parts.Length > 8 || parts.Any(p => p == null)) throw Invalid();
                    Array.Sort(parts, (a, b) => a.MaterialKey.CompareTo(b.MaterialKey));
                }
        }
        Array.Sort(value.PracticeGroundItems, (a, b) => StringComparer.Ordinal.Compare(a.WorldOwnerId, b.WorldOwnerId));
    }

    internal static void RequireOwned(OnlineRunMemberExitRequest request, ContentManifestValidator content)
    {
        var assets = request.Assets;
        if (assets == null || assets.SchemaVersion != OnlineOwnedAssetSnapshot.CurrentSchemaVersion
            || assets.RunId != request.RunId || assets.PlayerId != request.PlayerId || assets.ActorId != request.ActorId
            || assets.InventoryId != request.RunId + "/" + request.PlayerId
            || assets.InventoryVersion < request.ExpectedPersistentVersion) throw Invalid();
        OwnedCombatAssetRules.RequireCheckpoint(assets, request.PlayerId, content.GameplayConfigHash, content);
        RequireFinalRetirement(request);
    }

    internal static OnlineOwnedMaterialAmountSnapshot[] DeathLosses(OnlineRunMemberExitRequest request)
    {
        var total=new Dictionary<byte,ulong>();
        if(request.Reason!=OnlineRunMemberExitReason.FinalDeath)return [];
        try
        {
            foreach(var item in request.Assets.Items.Where(i=>i.LocationKind!=4))
            {
                if(item.ContainerState!=null)foreach(var part in item.ContainerState.Components)
                    total[part.MaterialKey]=checked(total.GetValueOrDefault(part.MaterialKey)+part.Quantity);
                if(item.MaterialState is{}material)
                    total[material.MaterialKey]=checked(total.GetValueOrDefault(material.MaterialKey)+material.Quantity);
            }
        }
        catch(OverflowException){throw Invalid();}
        return total.OrderBy(p=>p.Key).Select(p=>new OnlineOwnedMaterialAmountSnapshot{MaterialKey=p.Key,Quantity=p.Value}).ToArray();
    }

    private static void RequireFinalRetirement(OnlineRunMemberExitRequest request)
    {
        if(request.SchemaVersion==OnlineRunMemberExitRequest.LegacySchemaVersion)return;
        if(request.Reason==OnlineRunMemberExitReason.VoluntaryExit)
        {
            if(request.FinalDeathLostMaterials.Length!=0)throw Invalid();
            bool home = request.SchemaVersion == OnlineRunMemberExitRequest.CurrentSchemaVersion && request.PracticeSessionScope.Length != 0;
            if (!home) { if(request.FinalRetiredItems.Length!=0)throw Invalid(); return; }
            // Home不会把缺项猜成丢弃。可信DS提交真正Consumed/Merged/Destroyed journal，
            // 此处先核唯一性和keep互斥；原Run身份/Revision/新生证明仍由后续SQL事务核对。
            var keep = request.Assets.Items.Select(i => i.InstanceId).Concat(request.PracticeGroundItems.SelectMany(g => g.Children.Prepend(g.Item)).Select(i => i.InstanceId)).ToHashSet(StringComparer.Ordinal);
            var retired = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in request.FinalRetiredItems)
                if (!Id(item.InstanceId) || string.IsNullOrEmpty(item.ContentId) || item.ContentId.Length > 128 || item.Revision == 0
                    || item.Reason is not (OnlineRunAssetRetiredReason.Consumed or OnlineRunAssetRetiredReason.Merged or OnlineRunAssetRetiredReason.Destroyed)
                    || !retired.Add(item.InstanceId) || keep.Contains(item.InstanceId)) throw Invalid();
            return;
        }
        var expected=request.Assets.Items.Where(i=>i.LocationKind!=4).ToDictionary(i=>i.InstanceId,StringComparer.Ordinal);
        if(request.FinalRetiredItems.Length!=expected.Count)throw Invalid();
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var item in request.FinalRetiredItems)
            if(!seen.Add(item.InstanceId)||!expected.TryGetValue(item.InstanceId,out var original)||item.ContentId!=original.ContentId
                ||item.Revision!=original.Revision||item.Reason!=OnlineRunAssetRetiredReason.Destroyed)throw Invalid();
        var loss=DeathLosses(request);
        if(loss.Length!=request.FinalDeathLostMaterials.Length)throw Invalid();
        for(int i=0;i<loss.Length;i++)
            if(loss[i].MaterialKey!=request.FinalDeathLostMaterials[i].MaterialKey||loss[i].Quantity!=request.FinalDeathLostMaterials[i].Quantity)throw Invalid();
    }

    /// <summary>
    /// 原入局实例的不可重掷身份。材料量/附件可由获批玩法改变，但有限法术次数不应凭退出增加。
    /// 这不代替Run custody/settled/retired和全局ID查验；那些必须在最终SQL事务内验证。
    /// </summary>
    internal static void RequireOriginal(OnlineOwnedItemSnapshot current, OnlineOwnedItemSnapshot original)
    {
        if (current.InstanceId != original.InstanceId || current.ContentId != original.ContentId
            || current.Revision < original.Revision || current.HasUnlimitedCharges != original.HasUnlimitedCharges
            || current.EntryBoundBackpack != original.EntryBoundBackpack
            || !original.HasUnlimitedCharges && current.RemainingCharges > original.RemainingCharges
            || current.UseTemplateWandStats != original.UseTemplateWandStats || !SameWand(current.WandStats, original.WandStats))
            throw new OnlineFailure(409, "RunMemberExitOwnershipConflict");
    }

    private static bool SameWand(OnlineOwnedWandStatsSnapshot? a, OnlineOwnedWandStatsSnapshot? b)
    {
        if (a == null || b == null) return a == null && b == null;
        return a.MaxMana == b.MaxMana && a.ManaRegenPerSecond == b.ManaRegenPerSecond
            && a.BaseCastDelay == b.BaseCastDelay && a.BaseReloadTime == b.BaseReloadTime
            && a.BaseSpreadDegrees == b.BaseSpreadDegrees && a.ProgramCapacity == b.ProgramCapacity
            && a.BaseDraws == b.BaseDraws && a.Shuffle == b.Shuffle
            && a.AlwaysCastContentIds != null && b.AlwaysCastContentIds != null
            && a.AlwaysCastContentIds.SequenceEqual(b.AlwaysCastContentIds, StringComparer.Ordinal);
    }
    private static bool Id(string? value) => value != null && Guid.TryParseExact(value, "N", out var id) && id.ToString("N") == value;
    private static OnlineFailure Invalid() => new(409, "RunMemberExitInvalid");
}
