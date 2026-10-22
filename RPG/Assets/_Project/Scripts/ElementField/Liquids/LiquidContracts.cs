using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>只读的活动落水姿态；位置仍属于数量所在格，Offset 只记录不足一格的位移。</summary>
    public readonly struct LiquidFallingMotion
    {
        public readonly float OffsetCells, Velocity, Acceleration, SafeDistance, SampleTime;
        public readonly Vector3 HorizontalOffsetCells, HorizontalVelocityWorld;
        public LiquidFallingMotion(float offsetCells, float velocity, float acceleration,
            float safeDistance, float sampleTime,Vector3 horizontalOffsetCells=default,Vector3 horizontalVelocityWorld=default)
        {
            OffsetCells = offsetCells; Velocity = velocity; Acceleration = acceleration;
            SafeDistance = safeDistance; SampleTime = sampleTime;
            HorizontalOffsetCells=horizontalOffsetCells;HorizontalVelocityWorld=horizontalVelocityWorld;
        }
    }

    public interface ILiquidMotionReadOnly
    {
        float MotionHorizon { get; }
        bool TryGetFallingMotion(Vector3Int cell, MaterialId material, out LiquidFallingMotion motion);
    }

    /// <summary>
    /// 历史单材料表面的兼容自由面入口，仍返回最上层赢家。
    /// 新统一外壳读取ILiquidLayeredSurfaceReadOnly，不能用这个赢家入口代替真实液层。
    /// </summary>
    public interface ILiquidSurfaceLayerReadOnly
    {
        bool TryGetSurfaceFill(Vector3Int cell, MaterialId material, out float totalFill);
        float GetTotalFill(Vector3Int cell);
        int CopyLiquidChunkKeys(ElementChunkKey[] destination);
    }

    /// <summary>复制稳定水面的稀疏行段；冻结边界无需复制湖底所有液体格。</summary>
    public interface ILiquidStableSurfaceSnapshotReadOnly
    {
        int CopyAllStableSurfacePatches(StableLiquidSurfacePatch[] destination);
    }

    /// <summary>新液体后端以 GMU 记账；byte 状态读数只是对角色的投影。</summary>
    public interface ILiquidQuantityReadOnly : IMaterialAmountReadOnly
    {
        bool TryGetQuantity(Vector3Int globalCell, MaterialId material, out uint quantity);
        ulong TotalQuantity { get; }
        ulong PendingQuantity { get; }
    }

    /// <summary>角色等调用方传世界细格；液体内部与 Renderer 继续传液体专用格，避免混用。</summary>
    public interface ILiquidWorldQueryReadOnly
    {
        bool TryGetWorldAmount(Vector3Int worldCell, MaterialId material, out byte amount);
    }

    /// <summary>Renderer 只能读取已提交数量与版本，绝不改变模拟活动状态。</summary>
    public interface ILiquidSurfaceReadOnly : ILiquidQuantityReadOnly
    {
        Vector3 Origin { get; }
        float CellSize { get; }
        int ChunkSize { get; }
        int CellCapacityGmu { get; }
        int MaximumSurfaceCandidateKeys { get; }
        int MaximumStableRegions { get; }
        int MaximumStableSurfacePatches { get; }
        uint SurfaceVersion { get; }
        uint DynamicSurfaceVersion { get; }
        uint GetDynamicSurfaceVersion(MaterialId material);
        uint GetDynamicCandidateVersion(MaterialId material);
        uint GetSurfaceChunkVersion(ElementChunkKey key, MaterialId material);
        bool HasDynamicMaterialInChunk(ElementChunkKey key, MaterialId material);
        // 仅复制实际含该材料的动态页。Halo 是采样范围，不是额外的 Mesh owner。
        int CopySurfaceChunkKeys(MaterialId material, ElementChunkKey[] destination);
        bool IsSurfaceMaterialDominant(Vector3Int globalCell, MaterialId material);
        bool TryGetDynamicQuantity(Vector3Int globalCell, MaterialId material, out uint quantity);
        bool TryGetStableSurfaceHeight(Vector3Int globalCell, MaterialId material,
            out float heightCells);
        uint GetStableSurfaceVersion(MaterialId material);
        uint GetStableNeighborhoodVersion(ElementChunkKey key, MaterialId material);
        int CopyStableSurfacePatches(MaterialId material, StableLiquidSurfacePatch[] destination);
        bool IsStableSurfaceHandoffPending(MaterialId material, ElementChunkKey key);
        bool IsActiveStableSurfaceOverlapping(MaterialId material, ElementChunkKey key);
        bool IsSolidForSurface(Vector3Int globalCell);
    }

    /// <summary>一个静水区域的矩形面片；不规则占地可由多段组成，Renderer 按 RegionId 合批。</summary>
    public readonly struct StableLiquidSurfacePatch
    {
        public readonly int RegionId;
        public readonly uint Version;
        public readonly uint TopologyVersion;
        public readonly Vector3 Center;
        public readonly Vector2 SizeXZ;
        public readonly MaterialId Material;
        public readonly int MinimumYCells, MaximumYCells;
        public readonly bool HasGameplayQuantity;

        public StableLiquidSurfacePatch(int regionId, uint version, Vector3 center,
            Vector2 sizeXZ, uint topologyVersion = 0, MaterialId material = MaterialId.Empty,
            int minimumYCells = int.MinValue, int maximumYCells = int.MaxValue,
            bool hasGameplayQuantity = true)
        {
            RegionId = regionId;
            Version = version;
            TopologyVersion = topologyVersion;
            Center = center;
            SizeXZ = sizeXZ;
            Material = material; MinimumYCells = minimumYCells; MaximumYCells = maximumYCells;
            HasGameplayQuantity = hasGameplayQuantity;
        }
    }
}
