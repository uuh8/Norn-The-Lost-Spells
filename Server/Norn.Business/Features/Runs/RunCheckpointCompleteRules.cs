using System.Text.Json;
using Game.NetworkProtocol;
using Norn.Business.Content;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Runs;

/// <summary>V2世界事实与V3含金币完整事实的值校验；不选择退出、返还或安装策略。</summary>
internal static class RunCheckpointCompleteRules
{
    /// <summary>
    /// 世界终止只消费已验证的最终Head。未拾金币不入任何钱包；有ID的地面原物和子项则必须
    /// 精确覆盖剩余Active托管，防遗漏/额外身份静默消失。成功重放只接受相同身份均已退休的事实。
    /// 飞瓶/供料在个人可靠退出前已排空，不把缺失的在途资产当作可直接销毁的地面物。
    /// </summary>
    internal static string[] RequireTerminalDiscard(OnlineRunAssetCheckpoint value, IReadOnlySet<string> active,
        IReadOnlySet<string> retired, bool completed)
    {
        RequireShape(value);
        if (value.SchemaVersion != OnlineRunAssetCheckpoint.CompleteSchemaVersion || value.Members.Length != 0
            || value.Flights.Length != 0 || value.MaterialOutputs.Length != 0)
            throw new OnlineFailure(409, "InstanceCompletionWorldAssetsPending");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Add(OnlineOwnedItemSnapshot item)
        {
            if (item == null || !Guid.TryParseExact(item.InstanceId, "N", out var id)
                || id.ToString("N") != item.InstanceId || !ids.Add(item.InstanceId))
                throw new OnlineFailure(409, "InstanceCompletionWorldAssetsPending");
        }
        foreach (var ground in value.GroundItems)
        {
            if (ground == null || ground.Children == null) throw new OnlineFailure(409, "InstanceCompletionWorldAssetsPending");
            Add(ground.Item); foreach (var child in ground.Children) Add(child);
        }
        if (completed ? active.Count != 0 || ids.Any(id => !retired.Contains(id)) : !ids.SetEquals(active))
            throw new OnlineFailure(409, "InstanceCompletionWorldAssetsPending");
        return ids.Order(StringComparer.Ordinal).ToArray();
    }

    internal static void RequireShape(OnlineRunAssetCheckpoint value)
    {
        // 新能力必须显式给出Coin全表；历史正文不接受新字段，避免保存时被Canonical静默丢弃。
        if (value.SchemaVersion == OnlineRunAssetCheckpoint.CompleteSchemaVersion
            ? !OnlineRunAssetCheckpoint.HasValidCoinFacts(value.Coins) : value.Coins != null) throw Failure();
        if (value.SchemaVersion == OnlineRunAssetCheckpoint.CurrentSchemaVersion)
        {
            if (value.World != null || value.Flights != null || value.MaterialOutputs != null || value.LostMaterials != null
                || value.Members != null && value.Members.Any(m => m != null && (m.NeverAdmitted || m.ResourceVersion != 0
                    || m.SelectionVersion != 0 || m.SpellUseVersion != 0 || m.SelectedToolSlotPlusOne != 0 || m.Wands != null || m.SpellUses != null))
                || value.GroundItems != null && value.GroundItems.Any(g => g != null && (g.Position != null || g.Rotation != null))) throw Failure();
            return;
        }
        if (!OnlineRunAssetCheckpoint.HasWorldFacts(value.SchemaVersion) || value.World?.Chunks == null
            || value.World.Chunks.Length > OnlineRunAssetCheckpoint.MaximumWorldKeys
            || value.World.Chunks.Any(c => c == null || c.Words == null || c.Words.Length > OnlineRunAssetCheckpoint.MaximumWordsPerChunk)
            || value.Flights == null || value.Flights.Length > OnlineRunAssetCheckpoint.MaximumMembers * OnlineRunAssetCheckpoint.MaximumFlightsPerMember
            || value.Flights.Any(f => f == null || f.Item == null)
            || value.MaterialOutputs == null || value.MaterialOutputs.Length > OnlineRunAssetCheckpoint.MaximumMaterialOutputs
            || value.MaterialOutputs.Any(o => o == null || o.Remaining == null || o.Remaining.Length > 8 || o.Remaining.Any(p => p == null))
            || value.LostMaterials == null || value.LostMaterials.Length > (value.SchemaVersion == OnlineRunAssetCheckpoint.CompleteSchemaVersion
                ? OnlineRunAssetCheckpoint.MaximumLostMaterials : OnlineRunAssetCheckpoint.MaximumMembers * 8 * 2)
            || value.LostMaterials.Any(l => l == null) || value.Members == null
            || value.Members.Any(m => m == null || m.Wands == null || m.Wands.Length > 3 || m.Wands.Any(w => w == null)
                || m.SpellUses == null || m.SpellUses.Length > OnlineOwnedAssetSnapshot.MaximumItems || m.SpellUses.Any(s => s == null))) throw Failure();
    }

