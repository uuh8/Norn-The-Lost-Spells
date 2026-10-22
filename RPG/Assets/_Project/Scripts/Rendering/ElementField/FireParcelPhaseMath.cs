using System;
using UnityEngine;

namespace Game.Rendering
{
    public readonly struct FireParcelPhaseSettings
    {
        public const float GasFadeOutFraction = 0.3f;

        public readonly float LiquidLikeHoldSeconds;
        public readonly float SurfaceToGasBlendSeconds;
        public readonly float GasLifetimeSeconds;
        public readonly float InitialRadius;
        public readonly float FinalRadius;

        public float GasFullVisibilityStartSeconds =>
            LiquidLikeHoldSeconds + SurfaceToGasBlendSeconds;
        public float TotalLifetimeSeconds =>
            GasFullVisibilityStartSeconds + GasLifetimeSeconds;

        public FireParcelPhaseSettings(
            float liquidLikeHoldSeconds,
            float surfaceToGasBlendSeconds,
            float gasLifetimeSeconds,
            float initialRadius,
            float finalRadius)
        {
            if (!IsPositiveFinite(liquidLikeHoldSeconds))
                throw new ArgumentOutOfRangeException(nameof(liquidLikeHoldSeconds));
            if (!IsPositiveFinite(surfaceToGasBlendSeconds))
                throw new ArgumentOutOfRangeException(nameof(surfaceToGasBlendSeconds));
            if (!IsPositiveFinite(gasLifetimeSeconds))
                throw new ArgumentOutOfRangeException(nameof(gasLifetimeSeconds));
            if (!IsPositiveFinite(initialRadius) || !IsPositiveFinite(finalRadius)
                || finalRadius < initialRadius)
                throw new ArgumentOutOfRangeException(nameof(finalRadius));
            LiquidLikeHoldSeconds = liquidLikeHoldSeconds;
            SurfaceToGasBlendSeconds = surfaceToGasBlendSeconds;
            GasLifetimeSeconds = gasLifetimeSeconds;
            InitialRadius = initialRadius;
            FinalRadius = finalRadius;
        }

        private static bool IsPositiveFinite(float value) =>
            value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public readonly struct FireParcelPhaseSample
    {
        public readonly float SurfaceWeight;
        public readonly float GasWeight;
        public readonly float Radius;

        public FireParcelPhaseSample(float surfaceWeight, float gasWeight, float radius)
        {
            SurfaceWeight = surfaceWeight;
            GasWeight = gasWeight;
            Radius = radius;
        }
    }

    /// <summary>
    /// Fire 的“液团→气团”只是一条 Presentation 生命周期曲线。这里使用绝对秒而不是归一化比例：
    /// 延长 Liquid-like Hold 只会把 Gas 整段向后平移，不会压缩 Gas 自己的可见时间。
    /// 交叉段使用 Smoothstep，因此权重的一阶变化不会在阶段边界突然跳变。
    /// </summary>
    public static class FireParcelPhaseMath
    {
        public static FireParcelPhaseSample Evaluate(
            float ageSeconds,
            in FireParcelPhaseSettings settings)
        {
            float age = Mathf.Clamp(ageSeconds, 0f, settings.TotalLifetimeSeconds);
            float gasFullStart = settings.GasFullVisibilityStartSeconds;
            float transition = Smoothstep(
                settings.LiquidLikeHoldSeconds,
                gasFullStart,
                age);
            float gasFadeStart = gasFullStart
                + settings.GasLifetimeSeconds * (1f - FireParcelPhaseSettings.GasFadeOutFraction);
            float gasFade = 1f - Smoothstep(
                gasFadeStart,
                settings.TotalLifetimeSeconds,
                age);
            float surface = 1f - transition;
            float gas = transition * gasFade;
            float normalizedAge = age / settings.TotalLifetimeSeconds;
            float radius = Mathf.Lerp(settings.InitialRadius, settings.FinalRadius, normalizedAge);
            return new FireParcelPhaseSample(surface, gas, radius);
        }

        private static float Smoothstep(float min, float max, float value)
        {
            float t = Mathf.Clamp01((value - min) / Mathf.Max(max - min, 0.000001f));
            return t * t * (3f - 2f * t);
        }
    }
}
