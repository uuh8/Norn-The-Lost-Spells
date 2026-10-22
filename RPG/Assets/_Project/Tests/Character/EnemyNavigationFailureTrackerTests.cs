using NUnit.Framework;

namespace Game.Character.Tests
{
    public sealed class EnemyNavigationFailureTrackerTests
    {
        [Test]
        public void Tick_WhenFailurePersists_KeepsRequestingRepathWithoutDroppingTarget()
        {
            var tracker = new EnemyNavigationFailureTracker();

            EnemyNavigationFailureSignal first = tracker.Tick(
                0.5f, true, true, false, 0.5f);
            EnemyNavigationFailureSignal second = tracker.Tick(
                0.5f, true, true, false, 0.5f);
            EnemyNavigationFailureSignal third = tracker.Tick(
                0.5f, true, true, false, 0.5f);
            EnemyNavigationFailureSignal fourth = tracker.Tick(
                0.5f, true, true, false, 0.5f);

            Assert.That(first, Is.EqualTo(EnemyNavigationFailureSignal.Repath));
            Assert.That(second, Is.EqualTo(EnemyNavigationFailureSignal.Repath));
            Assert.That(third, Is.EqualTo(EnemyNavigationFailureSignal.Repath));
            Assert.That(fourth, Is.EqualTo(EnemyNavigationFailureSignal.Repath));
        }

        [Test]
        public void Tick_AfterLongFailure_StillRequestsRepath()
        {
            var tracker = new EnemyNavigationFailureTracker();

            EnemyNavigationFailureSignal result = tracker.Tick(
                10f, true, true, false, 0.5f);

            Assert.That(result, Is.EqualTo(EnemyNavigationFailureSignal.Repath));
        }

        [Test]
        public void Tick_WhenMovementRecovers_ClearsAccumulatedFailureTime()
        {
            var tracker = new EnemyNavigationFailureTracker();
            tracker.Tick(0.4f, true, true, false, 0.5f);

            tracker.Tick(0.1f, false, true, true, 0.5f);
            EnemyNavigationFailureSignal result = tracker.Tick(
                0.4f, true, true, false, 0.5f);

            Assert.That(result, Is.EqualTo(EnemyNavigationFailureSignal.None));
        }

        [Test]
        public void Tick_WhenThereIsNoMovementIntent_DoesNotReportFailure()
        {
            var tracker = new EnemyNavigationFailureTracker();

            EnemyNavigationFailureSignal result = tracker.Tick(
                3f, true, false, false, 0.5f);

            Assert.That(result, Is.EqualTo(EnemyNavigationFailureSignal.None));
        }
    }
}
