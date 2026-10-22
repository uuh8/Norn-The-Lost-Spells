using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>已知湖盆的单一数量 owner；深水不在 LiquidVolumeStore 再存一份。</summary>
    public sealed class StableLiquidRegion
    {
        private readonly LiquidCapacityCurve _curve;
        private int _dynamicOverlapCells;
        public StableLiquidRegion(int id, MaterialId material, LiquidCapacityCurve curve,
            ulong initialQuantity)
        {
            if (curve == null || initialQuantity > curve.Capacity)
                throw new ArgumentOutOfRangeException(nameof(initialQuantity));
            Id = id;
            Material = material;
            _curve = curve;
            Quantity = initialQuantity;
            Version = 1;
            IsActive = true;
        }

        public int Id { get; }
        public MaterialId Material { get; }
        public Vector3Int Minimum => _curve.Minimum;
        public Vector3Int Maximum => _curve.Maximum;
        public int StepX => _curve.StepX;
        public int StepRise => _curve.StepRise;
        public bool HasFootprint => _curve.HasFootprint;
        public bool HasHeightfield => _curve.HasHeightfield;
        public int SurfacePatchCapacity => _curve.SurfacePatchCapacity;
        public uint SurfaceTopologyVersion => _curve.SurfaceTopologyVersion;
        public int FootprintRunCount => _curve.FootprintRunCount;
        public LiquidCapacityCurve.FootprintRun GetFootprintRun(int index) =>
            _curve.GetFootprintRun(index);
        public void RefreshSurfaceRuns() => _curve.RefreshSurfaceRuns(Quantity);
        public ulong Quantity { get; private set; }
        public ulong Capacity => _curve.Capacity;
        public uint Version { get; private set; }
        public bool HasDynamicOverlap => _dynamicOverlapCells > 0;
        public bool IsActive { get; private set; }
        public float SurfaceHeightInCells => _curve.SurfaceHeightInCells(Quantity);

        public bool Contains(Vector3Int cell) => _curve.Contains(cell);

        public uint QuantityAt(Vector3Int cell) => _curve.QuantityAt(cell, Quantity);
        public uint QuantityAt(Vector3Int cell, ulong total) =>
            _curve.QuantityAt(cell, total);

        public bool TryReactivateWithQuantity(ulong quantity)
        {
            // 回压缩只复用原湖盆形状和 ID；新数量来自动态 Store 的完整快照。
            if (IsActive || quantity > Capacity) return false;
            Quantity = quantity;
            Version++;
            IsActive = true;
            return true;
        }

        public bool TryRemove(uint quantity)
        {
            if (quantity == 0 || Quantity < quantity) return false;
            Quantity -= quantity;
            Version++;
            return true;
        }

        public bool TryAdd(uint quantity)
        {
            if (quantity == 0 || HasDynamicOverlap || Capacity - Quantity < quantity)
                return false;
            Quantity += quantity;
            Version++;
            return true;
        }

        public bool TryRestoreRemoved(uint quantity)
        {
            // 事务回滚恢复原 owner 的旧数量，即使区域已有异种动态覆盖也必须允许。
            // 普通外部流入仍走 TryAdd 的混合资格检查。
            if (quantity == 0 || Capacity - Quantity < quantity) return false;
            Quantity += quantity;
            Version++;
            return true;
        }

        public void ChangeDynamicOverlap(bool occupied)
        {
            if (occupied) _dynamicOverlapCells++;
            else if (_dynamicOverlapCells > 0) _dynamicOverlapCells--;
        }

        // 展开期间只切换 owner 可见性；原数量保持原值，失败时可以原样恢复。
        public void SetActive(bool active) => IsActive = active;

        public bool Overlaps(StableLiquidRegion other) =>
            Minimum.x < other.Maximum.x && Maximum.x > other.Minimum.x
            && Minimum.y < other.Maximum.y && Maximum.y > other.Minimum.y
            && Minimum.z < other.Maximum.z && Maximum.z > other.Minimum.z;
    }
}
