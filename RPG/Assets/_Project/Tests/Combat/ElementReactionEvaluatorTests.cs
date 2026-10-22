using NUnit.Framework;

namespace Game.Combat.Tests
{
    /// <summary>
    /// 这些测试只验证“输入元素快照 -> 纯公式结果”，不创建 GameObject。
    /// 这样失败时可以先排除 Unity 生命周期、Physics 和 VFX，只检查规则本身。
    /// </summary>
    public sealed class ElementReactionEvaluatorTests
    {
        // Task1 的公式测试不关心具体攻击者，只需要一个稳定的值快照填充 Source 字段。
        private static readonly StatusSource DefaultSource = new StatusSource(10, 1);

        /// <summary>
        /// 枚举的底层 byte 数值属于序列化协议，测试防止重排后旧 Asset/Event 被错误解释。
        /// </summary>
        [Test]
        public void SerializedEnumValues_AreStable()
        {
            // 这些值将来会进入 ScriptableObject 与跨模块事件。
            // 显式测试可以防止有人在枚举中间插值，导致旧资产被解释成另一种元素/反应。
            Assert.AreEqual(0, (byte)ElementChannel.Fire);
            Assert.AreEqual(1, (byte)ElementChannel.Water);
            Assert.AreEqual(2, (byte)ElementChannel.Poison);
            Assert.AreEqual(3, (byte)ElementChannel.Goo);

            Assert.AreEqual(0, (byte)ElementReactionId.Extinguish);
            Assert.AreEqual(1, (byte)ElementReactionId.ToxicCombustion);
            Assert.AreEqual(2, (byte)ElementReactionId.IgniteGoo);
        }

        /// <summary>
        /// 用阈值前、阈值上和高于阈值三类 Boundary Value 验证 >= 语义。
        /// </summary>
        /// <param name="fire">输入 Snapshot 的 Fire 强度。</param>
        /// <param name="water">输入 Snapshot 的 Water 强度。</param>
        /// <param name="expected">预期是否进入 Formal Extinguish。</param>
        [TestCase(9.99f, 100f, false)]
        [TestCase(100f, 9.99f, false)]
        [TestCase(10f, 10f, true)]
        [TestCase(100f, 100f, true)]
        public void Extinguish_FormalThreshold_IsInclusive(
            float fire,
            float water,
            bool expected)
        {
            // Arrange：建立固定阈值。Low/Formal Rate 对这个布尔公式没有影响。
            var tuning = new ExtinguishTuning
            {
                FormalThreshold = 10f,
                LowRatePerSecond = 10f,
                FormalRatePerSecond = 50f,
            };

            // Act：只调用纯函数，不创建 GameObject，也不推进 Unity Frame。
            bool actual = ElementReactionEvaluator.IsFormalExtinguish(
                Snapshot(fire: fire, water: water),
                in tuning);

            // Assert：参数化用例让“任一通道不足”和“恰好等于阈值”都被覆盖。
            Assert.AreEqual(expected, actual);
        }

        /// <summary>
        /// 正式灭火必须等量移除 Fire 与 Water，而且移除量不能超过任一方的现有存量。
        /// 这里用很高的 Formal Rate，让 Water=30 成为最先触发的守恒上限。
        /// </summary>
        [Test]
        public void CalculateExtinguishConsumption_IsEqualAndLimitedByWater()
        {
            var tuning = new ExtinguishTuning
            {
                LowRatePerSecond = 5f,
                FormalRatePerSecond = 100f,
            };

            float consumed = ElementReactionEvaluator.CalculateExtinguishConsumption(
                fire: 80f,
                water: 30f,
                deltaTime: 1f,
                useFormalRate: true,
                in tuning);

            // min(Fire=80, Water=30, Rate*dt=100) = 30。
            Assert.AreEqual(30f, consumed, 1e-4f);
        }

        /// <summary>
        /// 没有有效 Fire、Water 或模拟时间时，本步不能凭空发生反应。
        /// 参数化测试同时覆盖负输入、空通道和暂停时间三种边界。
        /// </summary>
        [TestCase(-1f, 30f, 1f)]
        [TestCase(80f, 0f, 1f)]
        [TestCase(80f, 30f, 0f)]
        public void CalculateExtinguishConsumption_InvalidOrEmptyInput_ReturnsZero(
            float fire,
            float water,
            float deltaTime)
        {
            var tuning = new ExtinguishTuning
            {
                FormalRatePerSecond = 100f,
            };

            float consumed = ElementReactionEvaluator.CalculateExtinguishConsumption(
                fire,
                water,
                deltaTime,
                useFormalRate: true,
                in tuning);

            Assert.AreEqual(0f, consumed, 1e-4f);
        }

        /// <summary>
        /// 同一组存量在低速与正式模式下只应切换速率参数，守恒上限保持一致。
        /// </summary>
        [Test]
        public void CalculateExtinguishConsumption_SelectsRequestedRate()
        {
            var tuning = new ExtinguishTuning
            {
                LowRatePerSecond = 5f,
                FormalRatePerSecond = 50f,
            };

            float low = ElementReactionEvaluator.CalculateExtinguishConsumption(
                80f, 30f, 0.5f, false, in tuning);
            float formal = ElementReactionEvaluator.CalculateExtinguishConsumption(
                80f, 30f, 0.5f, true, in tuning);

            Assert.AreEqual(2.5f, low, 1e-4f);
            Assert.AreEqual(25f, formal, 1e-4f);
        }

