using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    /// <summary>用真实EventBus和Collider检查选择边界；不启动副本或读写玩家存档。</summary>
    public sealed class DungeonEntryChoiceGateTests
    {
        private GameObject _root, _other;
        private Collider _actor;
        private DungeonEntryChoiceGate _gate;
        private bool _eligible, _paused;
        private int _requests, _enters, _matches;
        [SetUp] public void SetUp()
        {
            _root = new GameObject("Choice actor"); _actor = _root.AddComponent<BoxCollider>();
            _eligible = true; _paused = false; _requests = _enters = _matches = 0;
            _gate = new DungeonEntryChoiceGate(17, "configured-p7", "P7", c => _eligible && c == _actor,
                c => { _enters++; return true; }, () => { _paused = true; return true; }, () => _paused = false);
            _gate.Enable();
            EventBus<DungeonMatchRequestedEvent>.Subscribe(OnMatch);
        }
        private void OnRequest(DungeonEntryChoiceRequestedEvent e) => _requests++;
        private void OnMatch(DungeonMatchRequestedEvent e) => _matches++;
        private void Submit(DungeonEntryMode mode, int id = 17, Collider actor = null) => EventBus<DungeonEntryChoiceSubmittedEvent>.Publish(
            new DungeonEntryChoiceSubmittedEvent { PortalId = id, Actor = actor != null ? actor : _actor, Mode = mode });
        [Test] public void MissingPresentation_DoesNotPauseOrEnter()
        { Assert.That(_gate.Request(_actor), Is.False); Assert.That(_paused, Is.False); Assert.That(_enters, Is.Zero); }
        [Test] public void RepeatedInteraction_OpensExactlyOneChoice()
        {
            EventBus<DungeonEntryChoiceRequestedEvent>.Subscribe(OnRequest);
            Assert.That(_gate.Request(_actor), Is.True); Assert.That(_gate.Request(_actor), Is.False);
            Assert.That(_gate.Pending && _paused, Is.True); Assert.That(_requests, Is.EqualTo(1)); Assert.That(_enters, Is.Zero);
        }
        [Test] public void ForeignActorAndPortal_CannotConfirm()
        {
            EventBus<DungeonEntryChoiceRequestedEvent>.Subscribe(OnRequest); _gate.Request(_actor);
            _other = new GameObject("Other actor"); var other = _other.AddComponent<BoxCollider>();
            Submit(DungeonEntryMode.SinglePlayer, 18); Submit(DungeonEntryMode.SinglePlayer, 17, other);
            Assert.That(_gate.Pending, Is.True); Assert.That(_enters, Is.Zero);
        }
        [Test] public void Cancel_ReleasesOnlyChoiceAndAllowsRetry()
        {
            EventBus<DungeonEntryChoiceRequestedEvent>.Subscribe(OnRequest); _gate.Request(_actor); Submit(DungeonEntryMode.Cancel);
            Assert.That(_gate.Pending || _paused, Is.False); Assert.That(_enters, Is.Zero); Assert.That(_gate.Request(_actor), Is.True);
        }
        [Test] public void Match_DoesNotInvokeSinglePlayerOrConsumeChoice()
        {
            EventBus<DungeonEntryChoiceRequestedEvent>.Subscribe(OnRequest); _gate.Request(_actor); Submit(DungeonEntryMode.Matchmaking);
            Assert.That(_matches, Is.EqualTo(1)); Assert.That(_enters, Is.Zero); Assert.That(_gate.Pending && _paused, Is.True);
        }
        [Test] public void SinglePlayer_RevalidatesAndEntersOnceAfterUnpause()
        {
            EventBus<DungeonEntryChoiceRequestedEvent>.Subscribe(OnRequest); _gate.Request(_actor); Submit(DungeonEntryMode.SinglePlayer);
            Submit(DungeonEntryMode.SinglePlayer); Assert.That(_enters, Is.EqualTo(1)); Assert.That(_paused, Is.False);
        }
        [Test] public void MatchPending_CannotSwitchToSinglePlayerOrRepeatMatch()
        {
            EventBus<DungeonEntryChoiceRequestedEvent>.Subscribe(OnRequest); _gate.Request(_actor);
            Submit(DungeonEntryMode.Matchmaking); Submit(DungeonEntryMode.SinglePlayer); Submit(DungeonEntryMode.Matchmaking);
            Assert.That(_enters, Is.Zero); Assert.That(_matches, Is.EqualTo(1)); Assert.That(_gate.Pending && _paused, Is.True);
        }
        [Test] public void MatchRelease_ReopensChoice_WhileForeignEntryCannotUnpause()
        {
            EventBus<DungeonEntryChoiceRequestedEvent>.Subscribe(OnRequest); _gate.Request(_actor); Submit(DungeonEntryMode.Matchmaking);
            EventBus<DungeonOnlineEntryConfirmedEvent>.Publish(new DungeonOnlineEntryConfirmedEvent { PortalId = 99 });
            Assert.That(_paused, Is.True);
            EventBus<DungeonMatchReleasedEvent>.Publish(new DungeonMatchReleasedEvent { PortalId = 17, CloseChoice = false });
            Submit(DungeonEntryMode.SinglePlayer); Assert.That(_enters, Is.EqualTo(1)); Assert.That(_paused, Is.False);
        }
        [Test] public void AuthenticatedEntry_ReleasesChoiceWithoutCallingLocalRun()
        {
            EventBus<DungeonEntryChoiceRequestedEvent>.Subscribe(OnRequest); _gate.Request(_actor); Submit(DungeonEntryMode.Matchmaking);
            EventBus<DungeonOnlineEntryConfirmedEvent>.Publish(new DungeonOnlineEntryConfirmedEvent { PortalId = 17 });
            Assert.That(_paused || _gate.Pending, Is.False); Assert.That(_enters, Is.Zero);
        }
        [Test] public void LostEligibilityAndDisabledPortal_NeverEnter()
        {
            EventBus<DungeonEntryChoiceRequestedEvent>.Subscribe(OnRequest); _gate.Request(_actor); _eligible = false;
            Submit(DungeonEntryMode.SinglePlayer); Assert.That(_enters, Is.Zero); Assert.That(_paused, Is.False);
            _eligible = true; _gate.Request(_actor); _gate.Disable(); Submit(DungeonEntryMode.SinglePlayer);
            Assert.That(_enters, Is.Zero); Assert.That(_paused || _gate.Pending, Is.False);
        }
        [TearDown] public void TearDown()
        {
            _gate.Disable(); EventBus<DungeonEntryChoiceRequestedEvent>.Unsubscribe(OnRequest);
            EventBus<DungeonMatchRequestedEvent>.Unsubscribe(OnMatch);
            Object.DestroyImmediate(_root); if (_other != null) Object.DestroyImmediate(_other);
        }
    }
}
