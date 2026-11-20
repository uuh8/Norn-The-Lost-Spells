using System;
using System.Collections.Generic;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class CellularRestingLiquidStore
    {
        private static readonly Vector3Int[] Neighbors =
            { Vector3Int.down, Vector3Int.right, Vector3Int.forward, Vector3Int.left, Vector3Int.back, Vector3Int.up };
        private readonly Queue<Vector3Int> _work;
        private readonly HashSet<Vector3Int> _queued;
        private readonly int _workLimit;
        private readonly Vector3Int[] _deferred;
        private readonly HashSet<Vector3Int> _deferredSet;
        private int _deferredCount;
        // 每个有例外的 4³ 页只占一个存在位图；容量随编辑触及的空间变化，初始整湖不生成页面。
        private struct Page { public Vector3Int Key; public ulong Bits; }
        private readonly List<Page> _pages = new List<Page>();
        private readonly Dictionary<Vector3Int, int> _pageIndices = new Dictionary<Vector3Int, int>();
        private bool _scanning, _scanAgain, _preferWork, _preferSleep;
        private struct FaceCursor { public int Region, Face; public long Offset; }
        private readonly Queue<FaceCursor> _border;
        private int _nextNewFace, _scanPage, _scanBit, _scanNeighbor;

        private void TrackEdit(Vector3Int cell, bool exception)
        {
            var key = new Vector3Int(cell.x >> 2, cell.y >> 2, cell.z >> 2);
            ulong mask = 1UL << ((cell.x & 3) + ((cell.y & 3) << 2) + ((cell.z & 3) << 4));
            bool present = _pageIndices.TryGetValue(key, out int index);
            if (!present)
            {
                if (!exception) return;
                _pageIndices.Add(key, _pages.Count);
                _pages.Add(new Page { Key = key, Bits = mask });
                return;
            }
            Page page = _pages[index];
            page.Bits = exception ? page.Bits | mask : page.Bits & ~mask;
            if (page.Bits != 0) { _pages[index] = page; return; }
            int last = _pages.Count - 1;
            _pageIndices.Remove(key);
            if (index != last)
            {
                _pages[index] = _pages[last];
                _pageIndices[_pages[index].Key] = index;
                if (_scanning && index <= _scanPage) _scanAgain = true;
            }
            _pages.RemoveAt(last);
        }

        public void WakeNeighbors(Vector3Int cell)
        {
            Enqueue(cell);
            for (int i = 0; i < Neighbors.Length; i++) if (Neighbor(cell, Neighbors[i], out var n)) Enqueue(n);
            // 沙的目标可以在斜下方。只通知面邻居会让架在台阶上的沙看见一块永不开放的静水。
            if (Neighbor(cell, Vector3Int.down, out var below))
                for (int i = 1; i <= 4; i++) if (Neighbor(below, Neighbors[i], out var diagonal)) Enqueue(diagonal);
        }

        private void Enqueue(Vector3Int cell)
        {
            if (FindRegion(cell) < 0 || _queued.Contains(cell)) return;
            if (_work.Count < _workLimit) { _queued.Add(cell); _work.Enqueue(cell); return; }
            // 溢出的候选一定来自原始盒表面或已经编辑的内部界面。恢复游标只重新访问这些面及编辑页。
            RequestScan();
        }

        private void RequestScan()
        {
            if (_scanning) _scanAgain = true; else _scanning = true;
        }

        private void Defer(Vector3Int cell)
        {
            if (_deferredSet.Contains(cell)) return;
            if (_deferredCount == _deferred.Length) { RequestScan(); return; }
            _deferred[_deferredCount++] = cell;
            _deferredSet.Add(cell);
        }

        /// <summary>返回消耗的候选／恢复游标预算。容量失败只在下一次 Step 重试。</summary>
        public int Step(int budget, ElementCellSimulation simulation)
        {
            if (simulation == null || simulation.CellCapacity != CellCapacity || _pendingCount != 0 || budget <= 0) return 0;
            int visited = 0;
            // 只取本轮开始前真正休眠的满格；预算失败放回候选尾，不会一帧反复审批同一格。
            int sleepBudget = simulation.SleepingLiquidCandidateCount;
            while (visited < budget && (_work.Count != 0 || _scanning || sleepBudget > 0))
            {
                if (sleepBudget > 0 && (_preferSleep || _work.Count == 0 && !_scanning))
                {
                    _preferSleep = false;
                    if (!simulation.TryTakeSleepingLiquid(out Vector3Int sleeping)) { sleepBudget = 0; continue; }
                    sleepBudget--;
                    TryReclaimFull(sleeping, simulation, out bool retryArchive);
                    if (retryArchive) simulation.QueueSleepingLiquid(sleeping);
                    visited++;
                    continue;
                }
                _preferSleep = true;
                Vector3Int cell;
                if (_work.Count != 0 && (_preferWork || !_scanning))
                {
                    cell = _work.Dequeue(); _queued.Remove(cell);
                }
                else if (!NextScan(out cell)) { _preferWork = true; visited++; continue; }
                _preferWork = !_preferWork;
                int region = FindRegion(cell);
                if (region >= 0 && !_deferredSet.Contains(cell))
                {
                    MaterialId mat = GetRegion(region).Material;
                    uint amount = Read(region, cell);
                    if (amount == 0)
                    {
                        TryReclaimFull(cell, simulation, out bool retry);
                        if (retry) Defer(cell);
                    }
                    else if (NeedsDynamic(cell, mat, amount, simulation) && !TryMaterialize(cell, simulation)) Defer(cell);
                }
                visited++;
            }
            for (int i = 0; i < _deferredCount; i++) Enqueue(_deferred[i]);
            _deferredCount = 0; _deferredSet.Clear();
            return visited;
        }

        private bool NeedsDynamic(Vector3Int cell, MaterialId material, uint amount, ElementCellSimulation simulation)
        {
            for (int i = 0; i < Neighbors.Length; i++)
            {
                if (!Neighbor(cell, Neighbors[i], out var n)) continue;
                bool resting = TryGetCell(n, out MaterialId neighborMaterial, out uint neighborAmount);
                if (!resting)
                {
                    if (!simulation.TryGetOccupiedCell(n, out neighborMaterial, out neighborAmount)) neighborMaterial = MaterialId.Empty;
                    // 部分量静水暂由区域树拥有。附近有真实气体时先把液体无损交给元胞，
                    // 让两种气体看到未被液体占用的容积，也覆盖上方气体与液气共存的来源格。
                    if (amount < CellCapacity && (simulation.TryGetQuantity(n, MaterialId.Steam, out _) ||
                        simulation.TryGetQuantity(n, MaterialId.ToxicVapor, out _))) return true;
                    if (simulation.HasSolidAt(n) && ElementCellMaterialRules.Motion(neighborMaterial) != ElementCellMotion.Grain) continue;
                }
                ElementCellMotion motion = ElementCellMaterialRules.Motion(neighborMaterial);
                if (i == Neighbors.Length - 1)
                {
                    // 顶面接触空气时湖面可以休眠；上方有比本格重的砂/液，必须先让出表示以完成置换。
                    if (simulation.CanDisplaceRestingLiquid(neighborMaterial, material) ||
                        amount < CellCapacity && neighborMaterial == material && neighborAmount != 0) return true;
                    if ((neighborMaterial == MaterialId.Empty || motion == ElementCellMotion.Gas) &&
                        HasDiagonalGrain(n, material, simulation)) return true;
                    continue;
                }
                if (neighborMaterial == material)
                {
                    if (i == 0 ? neighborAmount < CellCapacity : amount > neighborAmount + 1 || neighborAmount > amount + 1)
                        return true;
                }
                else if (neighborMaterial == MaterialId.Empty || motion == ElementCellMotion.Gas)
                {
                    if (i == 0 || amount > 1) return true;
                }
                else if (i == 0 && simulation.CanDisplaceRestingLiquid(material, neighborMaterial)) return true;
            }
            return false;
        }

        private static bool HasDiagonalGrain(Vector3Int passage, MaterialId liquid, ElementCellSimulation simulation)
        {
            for (int i = 1; i <= 4; i++)
                if (Neighbor(passage, Neighbors[i], out var source) &&
                    simulation.TryGetOccupiedCell(source, out MaterialId material, out _) &&
                    ElementCellMaterialRules.Motion(material) == ElementCellMotion.Grain &&
                    simulation.CanDisplaceRestingLiquid(material, liquid)) return true;
            return false;
        }

        private static bool Neighbor(Vector3Int cell, Vector3Int direction, out Vector3Int result)
            => ElementCellSimulation.Offset(cell, direction, out result);

        private bool NextScan(out Vector3Int cell)
        {
            // 初始逐步检查六个外表面。顶面只关心装配前已放好的砂/重液，敞开的平湖本身不会向上漏水。
            if (_nextNewFace < _regions.Length * 6 || _border.Count != 0)
            {
                // 每个已配置区域和每个面先获得一次探测，再轮转长面的后续像素。
                // 一个极大的封闭湖底不能让另一个区域的小开口等到整片湖底扫描完毕。
                FaceCursor cursor;
                if (_nextNewFace < _regions.Length * 6)
                {
                    cursor = new FaceCursor { Region = _nextNewFace / 6, Face = _nextNewFace % 6 };
                    _nextNewFace++;
                }
                else cursor = _border.Dequeue();
                BoundsInt box = _regions[cursor.Region].Cells;
                var size = box.size;
                bool yFace = cursor.Face == 0 || cursor.Face == 5, xFace = cursor.Face == 1 || cursor.Face == 3;
                int width = xFace ? size.z : size.x;
                int height = yFace ? size.z : size.y;
                int u = (int)(cursor.Offset % width), v = (int)(cursor.Offset / width);
                Vector3Int offset;
                if (yFace) offset = new Vector3Int(u, cursor.Face == 0 ? 0 : size.y - 1, v);
                else if (xFace) offset = new Vector3Int(cursor.Face == 1 ? size.x - 1 : 0, v, u);
                else offset = new Vector3Int(u, v, cursor.Face == 2 ? size.z - 1 : 0);
                cell = box.min + offset;
                if (++cursor.Offset < (long)width * height) _border.Enqueue(cursor);
                return true;
            }
            if (_scanPage < _pages.Count)
            {
                Page page = _pages[_scanPage];
                ulong bit = 1UL << _scanBit;
                var source = new Vector3Int((page.Key.x << 2) + (_scanBit & 3),
                    (page.Key.y << 2) + ((_scanBit >> 2) & 3), (page.Key.z << 2) + (_scanBit >> 4));
                int neighbor = _scanNeighbor;
                if ((page.Bits & bit) == 0 || ++_scanNeighbor == 7)
                {
                    _scanNeighbor = 0;
                    if (++_scanBit == 64) { _scanBit = 0; _scanPage++; }
                }
                cell = source;
                return (page.Bits & bit) != 0 && (neighbor == 0 || Neighbor(source, Neighbors[neighbor - 1], out cell));
            }
            _nextNewFace = _scanPage = _scanBit = _scanNeighbor = 0;
            _scanning = _scanAgain; _scanAgain = false;
            cell = default;
            return false;
        }
    }
}