        /// <summary>
        /// 验证 Wet 预算先清 Poison，再把剩余预算按 Goo Multiplier 换算。
        /// </summary>
        [Test]
        public void WetCleanse_PrioritizesPoisonThenGoo()
        {
            var tuning = new WetCleanseTuning
            {
                BudgetPerSecondAtFullWet = 30f,
            };

            WetCleanseResult result = ElementReactionEvaluator.CalculateWetCleanse(
                Snapshot(water: 40f, poison: 3f, goo: 20f),
                deltaTime: 1f,
                poisonMultiplier: 0.75f,
                gooMultiplier: 1.25f,
                in tuning);

            // Wet=40 产生 12 点预算；清掉 3 Poison 花 4 点预算，
            // 剩余 8 点预算按 1.25 倍率清掉 10 Goo。
            Assert.AreEqual(3f, result.PoisonRemoved, 1e-4f);
            Assert.AreEqual(10f, result.GooRemoved, 1e-4f);
        }

        [Test]
        public void WetCleanse_DoesNotTouchGooWhilePoisonConsumesFullBudget()
        {
            // Poison 足够多时会吃完全部预算；优先级规则要求 GooRemoved 必须保持 0。
            var tuning = new WetCleanseTuning
            {
                BudgetPerSecondAtFullWet = 30f,
            };

            WetCleanseResult result = ElementReactionEvaluator.CalculateWetCleanse(
                Snapshot(water: 40f, poison: 100f, goo: 100f),
                deltaTime: 1f,
                poisonMultiplier: 0.75f,
                gooMultiplier: 1.25f,
                in tuning);

            Assert.AreEqual(9f, result.PoisonRemoved, 1e-4f);
            Assert.AreEqual(0f, result.GooRemoved, 1e-4f);
        }

        /// <summary>
        /// 用临界值验证 Toxic Combustion 必须同时满足 Fire 与 Poison Threshold。
        /// </summary>
        /// <param name="fire">Fire 强度。</param>
        /// <param name="poison">Poison 强度。</param>
        /// <param name="expected">预期能否启动毒爆。</param>
        [TestCase(24.99f, 20f, false)]
        [TestCase(25f, 19.99f, false)]
        [TestCase(25f, 20f, true)]
        public void ToxicThreshold_RequiresFireAndPoison(
            float fire,
            float poison,
            bool expected)
        {
            var tuning = new ToxicCombustionTuning
            {
                FireThreshold = 25f,
                PoisonThreshold = 20f,
            };

            bool actual = ElementReactionEvaluator.CanStartToxicCombustion(
                Snapshot(fire: fire, poison: poison),
                in tuning);

            Assert.AreEqual(expected, actual);
        }

        /// <summary>
        /// 验证 Wet 抑制阈值同样是包含等号的：Water 恰好达到阈值就应阻止 Ignite Goo。
        /// </summary>
        /// <param name="water">Water 强度。</param>
        /// <param name="expected">预期能否启动 Ignite Goo。</param>
        [TestCase(9.99f, true)]
        [TestCase(10f, false)]
        [TestCase(100f, false)]
        public void Ignite_IsBlockedAtWetThreshold(float water, bool expected)
        {
            var tuning = new IgniteGooTuning
            {
                FireThreshold = 15f,
                GooThreshold = 20f,
                WetInhibitThreshold = 10f,
                RequiredFireCapacity = 10f,
            };

            bool actual = ElementReactionEvaluator.CanStartIgniteGoo(
                Snapshot(fire: 80f, water: water, goo: 20f),
                in tuning);

            Assert.AreEqual(expected, actual);
        }

        [TestCase(90f, true)]
        [TestCase(90.01f, false)]
        public void Ignite_RequiresConfiguredFireCapacity(float fire, bool expected)
        {
            // RequiredFireCapacity=10 表示 Fire 最大只能从 90 开始转化；90.01 已没有足够空间。
            var tuning = new IgniteGooTuning
            {
                FireThreshold = 15f,
                GooThreshold = 20f,
                WetInhibitThreshold = 10f,
                RequiredFireCapacity = 10f,
            };

            bool actual = ElementReactionEvaluator.CanStartIgniteGoo(
                Snapshot(fire: fire, water: 0f, goo: 20f),
                in tuning);

            Assert.AreEqual(expected, actual);
        }

        private static ElementStateSnapshot Snapshot(
            float fire = 0f,
            float water = 0f,
            float poison = 0f,
            float goo = 0f)
        {
            // Test Data Builder：集中构造合法 Snapshot，让每个测试只显式写与自己相关的通道。
            return new ElementStateSnapshot(
                fire,
                water,
                poison,
                goo,
                DefaultSource,
                DefaultSource,
                DefaultSource,
                DefaultSource);
        }
    }
}