    internal static void Require(OnlineRunAssetCheckpoint value, RunAssetCheckpointContext context, ContentManifestValidator content)
    {
        RequireShape(value);
        if (!OnlineRunAssetCheckpoint.HasWorldFacts(value.SchemaVersion)) return;
        RunCheckpointCoinRules.Require(value, context.PreviousCheckpoint);
        var previous = context.PreviousCheckpoint;
        var prior = previous != null && OnlineRunAssetCheckpoint.HasWorldFacts(previous.SchemaVersion) ? previous : null;
        RunCheckpointWorldRules.Require(value.World, prior?.World);
        var owners = value.Members.ToDictionary(m => m.PlayerId, StringComparer.Ordinal);
        var expected = context.Members.ToDictionary(m => m.PlayerId, StringComparer.Ordinal);
        foreach (var member in value.Members)
        {
            if (!expected.TryGetValue(member.PlayerId, out var authority) || member.NeverAdmitted != authority.NeverAdmitted) throw Failure();
            if (OnlineRunAssetCheckpoint.HasCurrencyFacts(value.SchemaVersion))
            {
                var old = prior != null && OnlineRunAssetCheckpoint.HasCurrencyFacts(prior.SchemaVersion)
                    ? prior.Members.FirstOrDefault(m => m.PlayerId == member.PlayerId)?.Assets : null;
                // 同一修订不能改币值；DS新修订可表达真实变化，但保存Journal绝不等于Wallet加款。
                if (old != null && (member.Assets.CurrencyVersion < old.CurrencyVersion
                    || member.Assets.CurrencyVersion == old.CurrencyVersion && member.Assets.Gold != old.Gold)) throw Failure();
            }
            if (member.NeverAdmitted)
            {
                if (member.ConnectionEpoch != 0 || authority.EntryAssets == null || member.ResourceVersion != 0 || member.SelectionVersion != 0
                    || member.SpellUseVersion != 0 || member.SelectedToolSlotPlusOne != 0 || member.Wands.Length != 0 || member.SpellUses.Length != 0
                    || !SameAssets(member.Assets, authority.EntryAssets)
                    || context.PreviousCheckpoint?.Members.Any(m => m.PlayerId == member.PlayerId && m.ConnectionEpoch != 0) == true) throw Failure();
                continue;
            }
            if (member.ConnectionEpoch == 0 || member.ResourceVersion == 0 || member.SelectionVersion == 0 || member.SpellUseVersion == 0
                || member.SelectedToolSlotPlusOne > 7) throw Failure();
            var items = member.Assets.Items.ToDictionary(i => i.InstanceId, StringComparer.Ordinal);
            var wands = new HashSet<string>(StringComparer.Ordinal);
            foreach (var wand in member.Wands)
            {
                if (!items.TryGetValue(wand.InstanceId ?? "", out var item) || item.LocationKind != 3 || !wands.Add(wand.InstanceId!)
                    || wand.ProgramRevision == 0 || !NonNegative(wand.CurrentMana) || !NonNegative(wand.CooldownRemaining)
                    || !item.UseTemplateWandStats && item.WandStats != null && wand.CurrentMana > item.WandStats.MaxMana) throw Failure();
            }
            if (wands.Count != items.Values.Count(i => i.LocationKind == 3)) throw Failure();
            var cards = new HashSet<string>(StringComparer.Ordinal);
            foreach (var card in member.SpellUses)
                if (!items.TryGetValue(card.InstanceId ?? "", out var item) || content.RequireItem(item.ContentId).Kind != 0 || !cards.Add(card.InstanceId!)) throw Failure();
            if (cards.Count != items.Values.Count(i => content.RequireItem(i.ContentId).Kind == 0)) throw Failure();
            if (member.SelectedToolSlotPlusOne != 0 && !items.Values.Any(i => i.LocationKind == 8 && i.Slot == member.SelectedToolSlotPlusOne - 1)) throw Failure();
        }
        foreach (var ground in value.GroundItems)
            if (!Vector(ground.Position) || !Quaternion(ground.Rotation)) throw Failure();
        var flightIds = new HashSet<string>(StringComparer.Ordinal);
        var flightCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var oldFlights = prior?.Flights.ToDictionary(f => f.FlightId, StringComparer.Ordinal);
        foreach (var flight in value.Flights)
        {
            RequireOwner(flight.PlayerId, flight.ActorId, owners);
            int count = flightCounts.GetValueOrDefault(flight.PlayerId) + 1; flightCounts[flight.PlayerId] = count;
            var item = flight.Item; var definition = content.RequireItem(item.ContentId);
            if (count > OnlineRunAssetCheckpoint.MaximumFlightsPerMember || !Id(flight.FlightId) || !flightIds.Add(flight.FlightId)
                || item.InstanceId == flight.FlightId || flight.WorldId != value.World.WorldId || !Text(flight.SourceId)
                || item.LocationKind != 6 || item.ParentInstanceId != flight.FlightId || item.Slot != -1 || item.X != 0 || item.Y != 0 || item.QuarterTurns != 0
                || definition.Kind is not (3 or 4) || !NonNegative(flight.Radius) || !Vector(flight.Position)) throw Failure();
            if (flight.HasPhysicalProxy)
            { if (!Quaternion(flight.Rotation) || !Vector(flight.Velocity)) throw Failure(); }
            else if (!flight.Impacted || flight.Rotation != null || flight.Velocity != null) throw Failure();
            ulong remaining = item.MaterialState?.Quantity ?? Total(item.ContainerState?.Components);
            ulong capacity = definition.Kind == 3 ? definition.ContainerCapacityGmu : definition.MaterialItemCapacityGmu;
            if (flight.Deposited > capacity || remaining > capacity - flight.Deposited) throw Failure();
            if (oldFlights != null && oldFlights.TryGetValue(flight.FlightId, out var old))
            {
                ulong before = old.Item.MaterialState?.Quantity ?? Total(old.Item.ContainerState?.Components);
                if (old.PlayerId != flight.PlayerId || old.ActorId != flight.ActorId || old.Item.InstanceId != item.InstanceId
                    || old.WorldId != flight.WorldId || old.SourceId != flight.SourceId || old.Impacted && !flight.Impacted
                    || flight.Deposited < old.Deposited || remaining > before
                    || flight.Deposited - old.Deposited != before - remaining) throw Failure();
            }
        }
        var outputs = new Dictionary<string, OnlineRunAssetCheckpointMaterialOutput>(StringComparer.Ordinal);
        ulong maxContainer = content.Manifest.Items.Where(i => i.Kind == 3).Select(i => i.ContainerCapacityGmu).DefaultIfEmpty(0UL).Max();
        var oldOutputs = prior?.MaterialOutputs.ToDictionary(o => o.OutputId, StringComparer.Ordinal);
        foreach (var output in value.MaterialOutputs)
        {
            RequireOwner(output.PlayerId, output.ActorId, owners);
            if (!Id(output.OutputId) || !outputs.TryAdd(output.OutputId, output) || output.WorldId != value.World.WorldId) throw Failure();
            ulong total = Total(output.Remaining); if (total == 0 || total > maxContainer) throw Failure();
            if (oldOutputs != null && oldOutputs.TryGetValue(output.OutputId, out var old))
            {
                if (old.PlayerId != output.PlayerId || old.ActorId != output.ActorId || old.WorldId != output.WorldId) throw Failure();
                foreach (var amount in output.Remaining)
                    if (amount.Quantity > old.Remaining.Where(p => p.MaterialKey == amount.MaterialKey).Select(p => p.Quantity).DefaultIfEmpty(0UL).Single()) throw Failure();
            }
        }
        var losses = new Dictionary<(string, string, byte), ulong>();
        var lossActors = new Dictionary<(string, string, byte), ulong>();
        foreach (var lost in value.LostMaterials)
        {
            RequireLossOwner(lost.PlayerId, lost.ActorId, owners, context.ExitedActors);
            if (lost.Source is not (OnlineRunAssetCheckpointLostMaterial.ContainerTransitSource or OnlineRunAssetCheckpointLostMaterial.SpellOutputSource
                    or OnlineRunAssetCheckpointLostMaterial.FinalDeathSource)
                || lost.MaterialKey is < 1 or > 8 || !losses.TryAdd((lost.PlayerId, lost.Source, lost.MaterialKey), lost.Quantity)) throw Failure();
            if (lost.Source == OnlineRunAssetCheckpointLostMaterial.FinalDeathSource
                && (!context.ExitedActors.TryGetValue(lost.PlayerId, out ulong deadActor) || deadActor != lost.ActorId
                    || !context.FinalDeathLosses.TryGetValue(lost.PlayerId, out var destroyed)
                    || !destroyed.Any(p => p.MaterialKey == lost.MaterialKey && p.Quantity == lost.Quantity))) throw Failure();
            lossActors.Add((lost.PlayerId, lost.Source, lost.MaterialKey), lost.ActorId);
        }
        if (prior != null) foreach (var lost in prior.LostMaterials)
        {
            var key = (lost.PlayerId, lost.Source, lost.MaterialKey);
            if (losses.GetValueOrDefault(key) < lost.Quantity
                || lossActors.TryGetValue(key, out ulong actor) && actor != lost.ActorId) throw Failure();
        }
        if (context.RequireCurrentExitLosses && value.SchemaVersion == OnlineRunAssetCheckpoint.CompleteSchemaVersion)
            foreach (var dead in context.FinalDeathLosses)
                foreach (var amount in dead.Value)
                    if (losses.GetValueOrDefault((dead.Key, OnlineRunAssetCheckpointLostMaterial.FinalDeathSource, amount.MaterialKey)) != amount.Quantity)
                        throw Failure();
    }

