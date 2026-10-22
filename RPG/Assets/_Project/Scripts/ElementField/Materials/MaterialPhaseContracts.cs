using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>固态／气态的写入契约；成功只表示有界队列接纳，真实量由所属 Runtime 持有。</summary>
    public interface IMaterialDepositSink
    {
        bool IsMaterialInitialized { get; }
        bool TryEnqueueDeposit(in ElementWriteRequest request);
    }

    /// <summary>GMU 真数量与 byte 状态投影分开，避免相变／记账把 1024 截成 255。</summary>
    public interface IMaterialQuantityReadOnly
    {
        bool TryGetQuantity(Vector3Int cell, MaterialId material, out uint quantity);
    }

    public readonly struct MaterialVolumeCell
    {
        public readonly Vector3Int Cell;
        public readonly MaterialId Material;
        public readonly uint Quantity;
        public MaterialVolumeCell(Vector3Int cell, MaterialId material, uint quantity)
        { Cell = cell; Material = material; Quantity = quantity; }
    }

    /// <summary>Renderer 只能按索引读已发布的数量与局部版本；不会控制材料存在性。</summary>
    public interface IMaterialVolumeReadOnly : IMaterialQuantityReadOnly, IMaterialAmountReadOnly
    {
        int ResidentCellCount { get; }
        uint Version { get; }
        uint CellCapacity { get; }
        ulong TotalQuantity { get; }
        bool TryGetCell(int index, out MaterialVolumeCell cell);
    }
    public interface IFixedSolidSurfaceReadOnly : IMaterialVolumeReadOnly
    {
        int SurfaceCapacity { get; }
        bool TryGetSurface(int index, out Mesh mesh, out Vector3 position);
    }
    public interface IGasVolumeReadOnly : IMaterialVolumeReadOnly
    {
        int ChunkSize { get; }
        int MaximumChunks { get; }
        int ResidentChunkCount { get; }
        ElementChunkKey GetChunkKey(int index);
        uint GetChunkVersion(ElementChunkKey key);
        // 只读 Current 的局部矩形块；目标 Stride 描述布局，不要求 Rendering 回查逐 Voxel 世界地址。
        void CopyMaterialBox(ElementChunkKey key,Vector3Int localMinimum,Vector3Int size,MaterialId material,
            uint[] destination,int destinationOffset,int rowStride,int sliceStride);
    }
}
