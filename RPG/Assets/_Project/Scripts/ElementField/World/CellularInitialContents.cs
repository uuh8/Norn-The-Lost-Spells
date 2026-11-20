using System;
using System.Collections.Generic;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>关卡装配期的暂存账本。先按旧原生格重建整数 GMU，再在其覆盖的细格内从下往上装填。
    /// 原生水库已经声明的满格只校验一次；未被接管的满量静水合并成长方体，其余交给动态格。</summary>
    internal sealed class CellularInitialContents
    {
        internal struct Row { internal MaterialId Material; internal uint Quantity; internal bool Resting; }
        internal readonly Vector3 Origin;
        internal readonly LiquidVolumeSettings Volume;
        internal readonly Dictionary<Vector3Int, Row> Dynamic = new Dictionary<Vector3Int, Row>();
        internal CellularRestingLiquidRegion[] Regions;
        internal readonly HashSet<MonoBehaviour> Authors = new HashSet<MonoBehaviour>();
        private readonly Dictionary<Vector3Int, uint> _oldCells = new Dictionary<Vector3Int, uint>();
        private readonly HashSet<Vector3Int> _matchedNative = new HashSet<Vector3Int>();
        private readonly ElementCellWorldProfile _profile;
        private readonly Func<Vector3Int, bool> _blocked;
        private readonly LiquidWorldProfile _oldProfile;
        private ulong _oldTotal, _represented;
        private long _remaining;

        internal CellularInitialContents(ElementCellWorldProfile profile, LiquidWorldProfile oldProfile, Vector3 origin,
            float cellSize, Func<Vector3Int, bool> blocked)
        {
            _profile = profile; _oldProfile = oldProfile; Origin = origin; Volume = oldProfile.CreateVolumeSettings(cellSize);
            _blocked = blocked; _remaining = Math.Max(65536, Math.Min(1048576L, (long)profile.MaximumCells * 8));
        }

        internal BoundsInt CoarseBounds(Bounds bounds)
        {
            if (!float.IsFinite(bounds.size.sqrMagnitude) || !float.IsFinite(bounds.center.sqrMagnitude)
                || bounds.size.x <= 0 || bounds.size.y <= 0 || bounds.size.z <= 0)
                throw new InvalidOperationException("旧作者体积缺少可解释的有限边界。");
            var lo = ElementWorldCoordinates.WorldToGlobalCell(bounds.min, Origin, Volume.CellSize);
            var hi = ElementWorldCoordinates.WorldToGlobalCell(bounds.max - Vector3.one * (Volume.CellSize * .0001f),
                Origin, Volume.CellSize) + Vector3Int.one;
            var span = hi - lo;
            if (span.x < 1 || span.y < 1 || span.z < 1 || (long)span.x * span.y > _remaining / span.z)
                throw new InvalidOperationException("旧作者液体超出本次有界迁移预算。");
            _remaining -= (long)span.x * span.y * span.z;
            return new BoundsInt(lo, span);
        }

        internal void Add(Vector3Int coarse, MaterialId material, uint quantity, bool resting)
        {
            if (quantity == 0) return;
            bool supported = false;
            for (int i = 0; i < _oldProfile.MaterialCount; i++)
            {
                var behavior = _oldProfile.GetMaterial(i);
                if (behavior != null && behavior.Material == material) { supported = true; break; }
            }
            if (!supported || ElementCellMaterialRules.Motion(material) != ElementCellMotion.Liquid)
                throw new InvalidOperationException("旧体积含原/新世界无法解释的材料。");
            _oldCells.TryGetValue(coarse, out uint used);
            if ((ulong)used + quantity > (uint)Volume.CapacityGmu)
                throw new InvalidOperationException("重叠的作者液体超过原生粗格容量。");
            _oldCells[coarse] = used + quantity; _oldTotal += quantity;
            int scale = Volume.ResolutionScale;
            Vector3Int first;
            checked { first = new Vector3Int(coarse.x * scale, coarse.y * scale, coarse.z * scale); }
            uint left = quantity, capacity = (uint)_profile.Capacity;
            for (int y = 0; y < scale && left > 0; y++)
            for (int z = 0; z < scale && left > 0; z++)
            for (int x = 0; x < scale && left > 0; x++)
            {
                var cell = first + new Vector3Int(x,y,z);
                if (_blocked(cell)) continue;
                MaterialId native = MaterialId.Empty;
                var authored = _profile.RestingRegions;
                if (authored != null) foreach (var region in authored)
                    if (region.Cells.Contains(cell)) { native = region.Material; break; }
                if (native != MaterialId.Empty)
                {
                    if (_matchedNative.Contains(cell)) continue;
                    if (native != material || left < capacity) throw new InvalidOperationException("旧水量与既有原生静水归属不一致。");
                    _matchedNative.Add(cell); _represented += capacity; left -= capacity;
                    continue;
                }
                Dynamic.TryGetValue(cell, out var row);
                if (row.Quantity != 0 && row.Material != material) continue;
                uint add = Math.Min(capacity - row.Quantity, left);
                if (add == 0) continue;
                if (Dynamic.Count >= 1048576 && row.Quantity == 0) throw new InvalidOperationException("旧液体的细格数量超出迁移预算。");
                row.Resting = row.Quantity == 0 ? resting : row.Resting && resting;
                row.Material = material; row.Quantity += add;
                Dynamic[cell] = row; left -= add;
            }
            if (left != 0) throw new InvalidOperationException("原粗格中的实际 GMU 无法完整落在其覆盖的可用细格。");
        }

        internal bool AllowsStable(MaterialId material)
        {
            for (int i = 0; i < _oldProfile.MaterialCount; i++)
            {
                var behavior = _oldProfile.GetMaterial(i);
                if (behavior != null && behavior.Material == material) return behavior.AllowsStableRegion;
            }
            return false;
        }

        internal void Finish()
        {
            var regions = _profile.RestingRegions == null ? new List<CellularRestingLiquidRegion>()
                : new List<CellularRestingLiquidRegion>(_profile.RestingRegions);
            var order = new List<Vector3Int>();
            ulong total = _represented;
            foreach (var pair in Dynamic)
            {
                total += pair.Value.Quantity;
                if (pair.Value.Resting && pair.Value.Quantity == _profile.Capacity) order.Add(pair.Key);
            }
            if (total != _oldTotal) throw new InvalidOperationException("旧液体 GMU 总量与接管结果不同。");
            order.Sort((a,b) => a.y != b.y ? a.y.CompareTo(b.y) : a.z != b.z ? a.z.CompareTo(b.z) : a.x.CompareTo(b.x));
            foreach (var p in order)
            {
                if (!Dynamic.TryGetValue(p, out var row)) continue;
                int xEnd = checked(p.x + 1), zEnd = checked(p.z + 1), yEnd = checked(p.y + 1);
                while (Matches(new Vector3Int(xEnd, p.y, p.z), row.Material)) xEnd = checked(xEnd + 1);
                while (MatchesRow(p.x, xEnd, p.y, zEnd, row.Material)) zEnd = checked(zEnd + 1);
                while (MatchesLayer(p.x, xEnd, p.z, zEnd, yEnd, row.Material)) yEnd = checked(yEnd + 1);
                var box = new BoundsInt(p, new Vector3Int(xEnd-p.x, yEnd-p.y, zEnd-p.z));
                foreach (var cell in box.allPositionsWithin) Dynamic.Remove(cell);
                regions.Add(new CellularRestingLiquidRegion { Cells = box, Material = row.Material });
            }
            if (Dynamic.Count > _profile.MaximumCells) throw new InvalidOperationException("剩余活动材料超过元胞槽位容量。");
            Regions = regions.ToArray();
        }

        private bool Matches(Vector3Int cell, MaterialId material) => Dynamic.TryGetValue(cell, out var row)
            && row.Material == material && row.Resting && row.Quantity == _profile.Capacity;
        private bool MatchesRow(int lo, int hi, int y, int z, MaterialId material)
        {
            for (int x = lo; x < hi; x++) if (!Matches(new Vector3Int(x,y,z), material)) return false;
            return true;
        }
        private bool MatchesLayer(int lo, int hi, int front, int back, int y, MaterialId material)
        {
            for (int z = front; z < back; z++) if (!MatchesRow(lo, hi, y, z, material)) return false;
            return true;
        }
    }
}
