namespace Game.Multiplayer
{
    /// <summary>有界补步预算；冻结期间清债，不能在业务恢复后补跑整段失联时间。</summary>
    public sealed class FixedStepClock
    {
        public const float DeltaTime = 1f / 30f;
        private double _accumulator;
        public long DroppedSteps { get; private set; }
        public int Advance(double elapsed, bool enabled)
        {
            if (!enabled || double.IsNaN(elapsed) || double.IsInfinity(elapsed)) { Reset(); return 0; }
            _accumulator += System.Math.Max(0, elapsed);
            int due = (int)System.Math.Min(int.MaxValue, System.Math.Floor((_accumulator + 1e-8) / DeltaTime));
            int steps = System.Math.Min(4, due);
            if (due > steps) DroppedSteps += due - steps;
            _accumulator -= due * (double)DeltaTime;
            if (_accumulator < 0) _accumulator = 0;
            return steps;
        }
        public void Reset() => _accumulator = 0;
    }
}
