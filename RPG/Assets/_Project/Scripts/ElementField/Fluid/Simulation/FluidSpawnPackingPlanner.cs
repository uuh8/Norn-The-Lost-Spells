using System;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// CPU 侧的密度一致 Spawn 结果。RestSpacing 来自单粒子体积的立方根；
    /// RequiredRadius 是规则立方点阵的包围球半径，而不是随机撒点范围。
    /// </summary>
    public readonly struct FluidSpawnPacking
    {
        public readonly Vector3 Center;
        public readonly float RestSpacing;
        public readonly float RequiredRadius;
        public readonly int LatticeSide;

        public FluidSpawnPacking(
            Vector3 center,
            float restSpacing,
            float requiredRadius,
            int latticeSide)
        {
            Center = center;
            RestSpacing = restSpacing;
            RequiredRadius = requiredRadius;
            LatticeSide = latticeSide;
        }
    }

    /// <summary>
    /// 把粒子质量、静止密度和数量转换成确定性点阵所需的几何参数。
    /// Planner 是 Pure C#：不访问 Unity Scene 或 GPU，因此边界条件可用 EditMode Test 精确锁定。
    /// </summary>
    public static class FluidSpawnPackingPlanner
    {
        public const uint MaximumPackedParticleCount = 65536u;
        private const float SurfaceSkin = 0.001f;
        private const float MinimumNormalSquared = 1e-12f;

        public static bool TryCreate(
            uint particleCount,
            float particleMass,
            float restDensity,
            float particleRadius,
            float requestedRadius,
            Vector3 hitPoint,
            Vector3 surfaceNormal,
            out FluidSpawnPacking packing)
        {
            packing = default;
            if (particleCount == 0u
                || particleCount > MaximumPackedParticleCount
                || !IsPositiveFinite(particleMass)
                || !IsPositiveFinite(restDensity)
                || !IsNonNegativeFinite(particleRadius)
                || !IsNonNegativeFinite(requestedRadius)
                || !IsFinite(hitPoint)
                || !IsFinite(surfaceNormal)
                || surfaceNormal.sqrMagnitude <= MinimumNormalSquared)
            {
                return false;
            }

            float restSpacing = Mathf.Pow(particleMass / restDensity, 1f / 3f);
            if (!IsPositiveFinite(restSpacing))
                return false;

            int latticeSide = Mathf.CeilToInt(Mathf.Pow(particleCount, 1f / 3f));
            while ((uint)(latticeSide * latticeSide * latticeSide) < particleCount)
                latticeSide++;

            float requiredRadius = 0.5f
                * (latticeSide - 1)
                * restSpacing
                * Mathf.Sqrt(3f);
            if (!IsNonNegativeFinite(requiredRadius)
                || (requestedRadius > 0f && requestedRadius < requiredRadius))
            {
                return false;
            }

            Vector3 center = hitPoint
                + surfaceNormal.normalized
                * (requiredRadius + particleRadius + SurfaceSkin);
            if (!IsFinite(center))
                return false;

            packing = new FluidSpawnPacking(
                center,
                restSpacing,
                requiredRadius,
                latticeSide);
            return true;
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsPositiveFinite(float value)
        {
            return value > 0f && IsFinite(value);
        }

        private static bool IsNonNegativeFinite(float value)
        {
            return value >= 0f && IsFinite(value);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
