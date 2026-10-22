using NUnit.Framework;
using Game.Combat;

namespace Game.Combat.Tests
{
    public class ComboResolverTests
    {
        private const float Start = 0.40f;
        private const float End = 0.70f;
        private const float EndThreshold = 0.85f;

        [Test]
        public void InsideWindow_WithBuffer_HasNext_Advances()
        {
            ComboDecision d = ComboResolver.Resolve(0, 3, 0.50f, true, Start, End, EndThreshold);
            Assert.AreEqual(ComboDecision.Advance, d);
        }

        [Test]
        public void InsideWindow_NoBuffer_Continues()
        {
            ComboDecision d = ComboResolver.Resolve(0, 3, 0.50f, false, Start, End, EndThreshold);
            Assert.AreEqual(ComboDecision.Continue, d);
        }

        [Test]
        public void InsideWindow_WithBuffer_LastSegment_DoesNotAdvance()
        {
            // comboIndex 2 of 3 → no next segment
            ComboDecision d = ComboResolver.Resolve(2, 3, 0.50f, true, Start, End, EndThreshold);
            Assert.AreEqual(ComboDecision.Continue, d);
        }

        [Test]
        public void BeforeWindow_WithBuffer_Continues()
        {
            ComboDecision d = ComboResolver.Resolve(0, 3, 0.20f, true, Start, End, EndThreshold);
            Assert.AreEqual(ComboDecision.Continue, d);
        }

        [Test]
        public void AfterWindow_BeforeEndThreshold_WithBuffer_Continues()
        {
            // 0.75 is past End(0.70) but before EndThreshold(0.85): window missed, not yet ending
            ComboDecision d = ComboResolver.Resolve(0, 3, 0.75f, true, Start, End, EndThreshold);
            Assert.AreEqual(ComboDecision.Continue, d);
        }

        [Test]
        public void ReachedEndThreshold_NoBuffer_Ends()
        {
            ComboDecision d = ComboResolver.Resolve(0, 3, 0.90f, false, Start, End, EndThreshold);
            Assert.AreEqual(ComboDecision.End, d);
        }

        [Test]
        public void LastSegment_ReachedEndThreshold_Ends()
        {
            ComboDecision d = ComboResolver.Resolve(2, 3, 0.90f, false, Start, End, EndThreshold);
            Assert.AreEqual(ComboDecision.End, d);
        }

        [Test]
        public void AdvancePriorityOverEnd_WhenWindowOverlapsThreshold()
        {
            // window end (0.95) beyond threshold; inside window + buffer + next → Advance wins over End
            ComboDecision d = ComboResolver.Resolve(0, 3, 0.90f, true, 0.40f, 0.95f, EndThreshold);
            Assert.AreEqual(ComboDecision.Advance, d);
        }
    }
}
