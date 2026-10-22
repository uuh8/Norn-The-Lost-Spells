using NUnit.Framework;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// Runtime 测试先隔离固定 Tick 累计器：帧率可以变化，但相同的累计时间必须产生相同数量的模拟步。
    /// Physics Bake 与 Gizmo 属于 Unity 场景集成，留给本 Task 的 Editor Gate 验证。
    /// </summary>
    public sealed class ElementFieldRuntimeTests
    {
        [Test]
        public void HalfIntervalDoesNotRunTick()
        {
            var accumulator = new ElementFieldTickAccumulator(tickRate: 10f, maxCatchUpTicks: 3);

            Assert.That(accumulator.Consume(0.05f), Is.Zero);
        }

        [Test]
        public void TwoHalfIntervalsRunExactlyOneTick()
        {
            var accumulator = new ElementFieldTickAccumulator(tickRate: 10f, maxCatchUpTicks: 3);

            Assert.That(accumulator.Consume(0.05f), Is.Zero);
            Assert.That(accumulator.Consume(0.05f), Is.EqualTo(1));
        }

        [Test]
        public void LongFrameIsCappedAndRemainingDebtIsDiscarded()
        {
            var accumulator = new ElementFieldTickAccumulator(tickRate: 10f, maxCatchUpTicks: 3);

            Assert.That(accumulator.Consume(1f), Is.EqualTo(3));
            Assert.That(accumulator.DroppedDebtLastConsume, Is.True);
            Assert.That(accumulator.Consume(0f), Is.Zero,
                "超过 Catch-Up 上限后不能把旧债务带到下一帧形成 Spiral of Death。");
        }

        [Test]
        public void ZeroDeltaDoesNotRunTick()
        {
            var accumulator = new ElementFieldTickAccumulator(tickRate: 10f, maxCatchUpTicks: 3);

            Assert.That(accumulator.Consume(0f), Is.Zero);
            Assert.That(accumulator.DroppedDebtLastConsume, Is.False);
        }

        [Test]
        public void NonEmptyDebugCellKeepsMinimumVisibleAlpha()
        {
            float alpha = ElementFieldDebugView.CalculateActiveAlpha(
                amount: 1,
                minimumActiveAlpha: 0.25f);

            Assert.That(alpha, Is.EqualTo(0.25f).Within(0.0001f),
                "低 Amount 是有效 Gameplay 数据，Debug View 不能把它画到肉眼不可见。 ");
        }
    }
}
