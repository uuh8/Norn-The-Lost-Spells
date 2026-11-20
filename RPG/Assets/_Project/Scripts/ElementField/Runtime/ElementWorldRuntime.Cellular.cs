using System;
using System.Collections.Generic;
using Game.Core;
using Game.Combat;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementWorldRuntime
    {
        private ElementTerrainVolume _cellularTerrain;
        private CellularSolidColliders _cellularCollisions;
        private ElementCellWorldProfile _cellDefinition;
        internal ElementCellSimulation CellularSimulation { get; private set; }
        internal CellularRestingLiquidStore RestingLiquids { get; private set; }
        public ICellularRestingLiquidReadOnly CellularRestingLiquids => RestingLiquids;
        private struct DepositCell
        {
            internal Vector3Int Cell;
            internal uint Capacity, Added;
            internal float Weight;
            internal bool Resting;
        }
        private DepositCell[] _depositCells;
        private Dictionary<Vector3Int, bool> _sceneObstacles;
        private Vector3Int[] _obstacleKeys;
        private int _obstacleSlot, _obstacleSlotsUsed;
        private Dictionary<ElementChunkKey, bool> _sceneChunks;
        private ElementChunkKey[] _sceneChunkKeys;
        private int _sceneChunkSlot, _sceneChunkSlotsUsed;
        private List<Vector3Int> _cellPublications;
        private HashSet<Vector3Int> _publicationSet;
        private static readonly Vector3Int[] FireContacts = { Vector3Int.zero, Vector3Int.down,
            Vector3Int.up, Vector3Int.left, Vector3Int.right, Vector3Int.back, Vector3Int.forward };
        public event Action<Vector3Int> CellularContentsChanged;
        public event Action<Vector3Int> TerrainMaterialChanged;

        public bool UsesCellularWorld => IsInitialized ? CellularSimulation != null
            : _profile != null && _profile.CellularWorld != null;
        public uint CellularCellCapacity => CellularSimulation?.CellCapacity ?? 0;
        public int CellularOccupiedCount => CellularSimulation?.OccupiedCellCount ?? 0;
        public int CellularActiveCount => CellularSimulation?.ActiveCellCount ?? 0;
        public int CellularCellsProcessedLastTick { get; private set; }
        public int RestingCellsProcessedLastTick { get; private set; }
        public int DeferredCellularWritesLastTick { get; private set; }
        public ulong TerrainRevision { get; private set; }
        public MaterialId GetTerrainMaterial(Vector3Int worldCell) => _cellularTerrain?.Get(worldCell) ?? MaterialId.Empty;

        private void InitializeCellularWorld()
        {
            _cellDefinition = _profile.CellularWorld;
            if (_cellDefinition.WorkBudget < 1 || _cellDefinition.RestingBudget < 1 || _cellDefinition.RestingQueueCapacity < 1 ||
                _cellDefinition.MaximumCells < 1 || _cellDefinition.Capacity < 1 || _cellDefinition.DepositBudget < 1)
                throw new InvalidOperationException("元胞世界的每步预算、驻留量和格容量必须为正。");
            _cellularTerrain = new ElementTerrainVolume(_cellDefinition, transform, _profile.CellSize, _profile.ChunkSize);
            _solidBaker.SetEditableTerrain(_cellularTerrain, IsCellularBlockingCell);
            var regions = _cellDefinition.RestingRegions;
            RestingLiquids = new CellularRestingLiquidStore(regions, (uint)_cellDefinition.Capacity, _cellDefinition.RestingQueueCapacity);
            for (int i = 0; i < RestingLiquids.RegionCount; i++)
            {
                BoundsInt box = RestingLiquids.GetRegion(i).Cells;
                Vector3 span = (Vector3)box.size * _profile.CellSize;
                Vector3 mid = _origin + (Vector3)box.min * _profile.CellSize + span * .5f;
                if (_cellularTerrain.IntersectsSolid(box)
                    || _solidBaker.HasSceneObstacle(mid, span * .5f - Vector3.one * (_profile.CellSize * .01f)))
                    throw new InvalidOperationException($"静水区域 {i} 与地形或静态 Collider 重叠。");
            }
            int cacheSize = Math.Min(_cellDefinition.MaximumCells, 32768);
            _sceneObstacles = new Dictionary<Vector3Int, bool>(cacheSize);
            _obstacleKeys = new Vector3Int[cacheSize];
            int chunkCacheSize = Math.Min(cacheSize, 4096);
            _sceneChunks = new Dictionary<ElementChunkKey, bool>(chunkCacheSize);
            _sceneChunkKeys = new ElementChunkKey[chunkCacheSize];
            CellularSimulation = new ElementCellSimulation(_cellDefinition.Capacity, _cellDefinition.MaximumCells,
                IsBlockedForDynamicMaterial, _cellDefinition.Transport);
            _cellularCollisions = new CellularSolidColliders(transform, _profile.CellSize, _profile.ChunkSize, _cellDefinition.TerrainLayer);
            _depositCells = new DepositCell[_cellDefinition.DepositBudget];
            int noticeCapacity = (int)Math.Min(16384L, 7L * _cellDefinition.WorkBudget + _cellDefinition.RestingBudget);
            _cellPublications = new List<Vector3Int>(noticeCapacity);
            _publicationSet = new HashSet<Vector3Int>(noticeCapacity);
        }

        private void DisposeCellularWorld()
        {
            ClearCellularCombustion();
            if (_solidBaker != null) _solidBaker.SetEditableTerrain(null);
            _cellularTerrain?.Dispose();
            _cellularCollisions?.Dispose();
            _cellularTerrain = null;
            _cellularCollisions = null;
            _cellDefinition = null;
            CellularSimulation = null;
            RestingLiquids = null;
        }

        internal bool OwnsCellularMaterial(MaterialId material) => CellularSimulation != null && _materialRoutes != null &&
            _materialRoutes.TryResolve(material, out var backend) && backend == MaterialSimulationBackendKind.Cellular;

        internal bool TryGetCellularQuantity(Vector3Int cell, MaterialId material, out uint quantity)
        {
            quantity = 0;
            return CellularSimulation != null && (CellularSimulation.TryGetQuantity(cell, material, out quantity)
                || RestingLiquids != null && RestingLiquids.TryGetQuantity(cell, material, out quantity));
        }

        public bool TryGetCellularContents(Vector3Int cell, out MaterialId primary, out uint primaryAmount,
            out uint steam, out uint toxicVapor)
        {
            primary = MaterialId.Empty;
            primaryAmount = steam = toxicVapor = 0;
            if (CellularSimulation == null || !CellularSimulation.TryGetOccupiedCell(cell, out primary, out primaryAmount)) return false;
            CellularSimulation.TryGetQuantity(cell, MaterialId.Steam, out steam);
            CellularSimulation.TryGetQuantity(cell, MaterialId.ToxicVapor, out toxicVapor);
            if (primary == MaterialId.Steam || primary == MaterialId.ToxicVapor) { primary = MaterialId.Empty; primaryAmount = 0; }
            return true;
        }

        public void VisitCellularOccupiedCells(Action<Vector3Int> visitor)
        {
            if (visitor == null) throw new ArgumentNullException(nameof(visitor));
            if (CellularSimulation == null) return;
            for (int i = 0; i < CellularSimulation.OccupiedCellCount; i++)
                if (CellularSimulation.TryGetOccupiedCoordinate(i, out Vector3Int cell)) visitor(cell);
        }

        internal bool IsCellularBlockingCell(Vector3Int cell) =>
            CellularSimulation != null && CellularSimulation.TryGetOccupiedCell(cell, out var material, out _)
            && ElementCellMaterialRules.OccupiesWholeCell(material);

        private bool IsBlockedForDynamicMaterial(Vector3Int cell) => IsEnvironmentBlockedForCells(cell)
            || RestingLiquids != null && RestingLiquids.BlocksSimulation(cell);

        private bool IsEnvironmentBlockedForCells(Vector3Int cell)
        {
            if (GetTerrainMaterial(cell) != MaterialId.Empty) return true;
            if (_sceneObstacles.TryGetValue(cell, out bool blocked)) return blocked;
            float size = _profile.CellSize;
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell, _profile.ChunkSize, out var key, out _);
            if (!_sceneChunks.TryGetValue(key, out bool hasGeometry))
            {
                // 空 Chunk 只做一次 Physics 查询。掠过薄墙的 Chunk 仍按实际相交的细格裁决。
                float span = size * _profile.ChunkSize;
                var center = _origin + (new Vector3(key.X, key.Y, key.Z) + Vector3.one * .5f) * span;
                hasGeometry = _solidBaker.HasSceneObstacle(center, Vector3.one * (span * .5f));
                if (_sceneChunkSlotsUsed == _sceneChunkKeys.Length) _sceneChunks.Remove(_sceneChunkKeys[_sceneChunkSlot]);
                else _sceneChunkSlotsUsed++;
                _sceneChunkKeys[_sceneChunkSlot] = key;
                _sceneChunkSlot = (_sceneChunkSlot + 1) % _sceneChunkKeys.Length;
                _sceneChunks[key] = hasGeometry;
            }
            if (!hasGeometry) return false;
            blocked = _solidBaker.HasSceneObstacle(_origin + ((Vector3)cell + Vector3.one*.5f)*size, Vector3.one*(size*.49f));
            // 旧槽在局部失效后可能提前淘汰同坐标的新值，重采只影响缓存命中率。
            if (_obstacleSlotsUsed == _obstacleKeys.Length) _sceneObstacles.Remove(_obstacleKeys[_obstacleSlot]);
            else _obstacleSlotsUsed++;
            _obstacleKeys[_obstacleSlot] = cell;
            _obstacleSlot = (_obstacleSlot + 1) % _obstacleKeys.Length;
            _sceneObstacles[cell] = blocked;
            return blocked;
        }

        private void WakeCellularTerrainChange(Vector3Int cell)
        {
            if (CellularSimulation == null) return;
            _sceneObstacles.Remove(cell);
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell, _profile.ChunkSize, out var key, out _);
            _sceneChunks.Remove(key);
            CellularSimulation.WakeNeighbors(cell);
            RestingLiquids?.WakeNeighbors(cell);
        }

        /// <summary>可编辑基底从单格挖空、恢复和材质替换走同一事务。填实前拒绝压掉任何真实材料。
        /// 数据与碰撞都提交完毕后才通知观察者，回调期间世界互斥仍有效。</summary>
        public bool TrySetTerrainMaterial(Vector3Int cell, MaterialId material)
        {
            if (_cellularTerrain == null || !isActiveAndEnabled ||
                (material != MaterialId.Empty && material != MaterialId.Rock && material != MaterialId.Soil) ||
                TryBeginMaterialTransaction() != MaterialTransferFailure.None) return false;
            try
            {
                if (GetTerrainMaterial(cell) == material) return false;
                if (material != MaterialId.Empty && (CellularSimulation.TryGetOccupiedCell(cell, out _, out _)
                    || RestingLiquids != null && RestingLiquids.TryGetCell(cell, out _, out _)
                    || TryGetCell(cell, out var old) && !old.IsEmpty)) return false;
                if (!TryPrepareMaterialTopologyChange(cell, material != MaterialId.Empty) || !_cellularTerrain.TrySet(cell, material)) return false;
                TerrainRevision++;
                NotifyMaterialTopologyChanged(cell);
                TerrainMaterialChanged?.Invoke(cell);
                return true;
            }
            catch { MarkMaterialTransactionFaulted(); throw; }
            finally { EndMaterialTransaction(); }
        }

        /// <summary>工具或法术已通过 Physics 命中世界时，沿法线向内取样，避免点正处于半开格边界而挖到空气。
        /// 返回实际移除的地形身份，物品掉落仍由调用方的玩法规则决定。</summary>
        public bool TryExcavateTerrain(RaycastHit hit, out MaterialId removed)
        {
            removed = MaterialId.Empty;
            if (hit.collider == null || _cellularTerrain == null || !_cellularTerrain.OwnsCollider(hit.collider)) return false;
            var cell = ElementWorldCoordinates.WorldToGlobalCell(hit.point - hit.normal * (_profile.CellSize * .001f),
                _origin, _profile.CellSize);
            MaterialId material = GetTerrainMaterial(cell);
            if (material == MaterialId.Empty || !TrySetTerrainMaterial(cell, MaterialId.Empty)) return false;
            removed = material;
            return true;
        }

        /// <summary>权威单格命令供法术/工具使用，数量完整接收。库存调用自己的预约端口以保留回滚边界。</summary>
        public bool TryDepositCellular(Vector3Int cell, MaterialId material, uint quantity) =>
            TryChangeCellular(cell, material, quantity, true);

        public bool TryRemoveCellular(Vector3Int cell, MaterialId material, uint quantity) =>
            TryChangeCellular(cell, material, quantity, false);

        private bool TryChangeCellular(Vector3Int cell, MaterialId material, uint quantity, bool deposit)
        {
            if (!OwnsCellularMaterial(material) || !isActiveAndEnabled ||
                TryBeginMaterialTransaction() != MaterialTransferFailure.None) return false;
            try
            {
                bool changed;
                if (RestingLiquids != null && RestingLiquids.TryGetCell(cell, out var ownerMaterial, out _))
                {
                    if (ownerMaterial != material) return false;
                    changed = deposit ? RestingLiquids.TryStageAdd(cell, material, quantity)
                        : RestingLiquids.TryStageRemove(cell, material, quantity);
                    if (changed) RestingLiquids.CommitStaged();
                }
                else changed = deposit ? CellularSimulation.TryDeposit(cell, material, quantity)
                    : CellularSimulation.TryRemove(cell, material, quantity);
                if (changed) PublishCellularChanges();
                return changed;
            }
            catch { MarkMaterialTransactionFaulted(); throw; }
            finally { EndMaterialTransaction(); }
        }

        private bool ValidCellularWrite(in ElementWriteRequest request)
        {
            Vector3 position = request.WorldPosition;
            if (request.TotalAmount == 0 || !float.IsFinite(position.x) || !float.IsFinite(position.y) ||
                !float.IsFinite(position.z) || !float.IsFinite(request.Radius) || request.Radius < 0 ||
                !TryGetDepositBounds(in request, out var lo, out var hi)) return false;
            if (request.Radius == 0)
                return AvailableForDeposit(lo, request.MaterialKind, true, out _) >= request.TotalAmount;
            return PlanVolumeDeposit(in request, lo, hi, true, out _, out _);
        }

        private uint AvailableForDeposit(Vector3Int cell, MaterialId material, bool mayDisplaceGas, out bool resting)
        {
            resting = RestingLiquids.TryGetCell(cell, out _, out _);
            if (resting) return RestingLiquids.GetLocalFreeCapacity(cell, material);
            return mayDisplaceGas ? CellularSimulation.GetFreeCapacity(cell, material)
                : CellularSimulation.GetLocalFreeCapacity(cell, material);
        }

        private bool TryGetDepositBounds(in ElementWriteRequest r, out Vector3Int lo, out Vector3Int hi)
        {
            lo = hi = default;
            var delta = Vector3.one * r.Radius;
            Vector3 a = (r.WorldPosition - delta - _origin) / _profile.CellSize;
            Vector3 b = (r.WorldPosition + delta - _origin) / _profile.CellSize;
            // float 在极端坐标失去一格精度；同时保证含上界的整数循环不会溢出。
            const float limit = 16777216f;
            if (a.x <= -limit || a.y <= -limit || a.z <= -limit || b.x >= limit || b.y >= limit || b.z >= limit) return false;
            lo = Vector3Int.FloorToInt(a); hi = Vector3Int.FloorToInt(b);
            var size = hi-lo+Vector3Int.one;
            long rows = (long)size.x * size.y;
            return rows <= _depositCells.Length && rows * size.z <= _depositCells.Length;
        }

        private bool ApplyCellularWrite(in ElementWriteRequest request)
        {
            if (!TryGetDepositBounds(in request, out var lo, out var hi)) return false;
            if (request.Radius == 0)
            {
                if (RestingLiquids == null || !RestingLiquids.TryGetCell(lo, out _, out _))
                    return CellularSimulation.TryDeposit(lo, request.MaterialKind, request.TotalAmount);
                if (!RestingLiquids.TryStageAdd(lo, request.MaterialKind, request.TotalAmount)) return false;
                RestingLiquids.CommitStaged();
                return true;
            }
            if (!PlanVolumeDeposit(in request, lo, hi, false, out int count, out double weights)) return false;
            uint remaining = request.TotalAmount;
            // 按原有半径和衰减权重分配。容量截断后再补到其余有空间的位置。
            for (int i = 0; i < count && remaining > 0; i++)
            {
                ref var entry = ref _depositCells[i];
                entry.Added = Math.Min(entry.Capacity, (uint)Math.Min(remaining, Math.Round(remaining * entry.Weight / weights)));
                remaining -= entry.Added;
                weights -= entry.Weight;
            }
            for (int i = 0; i < count && remaining > 0; i++)
            {
                ref var entry = ref _depositCells[i];
                uint extra = Math.Min(remaining, entry.Capacity - entry.Added);
                entry.Added += extra;
                remaining -= extra;
            }
            bool hasStagedResting = false;
            for (int i = 0; i < count; i++)
            {
                var entry = _depositCells[i];
                if (entry.Added == 0) continue;
                bool accepted = entry.Resting ? RestingLiquids.TryStageAdd(entry.Cell, request.MaterialKind, entry.Added)
                    : CellularSimulation.TryDeposit(entry.Cell, request.MaterialKind, entry.Added);
                if (accepted) { hasStagedResting |= entry.Resting; continue; }
                if (hasStagedResting) RestingLiquids.CancelStaged();
                for (int j = i - 1; j >= 0; j--)
                {
                    var prior = _depositCells[j];
                    if (!prior.Resting && prior.Added != 0 && !CellularSimulation.TryRemove(prior.Cell, request.MaterialKind, prior.Added))
                        throw new InvalidOperationException("无法撤销元胞投放事务。");
                }
                return false;
            }
            if (hasStagedResting) RestingLiquids.CommitStaged();
            return true;
        }

        private bool PlanVolumeDeposit(in ElementWriteRequest request, Vector3Int lo, Vector3Int hi,
            bool checkOnly, out int count, out double weights)
        {
            int availableSlots = CellularSimulation.FreeResidentSlots;
            int availableRestingEdits = RestingLiquids.MaximumStagedCells;
            count = 0;
            weights = 0;
            ulong capacity = 0;
            for (int z = lo.z; z <= hi.z; z++)
            for (int y = lo.y; y <= hi.y; y++)
            for (int x = lo.x; x <= hi.x; x++)
            {
                var cell = new Vector3Int(x, y, z);
                float weight = ElementWorldWriteProcessor.CalculateWeight(cell, in request, _origin, _profile.CellSize);
                if (weight <= 0) continue;
                uint room = AvailableForDeposit(cell, request.MaterialKind, false, out bool resting);
                if (room == 0) continue;
                if (resting)
                {
                    if (availableRestingEdits == 0) continue;
                    availableRestingEdits--;
                }
                else if (!CellularSimulation.TryGetOccupiedCell(cell, out _, out _))
                {
                    if (availableSlots == 0) continue;
                    availableSlots--;
                }
                capacity += room;
                if (checkOnly)
                {
                    if (capacity >= request.TotalAmount) return true;
                    continue;
                }
                _depositCells[count++] = new DepositCell { Cell = cell, Capacity = room, Weight = weight, Resting = resting };
                weights += weight;
            }
            return capacity >= request.TotalAmount;
        }

        private void StepCellularWorld()
        {
            RestingCellsProcessedLastTick = RestingLiquids.Step(_cellDefinition.RestingBudget, CellularSimulation);
            ProcessCellularFireContacts();
            CellularCellsProcessedLastTick = CellularSimulation.Step(_cellDefinition.WorkBudget);
            PrepareCellularChanges();
        }

        // Fire 继续使用旧强度时钟。用已有的灭火公式处理强度，再按本世界的格容量折回水的 GMU。
        // 火和产物在同一个世界锁内提交；若产物放不下，恢复刚扣掉的火。
        private void ProcessCellularFireContacts()
        {
            CollectAndSortGridFireChunks();
            int count = CollectGridFireReactionSamples();
            if (_materialReactions.TryResolve(MaterialId.Fire, MaterialId.Water, out var rule) && rule == ElementReactionId.Extinguish)
                ProcessCellularExtinguish(count, rule);
            StepCellularCombustion(_fireReactionSamples, count);
        }

        private void ProcessCellularExtinguish(int count, ElementReactionId rule)
        {
            var tuning = new ElementReactionTuningSnapshot(_settings.Extinguish, default, default, default, default, 0f, 0f);
            for (int i = 0; i < count; i++)
            {
                var source = _fireReactionSamples[i];
                foreach (var offset in FireContacts)
                {
                    Vector3Int cell = source.GlobalCell + offset;
                    if (!TryGetCellularQuantity(cell, MaterialId.Water, out uint water)) continue;
                    var contact = new MaterialContactSnapshot(MaterialId.Fire, source.Amount, MaterialId.Water,
                        (byte)Math.Max(1UL, water * 255UL / CellularCellCapacity));
                    if (!ElementReactionEvaluator.TryEvaluateMaterialContact(in contact, _tickInterval, rule, in tuning,
                        out var result) || result.FirstConsumedGmu <= 0 || result.SecondConsumedGmu <= 0) continue;
                    uint used = (uint)Math.Min(water, ((ulong)result.SecondConsumedGmu * CellularCellCapacity + 254) / 255);
                    if (RestingLiquids != null && RestingLiquids.TryGetCell(cell, out _, out _)
                        && !RestingLiquids.TryMaterialize(cell, CellularSimulation)) continue;
                    var delta = new ElementCellDeltaRequest(source.GlobalCell, MaterialId.Fire, (byte)result.FirstConsumedGmu);
                    if (!TryApplyGridFireDelta(in delta)) break;
                    if (!CellularSimulation.TryConvert(cell, MaterialId.Water, MaterialId.Steam, used))
                    {
                        var restore = new ElementCellAddRequest(source.GlobalCell, MaterialId.Fire, delta.AmountToRemove);
                        if (!TryApplyInternalElementAdd(in restore)) throw new InvalidOperationException("无法撤销水火反应。");
                    }
                    break;
                }
            }
        }

        internal void PublishCellularChanges()
        {
            if (CellularSimulation == null) return;
            PrepareCellularChanges();
            NotifyCellularObservers();
        }

        private void PrepareCellularChanges()
        {
            _cellPublications.Clear();
            _publicationSet.Clear();
            while (CellularSimulation.TryDequeueChanged(out var cell))
            {
                RestingLiquids?.WakeNeighbors(cell);
                bool solid = IsCellularBlockingCell(cell);
                if (_cellularCollisions.Set(cell, solid) && TryResolveResidentCell(cell, out var chunk, out var local))
                    chunk.Grid.SetSolid(local, solid || IsEnvironmentBlockedForCells(cell));
                if (_publicationSet.Add(cell)) _cellPublications.Add(cell);
            }
            if (RestingLiquids != null)
                while (RestingLiquids.TryDequeueChanged(out var restingCell))
                {
                    CellularSimulation.WakeNeighbors(restingCell);
                    if (_publicationSet.Add(restingCell)) _cellPublications.Add(restingCell);
                }
            _cellularCollisions.Commit();
        }

        // Fire Solver 也要先走完本步再允许观察者读结果；碰撞更新提前为 Solver 提供准确边界。
        private void NotifyCellularObservers()
        {
            if (CellularContentsChanged != null)
                foreach (var cell in _cellPublications) CellularContentsChanged.Invoke(cell);
        }
    }
}
