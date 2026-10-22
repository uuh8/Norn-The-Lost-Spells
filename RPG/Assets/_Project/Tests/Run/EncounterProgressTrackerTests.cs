using System;
using NUnit.Framework;

namespace Game.Run.Tests
{
    /// <summary>
    /// Encounter Tracker 是单个战斗区域的纯规则内核。本组测试先定义“配置、武装、启动、
    /// 按 HealthComponent.Id 去重计数、清场”的期望，不依赖 Scene、Collider 或 MonoBehaviour。
    /// </summary>
    public sealed class EncounterProgressTrackerTests
    {
        [Test]
        public void Lifecycle_OnlyMovesWaitingToArmedToActiveToCleared()
        {
            var tracker = new EncounterProgressTracker();
            tracker.Configure(new[] { 101, 202 });

            Assert.That(tracker.State, Is.EqualTo(EncounterState.Waiting));
            Assert.That(tracker.TryBegin(), Is.False,
                "未 Armed 的区域不能因 Trigger 或误调用而越过顺序直接开战。");
            Assert.That(tracker.TryArm(), Is.True);
            Assert.That(tracker.State, Is.EqualTo(EncounterState.Armed));
            Assert.That(tracker.TryBegin(), Is.True);
            Assert.That(tracker.State, Is.EqualTo(EncounterState.Active));

            Assert.That(tracker.RecordDeath(101), Is.True);
            Assert.That(tracker.State, Is.EqualTo(EncounterState.Active));
            Assert.That(tracker.RecordDeath(202), Is.True);
            Assert.That(tracker.State, Is.EqualTo(EncounterState.Cleared));
            Assert.That(tracker.IsCleared, Is.True);

            Assert.That(tracker.TryArm(), Is.False,
                "Cleared 是本 Encounter 的 Terminal State，不能重新武装。");
            Assert.That(tracker.TryBegin(), Is.False);
        }

        [Test]
        public void RecordDeath_DuplicateTargetIsCountedOnlyOnce()
        {
            var tracker = CreateActiveTracker(11, 22);

            Assert.That(tracker.RecordDeath(11), Is.True);
            Assert.That(tracker.RecordDeath(11), Is.False,
                "同一个 DeathEvent 重复到达时必须按 TargetId 去重，否则会提前开门。");
            Assert.That(tracker.RemainingEnemyCount, Is.EqualTo(1));
            Assert.That(tracker.IsCleared, Is.False);
        }

        [Test]
        public void RecordDeath_TargetOutsideEncounterIsIgnored()
        {
            var tracker = CreateActiveTracker(11, 22);

            Assert.That(tracker.RecordDeath(999), Is.False);
            Assert.That(tracker.RemainingEnemyCount, Is.EqualTo(2));
            Assert.That(tracker.State, Is.EqualTo(EncounterState.Active));
        }

        [Test]
        public void RecordDeath_BeforeActiveOrAfterClearedDoesNotMutateProgress()
        {
            var tracker = new EncounterProgressTracker();
            tracker.Configure(new[] { 7 });

            Assert.That(tracker.RecordDeath(7), Is.False);
            Assert.That(tracker.RemainingEnemyCount, Is.EqualTo(1));

            tracker.TryArm();
            tracker.TryBegin();
            Assert.That(tracker.RecordDeath(7), Is.True);
            Assert.That(tracker.RecordDeath(7), Is.False);
            Assert.That(tracker.RemainingEnemyCount, Is.Zero);
        }

        [Test]
        public void Configure_EmptyEnemyListThrowsInsteadOfCreatingSoftLock()
        {
            var tracker = new EncounterProgressTracker();

            Assert.Throws<ArgumentException>(() => tracker.Configure(Array.Empty<int>()),
                "0 敌人配置必须显式失败，不能让 Active Encounter 永远等不到 DeathEvent。");
        }

        [Test]
        public void Configure_DuplicateEnemyIdThrowsBecauseMembershipMustBeUnambiguous()
        {
            var tracker = new EncounterProgressTracker();

            Assert.Throws<ArgumentException>(() => tracker.Configure(new[] { 5, 5 }));
        }

        private static EncounterProgressTracker CreateActiveTracker(params int[] enemyIds)
        {
            var tracker = new EncounterProgressTracker();
            tracker.Configure(enemyIds);
            tracker.TryArm();
            tracker.TryBegin();
            return tracker;
        }
    }
}
