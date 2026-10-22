using System.Runtime.InteropServices;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>GPU 归档 Readback 的临时粒子级记录；32-byte 布局必须与 HLSL 完全一致。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct FluidGpuArchiveSample
    {
        public const int Stride = 32;

        public readonly Vector3 Position;
        public readonly uint MaterialId;
        public readonly Vector3 Velocity;
        public readonly uint Flags;

        public FluidGpuArchiveSample(Vector3 position, MaterialId material, Vector3 velocity, uint flags)
        {
            Position = position;
            MaterialId = (uint)material;
            Velocity = velocity;
            Flags = flags;
        }
    }

    /// <summary>
    /// RAM 中长期保存的 Cell 粒度流体真值。Amount 保持 uint，不能使用 Gameplay Query 的 byte 饱和值。
    /// </summary>
    public readonly struct FluidArchiveCellRecord
    {
        public readonly ElementChunkKey Chunk;
        public readonly int LocalCellIndex;
        public readonly MaterialId Material;
        public readonly uint Amount;
        public readonly Vector3 AverageVelocity;

        public FluidArchiveCellRecord(
            ElementChunkKey chunk,
            int localCellIndex,
            MaterialId material,
            uint amount,
            Vector3 averageVelocity)
        {
            Chunk = chunk;
            LocalCellIndex = localCellIndex;
            Material = material;
            Amount = amount;
            AverageVelocity = averageVelocity;
        }
    }

    public readonly struct FluidArchiveBuildContext
    {
        public readonly Vector3 WorldOrigin;
        public readonly float CellSize;
        public readonly int ChunkSize;
        public readonly LiquidMaterialAmountScaleSnapshot AmountScales;

        public FluidArchiveBuildContext(
            Vector3 worldOrigin,
            float cellSize,
            int chunkSize,
            LiquidMaterialAmountScaleSnapshot amountScales)
        {
            WorldOrigin = worldOrigin;
            CellSize = cellSize;
            ChunkSize = chunkSize;
            AmountScales = amountScales;
        }
    }
}
