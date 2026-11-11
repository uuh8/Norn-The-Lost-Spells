using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;
using Shared = Game.NetworkProtocol;

namespace Game.Multiplayer
{
    public enum RunCheckpointFlightPoseStatus : byte { Missing, Available, Inconsistent }
    public struct RunCheckpointPhysicalPose { public Vector3 Position, Velocity; public Quaternion Rotation; }
    public delegate RunCheckpointFlightPoseStatus RunCheckpointFlightPoseReader(ulong actor, string inventory,
        string flight, string content, out RunCheckpointPhysicalPose pose);

    /// <summary>全部源在一次Authority提交边界同步取到；该值不接收Client上传，也没有库存写入口。</summary>
    public sealed class RunCheckpointCaptureInput
    {
        public Shared.InstanceLaunchDescriptor Launch;
        public OnlineInventorySession EntryInventory;
        public string CheckpointId;
        public ulong Sequence, LastAcknowledgedRetirement, RetirementSequence;
        public uint ServerTick;
        public OnlineOwnedCheckpointSnapshot[] Owners;
        // 只有真实个人Receipt提交后才由Instance只读口给出；保留事实，不恢复Owner/成员资格。
        // 无退出时明确空；一旦Entry标记Exited，缺少该人的记录必须拒绝，不能把未知损耗补成0。
        public OnlineExitedMemberLossSnapshot[] ExitedLosses = Array.Empty<OnlineExitedMemberLossSnapshot>();
        public uint[] ConnectionEpochs;
        public OnlineGroundInventorySnapshot Ground;
        // 正式Authority提供全表；Ground表现快照只有活动Coin，不能代替已拾墓碑。
        public OnlineCoinCheckpointRecord[] Coins;
        public Shared.OnlineRunAssetCheckpointWorld World;
        public OnlineItemRetirementSnapshot[] Retirements;
        public RunCheckpointFlightPoseReader ReadPhysicalPose;
        // 只由Instance核实所有个人最终收据后的终态入口设置；普通10Hz保存永不启用。
        public bool TerminalCapture;
    }
    public sealed class CapturedRunCheckpoint
    {
        public Shared.OnlineRunAssetCheckpoint Value;
        public ulong RetirementSequence;
        public bool TerminalCapture;
    }
    /// <summary>只做一次完整事实聚合；任何缺口返回失败，不发布半份候选或改写源资产。</summary>
    public static class RunAssetCheckpointCapture
    {
        public static bool TryBuild(RunCheckpointCaptureInput input, out CapturedRunCheckpoint captured, out string error)
        {
            captured = null; error = "CheckpointCaptureInvalidSource";
            if (input == null || input.Launch == null || input.EntryInventory == null || input.Owners == null
                || input.Owners.Length != 4 || input.ConnectionEpochs == null || input.ConnectionEpochs.Length != 4
                || input.ExitedLosses == null || input.ExitedLosses.Length > Shared.OnlineRunAssetCheckpoint.MaximumMembers
                || input.World == null || input.World.CanRestoreAuthorityRules || input.World.Chunks == null
                || string.IsNullOrWhiteSpace(input.World.WorldId) || input.Ground == null || input.Ground.Items == null
                || input.Ground.Items.Length > Shared.OnlineRunAssetCheckpoint.MaximumGroundItems
                || input.Coins == null || input.Coins.Length > Shared.OnlineRunAssetCheckpoint.MaximumCoins
                || input.Retirements == null || input.Retirements.Length > Shared.OnlineRunAssetCheckpoint.MaximumRetiredItems
                || input.LastAcknowledgedRetirement > input.RetirementSequence || input.Sequence == 0
                || !Id(input.CheckpointId) || !Id(input.Launch.RunId) || !Id(input.Launch.InstanceId)
                || input.Launch.RoomEpoch == 0) return false;

            // 入局身份仍保留作审计，但已取得个人最终收据的成员不能再次进入Run快照。
            // 尤其不能因为其工作区已经退役，就回灌旧Entry把物资重新变为局内可用。
            if (input.TerminalCapture ? input.EntryInventory.ActiveMemberCount != 0
                : input.EntryInventory.ActiveMemberCount == 0) return false;
            var members = new List<Shared.OnlineRunAssetCheckpointMember>(input.EntryInventory.ActiveMemberCount);
            var flights = new List<Shared.OnlineRunAssetCheckpointFlight>();
            var outputs = new List<Shared.OnlineRunAssetCheckpointMaterialOutput>();
            var losses = new List<Shared.OnlineRunAssetCheckpointLostMaterial>();
            int consumedExitedLosses = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int m = 0; m < input.EntryInventory.MemberCount; m++)
            {
                if (!input.EntryInventory.TryGetFrozenMember(m, out var entry) || entry.ActorId < 1 || entry.ActorId > 4
                    || entry.Profile == null || !Id(entry.Profile.PlayerId)) return false;
                if (input.EntryInventory.IsMemberExited(entry.ActorId))
                {
                    int found = -1;
                    for (int i = 0; i < input.ExitedLosses.Length; i++)
                    {
                        var cached = input.ExitedLosses[i];
                        if (cached == null) return false;
                        if (cached.ActorId != entry.ActorId) continue;
                        if (found >= 0) return false;
                        found = i;
                    }
                    if (found < 0) return false;
                    var final = input.ExitedLosses[found];
                    if (final.RunId != input.Launch.RunId || final.PlayerId != entry.Profile.PlayerId
                        || !ValidExitedLossAmounts(final.ContainerLost) || !ValidExitedLossAmounts(final.SpellMaterialLost)
                        || !ValidExitedLossAmounts(final.FinalDeathLost)
                        || !Losses(final.PlayerId, final.ActorId, final.ContainerLost,
                            Shared.OnlineRunAssetCheckpointLostMaterial.ContainerTransitSource, losses)
                        || !Losses(final.PlayerId, final.ActorId, final.SpellMaterialLost,
                            Shared.OnlineRunAssetCheckpointLostMaterial.SpellOutputSource, losses)
                        || !Losses(final.PlayerId, final.ActorId, final.FinalDeathLost,
                            Shared.OnlineRunAssetCheckpointLostMaterial.FinalDeathSource, losses)) return false;
                    consumedExitedLosses |= 1 << found;
                    // 不读取已退役Owned/旧Entry资产，不再生成Member；只延续已发生的累计损耗事实。
                    continue;
                }
                int index = (int)entry.ActorId - 1;
                uint epoch = input.ConnectionEpochs[index];
                var current = input.Owners[index];
                var member = new Shared.OnlineRunAssetCheckpointMember { PlayerId = entry.Profile.PlayerId,
                    ActorId = entry.ActorId, ConnectionEpoch = epoch, NeverAdmitted = epoch == 0,
                    Wands = Array.Empty<Shared.OnlineRunAssetCheckpointWandResource>(),
                    SpellUses = Array.Empty<Shared.OnlineRunAssetCheckpointSpellUse>() };
                if (epoch == 0)
                {
                    if (current != null || entry.Profile.CombatAssets == null) return false;
                    // 只有“从未创建工作区”可复制冻结Entry。曾入场但当前工作区缺失必须拒绝，而非补满资源。
                    member.Assets = OnlineOwnedAssetMapping.ToShared(OnlineOwnedAssetMapping.ToCore(entry.Profile.CombatAssets));
                    if (member.Assets == null || member.Assets.Items == null) return false;
                    foreach (var item in member.Assets.Items) if (!Add(seen, item)) return false;
                }
                else
                {
                    if (current == null || current.Assets == null || current.Assets.Items == null
                        || current.Assets.Items.Length > OnlineOwnedAssetSnapshot.MaximumItems
                        || current.Assets.PlayerId != member.PlayerId || current.Assets.ActorId != member.ActorId
                        || current.Assets.RunId != input.Launch.RunId || current.Assets.InventoryId != input.Launch.RunId + "/" + member.PlayerId
                        || current.Assets.GameplayConfigHash != input.Launch.GameplayConfigHash
                        || current.ContainerTransits == null || current.ContainerTransits.Length > OnlineOwnedCheckpointSnapshot.MaximumContainerTransits
                        || current.MaterialOutputs == null || current.MaterialOutputs.Length > OnlineOwnedCheckpointSnapshot.MaximumMaterialOutputs
                        || current.ContainerLost == null || current.SpellMaterialLost == null || current.Wands == null || current.SpellUses == null)
                        return false;
                    member.Assets = OnlineOwnedAssetMapping.ToShared(current.Assets);
                    var carried = new List<Shared.OnlineOwnedItemSnapshot>(member.Assets.Items.Length);
                    var transitItems = new Dictionary<string, Shared.OnlineOwnedItemSnapshot>(StringComparer.Ordinal);
                    foreach (var item in member.Assets.Items)
                    {
                        if (item == null) return false;
                        if (item.LocationKind == 6)
                        { if (!Id(item.InstanceId) || !transitItems.TryAdd(item.InstanceId, item)) return false; }
                        else { if (!Add(seen, item)) return false; carried.Add(item); }
                    }
                    member.Assets.Items = carried.ToArray();
                    if (transitItems.Count != current.ContainerTransits.Length) return false;
                    foreach (var transit in current.ContainerTransits)
                    {
                        if (transit == null || transit.Item == null || transit.InventoryId != current.Assets.InventoryId
                            || transit.WorldId != input.World.WorldId || !Id(transit.FlightId)
                            || !transitItems.TryGetValue(transit.ItemId, out var item)
                            || transit.Item.InstanceId != item.InstanceId || transit.Item.ContentId != item.ContentId
                            || transit.Item.Revision != item.Revision || !Add(seen, item)) return false;
                        var status = RunCheckpointFlightPoseStatus.Missing; RunCheckpointPhysicalPose pose = default;
                        if (input.ReadPhysicalPose != null) status = input.ReadPhysicalPose(member.ActorId,
                            current.Assets.InventoryId, transit.FlightId, item.ContentId, out pose);
                        if (status != RunCheckpointFlightPoseStatus.Missing && status != RunCheckpointFlightPoseStatus.Available
                            || status == RunCheckpointFlightPoseStatus.Missing && !transit.Impacted
                            || status == RunCheckpointFlightPoseStatus.Available && (!Finite(pose.Position) || !Finite(pose.Velocity) || !Finite(pose.Rotation)))
                            return false;
                        bool physical = status == RunCheckpointFlightPoseStatus.Available;
                        if (!physical && !Finite(transit.ImpactPosition)) return false;
                        // V2在独立Flight分区显式保留原Location.OwnerId，不把它当携带父物品或新归属。
                        item.ParentInstanceId = transit.FlightId;
                        flights.Add(new Shared.OnlineRunAssetCheckpointFlight { PlayerId = member.PlayerId, ActorId = member.ActorId,
                            FlightId = transit.FlightId, WorldId = transit.WorldId, SourceId = transit.SourceId, Item = item,
                            Impacted = transit.Impacted, Radius = transit.Radius, Deposited = transit.Deposited, HasPhysicalProxy = physical,
                            Position = Vector(physical ? pose.Position : transit.ImpactPosition),
                            Rotation = physical ? Rotation(pose.Rotation) : null, Velocity = physical ? Vector(pose.Velocity) : null });
                    }
                    member.ResourceVersion = current.ResourceVersion; member.SelectionVersion = current.SelectionVersion;
                    member.SpellUseVersion = current.SpellUseVersion; member.SelectedToolSlotPlusOne = current.SelectedToolSlotPlusOne;
                    member.Wands = new Shared.OnlineRunAssetCheckpointWandResource[current.Wands.Length];
                    for (int i = 0; i < current.Wands.Length; i++)
                    {
                        var wand = current.Wands[i]; if (wand == null) return false;
                        member.Wands[i] = new Shared.OnlineRunAssetCheckpointWandResource { InstanceId = wand.InstanceId,
                            ProgramRevision = wand.ProgramRevision, CurrentMana = wand.CurrentMana, CooldownRemaining = wand.CooldownRemaining };
                    }
                    member.SpellUses = new Shared.OnlineRunAssetCheckpointSpellUse[current.SpellUses.Length];
                    for (int i = 0; i < current.SpellUses.Length; i++)
                    {
                        var use = current.SpellUses[i]; if (use == null) return false;
                        member.SpellUses[i] = new Shared.OnlineRunAssetCheckpointSpellUse { InstanceId = use.InstanceId, Eligible = use.Eligible };
                    }
                    foreach (var output in current.MaterialOutputs)
                    {
                        if (output == null || !Id(output.OutputId) || output.WorldId != input.World.WorldId || output.Components == null) return false;
                        outputs.Add(new Shared.OnlineRunAssetCheckpointMaterialOutput { PlayerId = member.PlayerId, ActorId = member.ActorId,
                            OutputId = output.OutputId, WorldId = output.WorldId, Remaining = OnlineOwnedAssetMapping.ToSharedAmounts(output.Components) });
                    }
                    if (!Losses(member, current.ContainerLost, Shared.OnlineRunAssetCheckpointLostMaterial.ContainerTransitSource, losses)
                        || !Losses(member, current.SpellMaterialLost, Shared.OnlineRunAssetCheckpointLostMaterial.SpellOutputSource, losses)) return false;
                }
                if (member.Assets == null || member.Assets.RunId != input.Launch.RunId || member.Assets.ActorId != member.ActorId
                    || member.Assets.PlayerId != member.PlayerId || member.Assets.GameplayConfigHash != input.Launch.GameplayConfigHash)
                    return false;
                members.Add(member);
            }
            // 多余的活动Owner/外来身份缓存也拒绝，避免把无Receipt的损耗伪装成退出历史。
            if (consumedExitedLosses != (1 << input.ExitedLosses.Length) - 1) return false;
            var coins = new Shared.OnlineRunAssetCheckpointCoin[input.Coins.Length];
            for (int i = 0; i < coins.Length; i++)
            {
                var coin = input.Coins[i];
                coins[i] = new Shared.OnlineRunAssetCheckpointCoin { Id = coin.Id, Amount = coin.Amount,
                    Position = Vector(coin.Position), Collected = coin.Collected, CollectorActorId = coin.CollectorActorId };
            }
            if (!Shared.OnlineRunAssetCheckpoint.HasValidCoinFacts(coins) || !SameActiveCoins(coins, input.Ground.Coins)) return false;
            var ground = new Shared.OnlineRunAssetCheckpointGround[input.Ground.Items.Length];
            for (int i = 0; i < ground.Length; i++)
            {
                var item = input.Ground.Items[i];
                if (item == null || item.Item == null || item.Children == null || !Finite(item.Position) || !Finite(item.Rotation)
                    || !Add(seen, item.Item)) return false;
                var children = new Shared.OnlineOwnedItemSnapshot[item.Children.Length];
                for (int j = 0; j < children.Length; j++)
                { if (!Add(seen, item.Children[j])) return false; children[j] = OnlineOwnedAssetMapping.ToSharedItem(item.Children[j]); }
                ground[i] = new Shared.OnlineRunAssetCheckpointGround { WorldOwnerId = item.WorldOwnerId,
                    Item = OnlineOwnedAssetMapping.ToSharedItem(item.Item), Children = children,
                    Position = Vector(item.Position), Rotation = Rotation(item.Rotation) };
            }
            var retired = new List<Shared.OnlineRunAssetCheckpointRetired>();
            foreach (var item in input.Retirements)
            {
                if (item == null || item.Sequence == 0 || item.Sequence > input.RetirementSequence || !Id(item.InstanceId)) return false;
                if (item.Sequence <= input.LastAcknowledgedRetirement) continue;
                if (!seen.Add(item.InstanceId) || seen.Count > Shared.OnlineRunAssetCheckpoint.MaximumActiveItems) return false;
                retired.Add(new Shared.OnlineRunAssetCheckpointRetired { InstanceId = item.InstanceId, ContentId = item.ContentId,
                    Revision = item.Revision, Reason = (Shared.OnlineRunAssetRetiredReason)item.Reason });
            }
            captured = new CapturedRunCheckpoint { RetirementSequence = input.RetirementSequence, TerminalCapture = input.TerminalCapture,
                Value = new Shared.OnlineRunAssetCheckpoint { SchemaVersion = Shared.OnlineRunAssetCheckpoint.CompleteSchemaVersion,
                    InstanceId = input.Launch.InstanceId, RunId = input.Launch.RunId, RoomEpoch = input.Launch.RoomEpoch,
                    CheckpointId = input.CheckpointId, Sequence = input.Sequence, ServerTick = input.ServerTick,
                    GameplayConfigHash = input.Launch.GameplayConfigHash, Members = members.ToArray(), GroundItems = ground,
                    Flights = flights.ToArray(), MaterialOutputs = outputs.ToArray(), LostMaterials = losses.ToArray(),
                    RetiredItems = retired.ToArray(), World = input.World, Coins = coins } };
            error = null; return true;
        }
        private static bool SameActiveCoins(Shared.OnlineRunAssetCheckpointCoin[] coins, OnlineCoinSnapshot[] active)
        {
            if (active == null || active.Length > Shared.OnlineRunAssetCheckpoint.MaximumCoins) return false;
            int expected = 0; ushort seen = 0;
            foreach (var coin in coins)
            {
                if (coin.Collected) continue;
                expected++; bool found = false;
                for (int i = 0; i < active.Length; i++) if (active[i].Id == coin.Id)
                {
                    if (found || active[i].Amount != coin.Amount || !active[i].Position.Equals(new Vector3(coin.Position.X, coin.Position.Y, coin.Position.Z))) return false;
                    found = true;
                }
                if (!found) return false;
            }
            if (expected != active.Length) return false;
            foreach (var coin in active)
            {
                if (coin.Id < 5 || coin.Id > 16) return false;
                int bit = 1 << (int)(coin.Id - 5); if ((seen & bit) != 0) return false; seen |= (ushort)bit;
            }
            return true;
        }
        private static bool Add(HashSet<string> ids, OnlineOwnedItemSnapshot item)
            => item != null && Id(item.InstanceId) && ids.Add(item.InstanceId) && ids.Count <= Shared.OnlineRunAssetCheckpoint.MaximumActiveItems;
        private static bool Add(HashSet<string> ids, Shared.OnlineOwnedItemSnapshot item)
            => item != null && Id(item.InstanceId) && ids.Add(item.InstanceId) && ids.Count <= Shared.OnlineRunAssetCheckpoint.MaximumActiveItems;
        private static bool Id(string value) => value != null && Guid.TryParseExact(value, "N", out _);
        private static Shared.OnlineRunAssetCheckpointVector3 Vector(Vector3 value)
            => new Shared.OnlineRunAssetCheckpointVector3 { X = value.x, Y = value.y, Z = value.z };
        private static Shared.OnlineRunAssetCheckpointQuaternion Rotation(Quaternion value)
            => new Shared.OnlineRunAssetCheckpointQuaternion { X = value.x, Y = value.y, Z = value.z, W = value.w };
        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        private static bool Finite(Quaternion value) => float.IsFinite(value.x) && float.IsFinite(value.y)
            && float.IsFinite(value.z) && float.IsFinite(value.w)
            && Quaternion.Dot(value, value) > .99f && Quaternion.Dot(value, value) < 1.01f;
        private static bool Losses(Shared.OnlineRunAssetCheckpointMember member, OnlineOwnedMaterialAmountSnapshot[] source,
            string kind, List<Shared.OnlineRunAssetCheckpointLostMaterial> result)
            => Losses(member.PlayerId, member.ActorId, source, kind, result);
        private static bool ValidExitedLossAmounts(OnlineOwnedMaterialAmountSnapshot[] source)
        {
            if (source == null || source.Length > OnlineExitedMemberLossSnapshot.MaximumMaterialsPerSource) return false;
            ushort seen = 0;
            foreach (var amount in source)
            {
                if (amount == null || amount.MaterialKey < 1 || amount.MaterialKey > 8) return false;
                int bit = 1 << amount.MaterialKey;
                if ((seen & bit) != 0) return false;
                seen |= (ushort)bit;
            }
            return true;
        }
        private static bool Losses(string playerId, ulong actorId, OnlineOwnedMaterialAmountSnapshot[] source,
            string kind, List<Shared.OnlineRunAssetCheckpointLostMaterial> result)
        {
            if (source.Length > 8) return false;
            for (int i = 0; i < source.Length; i++)
            {
                var amount = source[i]; if (amount == null || amount.MaterialKey < 1 || amount.MaterialKey > 8) return false;
                if (amount.Quantity != 0) result.Add(new Shared.OnlineRunAssetCheckpointLostMaterial { PlayerId = playerId,
                    ActorId = actorId, Source = kind, MaterialKey = amount.MaterialKey, Quantity = amount.Quantity });
            }
            return true;
        }
    }
}
