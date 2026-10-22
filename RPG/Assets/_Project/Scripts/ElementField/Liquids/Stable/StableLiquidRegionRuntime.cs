using System;
using System.Collections.Generic;
using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public enum StableLiquidExpansionFailure : byte
    {
        None,
        RegionTooLarge,
        OccupiedTarget,
        DynamicOverlap,
        ChunkBudget,
        SolidConflict,
        SurfaceBudget,
        SurfaceHandoffBusy,
        CommitFailure,
    }

    public enum StableLiquidRecaptureFailure : byte
    {
        None,
        SurfaceBudget,
        OtherRegion,
        TopologyChanged,
        OpenBoundary,
        MixedMaterial,
        Capacity,
        UnevenSurface,
        CommitFailure,
    }

    /// <summary>
    /// 已知稳定湖盆与动态格子共用一个世界时钟。数组在初始化期预留，运行时只改数量和版本。
    /// 不把相机可见性用作数量权威，也不为深水创建 PBF 粒子。
    /// </summary>
    public sealed class StableLiquidRegionRuntime
    {
        private struct Outlet
        {
            public int RegionId;
            public int RegionSlot, HandleId;
            public Vector3Int TargetCell;
            public float HeightCells, AreaCells, Conductance, Fractional;
            public bool Open, Suspended, SuspendedOpen, WasDynamicMapped;
        }

        private sealed class SectorLink
        {
            public int FirstSlot, SecondSlot, FirstId, SecondId;
            public Vector3Int[] FirstCells, SecondCells;
            public int Cursor;
            public float Fractional;
            public int Length => FirstCells.Length;
        }

        private readonly StableLiquidRegion[] _regions;
        private readonly int[] _sectorGroupIds;
        private readonly bool[] _sectorized;
        private readonly SectorLink[] _sectorLinks;
        private readonly bool[] _reportedInternalGeometry;
        private readonly int[] _surfaceHandoffUntilFrame;
        private readonly ulong[] _recaptureObservedSignatures;
        private readonly ulong[] _recaptureRejectedSignatures;
        private readonly int[] _recaptureChangedTicks;
        private readonly Vector3Int[] _recaptureCells;
        private readonly uint[] _recaptureAmounts;
        private readonly float[] _mergedSurfaceHeights;
        private readonly uint[] _mergedSurfaceTopologies, _mergedSurfaceVersions;
        private readonly int[] _mergedSurfaceFrames;
        private readonly bool[] _mergedSurfaceResults;
        private readonly Outlet[] _outlets;
        private readonly LiquidVolumeStore _store;
        private readonly List<Vector3Int> _terrainRelocationTargets=new List<Vector3Int>(4096);
        private readonly LiquidActivityScheduler _activity;
        private readonly LiquidTransferLedger _ledger = new LiquidTransferLedger();
        private readonly Dictionary<Vector3Int, int> _dynamicOutlets;
        private readonly HashSet<ElementChunkKey> _expansionChunks, _expansionWetChunks;
        private readonly int _maximumRegionCells;
        private readonly int _maximumSurfacePatches;
        private readonly int _maximumImmediateExpansionCells;
        private readonly int _maximumImmediateExpansionSurfaceChunks;
        private readonly Vector3 _origin;
        private readonly float _cellSize;
        private int _regionCount, _activeRegionCount, _outletCount, _activeOutletCount;
        private int _sectorLinkCount;
        private int _nextRegionId, _nextOutletId;
        private int _sampleRegionCursor, _sampleCellCursor;
        private int _recaptureCursor, _simulationTick, _expandedSectorCount;
        private uint _surfaceVersion;

        public StableLiquidRegionRuntime(int maximumRegions, int maximumOutlets,
            int maximumRegionCells, LiquidVolumeStore store, LiquidActivityScheduler activity,
            Vector3 origin, float cellSize, int maximumImmediateExpansionCells = 1024,
            int maximumImmediateExpansionSurfaceChunks = 4,
            int maximumSurfacePatches = 1024)
        {
            _regions = new StableLiquidRegion[maximumRegions];
            _sectorGroupIds = new int[maximumRegions];
            _sectorized = new bool[maximumRegions];
            // 平面 Chunk 网格最多约两条内部边/片；四倍容量给不规则占地留余量。
            _sectorLinks = new SectorLink[checked(maximumRegions * 4)];
            _reportedInternalGeometry = new bool[maximumRegions];
            _surfaceHandoffUntilFrame = new int[maximumRegions];
            Array.Fill(_surfaceHandoffUntilFrame, -1);
            _recaptureObservedSignatures = new ulong[maximumRegions];
            _recaptureRejectedSignatures = new ulong[maximumRegions];
            _recaptureChangedTicks = new int[maximumRegions];
            _recaptureCells = new Vector3Int[Math.Max(1, maximumImmediateExpansionCells)];
            _recaptureAmounts = new uint[_recaptureCells.Length];
            _mergedSurfaceHeights = new float[maximumRegions];
            _mergedSurfaceTopologies = new uint[maximumRegions];
            _mergedSurfaceVersions = new uint[maximumRegions];
            _mergedSurfaceFrames = new int[maximumRegions];
            _mergedSurfaceResults = new bool[maximumRegions];
            Array.Fill(_mergedSurfaceFrames, -1);
            _outlets = new Outlet[maximumOutlets];
            _maximumRegionCells = maximumRegionCells;
            _maximumSurfacePatches = Math.Max(1, maximumSurfacePatches);
            _maximumImmediateExpansionCells = Math.Max(1, maximumImmediateExpansionCells);
            _maximumImmediateExpansionSurfaceChunks = Math.Max(1,
                maximumImmediateExpansionSurfaceChunks);
            // 异种液体接触也会局部展开，预留 scratch 避免在 Solver 热路径分配 HashSet。
            _expansionChunks = new HashSet<ElementChunkKey>(_maximumImmediateExpansionCells);
            _expansionWetChunks = new HashSet<ElementChunkKey>(_maximumImmediateExpansionCells);
            _dynamicOutlets = new Dictionary<Vector3Int, int>(maximumOutlets);
            _store = store;
            _activity = activity;
            _origin = origin;
            _cellSize = cellSize;
        }

        public int RegionCount => _activeRegionCount;
        public int OutletCount => _activeOutletCount;
        public uint UnsupportedInternalGeometryChanges { get; private set; }
        public uint ExpandedRegions { get; private set; }
        public uint RejectedExpansions { get; private set; }
        public StableLiquidExpansionFailure LastExpansionFailure { get; private set; }
        public int ExpandedSectorCount => _expandedSectorCount;
        public uint RecapturedRegions { get; private set; }
        public uint RejectedRecaptures { get; private set; }
        public StableLiquidRecaptureFailure LastRecaptureFailure { get; private set; }
        public uint SurfaceVersion => _surfaceVersion;
        internal ulong MaterialQuantity(MaterialId material)
        { ulong total=0; for(int i=0;i<_regionCount;i++) if(_regions[i].IsActive && _regions[i].Material==material) total+=_regions[i].Quantity; return total; }
        public LiquidTransferLedger Ledger => _ledger;
        public ulong TotalQuantity
        {
            get
            {
                ulong total = 0;
                for (int i = 0; i < _regionCount; i++)
                    if (_regions[i].IsActive) total += _regions[i].Quantity;
                return total;
            }
        }

        public bool TryRegister(MaterialId material, Vector3Int minimum, Vector3Int maximum,
            int stepX, int stepRise, ulong initialQuantity, out int regionId)
        {
            regionId = -1;
            int slot = AvailableRegionSlot();
            if (slot < 0 || !_store.Supports(material)) return false;
            long cellCount = (long)(maximum.x - minimum.x) * (maximum.y - minimum.y)
                * (maximum.z - minimum.z);
            if (cellCount <= 0 || cellCount > _maximumRegionCells) return false;
            LiquidCapacityCurve curve;
            try { curve = new LiquidCapacityCurve(minimum, maximum, stepX, stepRise,
                _store.CapacityGmu); }
            catch (ArgumentOutOfRangeException) { return false; }
            return TryRegisterCurve(material, curve, initialQuantity, slot, out regionId);
        }

        public bool TryRegisterFootprint(MaterialId material, Vector3Int minimum,
            Vector3Int maximum, bool[] footprint, ulong initialQuantity, out int regionId)
        {
            regionId = -1;
            int slot = AvailableRegionSlot();
            if (slot < 0 || !_store.Supports(material)) return false;
            long cellCount = (long)(maximum.x - minimum.x) * (maximum.y - minimum.y)
                * (maximum.z - minimum.z);
            if (cellCount <= 0 || cellCount > _maximumRegionCells) return false;
            LiquidCapacityCurve curve;
            try { curve = new LiquidCapacityCurve(minimum, maximum, footprint,
                _store.CapacityGmu); }
            catch (ArgumentOutOfRangeException) { return false; }
            return TryRegisterCurve(material, curve, initialQuantity, slot, out regionId);
        }

        public bool TryRegisterHeightfield(MaterialId material, Vector3Int minimum,
            Vector3Int maximum, int[] floorByColumn, ulong initialQuantity,
            out int regionId)
        {
            regionId = -1;
            int slot = AvailableRegionSlot();
            long cellCount = (long)(maximum.x - minimum.x) * (maximum.y - minimum.y)
                * (maximum.z - minimum.z);
            if (slot < 0 || !_store.Supports(material)
                || cellCount <= 0 || cellCount > _maximumRegionCells) return false;
            LiquidCapacityCurve curve;
            try { curve = new LiquidCapacityCurve(minimum, maximum, floorByColumn,
                _store.CapacityGmu); }
            catch (ArgumentOutOfRangeException) { return false; }
            return TryRegisterCurve(material, curve, initialQuantity, slot, out regionId);
        }

        public bool TryRegisterSectorizedHeightfield(MaterialId material,
            Vector3Int minimum, Vector3Int maximum, int[] floorByColumn,
            ulong initialQuantity, out int groupId)
        {
            groupId = -1;
            if (!_store.Supports(material) || floorByColumn == null) return false;
            LiquidCapacityCurve whole;
            try { whole = new LiquidCapacityCurve(minimum, maximum, floorByColumn,
                _store.CapacityGmu); }
            catch (ArgumentOutOfRangeException) { return false; }
            if (initialQuantity > whole.Capacity) return false;

            var prepared = new StableLiquidRegion[_regions.Length];
            var slots = new int[_regions.Length];
            int available = 0;
            for (int i = 0; i < _regions.Length; i++)
            {
                if (i < _regionCount && (_regions[i].IsActive || _sectorized[i]
                    || _surfaceHandoffUntilFrame[i] >= Time.frameCount)) continue;
                slots[available++] = i;
            }
            int count = 0;
            int reservedPatches = ReservedSurfacePatches();
            int width = maximum.x - minimum.x;
            int height = maximum.y - minimum.y;
            if (width <= 0 || height <= 0) return false;
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(minimum, _store.ChunkSize,
                out ElementChunkKey first, out _);
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(maximum - Vector3Int.one,
                _store.ChunkSize, out ElementChunkKey last, out _);
            // 以全局 Chunk 边界裁剪，不以湖盆自身左下角分割；动态 Mesh 和
            // 稳定 Mesh 因此不会在同一个 XZ Chunk 上争夺整页的交接可见性。
            for (int cz = first.Z; cz <= last.Z; cz++)
            for (int cx = first.X; cx <= last.X; cx++)
            {
                int minX = Math.Max(minimum.x, cx * _store.ChunkSize);
                int maxX = Math.Min(maximum.x, (cx + 1) * _store.ChunkSize);
                int minZ = Math.Max(minimum.z, cz * _store.ChunkSize);
                int maxZ = Math.Min(maximum.z, (cz + 1) * _store.ChunkSize);
                int localWidth = maxX - minX;
                int localDepth = maxZ - minZ;
                var localFloors = new int[localWidth * localDepth];
                int included = 0;
                for (int z = minZ; z < maxZ; z++)
                for (int x = minX; x < maxX; x++)
                {
                    int floor = floorByColumn[(z - minimum.z) * width + x - minimum.x];
                    localFloors[(z - minZ) * localWidth + x - minX] = floor;
                    if (floor != int.MaxValue) included++;
                }
                if (included == 0) continue;
                long boxCells = (long)localWidth * localDepth * height;
                if (count == available || boxCells > _maximumRegionCells
                    || boxCells > _maximumImmediateExpansionCells) return false;
                var sectorMinimum = new Vector3Int(minX, minimum.y, minZ);
                var sectorMaximum = new Vector3Int(maxX, maximum.y, maxZ);
                LiquidCapacityCurve sectorCurve;
                try { sectorCurve = new LiquidCapacityCurve(sectorMinimum,
                    sectorMaximum, localFloors, _store.CapacityGmu); }
                catch (ArgumentOutOfRangeException) { return false; }
                ulong quantity = 0;
                for (int z = minZ; z < maxZ; z++)
                for (int x = minX; x < maxX; x++)
                for (int y = minimum.y; y < maximum.y; y++)
                    quantity += whole.QuantityAt(new Vector3Int(x, y, z), initialQuantity);
                var sector = new StableLiquidRegion(_nextRegionId + count,
                    material, sectorCurve, quantity);
                reservedPatches += sector.SurfacePatchCapacity;
                if (reservedPatches > _maximumSurfacePatches) return false;
                for (int i = 0; i < _regionCount; i++)
                    if ((_regions[i].IsActive || _sectorized[i])
                        && sector.Overlaps(_regions[i])) return false;
                for (int z = minZ; z < maxZ; z++)
                for (int x = minX; x < maxX; x++)
                for (int y = minimum.y; y < maximum.y; y++)
                {
                    var cell = new Vector3Int(x, y, z);
                    if (!sector.Contains(cell)) continue;
                    if (_store.HasAnyLiquid(cell) || _store.IsSolid(cell)) return false;
                }
                prepared[count++] = sector;
            }
            if (count == 0) return false;

            var links = new SectorLink[_sectorLinks.Length - _sectorLinkCount];
            int linkCount = 0;
            for (int a = 0; a < count; a++)
            for (int b = a + 1; b < count; b++)
            {
                SectorLink link = CreateSectorLink(prepared[a], prepared[b],
                    slots[a], slots[b]);
                if (link == null) continue;
                if (linkCount == links.Length) return false;
                links[linkCount++] = link;
            }
            // 所有预算、碰撞和接触边均已准备好；下面一次性公开所有数量 owner。
            groupId = _nextRegionId;
            for (int i = 0; i < count; i++)
            {
                int slot = slots[i];
                _regions[slot] = prepared[i];
                _sectorGroupIds[slot] = groupId;
                _sectorized[slot] = true;
                _surfaceHandoffUntilFrame[slot] = -1;
                _reportedInternalGeometry[slot] = false;
                _regionCount = Math.Max(_regionCount, slot + 1);
            }
            for (int i = 0; i < linkCount; i++)
                _sectorLinks[_sectorLinkCount++] = links[i];
            _nextRegionId += count;
            _activeRegionCount += count;
            _surfaceVersion++;
            return true;
        }

        private static SectorLink CreateSectorLink(StableLiquidRegion first,
            StableLiquidRegion second, int firstSlot, int secondSlot)
        {
            bool xFace = first.Maximum.x == second.Minimum.x
                || second.Maximum.x == first.Minimum.x;
            bool zFace = first.Maximum.z == second.Minimum.z
                || second.Maximum.z == first.Minimum.z;
            if (xFace == zFace) return null;
            int start = xFace ? Math.Max(first.Minimum.z, second.Minimum.z)
                : Math.Max(first.Minimum.x, second.Minimum.x);
            int end = xFace ? Math.Min(first.Maximum.z, second.Maximum.z)
                : Math.Min(first.Maximum.x, second.Maximum.x);
            if (end <= start) return null;
            var firstCells = new Vector3Int[end - start];
            var secondCells = new Vector3Int[end - start];
            int count = 0;
            for (int along = start; along < end; along++)
            {
                int xA = xFace ? (first.Maximum.x == second.Minimum.x
                    ? first.Maximum.x - 1 : first.Minimum.x) : along;
                int xB = xFace ? (first.Maximum.x == second.Minimum.x
                    ? second.Minimum.x : second.Maximum.x - 1) : along;
                int zA = zFace ? (first.Maximum.z == second.Minimum.z
                    ? first.Maximum.z - 1 : first.Minimum.z) : along;
                int zB = zFace ? (first.Maximum.z == second.Minimum.z
                    ? second.Minimum.z : second.Maximum.z - 1) : along;
                for (int y = Math.Max(first.Minimum.y, second.Minimum.y);
                    y < Math.Min(first.Maximum.y, second.Maximum.y); y++)
                {
                    var a = new Vector3Int(xA, y, zA);
                    var b = new Vector3Int(xB, y, zB);
                    if (!first.Contains(a) || !second.Contains(b)) continue;
                    firstCells[count] = a;
                    secondCells[count++] = b;
                    break;
                }
            }
            if (count == 0) return null;
            Array.Resize(ref firstCells, count);
            Array.Resize(ref secondCells, count);
            return new SectorLink { FirstSlot = firstSlot, SecondSlot = secondSlot,
                FirstId = first.Id, SecondId = second.Id,
                FirstCells = firstCells, SecondCells = secondCells };
        }

        private bool TryRegisterCurve(MaterialId material, LiquidCapacityCurve curve,
            ulong initialQuantity, int slot, out int regionId)
        {
            regionId = -1;
            if (initialQuantity > curve.Capacity
                || ReservedSurfacePatches() + curve.SurfacePatchCapacity > _maximumSurfacePatches)
                return false;
            var region = new StableLiquidRegion(_nextRegionId, material, curve, initialQuantity);
            for (int i = 0; i < _regionCount; i++)
                if ((_regions[i].IsActive || _sectorized[i])
                    && region.Overlaps(_regions[i])) return false;
            // 只在加载边界验证重叠；一旦压缩，原 Store 内就不保留第二份数量。
            for (int z = curve.Minimum.z; z < curve.Maximum.z; z++)
            for (int y = curve.Minimum.y; y < curve.Maximum.y; y++)
            for (int x = curve.Minimum.x; x < curve.Maximum.x; x++)
            {
                var cell = new Vector3Int(x, y, z);
                if (!region.Contains(cell)) continue;
                // 未填到的上层也属于未来容量；已有动态液体会在涨水后形成双 owner。
                if (_store.HasAnyLiquid(cell)) return false;
                if ((region.HasFootprint || region.QuantityAt(cell) > 0)
                    && _store.IsSolid(cell)) return false;
            }
            regionId = _nextRegionId++;
            _regions[slot] = region;
            _sectorGroupIds[slot] = regionId;
            _sectorized[slot] = false;
            _surfaceHandoffUntilFrame[slot] = -1;
            _reportedInternalGeometry[slot] = false;
            if (slot == _regionCount) _regionCount++;
            _activeRegionCount++;
            _surfaceVersion++;
            return true;
        }

        private int ReservedSurfacePatches()
        {
            int count = 0;
            for (int i = 0; i < _regionCount; i++)
                if (_regions[i].IsActive || _surfaceHandoffUntilFrame[i] >= Time.frameCount)
                    count += _regions[i].SurfacePatchCapacity;
            return count;
        }

        public bool TryPrepareExtracted(MaterialId material, Vector3Int minimum,
            Vector3Int maximum, ulong quantity, out StableLiquidRegion region)
        {
            region = null;
            long cellCount = (long)(maximum.x - minimum.x) * (maximum.y - minimum.y)
                * (maximum.z - minimum.z);
            if (AvailableRegionSlot() < 0 || !_store.Supports(material)
                || cellCount <= 0 || cellCount > _maximumRegionCells) return false;
            LiquidCapacityCurve curve;
            try { curve = new LiquidCapacityCurve(minimum, maximum, maximum.x, 0,
                _store.CapacityGmu); }
            catch (ArgumentOutOfRangeException) { return false; }
            if (quantity == 0 || quantity > curve.Capacity
                || ReservedSurfacePatches() + curve.SurfacePatchCapacity > _maximumSurfacePatches)
                return false;
            region = new StableLiquidRegion(_nextRegionId, material, curve, quantity);
            for (int i = 0; i < _regionCount; i++)
                if ((_regions[i].IsActive || _sectorized[i])
                    && region.Overlaps(_regions[i]))
                { region = null; return false; }
            return true;
        }

        public bool TryPrepareExtractedFootprint(MaterialId material, Vector3Int minimum,
            Vector3Int maximum, bool[] footprint, ulong quantity,
            out StableLiquidRegion region)
        {
            region = null;
            long cellCount = (long)(maximum.x - minimum.x) * (maximum.y - minimum.y)
                * (maximum.z - minimum.z);
            if (AvailableRegionSlot() < 0 || !_store.Supports(material)
                || cellCount <= 0 || cellCount > _maximumRegionCells) return false;
            LiquidCapacityCurve curve;
            try { curve = new LiquidCapacityCurve(minimum, maximum, footprint,
                _store.CapacityGmu); }
            catch (ArgumentOutOfRangeException) { return false; }
            if (quantity == 0 || quantity > curve.Capacity
                || ReservedSurfacePatches() + curve.SurfacePatchCapacity > _maximumSurfacePatches)
                return false;
            region = new StableLiquidRegion(_nextRegionId, material, curve, quantity);
            for (int i = 0; i < _regionCount; i++)
                if ((_regions[i].IsActive || _sectorized[i])
                    && region.Overlaps(_regions[i]))
                { region = null; return false; }
            return true;
        }

        public bool TryPrepareExtractedHeightfield(MaterialId material, Vector3Int minimum,
            Vector3Int maximum, int[] floorByColumn, ulong quantity,
            out StableLiquidRegion region)
        {
            region = null;
            long cellCount = (long)(maximum.x - minimum.x) * (maximum.y - minimum.y)
                * (maximum.z - minimum.z);
            if (AvailableRegionSlot() < 0 || !_store.Supports(material)
                || cellCount <= 0 || cellCount > _maximumRegionCells) return false;
            LiquidCapacityCurve curve;
            try { curve = new LiquidCapacityCurve(minimum, maximum, floorByColumn,
                _store.CapacityGmu); }
            catch (ArgumentOutOfRangeException) { return false; }
            if (quantity == 0 || quantity > curve.Capacity
                || ReservedSurfacePatches() + curve.SurfacePatchCapacity > _maximumSurfacePatches)
                return false;
            region = new StableLiquidRegion(_nextRegionId, material, curve, quantity);
            for (int i = 0; i < _regionCount; i++)
                if ((_regions[i].IsActive || _sectorized[i])
                    && region.Overlaps(_regions[i]))
                { region = null; return false; }
            return true;
        }

        public void CommitExtracted(StableLiquidRegion prepared)
        {
            // 调用方在同一 Tick 已完成数量移交；Prepared 不曾对外可见。
            int slot = AvailableRegionSlot();
            if (prepared == null || prepared.Id != _nextRegionId || slot < 0)
                throw new InvalidOperationException("稳定区域交接版本失配。");
            _regions[slot] = prepared;
            _sectorGroupIds[slot] = prepared.Id;
            _sectorized[slot] = false;
            _surfaceHandoffUntilFrame[slot] = -1;
            _reportedInternalGeometry[slot] = false;
            if (slot == _regionCount) _regionCount++;
            _nextRegionId++;
            _activeRegionCount++;
            _surfaceVersion++;
        }

        public bool TryRegisterOutlet(int regionId, Vector3Int targetCell,
            float heightCells, float areaCells, float conductance, bool open, out int outletId)
        {
            outletId = -1;
            int regionSlot = FindActiveRegionSlot(regionId);
            if (regionSlot < 0)
                for (int i = 0; i < _regionCount; i++)
                    if (_sectorized[i] && _sectorGroupIds[i] == regionId
                        && _regions[i].IsActive)
                    { regionSlot = i; break; }
            int outletSlot = AvailableOutletSlot();
            if (regionSlot < 0 || outletSlot < 0 || areaCells <= 0f
                || conductance <= 0f || !float.IsFinite(heightCells)
                || !float.IsFinite(areaCells) || !float.IsFinite(conductance)) return false;
            if (_sectorized[regionSlot])
            {
                // 出口的 Authoring 持有组首 ID；实际出流由离接收格最近的一片负责。
                // 接收格可隔着一格厚墙，不能只用“恰好相邻”来寻找分片。
                int bestDistance = int.MaxValue;
                for (int i = 0; i < _regionCount; i++)
                {
                    StableLiquidRegion candidate = _regions[i];
                    if (!candidate.IsActive || _sectorGroupIds[i] != regionId) continue;
                    if (candidate.Contains(targetCell)) return false;
                    int dx = Math.Max(candidate.Minimum.x - targetCell.x,
                        Math.Max(0, targetCell.x - candidate.Maximum.x + 1));
                    int dz = Math.Max(candidate.Minimum.z - targetCell.z,
                        Math.Max(0, targetCell.z - candidate.Maximum.z + 1));
                    int distance = dx + dz;
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    regionSlot = i;
                }
            }
            else if (_regions[regionSlot].Contains(targetCell)) return false;
            outletId = _nextOutletId++;
            _outlets[outletSlot] = new Outlet
            {
                RegionId = _regions[regionSlot].Id, RegionSlot = regionSlot,
                HandleId = outletId,
                TargetCell = targetCell,
                HeightCells = heightCells, AreaCells = areaCells,
                Conductance = conductance, Open = open,
            };
            if (outletSlot == _outletCount) _outletCount++;
            _activeOutletCount++;
            return true;
        }

        public bool SetOutletOpen(int outletId, bool open)
        {
            for (int i = 0; i < _outletCount; i++)
            {
                if (_outlets[i].HandleId != outletId) continue;
                // 展开期出口句柄仍归 Authoring 持有；允许关口并保留恢复意图。
                if (_outlets[i].Suspended)
                {
                    _outlets[i].SuspendedOpen = open;
                    for (int slot = 0; slot < _regionCount; slot++)
                        if (_sectorized[slot] && !_regions[slot].IsActive
                            && _regions[slot].Id == _outlets[i].RegionId)
                            _recaptureObservedSignatures[slot] = ulong.MaxValue;
                    return true;
                }
                if (_outlets[i].RegionSlot < 0) return false;
                StableLiquidRegion region = _regions[_outlets[i].RegionSlot];
                if (!region.IsActive || region.Id != _outlets[i].RegionId) return false;
                _outlets[i].Open = open;
                return true;
            }
            return false;
        }

        private int AvailableRegionSlot()
        {
            for (int i = 0; i < _regionCount; i++)
                if (!_regions[i].IsActive && !_sectorized[i]
                    && _surfaceHandoffUntilFrame[i] < Time.frameCount)
                    return i;
            return _regionCount < _regions.Length ? _regionCount : -1;
        }

        private int FindActiveRegionSlot(int id)
        {
            for (int i = 0; i < _regionCount; i++)
                if (_regions[i].IsActive && _regions[i].Id == id) return i;
            return -1;
        }

        private int AvailableOutletSlot()
        {
            for (int i = 0; i < _outletCount; i++)
                if (_outlets[i].RegionSlot < 0 && !_outlets[i].Suspended) return i;
            return _outletCount < _outlets.Length ? _outletCount : -1;
        }

        public void OnStaticCellChanged(Vector3Int cell)
        {
            for (int i = 0; i < _regionCount; i++)
            {
                StableLiquidRegion region = _regions[i];
                if (!region.IsActive) continue;
                if (region.Contains(cell))
                {
                    if (!_reportedInternalGeometry[i])
                    {
                        _reportedInternalGeometry[i] = true;
                        UnsupportedInternalGeometryChanges++;
                        GameLog.Error($"稳定区域 {region.Id} 的内部地形已改变；请在改变 Collider 前调用 TryPrepareStaticCellChange，并检查展开预算。",
                            "ElementField");
                    }
                    continue;
                }
                if (!AdjacentFace(region, cell)) continue;
                bool open = !_store.IsSolid(cell);
                if (_dynamicOutlets.TryGetValue(cell, out int outletId))
                {
                    SetOutletOpen(outletId, open);
                    continue;
                }
                if (open && TryRegisterOutlet(region.Id, cell, cell.y + .5f,
                    1f, 180f, true, out outletId))
                    _dynamicOutlets.Add(cell, outletId);
            }
        }

        public bool CanChangeStaticCell(Vector3Int cell)
        {
            // 只读预览仍保持保守语义；真正修改内部格时需调用带目标 Solid 状态的预检入口。
            if (_store.HasAnyLiquid(cell)) return false;
            for (int i = 0; i < _regionCount; i++)
                if (_regions[i].IsActive && _regions[i].Contains(cell)) return false;
            return true;
        }

        public bool TryPrepareStaticCellChange(Vector3Int cell, bool becomesSolid)
        {
            LastExpansionFailure = StableLiquidExpansionFailure.None;
            if (_store.IsSolid(cell) == becomesSolid) return true;
            if (becomesSolid && _store.HasAnyLiquid(cell))
            {
                // 活跃稳定 Owner 内还可能需要整片展开；此时先搬动态份额会
                // 使后续预算拒绝留下副作用，所以仍保守拒绝。
                for (int i = 0; i < _regionCount; i++)
                    if (_regions[i].IsActive && _regions[i].Contains(cell))
                        return RejectExpansion(StableLiquidExpansionFailure.OccupiedTarget);
                var targets = _terrainRelocationTargets;
                if (!_store.TryRelocateCellForStaticSolid(cell,
                    Math.Min(_maximumImmediateExpansionCells, 512), targets))
                    return RejectExpansion(StableLiquidExpansionFailure.OccupiedTarget);
                for (int i = 0; i < targets.Count; i++) _activity.WakeNeighbors(targets[i]);
            }
            for (int i = 0; i < _regionCount; i++)
            {
                StableLiquidRegion region = _regions[i];
                if (region.IsActive && region.Contains(cell))
                    return TryExpandBeforeStaticChange(i, region, cell, becomesSolid);
            }
            if (!becomesSolid)
            {
                for (int outlet = 0; outlet < _outletCount; outlet++)
                    if (_outlets[outlet].RegionSlot >= 0
                        && _outlets[outlet].TargetCell == cell) return true;
                for (int i = 0; i < _regionCount; i++)
                {
                    StableLiquidRegion region = _regions[i];
                    if (_sectorized[i] && region.IsActive && AdjacentFace(region, cell))
                        return TryExpandBeforeStaticChange(i, region, cell, false);
                }
            }
            return true;
        }

        public bool HasForeignOccupancy(Vector3Int cell, MaterialId incomingMaterial)
        {
            for (int i = 0; i < _regionCount; i++)
            {
                StableLiquidRegion region = _regions[i];
                if (region.IsActive && region.Material != incomingMaterial
                    && region.Contains(cell) && region.QuantityAt(cell) > 0) return true;
            }
            return false;
        }

        public bool TryPrepareMaterialContact(Vector3Int cell, MaterialId incomingMaterial)
        {
            for (int i = 0; i < _regionCount; i++)
            {
                StableLiquidRegion region = _regions[i];
                if (!region.IsActive || region.Material == incomingMaterial
                    || !region.Contains(cell) || region.QuantityAt(cell) == 0) continue;
                // 混合格必须只有一个数量/表面 Owner。只展开接触分片，远处深水继续休眠；
                // 沿用先预检再交接的预算，拒绝时不注入新材料，也不丢失旧数量。
                return TryExpandBeforeStaticChange(i, region, cell, false);
            }
            return true;
        }

        private bool TryExpandBeforeStaticChange(int regionSlot, StableLiquidRegion region,
            Vector3Int changedCell, bool becomesSolid)
        {
            long cells = (long)(region.Maximum.x - region.Minimum.x)
                * (region.Maximum.y - region.Minimum.y)
                * (region.Maximum.z - region.Minimum.z);
            if (cells > _maximumImmediateExpansionCells)
                return RejectExpansion(StableLiquidExpansionFailure.RegionTooLarge);
            if (becomesSolid && region.QuantityAt(changedCell) > 0)
                return RejectExpansion(StableLiquidExpansionFailure.OccupiedTarget);
            if (region.HasDynamicOverlap)
                return RejectExpansion(StableLiquidExpansionFailure.DynamicOverlap);
            for (int i = 0; i < _regionCount; i++)
                if (!_regions[i].IsActive && _regions[i].Material == region.Material
                    && _surfaceHandoffUntilFrame[i] >= Time.frameCount)
                    return RejectExpansion(StableLiquidExpansionFailure.SurfaceHandoffBusy);

            // Collider/数量尚未改变，先检查所有接收格及新增 Chunk。scratch 容量
            // 不小于本次最大格数，热路径 Clear 后重用，不能随接触反复创建集合。
            var neededChunks = _expansionChunks;
            var wetChunks = _expansionWetChunks;
            neededChunks.Clear(); wetChunks.Clear();
            for (int z = region.Minimum.z; z < region.Maximum.z; z++)
            for (int y = region.Minimum.y; y < region.Maximum.y; y++)
            for (int x = region.Minimum.x; x < region.Maximum.x; x++)
            {
                var target = new Vector3Int(x, y, z);
                if (region.QuantityAt(target) == 0) continue;
                if (_store.IsSolid(target))
                    return RejectExpansion(StableLiquidExpansionFailure.SolidConflict);
                if (_store.HasAnyLiquid(target))
                    return RejectExpansion(StableLiquidExpansionFailure.DynamicOverlap);
                ElementWorldCoordinates.GlobalCellToChunkAndLocal(target,
                    _store.ChunkSize, out ElementChunkKey key, out _);
                wetChunks.Add(key);
                if (!_store.HasResidentChunk(key)) neededChunks.Add(key);
            }
            if (neededChunks.Count > _store.MaximumChunks - _store.ResidentChunkCount)
                return RejectExpansion(StableLiquidExpansionFailure.ChunkBudget);
            // 与原型 Renderer 的单帧重建预算匹配；更大的地形修改需要分片表面交接。
            if (wetChunks.Count > _maximumImmediateExpansionSurfaceChunks)
                return RejectExpansion(StableLiquidExpansionFailure.SurfaceBudget);

            // 主线程单次调用内交接：旧 owner 暂时退出 OccupancyAt，然后写入新 owner。
            // 若任一写入意外失败，移除本次新写入并恢复旧 owner，不丢数量。
            region.SetActive(false);
            for (int z = region.Minimum.z; z < region.Maximum.z; z++)
            for (int y = region.Minimum.y; y < region.Maximum.y; y++)
            for (int x = region.Minimum.x; x < region.Maximum.x; x++)
            {
                var target = new Vector3Int(x, y, z);
                uint quantity = region.QuantityAt(target);
                if (quantity == 0) continue;
                if (_store.TryAdd(target, region.Material, quantity)) continue;
                RollBackExpansion(region);
                GameLog.Error($"稳定区域 {region.Id} 展开提交失败，数量已回滚。", "ElementField");
                return RejectExpansion(StableLiquidExpansionFailure.CommitFailure);
            }

            for (int i = 0; i < _outletCount; i++)
            {
                if (_outlets[i].RegionSlot != regionSlot
                    || _outlets[i].RegionId != region.Id) continue;
                _outlets[i].SuspendedOpen = _outlets[i].Open;
                _outlets[i].Open = false;
                // 只有可回收的分片保留 Authoring 句柄；一次性展开的旧湖区
                // 不会回到稳定 Owner，出口必须退役，避免旧开关误报成功。
                _outlets[i].Suspended = _sectorized[regionSlot];
                if (!_sectorized[regionSlot]) _outlets[i].SuspendedOpen = false;
                if (_dynamicOutlets.TryGetValue(_outlets[i].TargetCell, out int mapped)
                    && mapped == _outlets[i].HandleId)
                {
                    _outlets[i].WasDynamicMapped = true;
                    _dynamicOutlets.Remove(_outlets[i].TargetCell);
                }
                _outlets[i].RegionSlot = -1;
                _activeOutletCount--;
            }
            _activeRegionCount--;
            _surfaceVersion++;
            _surfaceHandoffUntilFrame[regionSlot] = Time.frameCount + 1;
            _recaptureObservedSignatures[regionSlot] = ulong.MaxValue;
            _recaptureRejectedSignatures[regionSlot] = ulong.MaxValue;
            _recaptureChangedTicks[regionSlot] = _simulationTick;
            if (_sectorized[regionSlot]) _expandedSectorCount++;
            ExpandedRegions++;
            for (int z = region.Minimum.z; z < region.Maximum.z; z++)
            for (int y = region.Minimum.y; y < region.Maximum.y; y++)
            for (int x = region.Minimum.x; x < region.Maximum.x; x++)
            {
                var target = new Vector3Int(x, y, z);
                if (region.QuantityAt(target) > 0) _activity.WakeNeighbors(target);
            }
            return true;
        }

        private void RollBackExpansion(StableLiquidRegion region)
        {
            // 预检保证目标原本没有动态液体，所以同种材料的现存量只可能由本次写入产生。
            for (int z = region.Minimum.z; z < region.Maximum.z; z++)
            for (int y = region.Minimum.y; y < region.Maximum.y; y++)
            for (int x = region.Minimum.x; x < region.Maximum.x; x++)
            {
                var target = new Vector3Int(x, y, z);
                if (_store.TryGetQuantity(target, region.Material, out uint quantity))
                    _store.TryRemove(target, region.Material, quantity);
            }
            region.SetActive(true);
            _surfaceVersion++;
        }

        public void TryRecaptureQuietSector(bool noPendingWrites)
        {
            _simulationTick++;
            if (!noPendingWrites || _expandedSectorCount == 0) return;
            // 一次 Tick 最多查看一个展开片；版本未变时不扫描内部格子。
            for (int visited = 0; visited < _regionCount; visited++)
            {
                int slot = (_recaptureCursor + visited) % _regionCount;
                if (!_sectorized[slot] || _regions[slot].IsActive) continue;
                _recaptureCursor = (slot + 1) % _regionCount;
                ulong signature = RecaptureSignature(_regions[slot]);
                if (_recaptureObservedSignatures[slot] != signature)
                {
                    _recaptureObservedSignatures[slot] = signature;
                    _recaptureChangedTicks[slot] = _simulationTick;
                    return;
                }
                if (_simulationTick - _recaptureChangedTicks[slot] < 24
                    || _recaptureRejectedSignatures[slot] == signature) return;
                if (TryRecaptureSector(slot, out StableLiquidRecaptureFailure failure))
                {
                    RecapturedRegions++;
                    _expandedSectorCount--;
                    LastRecaptureFailure = StableLiquidRecaptureFailure.None;
                }
                else
                {
                    RejectedRecaptures++;
                    LastRecaptureFailure = failure;
                    // Patch 预算和别的 Owner 可独立变化；其余拒绝直到本片数据或地形变化再查。
                    if (failure == StableLiquidRecaptureFailure.SurfaceBudget
                        || failure == StableLiquidRecaptureFailure.OtherRegion)
                        _recaptureChangedTicks[slot] = _simulationTick;
                    else _recaptureRejectedSignatures[slot] = signature;
                }
                return;
            }
        }

        private ulong RecaptureSignature(StableLiquidRegion region)
        {
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(region.Minimum,
                _store.ChunkSize, out ElementChunkKey first, out _);
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(region.Maximum - Vector3Int.one,
                _store.ChunkSize, out ElementChunkKey last, out _);
            ulong hash = 14695981039346656037ul;
            unchecked
            {
                hash = (hash ^ _store.StaticVersion) * 1099511628211ul;
                for (int z = first.Z; z <= last.Z; z++)
                for (int y = first.Y; y <= last.Y; y++)
                for (int x = first.X; x <= last.X; x++)
                    hash = (hash ^ _store.GetChunkVersion(
                        new ElementChunkKey(x, y, z))) * 1099511628211ul;
            }
            return hash;
        }

        private bool TryRecaptureSector(int slot,
            out StableLiquidRecaptureFailure failure)
        {
            failure = StableLiquidRecaptureFailure.None;
            StableLiquidRegion region = _regions[slot];
            if (ReservedSurfacePatches() + region.SurfacePatchCapacity
                > _maximumSurfacePatches)
            { failure = StableLiquidRecaptureFailure.SurfaceBudget; return false; }
            for (int i = 0; i < _regionCount; i++)
                if (i != slot && _regions[i].IsActive && region.Overlaps(_regions[i]))
                { failure = StableLiquidRecaptureFailure.OtherRegion; return false; }
            for (int i = 0; i < _outletCount; i++)
            {
                Outlet outlet = _outlets[i];
                if (!outlet.Suspended || outlet.RegionId != region.Id) continue;
                if (outlet.SuspendedOpen)
                { failure = StableLiquidRecaptureFailure.OpenBoundary; return false; }
            }

            ulong total = 0;
            int count = 0;
            for (int z = region.Minimum.z; z < region.Maximum.z; z++)
            for (int y = region.Minimum.y; y < region.Maximum.y; y++)
            for (int x = region.Minimum.x; x < region.Maximum.x; x++)
            {
                var cell = new Vector3Int(x, y, z);
                if (!region.Contains(cell)) continue;
                if (_store.IsSolid(cell) || (!region.Contains(cell + Vector3Int.down)
                    && !_store.IsSolid(cell + Vector3Int.down)))
                { failure = StableLiquidRecaptureFailure.TopologyChanged; return false; }
                if (HasOpenExternalFace(slot, cell))
                { failure = StableLiquidRecaptureFailure.OpenBoundary; return false; }
                if (_store.HasOtherMaterial(cell, region.Material))
                { failure = StableLiquidRecaptureFailure.MixedMaterial; return false; }
                if (!_store.TryGetQuantity(cell, region.Material, out uint amount))
                    continue;
                if (count == _recaptureCells.Length)
                { failure = StableLiquidRecaptureFailure.Capacity; return false; }
                _recaptureCells[count] = cell;
                _recaptureAmounts[count++] = amount;
                total += amount;
            }
            if (total > region.Capacity)
            { failure = StableLiquidRecaptureFailure.Capacity; return false; }

            ulong redistribution = 0;
            for (int z = region.Minimum.z; z < region.Maximum.z; z++)
            for (int y = region.Minimum.y; y < region.Maximum.y; y++)
            for (int x = region.Minimum.x; x < region.Maximum.x; x++)
            {
                var cell = new Vector3Int(x, y, z);
                if (!region.Contains(cell)) continue;
                _store.TryGetQuantity(cell, region.Material, out uint actual);
                uint expected = region.QuantityAt(cell, total);
                ulong difference = actual > expected
                    ? actual - expected : expected - actual;
                redistribution += difference;
                // 不把受扰动的孤立水团突然平铺成整片湖面。
                if (difference > (ulong)Math.Max(8, _store.CapacityGmu / 8)
                    || redistribution > Math.Max(16ul, total / 50ul))
                { failure = StableLiquidRecaptureFailure.UnevenSurface; return false; }
            }

            int removed = 0;
            for (; removed < count; removed++)
                if (!_store.TryRemove(_recaptureCells[removed], region.Material,
                    _recaptureAmounts[removed]))
                {
                    RestoreRecaptureSnapshot(removed, region.Material);
                    failure = StableLiquidRecaptureFailure.CommitFailure;
                    return false;
                }
            if (!region.TryReactivateWithQuantity(total))
            {
                RestoreRecaptureSnapshot(count, region.Material);
                failure = StableLiquidRecaptureFailure.CommitFailure;
                return false;
            }
            _activeRegionCount++;
            _surfaceVersion++;
            _surfaceHandoffUntilFrame[slot] = -1;
            for (int i = 0; i < _outletCount; i++)
            {
                if (!_outlets[i].Suspended || _outlets[i].RegionId != region.Id) continue;
                _outlets[i].RegionSlot = slot;
                _outlets[i].Open = false;
                _outlets[i].Suspended = false;
                if (_outlets[i].WasDynamicMapped)
                {
                    // 相邻稳定片也可能注册了同一格的关闭出口；保持现有映射唯一。
                    if (!_dynamicOutlets.ContainsKey(_outlets[i].TargetCell))
                        _dynamicOutlets.Add(_outlets[i].TargetCell,
                            _outlets[i].HandleId);
                    _outlets[i].WasDynamicMapped = false;
                }
                _activeOutletCount++;
            }
            return true;
        }

        private void RestoreRecaptureSnapshot(int count, MaterialId material)
        {
            // 单个主线程调用期间没有其他写入；回滚仍显式校验，不能默默丢 GMU。
            for (int i = 0; i < count; i++)
                if (!_store.TryAdd(_recaptureCells[i], material,
                    _recaptureAmounts[i]))
                    throw new InvalidOperationException("稳定分片回压缩回滚失败。");
        }

        private bool HasOpenExternalFace(int slot, Vector3Int cell)
        {
            StableLiquidRegion region = _regions[slot];
            return (cell.x == region.Minimum.x
                    && ExternalFaceOpen(slot, cell + Vector3Int.left))
                || (cell.x == region.Maximum.x - 1
                    && ExternalFaceOpen(slot, cell + Vector3Int.right))
                || (cell.z == region.Minimum.z
                    && ExternalFaceOpen(slot, cell + Vector3Int.back))
                || (cell.z == region.Maximum.z - 1
                    && ExternalFaceOpen(slot, cell + Vector3Int.forward));
        }

        private bool ExternalFaceOpen(int slot, Vector3Int neighbor)
        {
            // 同一湖的邻片由 SectorLink 接水头；真正对外的开口需要维持动态表示。
            for (int i = 0; i < _regionCount; i++)
                if (i != slot && _sectorized[i]
                    && _sectorGroupIds[i] == _sectorGroupIds[slot]
                    && _regions[i].Contains(neighbor)) return false;
            return !_store.IsSolid(neighbor);
        }

        private bool RejectExpansion(StableLiquidExpansionFailure reason)
        {
            LastExpansionFailure = reason;
            RejectedExpansions++;
            return false;
        }

        public bool IsSurfaceHandoffPending(MaterialId material, ElementChunkKey key)
        {
            for (int i = 0; i < _regionCount; i++)
            {
                StableLiquidRegion region = _regions[i];
                if (region.IsActive || region.Material != material
                    || _surfaceHandoffUntilFrame[i] < Time.frameCount) continue;
                if (OverlapsChunk(region, key)) return true;
            }
            return false;
        }

        public bool IsActiveSurfaceOverlapping(MaterialId material, ElementChunkKey key)
        {
            for (int i = 0; i < _regionCount; i++)
            {
                StableLiquidRegion region = _regions[i];
                if (region.IsActive && region.Material == material && region.Quantity > 0
                    && OverlapsChunk(region, key)) return true;
            }
            return false;
        }

        private bool OverlapsChunk(StableLiquidRegion region, ElementChunkKey key)
        {
            int size = _store.ChunkSize;
            return key.X * size < region.Maximum.x
                && (key.X + 1) * size > region.Minimum.x
                && key.Y * size < region.Maximum.y
                && (key.Y + 1) * size > region.Minimum.y
                && key.Z * size < region.Maximum.z
                && (key.Z + 1) * size > region.Minimum.z;
        }

        public void OnDynamicOccupancyChanged(Vector3Int cell, bool occupied)
        {
            for (int i = 0; i < _regionCount; i++)
            {
                StableLiquidRegion region = _regions[i];
                // 当前是干格也可能被将来上涨的水位覆盖；动态格清空后恢复吸收资格。
                if (region.IsActive && region.Contains(cell))
                    region.ChangeDynamicOverlap(occupied);
            }
        }

        public int AvailableAbsorption(Vector3Int target, MaterialId material)
        {
            for (int i = 0; i < _regionCount; i++)
            {
                StableLiquidRegion region = _regions[i];
                if (!region.IsActive || region.Material != material || !region.Contains(target)
                    || region.HasDynamicOverlap || _store.HasAnyLiquid(target)
                    || _store.IsSolid(target)) continue;
                // Solver 每次最多搬一格的 GMU；避免把 ulong 湖泊容量转成 int 溢出。
                return (int)Math.Min(region.Capacity - region.Quantity,
                    (ulong)_store.CapacityGmu);
            }
            return 0;
        }

        public bool OwnsMaterial(Vector3Int target, MaterialId material)
        {
            for (int i = 0; i < _regionCount; i++)
                if (_regions[i].IsActive && _regions[i].Material == material
                    && _regions[i].Contains(target)) return true;
            return false;
        }

        public int AvailableHorizontalAbsorption(Vector3Int source,
            Vector3Int target, MaterialId material, uint sourceQuantity)
        {
            for (int i = 0; i < _regionCount; i++)
            {
                StableLiquidRegion region = _regions[i];
                if (!region.IsActive || region.Material != material
                    || !region.Contains(target) || region.HasDynamicOverlap
                    || _store.HasAnyLiquid(target) || _store.IsSolid(target)) continue;
                int excess = HorizontalExcess(region, source, sourceQuantity);
                return (int)Math.Min((ulong)Math.Max(0, excess),
                    region.Capacity - region.Quantity);
            }
            return 0;
        }

        private int HorizontalExcess(StableLiquidRegion region,
            Vector3Int source, uint sourceQuantity)
        {
            // 深水层的满格并不代表它高于邻区湖面；只让自由面以上的水头
            // 参与横向交换，避免活动片区被持续抽成方形坑。
            float sourceTop = source.y + sourceQuantity / (float)_store.CapacityGmu;
            float excessCells = sourceTop - region.SurfaceHeightInCells - .02f;
            return Mathf.Clamp(Mathf.FloorToInt(excessCells * _store.CapacityGmu),
                0, _store.CapacityGmu);
        }

        public bool TryAbsorb(Vector3Int source, Vector3Int target,
            MaterialId material, uint quantity)
        {
            for (int i = 0; i < _regionCount; i++)
            {
                StableLiquidRegion region = _regions[i];
                if (!region.IsActive || !region.Contains(target)
                    || region.Material != material
                    || region.HasDynamicOverlap || _store.HasAnyLiquid(target)
                    || _store.IsSolid(target)
                    || region.Capacity - region.Quantity < quantity
                    || !_store.TryGetQuantity(source, material, out uint sourceQuantity)
                    || sourceQuantity < quantity) continue;
                if (source.y == target.y
                    && HorizontalExcess(region, source, sourceQuantity) < quantity)
                    continue;
                // 入口在自由面上方也直接并入同一个数量 owner；容量曲线决定最终水位，
                // 不让一层落水暂存为区域内动态格并永久阻断后续吸收。
                if (!_store.TryRemove(source, material, quantity)) return false;
                if (!region.TryAdd(quantity))
                {
                    _store.TryAdd(source, material, quantity);
                    return false;
                }
                _surfaceVersion++;
                _ledger.RecordAbsorption(quantity);
                _activity.WakeNeighbors(source);
                return true;
            }
            return false;
        }

        public uint TryDeposit(Vector3Int cell, MaterialId material, uint requested,
            bool requireWhole = false)
        {
            if (requested == 0) return 0;
            for (int i = 0; i < _regionCount; i++)
            {
                StableLiquidRegion region = _regions[i];
                if (!region.IsActive || region.Material != material || !region.Contains(cell)
                    || region.HasDynamicOverlap || _store.HasAnyLiquid(cell)
                    || _store.IsSolid(cell)) continue;
                uint accepted = (uint)Math.Min((ulong)requested,
                    region.Capacity - region.Quantity);
                if (requireWhole && accepted != requested) return 0;
                if (accepted == 0 || !region.TryAdd(accepted)) return 0;
                // 同种法术注入直接进入湖泊权威账本，无须在静水中再建动态页。
                _surfaceVersion++;
                return accepted;
            }
            return 0;
        }

        private static bool AdjacentFace(StableLiquidRegion region, Vector3Int cell)
        {
            if (cell.y < region.Minimum.y || cell.y >= region.Maximum.y) return false;
            if (region.HasFootprint)
            {
                // 掩码内凹岸线也有边界；只检查四邻格，不扫描整个不规则周长。
                return region.Contains(cell + Vector3Int.left)
                    || region.Contains(cell + Vector3Int.right)
                    || region.Contains(cell + Vector3Int.forward)
                    || region.Contains(cell + Vector3Int.back);
            }
            bool xFace = (cell.x == region.Minimum.x - 1 || cell.x == region.Maximum.x)
                && cell.z >= region.Minimum.z && cell.z < region.Maximum.z;
            bool zFace = (cell.z == region.Minimum.z - 1 || cell.z == region.Maximum.z)
                && cell.x >= region.Minimum.x && cell.x < region.Maximum.x;
            return xFace || zFace;
        }

        public void Step(float deltaTime)
        {
            for (int i = 0; i < _outletCount; i++)
            {
                Outlet outlet = _outlets[i];
                if (!outlet.Open || outlet.RegionSlot < 0) continue;
                if(!_activity.IsInActivityDomain(outlet.TargetCell))continue;
                StableLiquidRegion region = _regions[outlet.RegionSlot];
                if (!region.IsActive || region.Id != outlet.RegionId) continue;
                int free = _store.FreeCapacity(outlet.TargetCell);
                float downstreamFill = 1f - (float)free / _store.CapacityGmu;
                uint proposed = LiquidOutletSolver.Compute(region.SurfaceHeightInCells,
                    outlet.HeightCells, downstreamFill, outlet.AreaCells,
                    outlet.Conductance, deltaTime, ref outlet.Fractional);
                _outlets[i].Fractional = outlet.Fractional;
                if (proposed == 0) continue;
                // 一个开口占据多个相邻接收格；避免单格 255 GMU 容量把整条河口限成细滴。
                int lanes = Mathf.Clamp(Mathf.CeilToInt(outlet.AreaCells), 1, 32);
                for (int lane = 0; lane < lanes && proposed > 0; lane++)
                {
                    int zOffset = lane % 2 == 0 ? lane / 2 : -(lane + 1) / 2;
                    Vector3Int target = outlet.TargetCell + new Vector3Int(0, 0, zOffset);
                    ulong before = _ledger.CommittedGmu;
                    if (!_ledger.TryMove(region, target, proposed, _store, _activity)) continue;
                    proposed -= (uint)(_ledger.CommittedGmu - before);
                    _surfaceVersion++;
                }
            }
            StepSectorLinks(deltaTime);
        }

        private void StepSectorLinks(float deltaTime)
        {
            if (deltaTime <= 0f) return;
            for (int i = 0; i < _sectorLinkCount; i++)
            {
                SectorLink link = _sectorLinks[i];
                StableLiquidRegion first = _regions[link.FirstSlot];
                StableLiquidRegion second = _regions[link.SecondSlot];
                // 旧槽位将来可以被别的区域复用；ID 保证接触边只属于原湖盆。
                if (first.Id != link.FirstId || second.Id != link.SecondId
                    || (!first.IsActive && !second.IsActive)) continue;
                StableLiquidRegion source, destination;
                bool sourceIsFirst;
                float head;
                if (first.IsActive && second.IsActive)
                {
                    float difference = first.SurfaceHeightInCells
                        - second.SurfaceHeightInCells;
                    if (Mathf.Abs(difference) <= .05f) continue;
                    sourceIsFirst = difference > 0f;
                    source = sourceIsFirst ? first : second;
                    destination = sourceIsFirst ? second : first;
                    head = Mathf.Abs(difference);
                }
                else
                {
                    sourceIsFirst = first.IsActive;
                    source = sourceIsFirst ? first : second;
                    destination = sourceIsFirst ? second : first;
                    Vector3Int[] destinationCells = sourceIsFirst
                        ? link.SecondCells : link.FirstCells;
                    float sourceSurface = source.SurfaceHeightInCells;
                    head = 0f;
                    for (int lane = 0; lane < link.Length; lane++)
                    {
                        // 已展开分区的边界水位来自当前动态格，而非原湖底高度。
                        // 否则深水即使两侧等高，稳定区仍会每 Tick 向动态区泵水。
                        float deficit = sourceSurface - DynamicBoundaryHeight(
                            destinationCells[lane], destination.Maximum.y,
                            source.Material) - .05f;
                        if (deficit > 0f) head += deficit;
                    }
                    if (head <= 0f) continue;
                }
                // 每条接触边仅有有界宽度，按有限速传压；平静湖面不会访问深水格。
                float budget = Mathf.Min(1024f,
                    head * 180f * (destination.IsActive ? link.Length : 1)
                    * deltaTime + link.Fractional);
                uint requested = (uint)Mathf.FloorToInt(budget);
                link.Fractional = budget - requested;
                if (requested == 0) continue;
                if (destination.IsActive)
                {
                    if (_ledger.TryMove(source, destination, requested) > 0)
                        _surfaceVersion++;
                    continue;
                }
                uint remaining = requested;
                Vector3Int[] targets = sourceIsFirst
                    ? link.SecondCells : link.FirstCells;
                for (int lane = 0; lane < link.Length && remaining > 0; lane++)
                {
                    int at = (link.Cursor + lane) % link.Length;
                    Vector3Int baseCell = targets[at];
                    float deficit = source.SurfaceHeightInCells
                        - DynamicBoundaryHeight(baseCell, destination.Maximum.y,
                            source.Material) - .05f;
                    uint laneBudget = (uint)Mathf.Max(0,
                        Mathf.FloorToInt(deficit * _store.CapacityGmu));
                    if (laneBudget == 0) continue;
                    uint laneRemaining = Math.Min(remaining, laneBudget);
                    int ceiling = Math.Min(destination.Maximum.y,
                        Mathf.CeilToInt(source.SurfaceHeightInCells));
                    for (int y = baseCell.y; y < ceiling && laneRemaining > 0; y++)
                    {
                        var target = new Vector3Int(baseCell.x, y, baseCell.z);
                        ulong before = _ledger.CommittedGmu;
                        if (!_ledger.TryMove(source, target, laneRemaining, _store,
                            _activity)) continue;
                        uint moved = (uint)(_ledger.CommittedGmu - before);
                        remaining -= moved;
                        laneRemaining -= moved;
                        _surfaceVersion++;
                    }
                }
                link.Cursor = (link.Cursor + 1) % link.Length;
            }
        }

        private float DynamicBoundaryHeight(Vector3Int bottom, int ceiling,
            MaterialId material)
        {
            // 单条接触边最多访问其纵向格子；休眠分区仍不参与逐格扫描。
            // 将列内 GMU 折为局部水头，只用于有限速边界交接，不改 Gameplay 数量。
            uint quantity = 0;
            for (int y = bottom.y; y < ceiling; y++)
                if (_store.TryGetQuantity(new Vector3Int(bottom.x, y, bottom.z),
                    material, out uint cellQuantity)) quantity += cellQuantity;
            return bottom.y + quantity / (float)_store.CapacityGmu;
        }

        public bool TryGetQuantity(Vector3Int cell, MaterialId material, out uint quantity)
        {
            quantity = 0;
            for (int i = 0; i < _regionCount; i++)
            {
                StableLiquidRegion region = _regions[i];
                if (!region.IsActive || region.Material != material || !region.Contains(cell)) continue;
                quantity = region.QuantityAt(cell);
                return quantity != 0;
            }
            return false;
        }

        public bool TryGetSurfaceHeight(Vector3Int cell, MaterialId material,
            out float heightCells)
        {
            heightCells = 0f;
            for (int i = 0; i < _regionCount; i++)
            {
                StableLiquidRegion region = _regions[i];
                if (!region.IsActive || region.Material != material
                    || region.Quantity == 0 || !region.Contains(cell)) continue;
                float surface = region.SurfaceHeightInCells;
                // 多片稳定湖在显示层共用一张略作平均的平面时，动态接缝必须
                // 读取同一显示高度；Gameplay 的各片实际水头仍保持独立。
                if (_sectorized[i] && TryMergedSectorSurface(i, material,
                    out float sharedHeight, out _)) surface = sharedHeight;
                if (surface <= cell.y || _store.HasAnyLiquid(cell)) return false;
                heightCells = surface;
                return true;
            }
            return false;
        }

        public uint GetSurfaceNeighborhoodVersion(ElementChunkKey key,
            MaterialId material)
        {
            int size = _store.ChunkSize;
            int minX = key.X * size - 1, maxX = (key.X + 1) * size + 1;
            int minY = key.Y * size - 1, maxY = (key.Y + 1) * size + 1;
            int minZ = key.Z * size - 1, maxZ = (key.Z + 1) * size + 1;
            uint hash = 0;
            for (int i = 0; i < _regionCount; i++)
            {
                StableLiquidRegion region = _regions[i];
                if (!region.IsActive || region.Material != material
                    || region.Quantity == 0 || minX >= region.Maximum.x
                    || maxX <= region.Minimum.x || minY >= region.Maximum.y
                    || maxY <= region.Minimum.y || minZ >= region.Maximum.z
                    || maxZ <= region.Minimum.z) continue;
                unchecked
                {
                    hash = (hash ^ (uint)region.Id) * 16777619u;
                    hash = (hash ^ region.Version) * 16777619u;
                    if (_sectorized[i])
                        hash = (hash ^ SectorGroupVersion(_sectorGroupIds[i]))
                            * 16777619u;
                }
            }
            return hash;
        }

        private uint SectorGroupVersion(int groupId)
        {
            uint hash = 2166136261u;
            for (int i = 0; i < _regionCount; i++)
            {
                if (!_sectorized[i] || _sectorGroupIds[i] != groupId) continue;
                StableLiquidRegion member = _regions[i];
                unchecked
                {
                    hash = (hash ^ (uint)member.Id) * 16777619u;
                    hash = (hash ^ member.Version) * 16777619u;
                    hash = (hash ^ (member.IsActive ? 1u : 0u)) * 16777619u;
                }
            }
            return hash;
        }

        public uint GetMaterialSurfaceVersion(MaterialId material)
        {
            // 每材料仅扫描预分配的区域槽；读版本时不构建水面 Patch。
            // 远处另一种液体的涨落不触发当前 Renderer 的版本检查。
            uint hash = 0;
            for (int i = 0; i < _regionCount; i++)
            {
                StableLiquidRegion region = _regions[i];
                if (!region.IsActive || region.Material != material) continue;
                unchecked
                {
                    hash = (hash ^ (uint)region.Id) * 16777619u;
                    hash = (hash ^ region.Version) * 16777619u;
                    hash = (hash ^ region.SurfaceTopologyVersion) * 16777619u;
                }
            }
            return hash;
        }

        public uint OccupancyAt(Vector3Int cell)
        {
            for (int i = 0; i < _regionCount; i++)
                if (_regions[i].IsActive && _regions[i].Contains(cell))
                    return _regions[i].QuantityAt(cell);
            return 0;
        }

        public bool TryConsume(Vector3Int cell, MaterialId material, uint quantity)
        {
            for (int i = 0; i < _regionCount; i++)
            {
                StableLiquidRegion region = _regions[i];
                if (!region.IsActive || region.Material != material
                    || region.QuantityAt(cell) < quantity) continue;
                if (!region.TryRemove(quantity)) return false;
                _surfaceVersion++;
                return true;
            }
            return false;
        }

        public bool TryAddBack(Vector3Int cell, MaterialId material, uint quantity)
        {
            for (int i = 0; i < _regionCount; i++)
            {
                StableLiquidRegion region = _regions[i];
                if (!region.IsActive || region.Material != material || !region.Contains(cell)) continue;
                if (!region.TryRestoreRemoved(quantity)) return false;
                _surfaceVersion++;
                return true;
            }
            return false;
        }

        public int CopySurfacePatches(MaterialId material, StableLiquidSurfacePatch[] destination)
            => CopySurfacePatches(material, destination, false);

        public int CopyAllSurfacePatches(StableLiquidSurfacePatch[] destination)
            => CopySurfacePatches(MaterialId.Empty, destination, true);

        private int CopySurfacePatches(MaterialId requestedMaterial,
            StableLiquidSurfacePatch[] destination, bool allMaterials)
        {
            if (destination == null) return 0;
            int count = 0;
            for (int i = 0; i < _regionCount; i++)
            {
                // 统一冻结输入只复制仍有数量的 Owner，旧交接面由 Renderer 缓存保留。
                // 若把退役行段也塞入总 Patch 预算，会挤掉仍活跃的湖面。
                if (allMaterials && !_regions[i].IsActive) continue;
                MaterialId material = allMaterials ? _regions[i].Material : requestedMaterial;
                if (!IsSurfaceVisible(i, material)) continue;
                if (_sectorized[i] && TryMergedSectorSurface(i, material,
                    out float sharedHeight, out uint sharedTopology))
                {
                    bool alreadyEmitted = false;
                    for (int earlier = 0; earlier < i; earlier++)
                        if (_sectorGroupIds[earlier] == _sectorGroupIds[i]
                            && _sectorized[earlier]
                            && (!allMaterials || _regions[earlier].IsActive)
                            && IsSurfaceVisible(earlier, material))
                        { alreadyEmitted = true; break; }
                    if (alreadyEmitted) continue;
                    for (int member = i; member < _regionCount; member++)
                    {
                        if (_sectorized[member]
                            && _sectorGroupIds[member] == _sectorGroupIds[i]
                            && (!allMaterials || _regions[member].IsActive)
                            && IsSurfaceVisible(member, material))
                            AppendSurfacePatches(_regions[member],
                                _sectorGroupIds[i], sharedTopology, sharedHeight,
                                destination, ref count);
                    }
                    continue;
                }
                StableLiquidRegion region = _regions[i];
                AppendSurfacePatches(region, region.Id, region.SurfaceTopologyVersion,
                    region.SurfaceHeightInCells, destination, ref count);
            }
            return count;
        }

        private bool IsSurfaceVisible(int slot, MaterialId material)
        {
            StableLiquidRegion region = _regions[slot];
            return region.Material == material && region.Quantity > 0
                && (region.IsActive || _surfaceHandoffUntilFrame[slot] >= Time.frameCount);
        }

        private bool TryMergedSectorSurface(int slot, MaterialId material,
            out float height, out uint topology)
        {
            if (_mergedSurfaceFrames[slot] == Time.frameCount
                && _mergedSurfaceVersions[slot] == _surfaceVersion)
            {
                height = _mergedSurfaceHeights[slot];
                topology = _mergedSurfaceTopologies[slot];
                return _mergedSurfaceResults[slot];
            }
            int group = _sectorGroupIds[slot];
            float minimum = float.MaxValue, maximum = float.MinValue;
            float weightedHeight = 0f;
            int weightSum = 0;
            uint hash = 2166136261u;
            for (int i = 0; i < _regionCount; i++)
            {
                if (!_sectorized[i] || _sectorGroupIds[i] != group
                    || !IsSurfaceVisible(i, material)) continue;
                StableLiquidRegion region = _regions[i];
                region.RefreshSurfaceRuns();
                float current = region.SurfaceHeightInCells;
                minimum = Mathf.Min(minimum, current);
                maximum = Mathf.Max(maximum, current);
                int area = (region.Maximum.x - region.Minimum.x)
                    * (region.Maximum.z - region.Minimum.z);
                weightedHeight += current * area;
                weightSum += area;
                unchecked
                {
                    hash = (hash ^ (uint)region.Id) * 16777619u;
                    hash = (hash ^ region.SurfaceTopologyVersion) * 16777619u;
                }
            }
            height = weightSum > 0 ? weightedHeight / weightSum : 0f;
            topology = hash;
            // 小于 0.08 格的水位差只在显示层共享一个平面，避免透明 Mesh
            // 逐片提交时形成细缝；真实 GMU 和局部流量仍各归各片。
            bool shared = weightSum > 0 && maximum - minimum <= .08f;
            // 一座湖的分区共享同一次显示判定；大湖逐片重新遍历全组会变成 O(n²)。
            // 只缓存本 Frame／SurfaceVersion，数量或 Owner 交接仍会使整组重新计算。
            for (int i = 0; i < _regionCount; i++)
            {
                if (!_sectorized[i] || _sectorGroupIds[i] != group) continue;
                _mergedSurfaceFrames[i] = Time.frameCount;
                _mergedSurfaceVersions[i] = _surfaceVersion;
                _mergedSurfaceHeights[i] = height;
                _mergedSurfaceTopologies[i] = topology;
                _mergedSurfaceResults[i] = shared;
            }
            return shared;
        }

        private void AppendSurfacePatches(StableLiquidRegion region, int renderId,
            uint topology, float heightCells, StableLiquidSurfacePatch[] destination,
            ref int count)
        {
            if (region.HasFootprint)
            {
                region.RefreshSurfaceRuns();
                for (int runIndex = 0; runIndex < region.FootprintRunCount
                    && count < destination.Length; runIndex++)
                {
                    LiquidCapacityCurve.FootprintRun run = region.GetFootprintRun(runIndex);
                    destination[count++] = Patch(region, renderId, topology,
                        run.MinimumX, run.MaximumX, run.MinimumZ, run.MaximumZ,
                        heightCells);
                }
                return;
            }
            if (region.StepX > region.Minimum.x && heightCells > region.Minimum.y
                && count < destination.Length)
                destination[count++] = Patch(region, renderId, topology,
                    region.Minimum.x, region.StepX, region.Minimum.z,
                    region.Maximum.z, heightCells);
            if (region.StepX < region.Maximum.x
                && heightCells > region.Minimum.y + region.StepRise
                && count < destination.Length)
                destination[count++] = Patch(region, renderId, topology,
                    region.StepX, region.Maximum.x, region.Minimum.z,
                    region.Maximum.z, heightCells);
        }

        public int CopySurfaceCells(MaterialId material, LiquidMaterialCellSample[] destination,
            int startIndex = 0)
        {
            if (destination == null || startIndex < 0 || startIndex >= destination.Length
                || _regionCount == 0) return 0;
            bool hasMaterial = false;
            for (int i = 0; i < _regionCount; i++)
                if (_regions[i].IsActive && _regions[i].Material == material
                    && _regions[i].Quantity > 0)
                { hasMaterial = true; break; }
            if (!hasMaterial) return 0;
            int count = 0;
            int inspected = 0;
            int limit = Math.Min(4096, (destination.Length - startIndex) * 16);
            // 反应只需抽样稳定区域的自由表面；深水不会每 Tick 变成逐格扫描。
            while (count < destination.Length - startIndex && inspected++ < limit)
            {
                StableLiquidRegion region = _regions[_sampleRegionCursor];
                if (!region.IsActive || region.Material != material || region.Quantity == 0)
                {
                    _sampleCellCursor = 0;
                    _sampleRegionCursor = (_sampleRegionCursor + 1) % _regionCount;
                    continue;
                }
                int width = region.Maximum.x - region.Minimum.x;
                int depth = region.Maximum.z - region.Minimum.z;
                int area = width * depth;
                if (area <= 0) break;
                if (_sampleCellCursor >= area)
                {
                    _sampleCellCursor = 0;
                    _sampleRegionCursor = (_sampleRegionCursor + 1) % _regionCount;
                    continue;
                }
                int cellIndex = _sampleCellCursor++;
                int y = Mathf.Clamp(Mathf.CeilToInt(region.SurfaceHeightInCells) - 1,
                    region.Minimum.y, region.Maximum.y - 1);
                Vector3Int cell = new Vector3Int(region.Minimum.x + cellIndex % width,
                    y, region.Minimum.z + cellIndex / width);
                uint amount = region.QuantityAt(cell);
                if (amount == 0) continue;
                destination[startIndex + count++] = new LiquidMaterialCellSample(cell,
                    (byte)Math.Min(byte.MaxValue, amount));
            }
            return count;
        }

        private StableLiquidSurfacePatch Patch(StableLiquidRegion region, int renderId,
            uint topology, int minimumX, int maximumX, int minimumZ, int maximumZ,
            float heightCells)
        {
            Vector3 center = _origin + new Vector3(
                (minimumX + maximumX) * .5f * _cellSize,
                heightCells * _cellSize,
                (minimumZ + maximumZ) * .5f * _cellSize);
            Vector2 size = new Vector2((maximumX - minimumX) * _cellSize,
                (maximumZ - minimumZ) * _cellSize);
            return new StableLiquidSurfacePatch(renderId, region.Version, center, size,
                topology, region.Material, region.Minimum.y, region.Maximum.y, region.IsActive);
        }
    }
}
