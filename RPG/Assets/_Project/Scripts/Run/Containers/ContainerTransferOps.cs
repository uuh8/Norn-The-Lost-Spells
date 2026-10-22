using Game.Core;

namespace Game.Run
{
    public static class ContainerTransferOps
    {
        public static ulong SplitBudget(MaterialAmount[] sample, int count, ulong budget, MaterialAmount[] output, ulong[] remainders)
            => MaterialAmountOps.SplitBudget(sample, count, budget, output, remainders);
    }
    public sealed class CollectionRateAccumulator
    {
        private double _fraction;
        public void Reset() => _fraction = 0;
        public ulong Advance(double rate, double deltaTime)
        {
            if (double.IsNaN(rate) || double.IsInfinity(rate) || double.IsNaN(deltaTime) || double.IsInfinity(deltaTime)
                || rate <= 0 || deltaTime <= 0 || rate * deltaTime > uint.MaxValue) { Reset(); return 0; }
            double exact = _fraction + rate * deltaTime; ulong units = (ulong)exact; _fraction = exact - units; return units;
        }
    }
}
