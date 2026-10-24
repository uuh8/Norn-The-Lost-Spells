using System;
using System.Reflection;
using NUnit.Framework;

namespace Game.Character.Tests
{
    /// <summary>只推进显式 Authority Tick；Reflection 让缺少新调度器成为 RED 断言而不阻断工程编译。</summary>
    public sealed class AuthorityCastSchedulerTests
    {
        private static object Scheduler(int capacity = 2)
        {
            Type type = typeof(SpellCaster).Assembly.GetType("Game.Character.AuthorityCastScheduler");
            Assert.That(type, Is.Not.Null, "缺少有界 AuthorityCastScheduler；线上 Skyfall 不能由 Coroutine 墙钟推进");
            return Activator.CreateInstance(type, capacity);
        }
        private static bool Schedule(object scheduler, float delay, ulong command, Action<ulong> callback, out uint handle)
        {
            var args = new object[] { delay, command, callback, 0u };
            bool accepted = (bool)scheduler.GetType().GetMethod("TrySchedule").Invoke(scheduler, args);
            handle = (uint)args[3]; return accepted;
        }
        private static void Step(object scheduler, float delta, uint tick)
            => scheduler.GetType().GetMethod("StepAuthority").Invoke(scheduler, new object[] { delta, tick });
        private static bool Cancel(object scheduler, uint handle)
            => (bool)scheduler.GetType().GetMethod("Cancel").Invoke(scheduler, new object[] { handle });
        private static int Count(object scheduler) => (int)scheduler.GetType().GetProperty("PendingCount").GetValue(scheduler);

        [Test]
        public void FrozenWithoutAuthoritySteps_DoesNotReleasePendingCast()
        {
            object scheduler = Scheduler(); int fired = 0;
            Assert.That(Schedule(scheduler, .1f, 7, _ => fired++, out _), Is.True);
            Assert.That(fired, Is.Zero); Assert.That(Count(scheduler), Is.EqualTo(1));
            Step(scheduler, .05f, 1); Assert.That(fired, Is.Zero);
            Step(scheduler, .05f, 2); Assert.That(fired, Is.EqualTo(1)); Assert.That(Count(scheduler), Is.Zero);
        }

        [Test]
        public void RepeatedAndOlderTick_DoesNotCountDelayTwice()
        {
            object scheduler = Scheduler(); int fired = 0;
            Schedule(scheduler, .1f, 7, _ => fired++, out _);
            Step(scheduler, .05f, 1); Step(scheduler, .05f, 1); Step(scheduler, .05f, 0);
            Assert.That(fired, Is.Zero);
            Step(scheduler, .05f, 2); Step(scheduler, .1f, 3);
            Assert.That(fired, Is.EqualTo(1));
        }

        [Test]
        public void CallbackReceivesCapturedRootCommandAfterAnotherCommandIsScheduled()
        {
            object scheduler = Scheduler(); ulong first = 0, second = 0;
            Schedule(scheduler, .2f, 100, value => first = value, out _);
            Schedule(scheduler, .1f, 101, value => second = value, out _);
            Step(scheduler, .1f, 1); Assert.That(second, Is.EqualTo(101)); Assert.That(first, Is.Zero);
            Step(scheduler, .1f, 2); Assert.That(first, Is.EqualTo(100));
        }

        [Test]
        public void CancelledHandleCannotReleaseOrCancelNewOccupantOfSameSlot()
        {
            object scheduler = Scheduler(1); int fired = 0;
            Schedule(scheduler, .1f, 1, _ => fired += 1, out uint oldHandle);
            Assert.That(Cancel(scheduler, oldHandle), Is.True);
            Schedule(scheduler, .1f, 2, _ => fired += 10, out uint newHandle);
            Assert.That(newHandle, Is.Not.EqualTo(oldHandle)); Assert.That(Cancel(scheduler, oldHandle), Is.False);
            Step(scheduler, .1f, 1); Assert.That(fired, Is.EqualTo(10));
        }

        [Test]
        public void FullCapacityRejectsNewCastWithoutOverwritingAlreadyAcceptedCast()
        {
            object scheduler = Scheduler(1); int fired = 0;
            Assert.That(Schedule(scheduler, .1f, 1, _ => fired++, out _), Is.True);
            Assert.That(Schedule(scheduler, .1f, 2, _ => fired += 10, out uint rejected), Is.False);
            Assert.That(rejected, Is.Zero); Step(scheduler, .1f, 1); Assert.That(fired, Is.EqualTo(1));
        }

        [Test]
        public void CancelAllRemovesPendingCallbacksBeforeLaterAuthorityTick()
        {
            object scheduler = Scheduler(); int fired = 0;
            Schedule(scheduler, .1f, 1, _ => fired++, out _);
            scheduler.GetType().GetMethod("CancelAll").Invoke(scheduler, null);
            Assert.That(Count(scheduler), Is.Zero);
            Step(scheduler, 1f, 1); Assert.That(fired, Is.Zero);
        }

        [Test]
        public void CallbackScheduledDuringTickWaitsUntilNextAuthorityTick()
        {
            object scheduler = Scheduler(1); int fired = 0;
            Schedule(scheduler, 0, 1, ignoredCommand =>
            {
                fired++;
                Assert.That(Schedule(scheduler, 0, 2, __ => fired++, out _), Is.True);
            }, out _);
            Step(scheduler, .1f, 1); Assert.That(fired, Is.EqualTo(1));
            Step(scheduler, .1f, 2); Assert.That(fired, Is.EqualTo(2));
        }

        [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(-1f)]
        public void InvalidDelayDoesNotReserveQueueSlot(float delay)
        {
            object scheduler = Scheduler();
            Assert.That(Schedule(scheduler, delay, 1, _ => { }, out _), Is.False);
            Assert.That(Count(scheduler), Is.Zero);
        }
    }
}
