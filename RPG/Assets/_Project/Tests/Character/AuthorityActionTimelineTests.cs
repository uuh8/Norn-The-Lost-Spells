using System;
using System.Reflection;
using NUnit.Framework;

namespace Game.Character.Tests
{
    /// <summary>测试固定步动作的可见语义：补跑多个 Tick 不重发出手，暂停不推进，取消不能留下幽灵施法。</summary>
    public sealed class AuthorityActionTimelineTests
    {
        private static Type TimelineType => typeof(WizardController).Assembly.GetType("Game.Character.AuthorityActionTimeline");
        private static object Create()
        {
            Assert.That(TimelineType, Is.Not.Null, "缺少独立于 Animator 的权威动作 Timeline。");
            return Activator.CreateInstance(TimelineType);
        }
        private static T Call<T>(object timeline, string method, params object[] values) =>
            (T)TimelineType.GetMethod(method).Invoke(timeline, values);
        private static T Read<T>(object timeline, string property) =>
            (T)TimelineType.GetProperty(property).GetValue(timeline);

        [Test]
        public void CatchUpThreeTicks_ReleaseThresholdOnlyFiresOnce()
        {
            var timeline = Create();
            Assert.That(Call<bool>(timeline, "TryStart", 7ul, .6f, .1f), Is.True);
            Assert.That(Call<bool>(timeline, "Step", .06f, 1u), Is.False);
            Assert.That(Call<bool>(timeline, "Step", .06f, 2u), Is.True);
            Assert.That(Call<bool>(timeline, "Step", .06f, 3u), Is.False);
            Assert.That(Read<ulong>(timeline, "CommandId"), Is.EqualTo(7ul));
        }
        [Test]
        public void DuplicateOrOlderTick_DoesNotAdvanceTimeOrReleaseAgain()
        {
            var timeline = Create(); Call<bool>(timeline, "TryStart", 7ul, .6f, .1f);
            Assert.That(Call<bool>(timeline, "Step", .06f, 4u), Is.False);
            Assert.That(Call<bool>(timeline, "Step", .06f, 4u), Is.False);
            Assert.That(Call<bool>(timeline, "Step", .06f, 3u), Is.False);
            Assert.That(Read<float>(timeline, "ElapsedSeconds"), Is.EqualTo(.06f).Within(.0001));
            Assert.That(Call<bool>(timeline, "Step", .06f, 5u), Is.True);
        }
        [Test]
        public void Cancel_PreventsFutureReleaseAndAllowsNextAction()
        {
            var timeline = Create(); Call<bool>(timeline, "TryStart", 7ul, .6f, .1f);
            TimelineType.GetMethod("Cancel").Invoke(timeline, null);
            Assert.That(Call<bool>(timeline, "Step", 2f, 1u), Is.False);
            Assert.That(Read<bool>(timeline, "IsActive"), Is.False);
            Assert.That(Call<bool>(timeline, "TryStart", 8ul, .3f, .05f), Is.True);
            Assert.That(Call<bool>(timeline, "Step", .06f, 2u), Is.True);
        }
        [Test]
        public void InvalidTimesOrCommand_AreRejectedWithoutStarting()
        {
            var timeline = Create();
            Assert.That(Call<bool>(timeline, "TryStart", 0ul, .6f, .1f), Is.False);
            Assert.That(Call<bool>(timeline, "TryStart", 1ul, float.NaN, .1f), Is.False);
            Assert.That(Call<bool>(timeline, "TryStart", 1ul, .2f, .3f), Is.False);
            Assert.That(Call<bool>(timeline, "TryStart", 1ul, .2f, -.1f), Is.False);
            Assert.That(Read<bool>(timeline, "IsActive"), Is.False);
        }
        [Test]
        public void NoStepDuringFrozen_DoesNotDependOnWallClockOrUnityDelta()
        {
            var timeline = Create(); Call<bool>(timeline, "TryStart", 7ul, .6f, .1f);
            Call<bool>(timeline, "Step", .03f, 1u);
            // Frozen 的契约是不调用 Step；重复查询不能像 Coroutine / Time.time 一样暗中越过 deadline。
            for (int i = 0; i < 8; i++) Assert.That(Read<float>(timeline, "ElapsedSeconds"), Is.EqualTo(.03f));
            Assert.That(Call<bool>(timeline, "Step", .03f, 2u), Is.False);
            Assert.That(Call<bool>(timeline, "Step", .05f, 3u), Is.True);
        }
    }
}