    private static void RequireLossOwner(string player, ulong actor,
        Dictionary<string, OnlineRunAssetCheckpointMember> owners, IReadOnlyDictionary<string, ulong> exited)
    {
        if (player != null && owners.TryGetValue(player, out var member)
            && member.ActorId == actor && !member.NeverAdmitted) return;
        // 损耗已经发生，退出不能抹去累计事实；只有业务收据证明的原Player/Actor可继续署名。
        // 此例外限损耗账本，不用于Flight/Output的活动资源Owner门。
        if (player != null && actor is >= 1 and <= 4 && exited.TryGetValue(player, out ulong original) && original == actor) return;
        throw Failure();
    }

    private static void RequireOwner(string player, ulong actor, Dictionary<string, OnlineRunAssetCheckpointMember> owners)
    { if (player == null || !owners.TryGetValue(player, out var member) || member.ActorId != actor || member.NeverAdmitted) throw Failure(); }
    private static ulong Total(OnlineOwnedMaterialAmountSnapshot[]? parts)
    {
        if (parts == null || parts.Length > 8) throw Failure();
        ushort keys = 0; ulong total = 0;
        foreach (var part in parts)
        {
            if (part == null || part.MaterialKey is < 1 or > 8 || part.Quantity == 0 || (keys & (1 << part.MaterialKey)) != 0) throw Failure();
            keys |= (ushort)(1 << part.MaterialKey);
            try { total = checked(total + part.Quantity); } catch (OverflowException) { throw Failure(); }
        }
        return total;
    }
    private static bool SameAssets(OnlineOwnedAssetSnapshot a, OnlineOwnedAssetSnapshot b)
    {
        static string Canonical(OnlineOwnedAssetSnapshot assets)
        {
            var copy = JsonSerializer.Deserialize<OnlineOwnedAssetSnapshot>(JsonSerializer.Serialize(assets, ContentManifestValidator.Json), ContentManifestValidator.Json)!;
            Array.Sort(copy.Items, (x, y) => StringComparer.Ordinal.Compare(x.InstanceId, y.InstanceId));
            foreach (var item in copy.Items) if (item.ContainerState?.Components is { } parts)
                Array.Sort(parts, (x, y) => x.MaterialKey.CompareTo(y.MaterialKey));
            return JsonSerializer.Serialize(copy, ContentManifestValidator.Json);
        }
        return Canonical(a) == Canonical(b);
    }
    private static bool Vector(OnlineRunAssetCheckpointVector3? v) => v != null && float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool Quaternion(OnlineRunAssetCheckpointQuaternion? q)
        => q != null && float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W)
            && Math.Abs((double)q.X * q.X + (double)q.Y * q.Y + (double)q.Z * q.Z + (double)q.W * q.W - 1) <= .001;
    private static bool NonNegative(float v) => float.IsFinite(v) && v >= 0;
    private static bool Id(string? v) => v != null && Guid.TryParseExact(v, "N", out var id) && id.ToString("N") == v;
    private static bool Text(string? v) => !string.IsNullOrWhiteSpace(v) && v.Length <= 128 && !v.Any(char.IsControl);
    private static OnlineFailure Failure() => new(409, "RunCheckpointCompleteInvalid");
}
