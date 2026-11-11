using System;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using Shared = Game.NetworkProtocol;

namespace Game.Multiplayer.Tests
{
    public sealed class RunAssetCheckpointCaptureTests
    {
        private const string Run = "cccccccccccccccccccccccccccccccc", A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", WandA = "11111111111111111111111111111111",
            WandB = "22222222222222222222222222222222", Bottle = "33333333333333333333333333333333",
            Flight = "44444444444444444444444444444444", Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private static Shared.OnlineOwnedAssetSnapshot Assets(string player, ulong actor, string wand)
            => new Shared.OnlineOwnedAssetSnapshot { SchemaVersion = Shared.OnlineOwnedAssetSnapshot.CurrentSchemaVersion,
                RunId = Run, PlayerId = player, ActorId = actor, InventoryId = Run + "/" + player,
                InventoryVersion = 3, Gold = 0, CurrencyVersion = 1, GameplayConfigHash = Hash, SelectedWandInstanceId = wand,
                Items = new[] { new Shared.OnlineOwnedItemSnapshot { InstanceId = wand, ContentId = "wand.test", Revision = 1,
                    LocationKind = 3, Slot = 0, ParentInstanceId = "", UseTemplateWandStats = true } } };
        [Test] public void CurrentGoldAndNeverAdmittedFrozenGoldRemainDistinctFacts()
        {
            var source = Input(); source.Owners[0].Assets.Gold = 27; source.Owners[0].Assets.CurrencyVersion = 3;
            Assert.That(RunAssetCheckpointCapture.TryBuild(source, out var result, out var error), Is.True, error);
            Assert.That(result.Value.Members[0].Assets.Gold, Is.EqualTo(27));
            Assert.That(result.Value.Members[0].Assets.CurrencyVersion, Is.EqualTo(3));
            Assert.That(result.Value.Members[1].NeverAdmitted, Is.True);
            Assert.That(result.Value.Members[1].Assets.Gold, Is.Zero);
            Assert.That(result.Value.Members[1].Assets.CurrencyVersion, Is.EqualTo(1));
        }

        [Test] public void FinalExitedMemberIsExcludedWithoutReadingMissingWorkspaceOrReplayingEntry()
        {
            var source = Input();
            Assert.That(source.EntryInventory.MarkMemberExited(1, A), Is.True);
            source.ExitedLosses = new[] { new OnlineExitedMemberLossSnapshot { RunId = source.Launch.RunId, PlayerId = A, ActorId = 1,
                ContainerLost = Array.Empty<OnlineOwnedMaterialAmountSnapshot>(), SpellMaterialLost = Array.Empty<OnlineOwnedMaterialAmountSnapshot>() } };
            source.Owners[0] = null; // 本人已保存并退役，不能再要求它的旧工作区存在。
            Assert.That(RunAssetCheckpointCapture.TryBuild(source, out var result, out var error), Is.True, error);
            Assert.That(result.Value.Members.Length, Is.EqualTo(1));
            Assert.That(result.Value.Members[0].PlayerId, Is.EqualTo(B));
            Assert.That(result.Value.Members[0].ActorId, Is.EqualTo(2));
        }

        [Test] public void AllMembersFinallyExitedCannotPublishAnEntryOnlyCheckpoint()
        {
            var source = Input();
            Assert.That(source.EntryInventory.MarkMemberExited(1, A), Is.True);
            Assert.That(source.EntryInventory.MarkMemberExited(2, B), Is.True);
            Assert.That(RunAssetCheckpointCapture.TryBuild(source, out var result, out _), Is.False);
            Assert.That(result, Is.Null);
        }

