using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public enum FluidDormantWakeReason : byte
    {
        None = 0,
        Write = 1,
        Exposure = 2,
        Reaction = 3,
    }

    /// <summary>
    /// Presentation 只读取已经聚合的本地稳定液体；调用方提供数组，避免 Archive 版本变化时产生 GC。
    /// </summary>
    public interface IFluidDormantReadOnly
    {
        bool HasDormantData { get; }
        uint DormantVersion { get; }
        int MaximumDormantCellRecords { get; }
        int CopyDormantCells(MaterialId material, LiquidMaterialCellSample[] destination);
    }

    internal interface IFluidDormantWakeSink
    {
        bool RequestWake(Vector3Int globalCell, FluidDormantWakeReason reason);
    }

    public interface IFluidVisibleArchiveReadOnly
    {
        int CopyVisibleArchiveCells(MaterialId material, Bounds visible, LiquidMaterialCellSample[] destination);
    }
}
