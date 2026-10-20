using NUnit.Framework;

namespace Game.Character.Tests
{
    public sealed class OwnedWandFireSchedulerTests
    {
        [Test] public void ReadyRequestCanFireOncePerFrameWithoutCatchupBurst()
        {
            var scheduler = new OwnedWandFireScheduler(); Assert.That(scheduler.CanAttempt(1, 0, true, true, 0, true), Is.True);
            scheduler.RecordAttempt(1, 0, true); Assert.That(scheduler.CanAttempt(1, 5, true, true, 0, true), Is.False);
            Assert.That(scheduler.CanAttempt(2, 5, true, true, 0, true), Is.True);
        }
        [Test] public void IdlePauseEmptyAndUnreadyNeverBuildCandidates()
        {
            var scheduler = new OwnedWandFireScheduler();
            Assert.That(scheduler.CanAttempt(1, 0, false, true, 0, true), Is.False);
            Assert.That(scheduler.CanAttempt(1, 0, true, false, 0, true), Is.False);
            Assert.That(scheduler.CanAttempt(1, 0, true, true, 0, false), Is.False);
            Assert.That(scheduler.CanAttempt(1, 0, true, true, .1f, true), Is.False);
        }
        [Test] public void RejectionHasBoundedRetryAndResetReleasesInputState()
        {
            var scheduler = new OwnedWandFireScheduler(); scheduler.RecordAttempt(1, 0, false);
            Assert.That(scheduler.CanAttempt(2, .01, true, true, 0, true), Is.False);
            Assert.That(scheduler.CanAttempt(3, .2, true, true, 0, true), Is.True);
            scheduler.Reset(); Assert.That(scheduler.CanAttempt(1, 0, true, true, 0, true), Is.True);
        }
    }
}
