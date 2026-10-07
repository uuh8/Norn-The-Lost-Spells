using System;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 包含 Min/Max Chunk Key 的闭区间。Chunk 身份使用整数，因此闭区间能精确表达 Warm Region；
    /// 转成世界 Bounds 时才把 Max 加一，得到 Unity AABB 的 exclusive maximum。
    /// </summary>
    public readonly struct FluidChunkRegion
    {
        public readonly ElementChunkKey Min;
        public readonly ElementChunkKey Max;

        public FluidChunkRegion(ElementChunkKey min, ElementChunkKey max)
        {
            if (min.X > max.X || min.Y > max.Y || min.Z > max.Z)
                throw new ArgumentException("Fluid Chunk Region minimum must not exceed maximum.");
            Min = min;
            Max = max;
        }

        public bool Contains(ElementChunkKey chunk)
        {
            return chunk.X >= Min.X && chunk.X <= Max.X
                && chunk.Y >= Min.Y && chunk.Y <= Max.Y
                && chunk.Z >= Min.Z && chunk.Z <= Max.Z;
        }

        public Bounds ToWorldBounds(Vector3 worldOrigin, float cellSize, int chunkSize)
        {
            if (cellSize <= 0f || float.IsNaN(cellSize) || float.IsInfinity(cellSize))
                throw new ArgumentOutOfRangeException(nameof(cellSize));
            if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));

            // 先转 double/long，避免 Chunk 坐标乘 ChunkSize 时发生 int 回绕。
            double chunkWorldSize = (double)cellSize * chunkSize;
            Vector3 minimum = new Vector3(
                (float)(worldOrigin.x + (long)Min.X * chunkWorldSize),
                (float)(worldOrigin.y + (long)Min.Y * chunkWorldSize),
                (float)(worldOrigin.z + (long)Min.Z * chunkWorldSize));
            Vector3 maximum = new Vector3(
                (float)(worldOrigin.x + ((long)Max.X + 1L) * chunkWorldSize),
                (float)(worldOrigin.y + ((long)Max.Y + 1L) * chunkWorldSize),
                (float)(worldOrigin.z + ((long)Max.Z + 1L) * chunkWorldSize));
            var bounds = new Bounds();
            bounds.SetMinMax(minimum, maximum);
            return bounds;
        }
    }

    public static class FluidChunkStreamingPlanner
    {
        public static double ArchiveRetryDelay(bool hasPendingWrites)
        {
            // Backpressure 已经影响玩家写入时，用更快但仍有上限的 10 Hz 事务节奏释放槽位；
            // 无积压时维持 2 Hz，避免稳定世界为诊断和归档持续发起 Readback。
            return hasPendingWrites ? .1d : .5d;
        }

        public static bool ShouldIncludeSleepingRetained(
            bool localDormancyEnabled,
            uint freeParticles,
            int gameplayReserve,
            bool hasValidActivitySnapshot,
            uint sleepingParticles)
        {
            if (!localDormancyEnabled || gameplayReserve < 0 || !hasValidActivitySnapshot)
                return false;

            // 是否为完整 Sleeping Chunk 由归档读回后的事务过滤器最终确认；这里用 Counter
            // 只避免在没有候选时发起昂贵 Readback，不能承担正确性判断。
            return sleepingParticles > 0u && freeParticles <= (uint)gameplayReserve;
        }

        public static FluidChunkRegion BuildWarmRegion(
            ElementChunkKey interestChunk,
            int activeRadiusXZ,
            int activeRadiusY,
            int warmPadding)
        {
            if (activeRadiusXZ < 0) throw new ArgumentOutOfRangeException(nameof(activeRadiusXZ));
            if (activeRadiusY < 0) throw new ArgumentOutOfRangeException(nameof(activeRadiusY));
            if (warmPadding < 0) throw new ArgumentOutOfRangeException(nameof(warmPadding));

            int xz = checked(activeRadiusXZ + warmPadding);
            int y = checked(activeRadiusY + warmPadding);
            return new FluidChunkRegion(
                new ElementChunkKey(
                    checked(interestChunk.X - xz),
                    checked(interestChunk.Y - y),
                    checked(interestChunk.Z - xz)),
                new ElementChunkKey(
                    checked(interestChunk.X + xz),
                    checked(interestChunk.Y + y),
                    checked(interestChunk.Z + xz)));
        }

        public static bool ShouldStartArchive(
            bool interestChanged,
            float secondsSinceInterestChange,
            float archiveGraceSeconds,
            uint freeParticles,
            int gameplayReserve,
            bool transferInFlight)
        {
            if (transferInFlight) return false;
            if (secondsSinceInterestChange < 0f || float.IsNaN(secondsSinceInterestChange)) return false;
            if (archiveGraceSeconds < 0f || float.IsNaN(archiveGraceSeconds)) return false;
            if (gameplayReserve < 0) return false;

            // 容量压力允许跳过 Grace，但不能绕过“单事务”约束。
            bool underPressure = freeParticles <= (uint)gameplayReserve;
            return underPressure
                || (interestChanged && secondsSinceInterestChange >= archiveGraceSeconds);
        }
    }
}
