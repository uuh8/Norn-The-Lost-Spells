using System;
using System.Runtime.InteropServices;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>CPU 上传到 Restore Stage 的 32-byte 粒子快照；布局必须与 HLSL 完全一致。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct FluidGpuRestoreParticle
    {
        public const int Stride = 32;
        public readonly Vector3 Position;
        public readonly uint MaterialId;
        public readonly Vector3 Velocity;
        public readonly uint TransactionId;

        public FluidGpuRestoreParticle(
            Vector3 position, MaterialId material, Vector3 velocity, uint transactionId)
        {
            Position = position;
            MaterialId = (uint)material;
            Velocity = velocity;
            TransactionId = transactionId;
        }
    }

    /// <summary>GPU 单线程 Reservation 返回的 16-byte 事务结果。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct FluidGpuTransferStatus
    {
        public const int Stride = 16;
        public readonly uint Succeeded;
        public readonly uint ReservedBase;
        public readonly uint ParticleCount;
        public readonly uint TransactionId;

        public FluidGpuTransferStatus(
            uint succeeded, uint reservedBase, uint particleCount, uint transactionId)
        {
            Succeeded = succeeded;
            ReservedBase = reservedBase;
            ParticleCount = particleCount;
            TransactionId = transactionId;
        }
    }

    public static class FluidChunkRestorePlanner
    {
        public static int Expand(
            in FluidArchiveCellRecord record,
            uint amountUnitsPerParticle,
            float cellSize,
            uint snapshotVersion,
            FluidGpuRestoreParticle[] destination,
            int destinationOffset)
        {
            return Expand(in record, amountUnitsPerParticle, Vector3.zero, cellSize, 8,
                snapshotVersion, snapshotVersion, destination, destinationOffset);
        }

        public static int Expand(
            in FluidArchiveCellRecord record,
            uint amountUnitsPerParticle,
            Vector3 worldOrigin,
            float cellSize,
            int chunkSize,
            uint snapshotVersion,
            uint transactionId,
            FluidGpuRestoreParticle[] destination,
            int destinationOffset)
        {
            return ExpandRange(in record, amountUnitsPerParticle, worldOrigin, cellSize,
                chunkSize, snapshotVersion, transactionId, 0, int.MaxValue,
                destination, destinationOffset);
        }

        public static int ExpandRange(
            in FluidArchiveCellRecord record,
            uint amountUnitsPerParticle,
            Vector3 worldOrigin,
            float cellSize,
            int chunkSize,
            uint snapshotVersion,
            uint transactionId,
            int sourceParticleOffset,
            int maximumParticleCount,
            FluidGpuRestoreParticle[] destination,
            int destinationOffset)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (amountUnitsPerParticle == 0u || record.Amount == 0u
                || record.Amount % amountUnitsPerParticle != 0u)
                return -1;
            if (!(cellSize > 0f) || !float.IsFinite(cellSize) || chunkSize <= 0
                || record.Material == MaterialId.Empty || transactionId == 0u)
                return -1;

            uint unsignedCount = record.Amount / amountUnitsPerParticle;
            if (unsignedCount > int.MaxValue) return -1;
            int totalCount = (int)unsignedCount;
            if (sourceParticleOffset < 0 || sourceParticleOffset > totalCount
                || maximumParticleCount <= 0)
                return -1;
            int count = Math.Min(maximumParticleCount, totalCount - sourceParticleOffset);
            if (destinationOffset < 0 || destinationOffset > destination.Length - count)
                return -1;

            int cellsPerLayer;
            int localZ;
            try
            {
                cellsPerLayer = checked(chunkSize * chunkSize);
                if ((uint)record.LocalCellIndex >= (uint)checked(cellsPerLayer * chunkSize))
                    return -1;
                localZ = record.LocalCellIndex / cellsPerLayer;
            }
            catch (OverflowException) { return -1; }

            int remainder = record.LocalCellIndex - localZ * cellsPerLayer;
            int localY = remainder / chunkSize;
            int localX = remainder - localY * chunkSize;
            double globalX = (long)record.Chunk.X * chunkSize + localX;
            double globalY = (long)record.Chunk.Y * chunkSize + localY;
            double globalZ = (long)record.Chunk.Z * chunkSize + localZ;
            var cellMin = new Vector3(
                (float)(worldOrigin.x + globalX * cellSize),
                (float)(worldOrigin.y + globalY * cellSize),
                (float)(worldOrigin.z + globalZ * cellSize));

            int side = 1;
            while ((long)side * side * side < totalCount) side++;
            float spacing = cellSize / side;
            for (int local = 0; local < count; local++)
            {
                int i = sourceParticleOffset + local;
                int x = i % side;
                int y = (i / side) % side;
                int z = i / (side * side);
                uint seed = Seed(in record, snapshotVersion, i);
                Vector3 jitter = new Vector3(
                    SignedUnit(Hash(seed ^ 0xA511E9B3u)),
                    SignedUnit(Hash(seed ^ 0x63D83595u)),
                    SignedUnit(Hash(seed ^ 0xB8D3C5A7u))) * (spacing * .15f);
                Vector3 position = cellMin
                    + new Vector3((x + .5f) * spacing, (y + .5f) * spacing, (z + .5f) * spacing)
                    + jitter;
                destination[destinationOffset + local] = new FluidGpuRestoreParticle(
                    position, record.Material, record.AverageVelocity, transactionId);
            }
            return count;
        }

        private static uint Seed(in FluidArchiveCellRecord record, uint version, int particleIndex)
        {
            unchecked
            {
                uint hash = 2166136261u;
                hash = (hash ^ (uint)record.Chunk.X) * 16777619u;
                hash = (hash ^ (uint)record.Chunk.Y) * 16777619u;
                hash = (hash ^ (uint)record.Chunk.Z) * 16777619u;
                hash = (hash ^ (uint)record.LocalCellIndex) * 16777619u;
                hash = (hash ^ (byte)record.Material) * 16777619u;
                hash = (hash ^ version) * 16777619u;
                return (hash ^ (uint)particleIndex) * 16777619u;
            }
        }

        private static uint Hash(uint value)
        {
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            value *= 0x846CA68Bu;
            return value ^ (value >> 16);
        }

        private static float SignedUnit(uint value) =>
            ((value & 0x00FFFFFFu) * (1f / 16777215f)) * 2f - 1f;
    }
}
