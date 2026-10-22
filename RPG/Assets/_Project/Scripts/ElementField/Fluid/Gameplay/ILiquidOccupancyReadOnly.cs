using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>低频 Gameplay Snapshot 中一个确定的“材质 + Global Cell + GMU”样本。</summary>
    public readonly struct LiquidMaterialCellSample
    {
        public LiquidMaterialCellSample(Vector3Int globalCell, byte amount)
        {
            GlobalCell = globalCell;
            Amount = amount;
        }

        public Vector3Int GlobalCell { get; }
        public byte Amount { get; }
    }

    /// <summary>
    /// Gameplay 只读取 GPU Water 的低频 CPU 投影；它不是 Solver Truth，也不能反向修改粒子。
    /// </summary>
    public interface ILiquidOccupancyReadOnly : IMaterialAmountReadOnly
    {
        bool HasValidSnapshot { get; }
        Bounds SnapshotBounds { get; }
        uint SnapshotVersion { get; }
        bool TryGetAmountUnitsPerParticle(MaterialId material, out uint amountUnits);
        int CopyOccupiedCells(MaterialId material, LiquidMaterialCellSample[] destination);
    }

    internal interface IFluidGameplayTopologyReadOnly
    {
        bool HasValidSnapshot { get; }
        uint TopologyVersion { get; }
    }
}
