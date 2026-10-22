using System;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// Bitonic Sort 一次 compare-swap Dispatch 的不可变描述。Stage 决定升降序块大小，
    /// Pass 决定当前配对距离；两者在 Runtime 初始化后的固定容量内反复复用。
    /// </summary>
    internal readonly struct FluidBitonicPass : IEquatable<FluidBitonicPass>
    {
        internal readonly int Stage;
        internal readonly int Pass;

        internal FluidBitonicPass(int stage, int pass)
        {
            Stage = stage;
            Pass = pass;
        }

        public bool Equals(FluidBitonicPass other)
        {
            return Stage == other.Stage && Pass == other.Pass;
        }

        public override bool Equals(object obj)
        {
            return obj is FluidBitonicPass other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Stage * 397) ^ Pass;
            }
        }

        public static bool operator ==(FluidBitonicPass left, FluidBitonicPass right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(FluidBitonicPass left, FluidBitonicPass right)
        {
            return !left.Equals(right);
        }
    }

    /// <summary>
    /// C#/HLSL 必须共享的 Spatial Hash 纯规则。Hash bucket 只是候选列表，不保存真实 cell；
    /// 后续 PBF 邻居消费时必须根据 PredictedPosition 再 Floor 并比较 int3，排除 hash collision。
    /// </summary>
    internal static class FluidSpatialHash
    {
        internal const uint InactiveBucket = uint.MaxValue;
        internal const int NeighborOffsetCount = 27;

        internal static Vector3Int PositionToCell(Vector3 position, float smoothingRadius)
        {
            if (!IsPositiveFinite(smoothingRadius))
                throw new ArgumentOutOfRangeException(nameof(smoothingRadius));

            // Floor 而不是 int 截断：-0.01 / h 必须进入 cell -1，才能与 HLSL floor 保持一致。
            return new Vector3Int(
                Mathf.FloorToInt(position.x / smoothingRadius),
                Mathf.FloorToInt(position.y / smoothingRadius),
                Mathf.FloorToInt(position.z / smoothingRadius));
        }

        internal static uint HashCell(Vector3Int cell, int hashTableCapacity)
        {
            if (!IsPowerOfTwo(hashTableCapacity))
                throw new ArgumentOutOfRangeException(nameof(hashTableCapacity));

            unchecked
            {
                // 显式转 uint 保留负 int 的二进制补码；HLSL 端用 asuint 得到同一 bit pattern。
                uint hash = (uint)cell.x * 73856093u;
                hash ^= (uint)cell.y * 19349663u;
                hash ^= (uint)cell.z * 83492791u;
                return hash & ((uint)hashTableCapacity - 1u);
            }
        }

        internal static Vector3Int GetNeighborOffset(int index)
        {
            if ((uint)index >= NeighborOffsetCount)
                throw new ArgumentOutOfRangeException(nameof(index));

            // 3 x 3 x 3 的线性索引不缓存数组，避免把可由整数运算得到的 27 个值扩散到热路径状态。
            int x = index % 3 - 1;
            int y = (index / 3) % 3 - 1;
            int z = index / 9 - 1;
            return new Vector3Int(x, y, z);
        }

        internal static bool IsCandidateInCell(
            Vector3 candidatePredictedPosition,
            Vector3Int targetCell,
            float smoothingRadius)
        {
            return PositionToCell(candidatePredictedPosition, smoothingRadius) == targetCell;
        }

        internal static int CompareEntries(in FluidGpuUInt2 left, in FluidGpuUInt2 right)
        {
            if (left.X != right.X)
                return left.X < right.X ? -1 : 1;
            if (left.Y != right.Y)
                return left.Y < right.Y ? -1 : 1;
            return 0;
        }

        internal static int GetBitonicPassCount(int particleCapacity)
        {
            RequirePowerOfTwoParticleCapacity(particleCapacity);

            int bitCount = 0;
            for (int capacity = particleCapacity; capacity > 1; capacity >>= 1)
                bitCount++;
            return bitCount * (bitCount + 1) / 2;
        }

        internal static bool TryGetBitonicPass(
            int particleCapacity,
            int passIndex,
            out FluidBitonicPass bitonicPass)
        {
            RequirePowerOfTwoParticleCapacity(particleCapacity);
            if (particleCapacity == 1 || passIndex < 0)
            {
                bitonicPass = default;
                return false;
            }

            int currentIndex = 0;
            for (int stage = 2; ; stage <<= 1)
            {
                for (int pass = stage >> 1; pass > 0; pass >>= 1)
                {
                    if (currentIndex == passIndex)
                    {
                        bitonicPass = new FluidBitonicPass(stage, pass);
                        return true;
                    }

                    currentIndex++;
                }

                if (stage == particleCapacity)
                    break;
            }

            bitonicPass = default;
            return false;
        }

        internal static bool IsValidBitonicPass(int particleCapacity, in FluidBitonicPass bitonicPass)
        {
            if (!IsPowerOfTwo(particleCapacity)
                || !IsPowerOfTwo(bitonicPass.Stage)
                || !IsPowerOfTwo(bitonicPass.Pass))
            {
                return false;
            }

            return bitonicPass.Stage >= 2
                && bitonicPass.Stage <= particleCapacity
                && bitonicPass.Pass > 0
                && bitonicPass.Pass < bitonicPass.Stage;
        }

        internal static bool IsPowerOfTwo(int value)
        {
            return value > 0 && (value & (value - 1)) == 0;
        }

        private static void RequirePowerOfTwoParticleCapacity(int particleCapacity)
        {
            if (!IsPowerOfTwo(particleCapacity))
                throw new ArgumentOutOfRangeException(nameof(particleCapacity));
        }

        private static bool IsPositiveFinite(float value)
        {
            return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
