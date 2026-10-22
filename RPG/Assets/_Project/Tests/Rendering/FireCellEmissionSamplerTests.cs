using NUnit.Framework;
using UnityEngine;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// 验证 Fire Cell 到三层粒子发射决策的纯数学。
    /// 测试不创建 ParticleSystem：概率、Sample Cap 与空间 Jitter 先脱离 Unity Scene 被锁定，
    /// Renderer 只负责把这些确定结果写入 ParticleSystem.EmitParams。
    /// </summary>
    public sealed class FireCellEmissionSamplerTests
    {
        private const uint BodySeed = 0xA341316Cu;
        private const uint CoreSeed = 0xC8013EA4u;

        [Test]
        public void LowAmountFireRemainsVisibleWithoutChangingGameplayAmount()
        {
            const byte amount=77;
            float heat=FireCellEmissionSampler.VisualHeat(amount,.5f);
            Assert.That(heat,Is.GreaterThan(.7f));
            Assert.That(FireCellEmissionSampler.VisualHeat(0,.5f),Is.Zero);
            int emitted=0;
            for(uint tick=0;tick<1000;tick++)
                if(FireCellEmissionSampler.ShouldEmitVisual(Vector3Int.zero,tick,BodySeed,amount,.65f,.5f))emitted++;
            Assert.That(emitted,Is.InRange(350,650),"低量燃烧不能每秒只偶尔出现一个点。");
            Assert.That(FireCellEmissionSampler.GroundSourceHeight(.25f,.56f),Is.GreaterThan(.32f*1.65f));
        }

        [Test]
        public void LiquidAndFireShadersImportWithoutErrors()
        {
            string[] names={"Game/Elemental/Liquid Dormant Cell","Game/Elemental/Fire Parcel Splat",
                "Game/Elemental/Fire Parcel Surface"};
            foreach(string name in names)
            {
                Shader shader=Shader.Find(name);
                Assert.That(shader,Is.Not.Null,name);
                Assert.That(UnityEditor.ShaderUtil.ShaderHasError(shader),Is.False,name);
            }
        }

        [Test]
        public void Hash01_SameInputsAreDeterministicAndStayInsideUnitInterval()
        {
            float expected = FireCellEmissionSampler.Hash01(
                cellIndex: 73,
                visualSequence: 11u,
                layerSeed: BodySeed,
                channel: 0u);

            for (int cellIndex = 0; cellIndex < 512; cellIndex++)
            {
                float actual = FireCellEmissionSampler.Hash01(
                    cellIndex,
                    visualSequence: 11u,
                    layerSeed: BodySeed,
                    channel: 0u);

                Assert.That(actual, Is.GreaterThanOrEqualTo(0f));
                Assert.That(actual, Is.LessThan(1f));
            }

            Assert.That(FireCellEmissionSampler.Hash01(73, 11u, BodySeed, 0u),
                Is.EqualTo(expected));
        }

        [Test]
        public void Hash01_ChangingLayerOrVisualSequenceChangesSample()
        {
            float body = FireCellEmissionSampler.Hash01(73, 11u, BodySeed, 0u);
            float core = FireCellEmissionSampler.Hash01(73, 11u, CoreSeed, 0u);
            float nextVisualTick = FireCellEmissionSampler.Hash01(73, 12u, BodySeed, 0u);

            Assert.That(core, Is.Not.EqualTo(body));
            Assert.That(nextVisualTick, Is.Not.EqualTo(body));
        }

        [Test]
        public void GlobalCellHash_IsStableAndUsesAllThreeWorldAxes()
        {
            var globalCell = new Vector3Int(-17, 3, 42);
            float expected = FireCellEmissionSampler.Hash01(
                globalCell,
                visualSequence: 7u,
                layerSeed: BodySeed,
                channel: 0u);

            Assert.That(FireCellEmissionSampler.Hash01(
                globalCell, 7u, BodySeed, 0u), Is.EqualTo(expected));
            Assert.That(FireCellEmissionSampler.Hash01(
                new Vector3Int(-16, 3, 42), 7u, BodySeed, 0u), Is.Not.EqualTo(expected));
            Assert.That(FireCellEmissionSampler.Hash01(
                new Vector3Int(-17, 4, 42), 7u, BodySeed, 0u), Is.Not.EqualTo(expected));
            Assert.That(FireCellEmissionSampler.Hash01(
                new Vector3Int(-17, 3, 43), 7u, BodySeed, 0u), Is.Not.EqualTo(expected));
        }

        [Test]
        public void GlobalCellJitter_DoesNotDependOnChunkIterationOrdinal()
        {
            var globalCell = new Vector3Int(8, 0, -1);
            Vector3 first = FireCellEmissionSampler.CalculateJitter(
                globalCell,
                visualSequence: 19u,
                layerSeed: BodySeed,
                cellSize: 0.25f,
                verticalJitter: 0.08f);

            // 同一个 Global Cell 即使后来由另一次 Chunk 枚举访问，也必须得到相同位置抖动。
            Vector3 second = FireCellEmissionSampler.CalculateJitter(
                globalCell,
                visualSequence: 19u,
                layerSeed: BodySeed,
                cellSize: 0.25f,
                verticalJitter: 0.08f);

            Assert.That(second, Is.EqualTo(first));
        }

        [Test]
        public void ShouldEmit_HandlesFullAmountAndEmptyProbabilityBoundaries()
        {
            Assert.That(FireCellEmissionSampler.ShouldEmit(
                cellIndex: 9,
                visualSequence: 3u,
                layerSeed: BodySeed,
                amount: byte.MaxValue,
                probabilityAtFullAmount: 1f), Is.True,
                "满 Amount 的 Body 概率为 1，应在每个视觉 Tick 发射。");

            Assert.That(FireCellEmissionSampler.ShouldEmit(
                9, 3u, BodySeed, amount: 0, probabilityAtFullAmount: 1f), Is.False);
            Assert.That(FireCellEmissionSampler.ShouldEmit(
                9, 3u, BodySeed, amount: byte.MaxValue, probabilityAtFullAmount: 0f), Is.False);
        }

        [Test]
        public void SampleCap_SelectsExactlyBudgetWhenActiveCountExceedsLimit()
        {
            const int activeCount = 2050;
            const int maxSampled = 1024;
            int selected = CountSelected(activeCount, maxSampled, visualSequence: 17u);

            Assert.That(selected, Is.EqualTo(maxSampled),
                "Rational Stride 应精确使用预算，不能在刚超过上限时突然只保留约一半样本。");
        }

        [Test]
        public void SampleCap_SelectsEveryActiveCellWhenWithinBudget()
        {
            const int activeCount = 37;
            int selected = CountSelected(activeCount, maxSampled: 1024, visualSequence: 17u);

            Assert.That(selected, Is.EqualTo(activeCount));
        }

        [Test]
        public void SampleCap_VisualSequenceRotatesWhichActiveOrdinalsAreSelected()
        {
            const int activeCount = 2050;
            const int maxSampled = 1024;
            bool foundDifferentDecision = false;

            for (int ordinal = 0; ordinal < activeCount; ordinal++)
            {
                bool first = FireCellEmissionSampler.ShouldSampleActiveOrdinal(
                    ordinal, activeCount, maxSampled, visualSequence: 17u);
                bool second = FireCellEmissionSampler.ShouldSampleActiveOrdinal(
                    ordinal, activeCount, maxSampled, visualSequence: 18u);
                if (first != second)
                {
                    foundDifferentDecision = true;
                    break;
                }
            }

            Assert.That(foundDifferentDecision, Is.True,
                "超出预算时应随视觉序列轮换样本，避免某些 Fire Cell 永久没有表现机会。");
            Assert.That(CountSelected(activeCount, maxSampled, 18u), Is.EqualTo(maxSampled));
        }

        [Test]
        public void CalculateJitter_IsDeterministicAndRemainsInsideConfiguredBounds()
        {
            const float cellSize = 0.25f;
            const float verticalJitter = 0.08f;
            Vector3 jitter = FireCellEmissionSampler.CalculateJitter(
                cellIndex: 99,
                visualSequence: 8u,
                layerSeed: BodySeed,
                cellSize,
                verticalJitter);

            Assert.That(FireCellEmissionSampler.CalculateJitter(
                99, 8u, BodySeed, cellSize, verticalJitter), Is.EqualTo(jitter));
            Assert.That(Mathf.Abs(jitter.x), Is.LessThanOrEqualTo(cellSize * 0.35f + 0.0001f));
            Assert.That(Mathf.Abs(jitter.z), Is.LessThanOrEqualTo(cellSize * 0.35f + 0.0001f));
            Assert.That(jitter.y, Is.GreaterThanOrEqualTo(0f));
            Assert.That(jitter.y, Is.LessThanOrEqualTo(verticalJitter + 0.0001f));
        }

        [Test]
        public void AmountMappings_AreMonotonicAndReachConfiguredEndpoints()
        {
            Assert.That(FireCellEmissionSampler.NormalizeAmount(0), Is.Zero);
            Assert.That(FireCellEmissionSampler.NormalizeAmount(byte.MaxValue), Is.EqualTo(1f));

            float emptySize = FireCellEmissionSampler.CalculateSizeMultiplier(
                amount: 0,
                minSizeMultiplier: 0.4f,
                maxSizeMultiplier: 1.2f);
            float halfSize = FireCellEmissionSampler.CalculateSizeMultiplier(
                amount: 128,
                minSizeMultiplier: 0.4f,
                maxSizeMultiplier: 1.2f);
            float fullSize = FireCellEmissionSampler.CalculateSizeMultiplier(
                byte.MaxValue,
                minSizeMultiplier: 0.4f,
                maxSizeMultiplier: 1.2f);

            Assert.That(emptySize, Is.EqualTo(0.4f).Within(0.0001f));
            Assert.That(halfSize, Is.GreaterThan(emptySize));
            Assert.That(fullSize, Is.EqualTo(1.2f).Within(0.0001f));
        }

        private static int CountSelected(int activeCount, int maxSampled, uint visualSequence)
        {
            int selected = 0;
            for (int ordinal = 0; ordinal < activeCount; ordinal++)
            {
                if (FireCellEmissionSampler.ShouldSampleActiveOrdinal(
                    ordinal,
                    activeCount,
                    maxSampled,
                    visualSequence))
                {
                    selected++;
                }
            }

            return selected;
        }
    }
}
