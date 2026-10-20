using Game.Core;
using NUnit.Framework;

namespace Game.Run.Tests
{
    public sealed class WandV2LifecycleTests : WandExecutionTransactionTestFixture
    {
        [Test] public void SceneTransferContinuityPreservesDrawManaWaitAndHistory()
        {
            Build(extra: 1); var root = Request(CastResourceOperation.PrepareRoot); Request(CastResourceOperation.Complete, root.TicketId);
            var before = State(); var archive = _casts.Archive;
            var capture = typeof(WandLoadoutService).GetMethod("CaptureContinuity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(capture, Is.Not.Null, "Scene加载新Session时不能只靠资产DTO恢复满Mana");
            object continuity = capture.Invoke(_wands, null); _casts.CancelAll();
            Assert.That(ProfileSaveMapper.TryRestore(ProfileSaveMapper.ToDto(_profile, 1), _store.Catalog, out var restored).Success, Is.True);
            _profile = restored; _store = restored.Items; _wands = new WandLoadoutService(restored, "scope");
            var restore = typeof(WandLoadoutService).GetMethod("RestoreContinuity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(restore, Is.Not.Null); restore.Invoke(_wands, new[] { continuity });
            _raid = new RaidSpellEligibility(restored); _casts = new WandExecutionTransactionService(restored, _wands, _raid, "scope", archive);
            Assert.That(State().Draw, Is.SameAs(before.Draw)); Assert.That(State().Mana, Is.EqualTo(before.Mana));
            Assert.That(State().CastRemaining, Is.EqualTo(before.CastRemaining)); Assert.That(_casts.Archive.Count, Is.EqualTo(1));
        }
        [Test] public void SwitchingAndMovingSameInstancePreserveResourcesAndDraw()
        {
            Build(extra: 1); var root = Request(CastResourceOperation.PrepareRoot); Request(CastResourceOperation.Complete, root.TicketId);
            var before = State();
            Assert.That(_store.TryAdd(new RuntimeItemState("other", "wand", 0), new ItemLocation(ItemLocationKind.EquippedWand, "player", 0, 0, 0, 1)), Is.True);
            Assert.That(_wands.TrySelect(1), Is.True); Assert.That(_wands.TrySelect(0), Is.True);
            Assert.That(State().Draw, Is.SameAs(before.Draw)); Assert.That(State().Mana, Is.EqualTo(before.Mana));
            Assert.That(_store.TryMove("wand", ItemLocation.Warehouse).Success, Is.True); State(); _wands.Tick(100);
            Assert.That(_store.TryMove("wand", new ItemLocation(ItemLocationKind.EquippedWand, "player", 0, 0, 0, 0)).Success, Is.True);
            Assert.That(State().Draw, Is.SameAs(before.Draw)); Assert.That(State().Mana, Is.EqualTo(before.Mana));
        }
        [Test] public void ManualEditRebuildsDeckWithWaitAndDoesNotRefillManaOrReseed()
        {
            Build(extra: 1); var root = Request(CastResourceOperation.PrepareRoot); Request(CastResourceOperation.Complete, root.TicketId);
            var before = State(); Assert.That(_store.TryMove("x0", ItemLocation.Warehouse).Success, Is.True); var edited = State();
            Assert.That(edited.Program.Revision, Is.GreaterThan(before.Program.Revision)); Assert.That(edited.Mana, Is.EqualTo(before.Mana));
            Assert.That(edited.ReloadRemaining, Is.GreaterThanOrEqualTo(edited.Program.BaseReloadTime)); Assert.That(edited.Draw.RandomState, Is.EqualTo(before.Draw.RandomState));
        }
        [Test] public void ReloadDeltaAccumulatesAcrossRootsAndClearsOnlyAtReloadCommit()
        {
            Build(trigger: false, extra: 1); _store.Catalog.TryGet("emit", out var emit); _store.Catalog.TryGet("payload", out var payload);
            emit.SpellDefinition.ReloadDelta = -.1f; payload.SpellDefinition.ReloadDelta = -.2f;
            var a = Request(CastResourceOperation.PrepareRoot); Request(CastResourceOperation.Complete, a.TicketId);
            Assert.That(State().Draw.CycleReloadDelta, Is.EqualTo(-.1f).Within(.0001)); _wands.Tick(1);
            var b = Request(CastResourceOperation.PrepareRoot); Assert.That(State().Draw.CycleReloadDelta, Is.EqualTo(-.1f).Within(.0001));
            Request(CastResourceOperation.Complete, b.TicketId); Assert.That(State().Draw.CycleReloadDelta, Is.Zero);
            Assert.That(State().ReloadRemaining, Is.EqualTo(.5f).Within(.0001));
        }
    }
}
