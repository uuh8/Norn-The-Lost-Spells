using NUnit.Framework;

namespace Game.Character.Tests
{
    /// <summary>
    /// 锁定法师 Enemy 的程序选择契约：普通怪只有一套时稳定选择，
    /// 精英怪有多套时使用外部随机样本，但不能连续重复上一套。
    /// </summary>
    public sealed class EnemySpellProgramSelectorTests
    {
        [Test]
        public void SelectNextIndex_NoProgram_ReturnsMinusOne()
        {
            Assert.That(
                EnemySpellProgramSelector.SelectNextIndex(0, -1, 0.5f),
                Is.EqualTo(-1));
        }

        [TestCase(-1f)]
        [TestCase(0f)]
        [TestCase(0.5f)]
        [TestCase(1f)]
        [TestCase(2f)]
        public void SelectNextIndex_SingleProgram_AlwaysReturnsZero(float sample)
        {
            Assert.That(
                EnemySpellProgramSelector.SelectNextIndex(1, 0, sample),
                Is.Zero);
        }

        [TestCase(0, 0f, 1)]
        [TestCase(0, 0.9999f, 1)]
        [TestCase(1, 0f, 0)]
        [TestCase(1, 0.9999f, 0)]
        public void SelectNextIndex_TwoPrograms_NeverRepeats(
            int previous,
            float sample,
            int expected)
        {
            Assert.That(
                EnemySpellProgramSelector.SelectNextIndex(2, previous, sample),
                Is.EqualTo(expected));
        }

        [Test]
        public void SelectNextIndex_ThreePrograms_CoversBothNonPreviousCandidates()
        {
            int low = EnemySpellProgramSelector.SelectNextIndex(3, 1, 0f);
            int high = EnemySpellProgramSelector.SelectNextIndex(3, 1, 0.9999f);

            Assert.That(low, Is.EqualTo(0));
            Assert.That(high, Is.EqualTo(2));
        }

        [TestCase(-1)]
        [TestCase(3)]
        public void SelectNextIndex_InvalidPrevious_UsesAllCandidates(int previous)
        {
            Assert.That(
                EnemySpellProgramSelector.SelectNextIndex(3, previous, 0f),
                Is.EqualTo(0));
            Assert.That(
                EnemySpellProgramSelector.SelectNextIndex(3, previous, 0.9999f),
                Is.EqualTo(2));
        }

        [TestCase(float.NegativeInfinity, 0)]
        [TestCase(-1f, 0)]
        [TestCase(float.NaN, 0)]
        [TestCase(1f, 2)]
        [TestCase(float.PositiveInfinity, 2)]
        public void SelectNextIndex_ClampsInvalidSample(float sample, int expected)
        {
            Assert.That(
                EnemySpellProgramSelector.SelectNextIndex(3, -1, sample),
                Is.EqualTo(expected));
        }
    }
}
