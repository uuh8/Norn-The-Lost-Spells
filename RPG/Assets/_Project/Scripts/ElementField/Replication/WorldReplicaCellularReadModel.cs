using System;
using System.Collections.Generic;
using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// Replica 的一份 Cellular Chunk replacement 同时包含地形、静水和活动材料。基底由发布内容加载，
    /// 缓存只持有偏离基底或具有动态量的格，没有本地输运、库存端口或反应写权。
    /// </summary>
    internal sealed class WorldReplicaCellularReadModel
    {
        private struct Cell
        {
            internal bool HasTerrain, HasLiquid;
            internal MaterialId Terrain, Liquid, Primary;
            internal uint LiquidAmount, Amount, Steam, Toxic;
            internal bool Dynamic => Amount != 0 || Steam != 0 || Toxic != 0;
            internal bool Same(in Cell other) => HasTerrain == other.HasTerrain && HasLiquid == other.HasLiquid &&
                Terrain == other.Terrain && Liquid == other.Liquid && Primary == other.Primary && LiquidAmount == other.LiquidAmount &&
                Amount == other.Amount && Steam == other.Steam && Toxic == other.Toxic;
        }

        private readonly Dictionary<WorldChunkKey, Dictionary<Vector3Int, Cell>> _pages =
            new Dictionary<WorldChunkKey, Dictionary<Vector3Int, Cell>>();
        private Dictionary<Vector3Int, Cell> _candidate = new Dictionary<Vector3Int, Cell>();
        private readonly Stack<Dictionary<Vector3Int, Cell>> _freePages = new Stack<Dictionary<Vector3Int, Cell>>(8);
        private readonly HashSet<Vector3Int> _changed = new HashSet<Vector3Int>();
        private readonly List<Vector3Int> _affected = new List<Vector3Int>();
        private readonly List<Vector3Int> _terrainChanged = new List<Vector3Int>();
        private readonly ElementWorldRuntime _world;
        private readonly ElementTerrainVolume _terrain;
        private readonly CellularSolidColliders _solids;
        internal readonly CellularRestingLiquidStore Resting;
        internal readonly WorldGridGeometry Geometry;
        internal bool Verified { get; private set; }
        internal int OccupiedCount { get; private set; }

        internal WorldReplicaCellularReadModel(ElementWorldRuntime world, WorldGridGeometry geometry,
            ElementTerrainVolume terrain, CellularRestingLiquidStore resting, CellularSolidColliders solids)
        {
            _world = world; Geometry = geometry; _terrain = terrain; Resting = resting; _solids = solids;
            _terrain.ShowGeometry(false);
        }

        internal bool Prepare(WorldChunkKey key, WorldReplicaBuffer buffer, bool resident, int workBudget)
        {
            _candidate.Clear();
            if (!resident) return Verified;
            if (buffer == null) return false;
            // 只在这个可信格距的网络块内展开覆盖。单个畸大的包不能借一条压缩记录绕开 CPU 工作上限。
            int remaining = workBudget;
            for (int i = 0; i < buffer.Header.SpanCount; i++)
            {
                var span = buffer.Spans[i];
                var size = span.Cells.size;
                long area = (long)size.x * size.y;
                if (size.x < 1 || size.y < 1 || size.z < 1 || area > remaining / size.z) return false;
                remaining -= (int)(area * size.z);
                foreach (var cell in span.Cells.allPositionsWithin)
                {
                    _candidate.TryGetValue(cell, out var value);
                    MaterialId material = (MaterialId)span.MaterialId;
                    if (span.Kind == WorldCellularSpanKind.Terrain)
                    {
                        if (value.HasTerrain || _terrain.GetBase(cell) == material) return false;
                        value.HasTerrain = true; value.Terrain = material;
                    }
                    else
                    {
                        var authored = Resting.AuthoredMaterial(cell);
                        if (value.HasLiquid || (span.Kind == WorldCellularSpanKind.AuthoredLiquid
                            ? authored != material || span.OriginalQuantity != Geometry.CellCapacity
                            : authored != MaterialId.Empty)) return false;
                        value.HasLiquid = true; value.Liquid = material; value.LiquidAmount = span.Quantity;
                    }
                    _candidate[cell] = value;
                }
            }
            for (int i = 0; i < buffer.Header.CellCount; i++)
            {
                if (--remaining < 0) return false;
                var row = buffer.Cells[i];
                _candidate.TryGetValue(row.Cell, out var value);
                switch ((MaterialId)row.MaterialId)
                {
                    case MaterialId.Steam: value.Steam = row.Quantity; break;
                    case MaterialId.ToxicVapor: value.Toxic = row.Quantity; break;
                    case MaterialId.Water: case MaterialId.Oil: case MaterialId.Sticky: case MaterialId.Poison:
                    case MaterialId.Ice: case MaterialId.Sand: case MaterialId.DriedSlime:
                        if (value.Primary != MaterialId.Empty) return false;
                        value.Primary = (MaterialId)row.MaterialId; value.Amount = row.Quantity; break;
                    default: return false;
                }
                _candidate[row.Cell] = value;
            }
            foreach (var pair in _candidate)
            {
                var value = pair.Value;
                var terrain = value.HasTerrain ? value.Terrain : _terrain.GetBase(pair.Key);
                RestingValue(pair.Key, value, out var coldMaterial, out uint coldAmount);
                bool gas = value.Steam != 0 || value.Toxic != 0;
                if (terrain != MaterialId.Empty && (coldAmount != 0 || value.Dynamic) || coldAmount != 0 && value.Amount != 0 ||
                    (ulong)coldAmount + value.Amount + value.Steam + value.Toxic > Geometry.CellCapacity ||
                    gas && (ElementCellMaterialRules.OccupiesWholeCell(value.Primary) ||
                        coldAmount != 0 && ElementCellMaterialRules.OccupiesWholeCell(coldMaterial)))
                    return false;
            }
            return true;
        }

        internal void Publish(WorldChunkKey key)
        {
            _pages.TryGetValue(key, out var previous);
            _changed.Clear(); _affected.Clear(); _terrainChanged.Clear();
            if (previous != null) foreach (var pair in previous)
                if (!_candidate.TryGetValue(pair.Key, out var current) || !pair.Value.Same(current)) AddAffected(pair.Key);
            foreach (var pair in _candidate)
                if (previous == null || !previous.ContainsKey(pair.Key)) AddAffected(pair.Key);
            int beforeCount = 0, afterCount = 0;
            try
            {
                // 先去掉候选将减少的静水，随后再回填，巨大水池在同一事务间转移时也不暂时突破 ulong 总量。
                foreach (var cell in _affected)
                {
                    var old = Get(previous, cell); var next = Get(_candidate, cell);
                    if (old.Dynamic) beforeCount++;
                    if (next.Dynamic) afterCount++;
                    var to = next.HasTerrain ? next.Terrain : _terrain.GetBase(cell);
                    if (_terrain.Get(cell) != to)
                    {
                        if (!_terrain.TrySet(cell, to)) throw new InvalidOperationException("无法安装 Cellular 地形碰撞覆盖。");
                        _terrainChanged.Add(cell);
                    }
                    RestingValue(cell, next, out var material, out uint quantity);
                    if (Resting.TryGetCell(cell, out var oldMaterial, out uint oldQuantity) &&
                        (oldMaterial != material || oldQuantity > quantity)) Resting.SetReplicaQuantity(cell, material, quantity < oldQuantity && oldMaterial == material ? quantity : 0);
                    _solids.Set(cell, ElementCellMaterialRules.OccupiesWholeCell(next.Primary) ||
                        quantity != 0 && ElementCellMaterialRules.OccupiesWholeCell(material));
                }
                foreach (var cell in _affected)
                {
                    RestingValue(cell, Get(_candidate, cell), out var material, out uint quantity);
                    Resting.SetReplicaQuantity(cell, material, quantity);
                }
                _solids.Commit();
                if (!Verified) { _terrain.ShowGeometry(true); _world.AttachVerifiedCellularBase(); Physics.SyncTransforms(); }
            }
            catch
            {
                // 主线程中尚未换入新数量表，也未发 Observer 通知。恢复已操作的场景投影，交由装配器锁存异常。
                foreach (var cell in _affected)
                {
                    var old = Get(previous, cell);
                    var terrain = old.HasTerrain ? old.Terrain : _terrain.GetBase(cell);
                    if (_terrain.Get(cell) != terrain) _terrain.TrySet(cell, terrain);
                    RestingValue(cell, old, out var liquid, out uint count);
                    Resting.SetReplicaQuantity(cell, liquid, count);
                    _solids.Set(cell, ElementCellMaterialRules.OccupiesWholeCell(old.Primary) ||
                        count != 0 && ElementCellMaterialRules.OccupiesWholeCell(liquid));
                }
                _solids.Commit();
                throw;
            }
            if (_candidate.Count == 0)
            {
                _pages.Remove(key);
                if (previous != null && _freePages.Count < 8) { previous.Clear(); _freePages.Push(previous); }
            }
            else
            {
                _pages[key] = _candidate;
                _candidate = previous ?? (_freePages.Count > 0 ? _freePages.Pop() : new Dictionary<Vector3Int, Cell>());
                _candidate.Clear();
            }
            OccupiedCount += afterCount - beforeCount;
            Verified = true;
            // Renderer 在正常表现循环取通知。所有数量与碰撞此时均已可读，外部回调不会重入网络安装器。
            _world.EnqueueReplicaCellularPresentation(_affected, _terrainChanged);
        }

        private void AddAffected(Vector3Int cell) { if (_changed.Add(cell)) _affected.Add(cell); }
        private static Cell Get(Dictionary<Vector3Int, Cell> values, Vector3Int cell)
            => values != null && values.TryGetValue(cell, out var value) ? value : default;

        private void RestingValue(Vector3Int cell, Cell value, out MaterialId material, out uint quantity)
        {
            material = value.HasLiquid ? value.Liquid : Resting.AuthoredMaterial(cell);
            quantity = value.HasLiquid ? value.LiquidAmount : material == MaterialId.Empty ? 0 : Geometry.CellCapacity;
        }

        internal bool TryGetContents(Vector3Int coordinate, out MaterialId primary, out uint amount, out uint steam, out uint toxic)
        {
            var key = WorldChunkPayloadCodec.CellKey(WorldMaterialDomain.Cellular, coordinate, Geometry.ChunkSize);
            _pages.TryGetValue(key, out var page);
            var cell = Get(page, coordinate);
            primary = cell.Primary; amount = cell.Amount; steam = cell.Steam; toxic = cell.Toxic;
            return cell.Dynamic;
        }

        internal bool TryGetQuantity(Vector3Int cell, MaterialId material, out uint quantity, out bool resting)
        {
            quantity = 0; resting = false;
            if (!Verified) return false;
            if (Resting.TryGetQuantity(cell, material, out quantity)) { resting = true; return true; }
            if (!TryGetContents(cell, out var primary, out uint amount, out uint steam, out uint toxic)) return false;
            quantity = material == MaterialId.Steam ? steam : material == MaterialId.ToxicVapor ? toxic : material == primary ? amount : 0;
            return quantity != 0;
        }

        internal bool BlocksCell(Vector3Int cell) => Verified &&
            (Resting.TryGetCell(cell, out var cold, out _) && ElementCellMaterialRules.OccupiesWholeCell(cold)
             || TryGetContents(cell, out var primary, out _, out _, out _) && ElementCellMaterialRules.OccupiesWholeCell(primary));

        internal void VisitOccupied(Action<Vector3Int> visitor)
        {
            foreach (var page in _pages.Values)
                foreach (var entry in page) if (entry.Value.Dynamic) visitor(entry.Key);
        }
    }
}
