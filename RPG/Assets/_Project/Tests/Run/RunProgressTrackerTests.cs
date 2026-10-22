using System;
using NUnit.Framework;

namespace Game.Run.Tests
{
    /// <summary>
    /// Run Tracker 只裁定整局顺序与胜负，不读取 Scene。测试锁定越序、重复完成和
    /// Completed/Failed Terminal State，避免 Gate、Enemy、UI 各自维护一份流程真相。
    /// </summary>
    public sealed class RunProgressTrackerTests
    {
        [Test]
        public void StartRun_ArmsFirstEncounterAndRejectsOutOfOrderStart()
        {
            var tracker = new RunProgressTracker();

            Assert.That(tracker.StartRun(3), Is.True);
            Assert.That(tracker.State, Is.EqualTo(RunState.Running));
            Assert.That(tracker.CurrentEncounterIndex, Is.Zero);
            Assert.That(tracker.TryStartEncounter(1), Is.False,
                "后续区域不能绕过当前区域直接进入 Active。");
            Assert.That(tracker.TryStartEncounter(0), Is.True);
            Assert.That(tracker.IsCurrentEncounterActive, Is.True);
            Assert.That(tracker.TryStartEncounter(0), Is.False,
                "同一 Encounter 不能被 Trigger 重复启动。");
        }

        [Test]
        public void CompleteCurrentEncounter_AdvancesOneStepAndArmsNextEncounter()
        {
            var tracker = CreateRunningTracker(encounterCount: 3);

            Assert.That(tracker.CompleteCurrentEncounter(), Is.True);
            Assert.That(tracker.State, Is.EqualTo(RunState.Running));
            Assert.That(tracker.CurrentEncounterIndex, Is.EqualTo(1));
            Assert.That(tracker.IsCurrentEncounterActive, Is.False,
                "前区完成只会 Armed 下一关，必须等下一入口 Trigger 再 Active。");
            Assert.That(tracker.CompleteCurrentEncounter(), Is.False,
                "尚未启动的下一关不能被误完成。");
        }

        [Test]
        public void CompleteLastEncounter_EntersCompletedExactlyOnce()
        {
            var tracker = CreateRunningTracker(encounterCount: 1);

            Assert.That(tracker.CompleteCurrentEncounter(), Is.True);
            Assert.That(tracker.State, Is.EqualTo(RunState.Completed));
            Assert.That(tracker.CompleteCurrentEncounter(), Is.False);
            Assert.That(tracker.TryStartEncounter(0), Is.False);
            Assert.That(tracker.FailRun(), Is.False,
                "Completed 是 Terminal State，迟到的 Player Death 不能覆盖胜利。");
        }

        [Test]
        public void CompleteLastEncounter_AwaitStageExit_EntersStageClearedAndStillAllowsFailure()
        {
            var tracker = new RunProgressTracker();
            Assert.That(
                tracker.StartRun(1, RunCompletionMode.AwaitStageExit),
                Is.True);
            Assert.That(tracker.TryStartEncounter(0), Is.True);

            Assert.That(tracker.CompleteCurrentEncounter(), Is.True);
            Assert.That(tracker.State, Is.EqualTo(RunState.StageCleared));
            Assert.That(tracker.CompleteCurrentEncounter(), Is.False,
                "StageCleared 后不能重复完成最终 Encounter。");
            Assert.That(tracker.FailRun(), Is.True,
                "StageCleared 不是 Terminal；玩家进入 Portal 前死亡仍应进入 Failed。");
            Assert.That(tracker.State, Is.EqualTo(RunState.Failed));
        }

        [Test]
        public void FailRun_EntersFailedExactlyOnceAndStopsFurtherProgress()
        {
            var tracker = CreateRunningTracker(encounterCount: 2);

            Assert.That(tracker.FailRun(), Is.True);
            Assert.That(tracker.State, Is.EqualTo(RunState.Failed));
            Assert.That(tracker.FailRun(), Is.False);
            Assert.That(tracker.CompleteCurrentEncounter(), Is.False,
                "失败后的迟到 Enemy Death 不能继续推进关卡。");
            Assert.That(tracker.TryStartEncounter(1), Is.False);
        }

        [Test]
        public void NotStartedRun_RejectsEncounterAndTerminalCommands()
        {
            var tracker = new RunProgressTracker();

            Assert.That(tracker.State, Is.EqualTo(RunState.NotStarted));
            Assert.That(tracker.TryStartEncounter(0), Is.False);
            Assert.That(tracker.CompleteCurrentEncounter(), Is.False);
            Assert.That(tracker.FailRun(), Is.False);
        }

        [Test]
        public void StartRun_InvalidEncounterCountThrows()
        {
            var tracker = new RunProgressTracker();

            Assert.Throws<ArgumentOutOfRangeException>(() => tracker.StartRun(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => tracker.StartRun(-1));
        }

        private static RunProgressTracker CreateRunningTracker(int encounterCount)
        {
            var tracker = new RunProgressTracker();
            tracker.StartRun(encounterCount);
            tracker.TryStartEncounter(0);
            return tracker;
        }
    }
}