        internal static RunCheckpointCaptureInput Input()
        {
            var entryA = Assets(A, 1, WandA); var entryB = Assets(B, 2, WandB);
            var session = new OnlineInventorySession(new Shared.InstanceReadyReply { Members = new[] {
                new Shared.InstanceEntryMember { ActorId = 1, Profile = new Shared.OnlineProfileReply {
                    PlayerId = A, ContentManifestHash = Hash, Items = Array.Empty<Shared.OnlineItemReply>(), CombatAssets = entryA } },
                new Shared.InstanceEntryMember { ActorId = 2, Profile = new Shared.OnlineProfileReply {
                    PlayerId = B, ContentManifestHash = Hash, Items = Array.Empty<Shared.OnlineItemReply>(), CombatAssets = entryB } } } }, Hash);
            var live = new OnlineOwnedCheckpointSnapshot { Assets = OnlineOwnedAssetMapping.ToCore(entryA), ResourceVersion = 5,
                SelectionVersion = 3, SpellUseVersion = 1,
                Wands = new[] { new OnlineInventoryWandPresentationSnapshot { InstanceId = WandA, ProgramRevision = 7, CurrentMana = 12.25f, CooldownRemaining = .4f } } };
            live.Assets.Items[0].UseTemplateWandStats = false;
            live.Assets.Items[0].WandStats = new OnlineOwnedWandStatsSnapshot { MaxMana = 55.5f, ProgramCapacity = 32, BaseDraws = 1,
                AlwaysCastContentIds = new[] { "spell.b", "spell.a" } };
            return new RunCheckpointCaptureInput { Launch = new Shared.InstanceLaunchDescriptor { InstanceId = new string('d', 32),
                    RunId = Run, RoomEpoch = 7, GameplayConfigHash = Hash }, EntryInventory = session,
                CheckpointId = new string('e', 32), Sequence = 1, ServerTick = 12,
                Owners = new[] { live, null, null, null }, ConnectionEpochs = new uint[] { 2, 0, 0, 0 },
                Ground = new OnlineGroundInventorySnapshot { Version = 1 }, Coins = Array.Empty<OnlineCoinCheckpointRecord>(),
                Retirements = Array.Empty<OnlineItemRetirementSnapshot>(),
                World = new Shared.OnlineRunAssetCheckpointWorld { WorldId = "world", WorldEpoch = 9, WorldTick = 4,
                    Chunks = Array.Empty<Shared.OnlineRunAssetCheckpointChunk>() } };
        }
        private static RunCheckpointFlightPoseStatus Pose(ulong actor, string inventory, string flight, string content, out RunCheckpointPhysicalPose pose)
        {
            Assert.That(actor, Is.EqualTo(1)); Assert.That(inventory, Is.EqualTo(Run + "/" + A)); Assert.That(flight, Is.EqualTo(Flight));
            pose = new RunCheckpointPhysicalPose { Position = new Vector3(3, 4, 5), Velocity = new Vector3(2, 0, 1), Rotation = Quaternion.identity };
            return RunCheckpointFlightPoseStatus.Available;
        }
        private static void AddTransit(RunCheckpointCaptureInput value, bool impacted)
        {
            var item = new OnlineOwnedItemSnapshot { InstanceId = Bottle, ContentId = "container.test", Revision = 4,
                LocationKind = 6, Slot = -1, ParentInstanceId = "", ContainerState = new OnlineOwnedContainerStateSnapshot {
                    SchemaVersion = 1, Components = new[] { new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 2, Quantity = 19 } } } };
            var owner = value.Owners[0]; owner.Assets.Items = new[] { owner.Assets.Items[0], item };
            owner.ContainerTransits = new[] { new OnlineContainerTransitCheckpointSnapshot { InventoryId = owner.Assets.InventoryId,
                FlightId = Flight, WorldId = "world", ItemId = Bottle, Item = item, SourceId = "throw", Radius = .2f,
                Impacted = impacted, ImpactPosition = new Vector3(8, 9, 10) } };
        }
        [Test] public void AdmittedMemberUsesCurrentFactsAndNeverAdmittedMemberKeepsExplicitFrozenEntry()
        {
            var source = Input(); Assert.That(RunAssetCheckpointCapture.TryBuild(source, out var result, out var error), Is.True, error);
            var cp = result.Value; Assert.That(cp.SchemaVersion, Is.EqualTo(Shared.OnlineRunAssetCheckpoint.CompleteSchemaVersion)); Assert.That(cp.ServerTick, Is.EqualTo(12));
            Assert.That(cp.World.WorldTick, Is.EqualTo(4)); Assert.That(cp.Members.Length, Is.EqualTo(2));
            Assert.That(cp.Members[0].ConnectionEpoch, Is.EqualTo(2)); Assert.That(cp.Members[0].NeverAdmitted, Is.False);
            Assert.That(cp.Members[0].Wands[0].CurrentMana, Is.EqualTo(12.25f));
            Assert.That(cp.Members[0].Assets.Items[0].WandStats.AlwaysCastContentIds, Is.EqualTo(new[] { "spell.b", "spell.a" }));
            Assert.That(cp.Members[1].NeverAdmitted, Is.True); Assert.That(cp.Members[1].ConnectionEpoch, Is.Zero);
            Assert.That(cp.Members[1].Assets.SelectedWandInstanceId, Is.EqualTo(WandB)); Assert.That(cp.Members[1].Wands, Is.Empty);
            cp.Members[0].Assets.Items[0].WandStats.AlwaysCastContentIds[0] = "mutated";
            Assert.That(source.Owners[0].Assets.Items[0].WandStats.AlwaysCastContentIds[0], Is.EqualTo("spell.b"));
        }
        [Test] public void APreviouslyAdmittedMissingWorkspaceCannotFallBackToTheEntrySnapshot()
        {
            var source = Input(); source.ConnectionEpochs[1] = 1;
            Assert.That(RunAssetCheckpointCapture.TryBuild(source, out var result, out _), Is.False); Assert.That(result, Is.Null);
        }
        [Test] public void FlightAppearsExactlyOnceAndUsesTheActualMatchingPhysicsPose()
        {
            var source = Input(); AddTransit(source, false); source.ReadPhysicalPose = Pose;
            Assert.That(RunAssetCheckpointCapture.TryBuild(source, out var result, out var error), Is.True, error);
            Assert.That(result.Value.Members[0].Assets.Items.Length, Is.EqualTo(1));
            var flight = result.Value.Flights[0]; Assert.That(flight.Item.InstanceId, Is.EqualTo(Bottle));
            Assert.That(flight.Item.ParentInstanceId, Is.EqualTo(Flight)); Assert.That(flight.Position.X, Is.EqualTo(3));
            Assert.That(flight.Item.ContainerState.Components[0].Quantity, Is.EqualTo(19));
        }
        [Test] public void ImpactedWaitingBudgetDoesNotInventANonexistentRotationOrVelocity()
        {
            var source = Input(); AddTransit(source, true);
            source.ReadPhysicalPose = (ulong actor, string inventory, string flight, string content, out RunCheckpointPhysicalPose pose) =>
                { pose = default; return RunCheckpointFlightPoseStatus.Missing; };
            Assert.That(RunAssetCheckpointCapture.TryBuild(source, out var result, out var error), Is.True, error);
            Assert.That(result.Value.Flights[0].Position.X, Is.EqualTo(8));
            Assert.That(result.Value.Flights[0].Velocity, Is.Null); Assert.That(result.Value.Flights[0].Rotation, Is.Null);
        }
        [Test] public void UnimpactedTransitWithoutItsPhysicsProxyRejectsTheWholeCandidate()
        {
            var source = Input(); AddTransit(source, false);
            Assert.That(RunAssetCheckpointCapture.TryBuild(source, out var result, out _), Is.False); Assert.That(result, Is.Null);
        }
        [Test] public void OnlyUnacknowledgedExplicitRetirementsAndIndependentMaterialOwnersArePublished()
        {
            var source = Input(); source.LastAcknowledgedRetirement = 1; source.RetirementSequence = 2;
            source.Retirements = new[] {
                new OnlineItemRetirementSnapshot { Sequence = 1, InventoryId = Run + "/" + A, InstanceId = new string('6', 32), ContentId = "spell.a", Revision = 2, Reason = OnlineItemRetirementReason.Consumed },
                new OnlineItemRetirementSnapshot { Sequence = 2, InventoryId = Run + "/" + A, InstanceId = new string('7', 32), ContentId = "material.test", Revision = 3, Reason = OnlineItemRetirementReason.Merged } };
            source.Owners[0].MaterialOutputs = new[] { new OnlineSpellMaterialCheckpointSnapshot { OutputId = new string('8', 32),
                WorldId = "world", Components = new[] { new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 2, Quantity = 11 } } } };
            source.Owners[0].SpellMaterialLost = new[] { new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 2, Quantity = 3 } };
            Assert.That(RunAssetCheckpointCapture.TryBuild(source, out var result, out var error), Is.True, error);
            Assert.That(result.Value.RetiredItems.Length, Is.EqualTo(1)); Assert.That(result.Value.RetiredItems[0].InstanceId, Is.EqualTo(new string('7', 32)));
            Assert.That(result.RetirementSequence, Is.EqualTo(2)); Assert.That(result.Value.MaterialOutputs[0].Remaining[0].Quantity, Is.EqualTo(11));
            Assert.That(result.Value.LostMaterials[0].Quantity, Is.EqualTo(3));
        }
        [Test] public void MissingMaterialPartitionCannotBeSilentlyTreatedAsAnEmptyWorld()
        {
            var source = Input(); source.World = null;
            Assert.That(RunAssetCheckpointCapture.TryBuild(source, out var result, out _), Is.False); Assert.That(result, Is.Null);
        }
    }
}
