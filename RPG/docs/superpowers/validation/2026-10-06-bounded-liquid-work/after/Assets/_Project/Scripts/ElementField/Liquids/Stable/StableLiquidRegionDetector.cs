using System;
using System.Collections.Generic;
using Game.Materials;
using Unity.Profiling;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 用有界 Flood Fill 搜索封闭湖盆；底层必须满格，顶层可以是接近平齐的
    /// 部分填充格。列底可高低不同，但自由水面必须处在同一高度附近。
    /// 搜索及安静期仍由动态 Store 持有数量，完整复核后才一次性切换 Owner。
    /// </summary>
    public sealed class StableLiquidRegionDetector
    {
        private static readonly ProfilerMarker DiscoveryMarker =
            new ProfilerMarker("GridLiquid.AutoRegion.Discover");
        private static readonly ProfilerMarker ValidationMarker =
            new ProfilerMarker("GridLiquid.AutoRegion.Validate");
        private static readonly ProfilerMarker PrepareMarker =
            new ProfilerMarker("GridLiquid.AutoRegion.Prepare");
        private static readonly ProfilerMarker ExtractMarker =
            new ProfilerMarker("GridLiquid.AutoRegion.Extract");

        private enum ColumnKind : byte { Full, Solid, Open }

        private readonly LiquidVolumeStore _store;
        private readonly StableLiquidRegionRuntime _regions;
        private readonly MaterialId[] _materials;
        private readonly ElementChunkKey[] _candidateChunks;
        private readonly uint[] _candidateVersions;
        private readonly HashSet<ElementChunkKey> _candidateChunkSet;
        private readonly int[] _seedCursors;
        private readonly int[] _seedScanRemaining;
        private readonly uint[] _seedScanVersions, _seedScanStaticVersions;
        private readonly Vector3Int[] _rejectedMinimum, _rejectedMaximum;
        private readonly uint[] _rejectedStoreVersions, _rejectedStaticVersions;
        private readonly bool[] _hasRejectedBounds;
        private readonly HashSet<Vector3Int>[] _oversizedCells;
        private readonly uint[] _oversizedStoreVersions, _oversizedStaticVersions;
        private readonly bool[] _oversizedScanSuspended;
        private readonly int _oversizedCacheCapacity;
        private readonly Vector2Int[] _columns;
        private readonly int[] _columnFloors, _floorByColumn;
        private readonly bool[] _footprint;
        private readonly HashSet<long> _visitedColumns;
        private readonly int _maximumCells, _validationPerTick, _quietThreshold;
        private int _quietTicks, _cooldown, _materialCursor, _validationCursor;
        private int _candidateChunkCount, _candidateMaterialIndex;
        private int _floorY, _ceilingY, _head, _tail, _nextNeighbor, _discoveryTicks;
        private int _candidateVolumeCells;
        private ulong _surfaceQuantity;
        private uint _surfaceMinimum, _surfaceMaximum;
        private int _oversizedCacheColumnCursor, _oversizedCacheY, _oversizedCacheTicks;
        private Vector3Int _minimum, _maximum;
        private MaterialId _candidateMaterial;
        private uint _staticVersion, _checkedSurfaceVersion;
        private bool _dependencyCapacityExceeded;
        private bool _discovering, _validating, _cachingOversize, _openBoundary;
        private bool _variableFloor;

        public StableLiquidRegionDetector(LiquidVolumeStore store,
            StableLiquidRegionRuntime regions, MaterialId[] eligibleMaterials,
            int maximumCells, int validationPerTick, int quietTicks)
        {
            if (maximumCells < 1 || maximumCells > 65536 || validationPerTick < 1
                || quietTicks < 1)
                throw new ArgumentOutOfRangeException(nameof(maximumCells));
            _store = store;
            _regions = regions;
            _materials = eligibleMaterials;
            _maximumCells = maximumCells;
            _validationPerTick = validationPerTick;
            _quietThreshold = quietTicks;
            _candidateChunks = new ElementChunkKey[maximumCells];
            _candidateVersions = new uint[maximumCells];
            _candidateChunkSet = new HashSet<ElementChunkKey>(maximumCells);
            _columns = new Vector2Int[maximumCells];
            _columnFloors = new int[maximumCells];
            _floorByColumn = new int[maximumCells];
            _footprint = new bool[maximumCells];
            // 每个有效列最多贡献四个被检查的邻列；预留上界避免 Tick 内 HashSet 扩容。
            _visitedColumns = new HashSet<long>(maximumCells * 4 + 1);
            _seedCursors = new int[eligibleMaterials.Length];
            _seedScanRemaining = new int[eligibleMaterials.Length];
            _seedScanVersions = new uint[eligibleMaterials.Length];
            _seedScanStaticVersions = new uint[eligibleMaterials.Length];
            for (int i = 0; i < eligibleMaterials.Length; i++)
                _seedScanVersions[i] = uint.MaxValue;
            _rejectedMinimum = new Vector3Int[eligibleMaterials.Length];
            _rejectedMaximum = new Vector3Int[eligibleMaterials.Length];
            _rejectedStoreVersions = new uint[eligibleMaterials.Length];
            _rejectedStaticVersions = new uint[eligibleMaterials.Length];
            _hasRejectedBounds = new bool[eligibleMaterials.Length];
            // 大湖超限后记住已探索的水格，避免下一粒水重新 Flood Fill 同一片区域。
            // 缓存满时暂停该材料当前版本的自动识别；动态 Store 仍完整负责模拟。
            _oversizedCacheCapacity = Math.Min(maximumCells * 4, 65536);
            _oversizedCells = new HashSet<Vector3Int>[eligibleMaterials.Length];
            _oversizedStoreVersions = new uint[eligibleMaterials.Length];
            _oversizedStaticVersions = new uint[eligibleMaterials.Length];
            _oversizedScanSuspended = new bool[eligibleMaterials.Length];
            for (int i = 0; i < eligibleMaterials.Length; i++)
                _oversizedCells[i] = new HashSet<Vector3Int>(_oversizedCacheCapacity);
        }

        public uint CompressedRegions { get; private set; }
        public uint PartialSurfaceCompressedRegions { get; private set; }
        public uint InvalidatedCandidates { get; private set; }
        public uint RejectedCandidates { get; private set; }
        public ulong SeedInspections { get; private set; }
        public bool HasCandidate => _discovering || _validating || _cachingOversize;
        public int CandidateAgeTicks => _discovering ? _discoveryTicks
            : _validating ? _quietTicks : _cachingOversize ? _oversizedCacheTicks : 0;

        public void Step()
        {
            if (_materials.Length == 0) return;
            if (_discovering)
            {
                _discoveryTicks++;
                using (DiscoveryMarker.Auto()) DiscoverSlice();
                return;
            }
            if (_validating)
            {
                if (!CandidateUnchanged()) { Cancel(); return; }
                if (++_quietTicks >= _quietThreshold)
                {
                    using (ValidationMarker.Auto()) ValidateSlice();
                }
                return;
            }
            if (_cachingOversize) { CacheOversizedSlice(); return; }
            if (_cooldown > 0) { _cooldown--; return; }
            StartCandidate();
        }

        private void StartCandidate()
        {
            int materialIndex = _materialCursor++ % _materials.Length;
            MaterialId material = _materials[materialIndex];
            RefreshOversizedCache(materialIndex);
            if (_oversizedScanSuspended[materialIndex]) return;
            uint materialVersion = _store.GetMaterialSurfaceVersion(material);
            if (_seedScanVersions[materialIndex] != materialVersion
                || _seedScanStaticVersions[materialIndex] != _store.StaticVersion)
            {
                // 不相关液体的写入不应让本材料从第一个 Seed 重扫；
                // 混合和数量仍由发现后的逐格验证及原子提取负责。
                _seedScanVersions[materialIndex] = materialVersion;
                _seedScanStaticVersions[materialIndex] = _store.StaticVersion;
                _seedScanRemaining[materialIndex] = _store.ResidentCellCount;
                _seedCursors[materialIndex] = 0;
            }
            if (_seedScanRemaining[materialIndex] == 0) return;
            int budget = _validationPerTick;
            Vector3Int seed = default;
            bool found = false;
            while (budget > 0 && _seedScanRemaining[materialIndex] > 0)
            {
                found = _store.TryFindFullCell(material, ref _seedCursors[materialIndex],
                    budget, out seed, out int inspected, out int traversed);
                SeedInspections += (uint)inspected;
                budget -= inspected;
                _seedScanRemaining[materialIndex] = Mathf.Max(0,
                    _seedScanRemaining[materialIndex] - traversed);
                if (!found) break;
                if (!IsRejected(materialIndex, seed)) break;
            }
            if (!found || IsRejected(materialIndex, seed))
            {
                _cooldown = _store.ResidentChunkCount == 0 ? 10 : 0;
                return;
            }

            _candidateMaterial = material;
            _candidateMaterialIndex = materialIndex;
            _candidateChunkSet.Clear();
            _candidateChunkCount = 0;
            _dependencyCapacityExceeded = false;
            _staticVersion = _store.StaticVersion;
            _checkedSurfaceVersion = _store.SurfaceVersion;
            TrackCellVersion(seed);
            GrowY(seed, material, out _floorY, out _ceilingY);
            _minimum = new Vector3Int(seed.x, _floorY, seed.z);
            _maximum = new Vector3Int(seed.x + 1, _ceilingY, seed.z + 1);
            if (ClassifyColumn(seed.x, seed.z, out int seedFloor) != ColumnKind.Full)
            {
                RememberRejected(materialIndex);
                return;
            }
            _floorY = seedFloor;
            _minimum.y = seedFloor;
            _visitedColumns.Clear();
            _visitedColumns.Add(ColumnKey(seed.x, seed.z));
            _columns[0] = new Vector2Int(seed.x, seed.z);
            _columnFloors[0] = seedFloor;
            _head = 0;
            _tail = 1;
            _candidateVolumeCells = _ceilingY - seedFloor;
            _variableFloor = false;
            _nextNeighbor = 0;
            _openBoundary = false;
            _discoveryTicks = 0;
            _discovering = true;
        }

        private void GrowY(Vector3Int seed, MaterialId material,
            out int floor, out int ceiling)
        {
            floor = seed.y;
            ceiling = seed.y + 1;
            for (int n = 1; n < 64 && IsFull(seed + Vector3Int.down * n, material); n++)
                floor--;
            for (int n = 1; n < 64 && IsFull(seed + Vector3Int.up * n, material); n++)
                ceiling++;
            // 一层未满水面也属于湖的数量；只纳入紧贴满格的那一层，
            // 不跨过空格或异种材料拼接远处的液体。
            var surface = new Vector3Int(seed.x, ceiling, seed.z);
            TrackCellVersion(surface);
            if (_store.TryGetQuantity(surface, material, out uint amount)
                && amount < _store.CapacityGmu
                && !_store.HasOtherMaterial(surface, material)) ceiling++;
        }

        private void DiscoverSlice()
        {
            // 跨 Tick 只检查已读取 Chunk 的版本。远处同种材料的持续施法
            // 不应让平静湖永远无法稳定化；局部混合、顶边界和地形变化仍取消候选。
            if (!CandidateUnchanged())
            {
                Cancel();
                return;
            }
            int budget = _validationPerTick;
            // 单列最多向下搜索 64 格；预算按最坏检查成本扣除，避免浅岸线
            // 让同一 Tick 实际做远多于配置值的地形和材料查询。
            const int inspectionCost = 66;
            while (budget > 0 && _head < _tail)
            {
                if (_nextNeighbor == 4)
                {
                    _head++;
                    _nextNeighbor = 0;
                    continue;
                }
                Vector2Int current = _columns[_head];
                int side = _nextNeighbor++;
                int x = current.x + (side == 0 ? -1 : side == 1 ? 1 : 0);
                int z = current.y + (side == 2 ? -1 : side == 3 ? 1 : 0);
                long key = ColumnKey(x, z);
                if (_visitedColumns.Contains(key)) { budget--; continue; }
                if (_visitedColumns.Count >= _maximumCells * 4) { RejectDiscovery(); return; }
                _visitedColumns.Add(key);
                ColumnKind kind = ClassifyColumn(x, z, out int floor);
                budget -= inspectionCost;
                if (kind == ColumnKind.Open) _openBoundary = true;
                if (kind != ColumnKind.Full) continue;
                if (_tail >= _columns.Length
                    || _candidateVolumeCells > _maximumCells - (_ceilingY - floor))
                { RejectDiscovery(); return; }
                _columns[_tail] = new Vector2Int(x, z);
                _columnFloors[_tail++] = floor;
                _candidateVolumeCells += _ceilingY - floor;
                if (floor != _floorY) _variableFloor = true;
                _minimum.y = Math.Min(_minimum.y, floor);
                _minimum.x = Math.Min(_minimum.x, x);
                _minimum.z = Math.Min(_minimum.z, z);
                _maximum.x = Math.Max(_maximum.x, x + 1);
                _maximum.z = Math.Max(_maximum.z, z + 1);
            }
            if (_head == _tail) CompleteDiscovery();
        }

        private void CompleteDiscovery()
        {
            _discovering = false;
            long width = (long)_maximum.x - _minimum.x;
            long depth = (long)_maximum.z - _minimum.z;
            long volume = width * depth * (_ceilingY - _minimum.y);
            if (_openBoundary || _candidateVolumeCells < 32 || volume > _maximumCells)
            {
                RememberRejected(_candidateMaterialIndex);
                return;
            }
            int area = (int)(width * depth);
            Array.Clear(_footprint, 0, area);
            Array.Fill(_floorByColumn, int.MaxValue, 0, area);
            for (int i = 0; i < _tail; i++)
            {
                Vector2Int column = _columns[i];
                int index = (column.y - _minimum.z) * (int)width
                    + column.x - _minimum.x;
                _footprint[index] = true;
                _floorByColumn[index] = _columnFloors[i];
            }
            if (!CandidateUnchanged()) { Cancel(); return; }
            _quietTicks = 0;
            _validationCursor = 0;
            _surfaceQuantity = 0;
            _surfaceMinimum = uint.MaxValue;
            _surfaceMaximum = 0;
            _validating = true;
        }

        private ColumnKind ClassifyColumn(int x, int z, out int floor)
        {
            floor = int.MaxValue;
            Vector3Int top = new Vector3Int(x, _ceilingY - 1, z);
            TrackCellVersion(top);
            if (_store.IsSolid(top)) return ColumnKind.Solid;
            TrackCellVersion(top + Vector3Int.up);
            if (!_store.TryGetQuantity(top, _candidateMaterial, out _)
                || _store.HasOtherMaterial(top, _candidateMaterial)
                || _store.IsSolid(top + Vector3Int.up)
                || _store.HasAnyLiquid(top + Vector3Int.up)) return ColumnKind.Open;
            int lowest = _ceilingY - 64;
            for (int y = _ceilingY - 2; y >= lowest; y--)
            {
                var cell = new Vector3Int(x, y, z);
                TrackCellVersion(cell);
                if (_store.IsSolid(cell))
                {
                    floor = y + 1;
                    return ColumnKind.Full;
                }
                if (!IsFull(cell, _candidateMaterial)) return ColumnKind.Open;
            }
            return ColumnKind.Open;
        }

        private void TrackCellVersion(Vector3Int cell)
        {
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell, _store.ChunkSize,
                out ElementChunkKey key, out _);
            if (_candidateChunkSet.Contains(key)) return;
            // 固定容量，溢出时保留动态表示；不能扩容产生 GC，也不能遗漏依赖后误压缩。
            if (_candidateChunkCount == _candidateChunks.Length)
            {
                _dependencyCapacityExceeded = true;
                return;
            }
            _candidateChunkSet.Add(key);
            _candidateChunks[_candidateChunkCount] = key;
            _candidateVersions[_candidateChunkCount++] = _store.GetChunkVersion(key);
        }

        private bool CandidateUnchanged()
        {
            if (_dependencyCapacityExceeded || _store.StaticVersion != _staticVersion) return false;
            // 世界没有任何数量变化时 O(1) 返回；有变化才检查本候选的局部依赖。
            if (_store.SurfaceVersion == _checkedSurfaceVersion) return true;
            for (int i = 0; i < _candidateChunkCount; i++)
                if (_store.GetChunkVersion(_candidateChunks[i]) != _candidateVersions[i])
                    return false;
            _checkedSurfaceVersion = _store.SurfaceVersion;
            return true;
        }

        private void ValidateSlice()
        {
            if (!CandidateUnchanged()) { Cancel(); return; }
            int width = _maximum.x - _minimum.x;
            int height = _maximum.y - _minimum.y;
            int depth = _maximum.z - _minimum.z;
            int total = width * height * depth;
            int end = Mathf.Min(total, _validationCursor + _validationPerTick);
            for (int i = _validationCursor; i < end; i++)
            {
                int x = i % width;
                int y = i / width % height;
                int z = i / (width * height);
                int floor = _floorByColumn[z * width + x];
                if (floor == int.MaxValue) continue;
                Vector3Int cell = _minimum + new Vector3Int(x, y, z);
                if (cell.y < floor)
                {
                    if (_store.IsSolid(cell)) continue;
                    RememberRejected(_candidateMaterialIndex);
                    return;
                }
                bool topLayer = y == height - 1;
                uint topAmount = 0;
                bool liquidValid = topLayer
                    ? _store.TryGetQuantity(cell, _candidateMaterial, out topAmount)
                        && !_store.HasOtherMaterial(cell, _candidateMaterial)
                    : IsFull(cell, _candidateMaterial);
                if (!liquidValid || _store.IsSolid(cell)
                    || (cell.y == floor && !_store.IsSolid(cell + Vector3Int.down))
                    || (topLayer && (_store.IsSolid(cell + Vector3Int.up)
                        || _store.HasAnyLiquid(cell + Vector3Int.up)))
                    || (!Covered(x - 1, z, cell.y, width, depth)
                        && !_store.IsSolid(cell + Vector3Int.left))
                    || (!Covered(x + 1, z, cell.y, width, depth)
                        && !_store.IsSolid(cell + Vector3Int.right))
                    || (!Covered(x, z - 1, cell.y, width, depth)
                        && !_store.IsSolid(cell + Vector3Int.back))
                    || (!Covered(x, z + 1, cell.y, width, depth)
                        && !_store.IsSolid(cell + Vector3Int.forward)))
                {
                    RememberRejected(_candidateMaterialIndex);
                    return;
                }
                if (!topLayer) continue;
                _surfaceQuantity += topAmount;
                _surfaceMinimum = Math.Min(_surfaceMinimum, topAmount);
                _surfaceMaximum = Math.Max(_surfaceMaximum, topAmount);
            }
            _validationCursor = end;
            if (end < total) return;
            // 将接近平齐的自由面摊成统一水位，避免压缩瞬间把高低差很大的
            // 水堆突然拉平。总 GMU 则仍由已复核的每个顶格精确相加。
            uint capacity = (uint)_store.CapacityGmu;
            if (_surfaceMinimum < Math.Max(1u, capacity / 4u)
                || _surfaceMaximum - _surfaceMinimum > Math.Max(1u, capacity / 8u))
            {
                RememberRejected(_candidateMaterialIndex);
                return;
            }
            ulong quantity = (ulong)(_candidateVolumeCells - _tail) * capacity
                + _surfaceQuantity;
            StableLiquidRegion prepared = null;
            using (PrepareMarker.Auto())
            {
                bool ready = _variableFloor
                    ? _regions.TryPrepareExtractedHeightfield(_candidateMaterial, _minimum,
                        _maximum, _floorByColumn, quantity, out prepared)
                    : _regions.TryPrepareExtractedFootprint(_candidateMaterial, _minimum,
                        _maximum, _footprint, quantity, out prepared);
                if (!ready)
                {
                    RememberRejected(_candidateMaterialIndex);
                    return;
                }
            }
            ulong extracted = 0;
            using (ExtractMarker.Auto())
            {
                bool extractedAll = CandidateUnchanged()
                    && (_variableFloor
                        ? _store.TryExtractLevelHeightfield(_minimum, _maximum,
                            _floorByColumn, _candidateMaterial, quantity, out extracted)
                        : _store.TryExtractLevelFootprint(_minimum, _maximum,
                            _footprint, _candidateMaterial, quantity, out extracted));
                if (!extractedAll)
                {
                    Cancel();
                    return;
                }
            }
            if (extracted != quantity)
                throw new InvalidOperationException("稳定区域掩码提取与准备数量不一致。");
            _regions.CommitExtracted(prepared);
            _validating = false;
            // 成功接管后候选格已从动态 Store 提取，下一 Tick 可以立即检查另一种
            // 材料。额外 30 Tick 冷却会让三种独立液体湖串行等待约六秒。
            _cooldown = 0;
            CompressedRegions++;
            if (_surfaceMinimum < capacity) PartialSurfaceCompressedRegions++;
        }

        private bool Covered(int x, int z, int y, int width, int depth) =>
            x >= 0 && x < width && z >= 0 && z < depth
            && _floorByColumn[z * width + x] <= y;

        private bool IsFull(Vector3Int cell, MaterialId material)
        {
            TrackCellVersion(cell);
            return _store.TryGetQuantity(cell, material, out uint amount)
                && amount == _store.CapacityGmu;
        }

        private bool IsRejected(int materialIndex, Vector3Int seed)
        {
            if (_oversizedCells[materialIndex].Contains(seed)) return true;
            if (!_hasRejectedBounds[materialIndex]) return false;
            if (_rejectedStoreVersions[materialIndex] != _store.SurfaceVersion
                || _rejectedStaticVersions[materialIndex] != _store.StaticVersion)
            {
                _hasRejectedBounds[materialIndex] = false;
                return false;
            }
            Vector3Int minimum = _rejectedMinimum[materialIndex];
            Vector3Int maximum = _rejectedMaximum[materialIndex];
            return seed.x >= minimum.x && seed.x < maximum.x
                && seed.y >= minimum.y && seed.y < maximum.y
                && seed.z >= minimum.z && seed.z < maximum.z;
        }

        private void RejectDiscovery()
        {
            int materialIndex = _candidateMaterialIndex;
            RefreshOversizedCache(materialIndex);
            RememberRejected(materialIndex);
            // 大湖可能包含数万格，负面缓存也必须分 Tick 写入，不能在发现
            // 超限的同一帧再把整片已发现水域塞入 HashSet。
            _oversizedCacheColumnCursor = 0;
            _oversizedCacheY = _columnFloors[0];
            _oversizedCacheTicks = 0;
            _cachingOversize = true;
        }

        private void CacheOversizedSlice()
        {
            _oversizedCacheTicks++;
            int materialIndex = _candidateMaterialIndex;
            if (_store.SurfaceVersion != _oversizedStoreVersions[materialIndex]
                || _store.StaticVersion != _oversizedStaticVersions[materialIndex])
            {
                RefreshOversizedCache(materialIndex);
                _cachingOversize = false;
                _cooldown = 30;
                return;
            }
            HashSet<Vector3Int> cache = _oversizedCells[materialIndex];
            int budget = _validationPerTick;
            while (budget-- > 0 && _oversizedCacheColumnCursor < _tail)
            {
                if (cache.Count >= _oversizedCacheCapacity)
                {
                    _oversizedScanSuspended[materialIndex] = true;
                    break;
                }
                Vector2Int column = _columns[_oversizedCacheColumnCursor];
                cache.Add(new Vector3Int(column.x, _oversizedCacheY, column.y));
                if (++_oversizedCacheY < _ceilingY) continue;
                _oversizedCacheColumnCursor++;
                if (_oversizedCacheColumnCursor < _tail)
                    _oversizedCacheY = _columnFloors[_oversizedCacheColumnCursor];
            }
            if (_oversizedScanSuspended[materialIndex]
                || _oversizedCacheColumnCursor >= _tail)
            {
                _cachingOversize = false;
                _cooldown = 30;
            }
        }

        private void RefreshOversizedCache(int materialIndex)
        {
            uint storeVersion = _store.SurfaceVersion;
            uint staticVersion = _store.StaticVersion;
            if (_oversizedStoreVersions[materialIndex] == storeVersion
                && _oversizedStaticVersions[materialIndex] == staticVersion) return;
            _oversizedCells[materialIndex].Clear();
            _oversizedScanSuspended[materialIndex] = false;
            _oversizedStoreVersions[materialIndex] = storeVersion;
            _oversizedStaticVersions[materialIndex] = staticVersion;
        }

        private void RememberRejected(int materialIndex)
        {
            // 拒绝缓存随 Store／地形版本失效；关墙或注水后仍可重新发现同一湖盆。
            _rejectedMinimum[materialIndex] = _minimum;
            _rejectedMaximum[materialIndex] = _maximum;
            _rejectedStoreVersions[materialIndex] = _store.SurfaceVersion;
            _rejectedStaticVersions[materialIndex] = _store.StaticVersion;
            _hasRejectedBounds[materialIndex] = true;
            _discovering = false;
            _validating = false;
            _cooldown = 0;
            RejectedCandidates++;
        }

        private void Cancel()
        {
            _discovering = false;
            _validating = false;
            _cooldown = 30;
            InvalidatedCandidates++;
        }

        private static long ColumnKey(int x, int z) => ((long)x << 32) | (uint)z;
    }
}
