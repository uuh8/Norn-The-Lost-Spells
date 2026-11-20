using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>记录数值来自哪个空间和数量所有者。枚举只供本地查询，不用作网络域或存档编号。</summary>
    public enum ElementWorldMaterialSource : byte
    {
        None, Terrain, Cellular, GridLiquid, FixedSolid, GridGas, LegacyCell, GpuLiquidProjection, RestingLiquid
    }

    /// <summary>
    /// 已提交材料在一个基础格的只读样本。按体积统计时用 (Source, NativeCell, Material) 去重：
    /// 多个基础格可以覆盖同一个液体粗格，QuantityGmu 仍是那一个原生格的整份库存。
    /// 容量是该域原生格的标尺，同格不同材料共享容量。CapacityGmu 为零表示该样本不计 GMU。
    /// </summary>
    public readonly struct ElementWorldMaterialSample
    {
        public readonly Vector3Int WorldCell;
        public readonly Vector3Int NativeCell;
        public readonly MaterialId Material;
        public readonly ElementWorldMaterialSource Source;
        public readonly uint QuantityGmu;
        public readonly uint CapacityGmu;
        // Legacy（包括 Fire）和 GPU Gameplay Snapshot 保留各自的 byte 读数，禁止写入库存账本。
        public readonly byte Intensity;
        // 当前材料是否阻挡完整基础格。部分高度固体仍可提供实际高度的 Collider。
        public readonly bool BlocksWholeCell;

        internal ElementWorldMaterialSample(Vector3Int worldCell, Vector3Int nativeCell, MaterialId material,
            ElementWorldMaterialSource source, uint quantity, uint capacity, byte intensity, bool blocksWholeCell)
        {
            WorldCell = worldCell;
            NativeCell = nativeCell;
            Material = material;
            Source = source;
            QuantityGmu = quantity;
            CapacityGmu = capacity;
            Intensity = intensity;
            BlocksWholeCell = blocksWholeCell;
        }
    }

    public interface IElementWorldMaterialReadOnly
    {
        /// <summary>查询指定材料是否有已提交样本。未安装的 Replica 页也返回 false，不能据此授权写入。</summary>
        bool TrySampleMaterial(Vector3Int worldCell, MaterialId material, out ElementWorldMaterialSample sample);

        /// <summary>取得可编辑地形的材料身份。原场景内独立的 Collider 可阻挡路径，但不自动取得岩石材料身份。</summary>
        MaterialId GetTerrainMaterial(Vector3Int worldCell);

        /// <summary>完整基础格对材料输运是否封闭。部分体积的表面高度由各相对应的支撑查询提供。</summary>
        bool IsMaterialCellBlocked(Vector3Int worldCell);
    }
}
