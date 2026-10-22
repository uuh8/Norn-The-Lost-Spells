using System;
using System.Reflection;
using NUnit.Framework;

namespace Game.Run.Tests
{
    // 首轮用反射检查新边界，缺失实现会表现为 NUnit 失败，而不是阻塞整个项目编译。
    public sealed class WandRuntimeStateTests
    {
        private object State()
        {
            Type type = typeof(ItemOwnershipStore).Assembly.GetType("Game.Run.WandRuntimeState");
            Assert.That(type, Is.Not.Null, "每根实例需要独立资源对象。");
            return Activator.CreateInstance(type, "wand-a", 100f, 10f, .8f);
        }
        private static float Value(object state, string name) => (float)state.GetType().GetProperty(name).GetValue(state);
        private static bool Pay(object state, float cost, bool root = true) =>
            (bool)state.GetType().GetMethod("TrySpend").Invoke(state, new object[] { cost, root });
        private static void Tick(object state, float dt) => state.GetType().GetMethod("Tick").Invoke(state, new object[] { dt });

        [Test] public void RootPayment_CommitsManaAndCooldownTogether()
        { object s = State(); Assert.That(Pay(s, 20), Is.True); Assert.That(Value(s, "CurrentMana"), Is.EqualTo(80)); Assert.That(Value(s, "CooldownRemaining"), Is.EqualTo(.8f)); }
        [Test] public void InsufficientMana_ChangesNeitherResource()
        { object s = State(); Assert.That(Pay(s, 101), Is.False); Assert.That(Value(s, "CurrentMana"), Is.EqualTo(100)); Assert.That(Value(s, "CooldownRemaining"), Is.Zero); }
        [Test] public void Cooldown_RejectsSecondRootWithoutDoubleCharge()
        { object s = State(); Pay(s, 20); Assert.That(Pay(s, 20), Is.False); Assert.That(Value(s, "CurrentMana"), Is.EqualTo(80)); }
        [Test] public void Payload_PaysManaWithoutRestartingRootCooldown()
        { object s = State(); Pay(s, 20); Tick(s, .5f); Assert.That(Pay(s, 10, false), Is.True); Assert.That(Value(s, "CurrentMana"), Is.EqualTo(75)); Assert.That(Value(s, "CooldownRemaining"), Is.EqualTo(.3f).Within(.0001f)); }
        [TestCase(-1f)] [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)]
        public void InvalidCost_IsRejected(float cost)
        { object s = State(); Assert.That(Pay(s, cost), Is.False); Assert.That(Value(s, "CurrentMana"), Is.EqualTo(100)); }
        [TestCase(0f)] [TestCase(-1f)] [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)]
        public void InvalidOrPausedTick_IsIgnored(float dt)
        { object s = State(); Pay(s, 20); Tick(s, dt); Assert.That(Value(s, "CurrentMana"), Is.EqualTo(80)); Assert.That(Value(s, "CooldownRemaining"), Is.EqualTo(.8f)); }
        [Test] public void Recovery_ClampsBothResources()
        { object s = State(); Pay(s, 20); Tick(s, 100); Assert.That(Value(s, "CurrentMana"), Is.EqualTo(100)); Assert.That(Value(s, "CooldownRemaining"), Is.Zero); }
    }
}
