using System;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// CPU/GPU 共享的 PBF Kernel 数学参考。它不持有 Unity 场景状态，目的不是在 CPU 解流体，
    /// 而是把 HLSL 的常数、支持域和退化输入规则锁成可由 EditMode 测试验证的 Contract。
    /// </summary>
    internal static class PbfKernelMath
    {
        private const float Pi = Mathf.PI;
        private const float DirectionEpsilonSquared = 1e-12f;
        private const float ArtificialPressureRatio = 0.3f;
        private const int ArtificialPressureExponent = 4;

        /// <summary>
        /// 3D Poly6：315/(64*pi*h^9) * (h^2-r^2)^3。密度只收集支持半径 h 内的贡献，
        /// 支持域外直接为零可避免 hash collision 候选误产生物理影响。
        /// </summary>
        internal static float Poly6(float distance, float smoothingRadius)
        {
            RequirePositiveFinite(smoothingRadius, nameof(smoothingRadius));
            if (distance < 0f || float.IsNaN(distance) || float.IsInfinity(distance))
                throw new ArgumentOutOfRangeException(nameof(distance));
            if (distance >= smoothingRadius)
                return 0f;

            float h2 = smoothingRadius * smoothingRadius;
            float difference = h2 - distance * distance;
            float h9 = Mathf.Pow(smoothingRadius, 9f);
            return 315f / (64f * Pi * h9) * difference * difference * difference;
        }

        /// <summary>
        /// 3D Spiky Gradient：-45/(pi*h^6) * (h-r)^2 * rHat。
        /// r 近零时方向未定义，故按零向量处理；GPU 使用同一阈值以阻断重合粒子的 NaN。
        /// </summary>
        internal static Vector3 SpikyGradient(Vector3 offset, float smoothingRadius)
        {
            RequirePositiveFinite(smoothingRadius, nameof(smoothingRadius));
            float distanceSquared = offset.sqrMagnitude;
            if (!IsFinite(offset) || distanceSquared <= DirectionEpsilonSquared)
                return Vector3.zero;

            float distance = Mathf.Sqrt(distanceSquared);
            if (distance >= smoothingRadius)
                return Vector3.zero;

            float coefficient = -45f / (Pi * Mathf.Pow(smoothingRadius, 6f));
            return coefficient * Mathf.Pow(smoothingRadius - distance, 2f) * (offset / distance);
        }

        /// <summary>
        /// lambda = -C / (sumGradSquared + epsilon)，其中 C=max(density/restDensity-1, 0)。
        /// 这是单向 Incompressibility Constraint：只抵抗压缩，不用负压力强迫 Free Surface
        /// 补齐不存在的外侧邻居；液体聚合由独立且有界的 Cohesion Pass 负责。
        /// </summary>
        internal static float CalculateLambda(
            float density,
            float restDensity,
            float gradientSumSquared,
            float lambdaEpsilon)
        {
            RequireFiniteNonNegative(density, nameof(density));
            RequirePositiveFinite(restDensity, nameof(restDensity));
            RequireFiniteNonNegative(gradientSumSquared, nameof(gradientSumSquared));
            RequirePositiveFinite(lambdaEpsilon, nameof(lambdaEpsilon));

            float constraint = Mathf.Max(density / restDensity - 1f, 0f);
            return -constraint / (gradientSumSquared + lambdaEpsilon);
        }

        /// <summary>
        /// 计算两个粒子间的一项 Delta Position，不写任何粒子位置。GPU 的每线程累积完成后再由
        /// Apply Kernel 统一提交，避免同一 Iteration 的邻居读取到已经被其他线程改写的预测位置。
        /// </summary>
        internal static Vector3 CalculatePairPositionCorrection(
            Vector3 offsetToNeighbor,
            float lambdaI,
            float lambdaJ,
            float artificialPressure,
            float smoothingRadius,
            float restDensity)
        {
            RequireFinite(lambdaI, nameof(lambdaI));
            RequireFinite(lambdaJ, nameof(lambdaJ));
            RequireFiniteNonNegative(artificialPressure, nameof(artificialPressure));
            RequirePositiveFinite(smoothingRadius, nameof(smoothingRadius));
            RequirePositiveFinite(restDensity, nameof(restDensity));

            Vector3 gradient = SpikyGradient(offsetToNeighbor, smoothingRadius);
            if (gradient == Vector3.zero)
                return Vector3.zero;

            float pressure = CalculateArtificialPressure(
                offsetToNeighbor.magnitude,
                artificialPressure,
                smoothingRadius);
            return (lambdaI + lambdaJ + pressure) * gradient / restDensity;
        }

        /// <summary>
        /// 欠密度粒子的 Tensile Pair Correction。SpikyGradient 在本项目符号约定下指向邻居，
        /// 因而正的 tensile pressure 会产生对称吸引；密度达到 RestDensity 后严格为零，
        /// 不会改写单向 Incompressibility 的压缩结果。
        /// </summary>
        internal static Vector3 CalculateTensilePairPositionCorrection(
            Vector3 offsetToNeighbor,
            float densityI,
            float densityJ,
            float restDensity,
            float tensileStrength,
            float smoothingRadius)
        {
            RequireFiniteNonNegative(densityI, nameof(densityI));
            RequireFiniteNonNegative(densityJ, nameof(densityJ));
            RequirePositiveFinite(restDensity, nameof(restDensity));
            RequireFiniteNonNegative(tensileStrength, nameof(tensileStrength));
            RequirePositiveFinite(smoothingRadius, nameof(smoothingRadius));

            float deficitI = Mathf.Clamp01(1f - densityI / restDensity);
            float deficitJ = Mathf.Clamp01(1f - densityJ / restDensity);
            float tensilePressure = tensileStrength * 0.5f * (deficitI + deficitJ);
            return tensilePressure * SpikyGradient(offsetToNeighbor, smoothingRadius) / restDensity;
        }

        /// <summary>
        /// 对整圈邻居累积后的 Tensile 向量做一次硬上限，而不是逐 Pair 截断；
        /// 这样对称邻域仍能先相消，同时任何稀疏边缘都不能注入超预算位移。
        /// </summary>
        internal static Vector3 ClampTensilePositionCorrection(
            Vector3 correction,
            float maximumCorrection)
        {
            if (!IsFinite(correction))
                throw new ArgumentOutOfRangeException(nameof(correction));
            RequirePositiveFinite(maximumCorrection, nameof(maximumCorrection));

            float length = correction.magnitude;
            return length > maximumCorrection && length > 0f
                ? correction * (maximumCorrection / length)
                : correction;
        }

        /// <summary>
        /// Cohesion 的平滑 Compact-support 权重。Rest Distance 内不吸引，避免和密度约束争夺静止间距；
        /// 到 h 时严格归零，因此粒子被强冲击拉出邻域后会自然断裂，不存在全局“橡皮筋”。
        /// </summary>
        internal static float CohesionWeight(
            float distance,
            float restDistance,
            float smoothingRadius)
        {
            if (!IsFinite(distance)
                || !IsFinite(restDistance)
                || !IsFinite(smoothingRadius)
                || restDistance < 0f
                || smoothingRadius <= restDistance
                || distance <= restDistance
                || distance >= smoothingRadius)
            {
                return 0f;
            }

            float q = (distance - restDistance) / (smoothingRadius - restDistance);
            return 4f * q * (1f - q);
        }

        /// <summary>
        /// sCorr=-k*(W(r)/max(W(0.3h),epsilon))^4；它被限制在 [-k,0]，用近距离排斥抑制 clumping，
        /// 又不允许分母极小或超支持域候选将修正放大为无界值。
        /// </summary>
        internal static float CalculateArtificialPressure(
            float distance,
            float strength,
            float smoothingRadius)
        {
            RequireFiniteNonNegative(distance, nameof(distance));
            RequireFiniteNonNegative(strength, nameof(strength));
            RequirePositiveFinite(smoothingRadius, nameof(smoothingRadius));
            if (distance >= smoothingRadius || strength == 0f)
                return 0f;

            float denominator = Mathf.Max(
                Poly6(ArtificialPressureRatio * smoothingRadius, smoothingRadius),
                1e-6f);
            float ratio = Mathf.Clamp01(Poly6(distance, smoothingRadius) / denominator);
            return -strength * Mathf.Pow(ratio, ArtificialPressureExponent);
        }

        private static void RequirePositiveFinite(float value, string parameterName)
        {
            if (value <= 0f || float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentOutOfRangeException(parameterName);
        }

        private static void RequireFiniteNonNegative(float value, string parameterName)
        {
            if (value < 0f || float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentOutOfRangeException(parameterName);
        }

        private static void RequireFinite(float value, string parameterName)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentOutOfRangeException(parameterName);
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
