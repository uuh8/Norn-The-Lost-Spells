using System;

namespace Game.ElementField
{
    public readonly struct MaterialEnvironmentSample
    {
        public readonly float Temperature, Dryness;
        public readonly bool VolatileEnabled;
        public MaterialEnvironmentSample(float temperature, float dryness, bool volatileEnabled = false)
        { Temperature=temperature; Dryness=dryness; VolatileEnabled=volatileEnabled; }
    }
    public static class MaterialConversionEvaluator
    {
        public static bool Matches(in MaterialConversionRule rule, in MaterialEnvironmentSample environment)
        {
            if (rule.Disabled || !rule.IsValid || !float.IsFinite(environment.Temperature) || !float.IsFinite(environment.Dryness)) return false;
            switch (rule.Condition)
            {
                case MaterialConversionCondition.Heated: return environment.Temperature >= rule.Threshold;
                case MaterialConversionCondition.Cooled:
                case MaterialConversionCondition.Freezing: return environment.Temperature <= rule.Threshold;
                case MaterialConversionCondition.Melting: return environment.Temperature >= rule.Threshold;
                case MaterialConversionCondition.Drying: return environment.Dryness >= rule.Threshold && environment.Temperature >= 15;
                case MaterialConversionCondition.Volatile: return environment.VolatileEnabled;
                default: return false;
            }
        }
        public static uint Batches(in MaterialConversionRule rule, uint available, double inputCredit)
        {
            if (!rule.IsValid || !double.IsFinite(inputCredit) || inputCredit <= 0) return 0;
            // 配方只处理完整整数批次，余量仍留在源 Owner；避免每 Tick 四舍五入持续漏量。
            return (uint)Math.Min(available / rule.InputUnits, Math.Min(uint.MaxValue, Math.Floor(inputCredit / rule.InputUnits)));
        }
    }
}
