using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.Combat.Tests
{
    /// <summary>落点预警的寿命属于服务器逻辑时间；不会把 Render frame 或墙钟时间当成已经模拟过的时间。</summary>
    public sealed class CombatVisualLifetimeTests
    {
        private readonly List<GameObject> _owned = new List<GameObject>();
        [TearDown] public void Cleanup()
        {
            foreach (GameObject root in _owned) if (root != null) Object.DestroyImmediate(root);
            _owned.Clear();
        }

        [Test]
        public void BirthTick_DoesNotConsumeTimeBeforeMarkerExisted()
        {
            var world = new CombatSimulationWorld(); world.BeginTick(10);
            Component timer = Create(world, 1f);
            world.StepBeforePhysics(.25f, 10);
            Assert.That(Remaining(timer), Is.EqualTo(1f));
            world.StepBeforePhysics(.25f, 11);
            Assert.That(Remaining(timer), Is.EqualTo(.75f).Within(.00001f));
        }

        [Test]
        public void DuplicateTick_DoesNotConsumeMarkerLifetimeAgain()
        {
            var world = new CombatSimulationWorld(); Component timer = Create(world, 1f);
            world.StepBeforePhysics(.25f, 1);
            world.StepBeforePhysics(.25f, 1);
            timer.GetType().GetMethod("StepAuthority").Invoke(timer, new object[] { .25f, 1u });
            Assert.That(Remaining(timer), Is.EqualTo(.75f).Within(.00001f));
            world.StepBeforePhysics(.25f, 2);
            Assert.That(Remaining(timer), Is.EqualTo(.5f).Within(.00001f));
        }

        [Test]
        public void NoSimulationDuringFreeze_LeavesRemainingTimeAndMarkerIntact()
        {
            var world = new CombatSimulationWorld(); Component timer = Create(world, 1f);
            world.StepBeforePhysics(.25f, 1);
            // 推进外部 Tick 标识并不等于推进 Gameplay；Frozen 恢复时只扣随后真正模拟的 dt。
            world.BeginTick(1000); world.StepBeforePhysics(0f, 1000);
            Assert.That(Remaining(timer), Is.EqualTo(.75f).Within(.00001f));
            Assert.That(timer.gameObject.activeSelf, Is.True);
            world.StepBeforePhysics(.25f, 1001);
            Assert.That(Remaining(timer), Is.EqualTo(.5f).Within(.00001f));
        }

        [Test]
        public void Expiry_DeactivatesBeforeDestroyAndHappensExactlyOnce()
        {
            var world = new CombatSimulationWorld(1); Component timer = Create(world, .5f);
            world.StepBeforePhysics(.25f, 1);
            LogAssert.Expect(LogType.Error, new Regex("Destroy may not be called from edit mode"));
            world.StepBeforePhysics(.25f, 2);
            Assert.That(timer.gameObject.activeSelf, Is.False, "Unity 帧尾 Destroy 前先停用，复制层才能在同帧观察到终止。");
            Assert.That((bool)timer.GetType().GetProperty("Expired").GetValue(timer), Is.True);
            timer.GetType().GetMethod("StepAuthority").Invoke(timer, new object[] { 1f, 3u });
            world.StepBeforePhysics(1f, 3);
            LogAssert.NoUnexpectedReceived();
            Assert.That(Remaining(timer), Is.Zero);
            Assert.DoesNotThrow(() => Create(world, 1f), "到期必须归还固定 World 槽位，不能逐次施法累积占用。");
        }

        private Component Create(CombatSimulationWorld world, float seconds)
        {
            Type type = typeof(CombatSimulationWorld).Assembly.GetType("Game.Combat.CombatVisualLifetime");
            Assert.That(type, Is.Not.Null, "预警不能再用真实时间 Destroy，而应登记到已有固定容量的 Combat World。");
            var root = new GameObject("N4 visual lifetime fixture"); _owned.Add(root);
            Component timer = root.AddComponent(type);
            MethodInfo configure = type.GetMethod("ConfigureAuthority", new[] { typeof(CombatSimulationWorld), typeof(float) });
            Assert.That(configure, Is.Not.Null);
            configure.Invoke(timer, new object[] { world, seconds }); return timer;
        }
        private static float Remaining(Component timer) => (float)timer.GetType().GetProperty("RemainingSeconds").GetValue(timer);
    }
}
