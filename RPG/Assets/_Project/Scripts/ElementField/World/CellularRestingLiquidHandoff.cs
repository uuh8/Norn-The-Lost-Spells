using System;
using System.Collections.Generic;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementCellSimulation
    {
        // 静水边界只询问资格，不搬量；与世界 Inspector 烘焙出的同一份密度参数比较。
        internal bool CanDisplaceRestingLiquid(MaterialId source, MaterialId sink)
        {
            var motion = ElementCellMaterialRules.Motion(source);
            return (motion == ElementCellMotion.Grain || motion == ElementCellMotion.Liquid)
                && ElementCellMaterialRules.Motion(sink) == ElementCellMotion.Liquid
                && _transport.Density(source) > _transport.Density(sink);
        }
    }

    public sealed partial class CellularRestingLiquidStore
    {
        private struct Pending { public Vector3Int Cell; public int Region; public uint Before, After; }
        private readonly Pending[] _pending = new Pending[512];
        private readonly Dictionary<Vector3Int, int> _pendingIndices = new Dictionary<Vector3Int, int>(512);
        private int _pendingCount, _reservedChanges;
        internal int MaximumStagedCells => _pending.Length;
        private bool _opening;
        private Vector3Int _openingCell;

        // 只在同步交接期间开放当前源格，不开放邻格；数量/版本查询仍只见归属表的上一次提交。
        public bool BlocksSimulation(Vector3Int cell) => (!_opening || _openingCell != cell) && TryGetCell(cell, out _, out _);

        public bool TryMaterialize(Vector3Int cell, ElementCellSimulation simulation)
        {
            if (simulation == null || simulation.CellCapacity != CellCapacity || _opening || _pendingCount != 0 ||
                !CanRecord(cell) || simulation.TryGetOccupiedCell(cell, out _, out _)) return false;
            int region = FindRegion(cell);
            uint quantity = region < 0 ? 0 : Read(region, cell);
            if (quantity == 0) return false;
            MaterialId material = GetRegion(region).Material;
            _opening = true;
            _openingCell = cell;
            try
            {
                // 动态池满、碰撞或变化队列背压时，整份量留在原处。没有逐半格交接的双归属窗口。
                if (!simulation.TryDeposit(cell, material, quantity)) return false;
                Set(region, cell, quantity, 0);
                return true;
            }
            finally { _opening = false; }
        }

        public bool TryReclaimFull(Vector3Int cell, ElementCellSimulation simulation) =>
            simulation != null && simulation.TryGetOccupiedCell(cell, out var material, out uint amount) &&
            amount == CellCapacity && ElementCellMaterialRules.Motion(material) == ElementCellMotion.Liquid &&
            TryArchiveSettled(cell, simulation, out _);

        internal bool TryArchiveSettled(Vector3Int cell, ElementCellSimulation simulation, out bool retry)
        {
            retry = false;
            if (simulation == null || simulation.CellCapacity != CellCapacity || _pendingCount != 0 ||
                _opening) return false;
            if (!simulation.TryGetOccupiedCell(cell, out MaterialId material, out uint quantity) ||
                !CanArchive(material, quantity) || simulation.TryGetQuantity(cell, MaterialId.Steam, out _) ||
                simulation.TryGetQuantity(cell, MaterialId.ToxicVapor, out _) || _totals[(byte)material] > ulong.MaxValue - quantity ||
                NeedsDynamic(cell, material, quantity, simulation)) return false;
            int region = FindRegion(cell);
            if (region >= 0 && (Read(region, cell) != 0 || region < _regions.Length && GetRegion(region).Material != material)) return false;
            // BoundsInt 半开盒无法表示 int.MaxValue 这一格的上界；该边缘继续由动态池持有。
            if (region < 0 && (cell.x == int.MaxValue || cell.y == int.MaxValue || cell.z == int.MaxValue)) return false;
            if (!CanRecord(cell) || !simulation.TryRemove(cell, material, quantity)) { retry = true; return false; }
            // 先证实动态格整笔可转出才创建冷页，经过或未稳定的流水都不会占一个冷区域。
            if (region < 0 || GetRegion(region).Material != material) region = ClaimCold(cell, material, region);
            Set(region, cell, 0, quantity);
            return true;
        }

        private bool CanArchive(MaterialId material, uint quantity)
        {
            var motion = ElementCellMaterialRules.Motion(material);
            return motion == ElementCellMotion.Liquid ||
                material == MaterialId.Sand || material == MaterialId.Ice || material == MaterialId.DriedSlime && quantity == CellCapacity;
        }

        public uint GetLocalFreeCapacity(Vector3Int cell, MaterialId material)
        {
            if (_pendingIndices.TryGetValue(cell, out int slot))
            {
                Pending entry = _pending[slot];
                return GetRegion(entry.Region).Material == material ? Math.Min(CellCapacity - entry.After, StagedQuantityRoom(material)) : 0;
            }
            return TryGetQuantity(cell, material, out uint quantity) && CanStageNew(cell)
                ? Math.Min(CellCapacity - quantity, StagedQuantityRoom(material)) : 0;
        }

        private uint StagedQuantityRoom(MaterialId material)
        {
            ulong room = ulong.MaxValue - _totals[(byte)material];
            if (room >= (ulong)(_pendingCount + 1) * CellCapacity) return CellCapacity;
            // 超大压缩湖接近 ulong 上界时才核算未发布的净变化；普通地图走上面的常数路径。
            long delta = 0;
            for (int i = 0; i < _pendingCount; i++)
                if (GetRegion(_pending[i].Region).Material == material) delta += (long)_pending[i].After - _pending[i].Before;
            return (uint)Math.Min((long)CellCapacity, (long)room - delta);
        }

        public bool TryStageAdd(Vector3Int cell, MaterialId material, uint amount) => Stage(cell, material, amount, true);
        public bool TryStageRemove(Vector3Int cell, MaterialId material, uint amount) => Stage(cell, material, amount, false);

        private bool CanStageNew(Vector3Int cell) => _pendingCount < _pending.Length &&
            (_changedSet.Contains(cell) || _changed.Count + _reservedChanges < _changeLimit);

        private bool Stage(Vector3Int cell, MaterialId material, uint amount, bool add)
        {
            if (amount == 0 || _opening) return false;
            bool existing = _pendingIndices.TryGetValue(cell, out int slot);
            Pending entry;
            if (existing) entry = _pending[slot];
            else
            {
                if (!CanStageNew(cell)) return false;
                int region = FindRegion(cell);
                if (region < 0) return false;
                uint before = Read(region, cell);
                // 已发布的空洞归动态 Owner。在同一个未发布事务中扣成 0 的格仍可回填。
                if (before == 0) return false;
                entry = new Pending { Cell = cell, Region = region, Before = before, After = before };
            }
            if (GetRegion(entry.Region).Material != material || amount > (add ? CellCapacity - entry.After : entry.After) ||
                add && amount > StagedQuantityRoom(material)) return false;
            entry.After = add ? entry.After + amount : entry.After - amount;
            if (!existing)
            {
                slot = _pendingCount++;
                _pendingIndices.Add(cell, slot);
                if (!_changedSet.Contains(cell)) _reservedChanges++;
            }
            _pending[slot] = entry;
            return true;
        }

        public void CommitStaged()
        {
            // 空间可能包含接近 ulong 上界的巨大均匀盒，先扣再加，提交途中也不会越过可表达的总量。
            for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < _pendingCount; i++)
            {
                Pending entry = _pending[i];
                if ((entry.Before > entry.After) == (pass == 0)) Set(entry.Region, entry.Cell, entry.Before, entry.After);
            }
            CancelStaged();
        }

        public void CancelStaged()
        {
            _pendingIndices.Clear();
            _reservedChanges = _pendingCount = 0;
        }

        public bool TryRemove(Vector3Int cell, MaterialId material, uint amount, ElementCellSimulation simulation)
        {
            if (_pendingCount != 0 || !TryStageRemove(cell, material, amount)) return false;
            CommitStaged();
            simulation?.WakeNeighbors(cell);
            return true;
        }
    }
}
