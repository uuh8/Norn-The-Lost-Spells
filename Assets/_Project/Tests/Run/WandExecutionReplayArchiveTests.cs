using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class WandExecutionReplayArchiveTests : WandExecutionTransactionTestFixture
    {
        [Test] public void DifferentWandsShareCapacityButLatestLookupRemainsPerInstance()
        {
            Build(root: 20, child: 20);
            Assert.That(_store.TryAdd(new RuntimeItemState("wand.B", "wand", 0), new ItemLocation(ItemLocationKind.EquippedWand, "player", 0, 0, 0, 1)), Is.True);
            Assert.That(_store.TryAdd(new RuntimeItemState("card.B", "payload", 20), new ItemLocation(ItemLocationKind.WandSlot, "wand.B", 0, 0, 0, 0)), Is.True);
            string lastA = null, lastB = null;
            for (int i = 0; i < 4; i++)
            {
                string id = i % 2 == 0 ? "wand" : "wand.B";
                Assert.That(_casts.TryGetState(id, out var before), Is.True);
                var request = new CastResourceRequestedEvent { InventoryId = "scope", SourceWandId = id, CallerId = "multi",
                    RequestId = ++_sequence, ProgramRevision = (uint)before.Program.Revision, ExecutionRevision = before.Draw.ExecutionRevision,
                    ResourceVersion = before.ResourceVersion, InventoryVersion = _store.Version, Operation = CastResourceOperation.PrepareRoot };
                var root = _casts.Execute(request); Assert.That(root.Success, Is.True);
                request.Operation = CastResourceOperation.Complete; request.TicketId = root.TicketId; request.RequestId = ++_sequence;
                Assert.That(_casts.Execute(request).Success, Is.True);
                if (id == "wand") lastA = root.TicketId; else lastB = root.TicketId;
                _wands.Tick(1);
            }
            Assert.That(_casts.Archive.Count, Is.EqualTo(3));
            Assert.That(_casts.Archive.TryGetLatest("wand", out var a), Is.True); Assert.That(a.TicketId, Is.EqualTo(lastA));
            Assert.That(_casts.Archive.TryGetLatest("wand.B", out var b), Is.True); Assert.That(b.TicketId, Is.EqualTo(lastB));
        }
        [Test] public void SessionKeepsArchiveWhenAdoptingCommittedProfile()
        {
            Build(); var root = Request(CastResourceOperation.PrepareRoot); Request(CastResourceOperation.Complete, root.TicketId);
            _casts.CancelAll(); _casts.Archive.TryGetLatest("wand", out var replay);
            var property = typeof(SinglePlayerInventorySession).GetProperty("ExecutionReplays");
            Assert.That(property, Is.Not.Null, "归档属于Session，不能只属于会被AdoptCommitted替换的服务");
            _profile.Items.CommitPolicy = null;
            var session = Track(new GameObject("V2 archive lifecycle test")).AddComponent<SinglePlayerInventorySession>();
            var repository = new ProfileSaveRepository("Temp/Spells/ArchiveTests/" + System.Guid.NewGuid().ToString("N"), _store.Catalog);
            Assert.That(session.Initialize(_profile, repository), Is.True);
            var archive = (WandExecutionReplayArchive)property.GetValue(session); archive.Add(replay);
            var restored = ProfileSaveMapper.TryRestore(ProfileSaveMapper.ToDto(_profile, 1), _store.Catalog, out var candidate);
            Assert.That(restored.Status, Is.EqualTo(ProfilePersistenceStatus.Success));
            session.AdoptCommitted(candidate);
            Assert.That(property.GetValue(session), Is.SameAs(archive));
            Assert.That(archive.TryGetLatest("wand", out var retained), Is.True); Assert.That(retained, Is.SameAs(replay));
        }
        [Test] public void SharedThreeRootLimitDoesNotCancelEvictedGameplayTicket()
        {
            Build(root: 20, child: 20); string first = null, last = null;
            for (int i = 0; i < 4; i++)
            {
                var root = Request(CastResourceOperation.PrepareRoot); Assert.That(root.Success, Is.True);
                if (i == 0) first = root.TicketId;
                last = root.TicketId; Assert.That(Request(CastResourceOperation.Complete, root.TicketId).Success, Is.True); _wands.Tick(10);
            }
            Assert.That(_casts.Archive.Count, Is.EqualTo(3));
            Assert.That(_casts.Archive.GetOldestFirst(0).TicketId, Is.Not.EqualTo(first));
            Assert.That(_casts.Archive.TryGetLatest("wand", out var latest), Is.True); Assert.That(latest.TicketId, Is.EqualTo(last));
            Assert.That(_casts.TryGetGraph(first, out _), Is.True);
            Assert.That(Request(CastResourceOperation.PrepareLayer, first, 1).Success, Is.True);
            Assert.That(Request(CastResourceOperation.Complete, first, 1).Success, Is.True);
        }
        [Test] public void ReplacingResourceServiceKeepsFrozenHistoryAndCancelledLayerStatus()
        {
            Build(root: 1, child: 1); var root = Request(CastResourceOperation.PrepareRoot); Request(CastResourceOperation.Complete, root.TicketId);
            Assert.That(_casts.Archive.TryGetLatest("wand", out var replay), Is.True);
            Assert.That(replay.GetLayerStatus(1), Is.EqualTo(WandLayerResourceStatus.Unattempted));
            Assert.That(replay.WorldStatus, Is.EqualTo(WandWorldExecutionStatus.NotObserved));
            _casts.CancelAll(); var archive = _casts.Archive;
            _casts = new WandExecutionTransactionService(_profile, _wands, _raid, "scope", archive);
            Assert.That(archive.TryGetLatest("wand", out var retained), Is.True); Assert.That(retained, Is.SameAs(replay));
            Assert.That(retained.GetLayerStatus(1), Is.EqualTo(WandLayerResourceStatus.Cancelled));
            Assert.That(retained.Graph.Outputs[1].SourceInstanceId, Is.EqualTo("b")); Assert.That(_store.TryGet("b", out _), Is.False);
            Assert.That(retained.Inputs.AvailableMana, Is.EqualTo(100)); Assert.That(retained.Summary.ManaAfter, Is.EqualTo(83));
            Assert.That(_casts.ActiveReservations, Is.Zero); Assert.That(State().Mana, Is.EqualTo(83));
        }
        [Test] public void ArchiveIsAbsentFromProfileDtoAndWrongWandHasNoHistory()
        {
            Build(); var root = Request(CastResourceOperation.PrepareRoot); Request(CastResourceOperation.Complete, root.TicketId);
            Assert.That(_casts.Archive.TryGetLatest("other", out _), Is.False);
            string json = JsonUtility.ToJson(ProfileSaveMapper.ToDto(_profile, 1));
            Assert.That(json, Does.Not.Contain(root.TicketId)); Assert.That(json, Does.Not.Contain("ExecutionReplay"));
        }
    }
}
