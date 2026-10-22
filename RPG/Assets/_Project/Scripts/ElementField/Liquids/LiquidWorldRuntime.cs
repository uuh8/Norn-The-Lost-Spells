using System;
using Game.Core;
using Game.Materials;
using Unity.Profiling;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 通用液体在 ElementWorld Tick 内运行；这个组件本身不另起 Update，避免两个时钟
    /// 分别决定写入、状态与表面。只有显式配置 GridLiquid 后才拥有数量权威。
    /// </summary>
    public sealed partial class LiquidWorldRuntime : MonoBehaviour, IFluidDepositSink, ILiquidSurfaceReadOnly,
        ILiquidMotionReadOnly, ILiquidWorldQueryReadOnly, ILiquidSurfaceLayerReadOnly,
        ILiquidStableSurfaceSnapshotReadOnly, ILiquidLayeredSurfaceReadOnly, ILiquidTransportReadOnly
    {
        private static readonly ProfilerMarker PendingMarker =
            new ProfilerMarker("GridLiquid.PendingDeposits");
        private static readonly ProfilerMarker StableMarker =
            new ProfilerMarker("GridLiquid.StableRegions");
        private static readonly ProfilerMarker SolverMarker =
            new ProfilerMarker("GridLiquid.FluxSolver");
        private static readonly ProfilerMarker RecaptureMarker =
            new ProfilerMarker("GridLiquid.StableRecapture");
        private static readonly ProfilerMarker DetectorMarker =
            new ProfilerMarker("GridLiquid.AutoRegionDetector");

        private struct PendingDeposit
        {
            public ElementWriteRequest Request;
            public uint Remaining;
            public int ScanIndex;
            public int WaitingTicks;
        }

        [SerializeField] private LiquidWorldProfile _profile;
        [Header("Runtime Debug (Read Only)")]
        [SerializeField] private int _residentChunks;
        [SerializeField] private int _activeCells;
        [SerializeField] private float _runtimeCellSizeMeters;
        [SerializeField] private int _runtimeCellCapacityGmu;
        [SerializeField] private double _totalVolumeCubicMeters;
        [SerializeField] private ulong _dynamicQuantityGmu;
        [SerializeField] private ulong _stableQuantityGmu;
        [SerializeField] private ulong _pendingQuantityGmu;
        [SerializeField] private ulong _totalQuantityGmu;
        [SerializeField] private ulong _ignitedStickyQuantityGmu;
        [SerializeField] private ulong _accountedQuantityGmu;
        [SerializeField] private int _pendingDeposits;
        [SerializeField] private uint _activityOverflows;
        [SerializeField] private int _oldestPendingTicks;
        [SerializeField] private int _stableRegionCount;
        [SerializeField] private uint _autoCompressedRegionCount;
        [SerializeField] private uint _autoRejectedCandidateCount;
        [SerializeField] private int _autoCandidateAgeTicks;
        [SerializeField] private uint _rejectedDeposits;
        [SerializeField] private uint _unsupportedInternalGeometryChanges;
        [SerializeField] private uint _expandedStableRegions;
        [SerializeField] private uint _rejectedStableExpansions;
        [SerializeField] private StableLiquidExpansionFailure _lastStableExpansionFailure;
        [SerializeField] private int _residentExpandedSectors;
        [SerializeField] private uint _recapturedStableRegions;
        [SerializeField] private uint _rejectedStableRecaptures;
        [SerializeField] private StableLiquidRecaptureFailure _lastStableRecaptureFailure;

        private LiquidVolumeStore _store;
        private LiquidActivityScheduler _activity;
        private LiquidFluxSolver _solver;
        private LiquidLayerComposer _layers;
        private uint[] _layerQuantities;
        private LiquidTerrainSupportCache _terrain;
        private LiquidTransportHistory _history;
        private Vector3 _recentPresentationDeposit;
        private float _recentPresentationDepositTime;
        private bool _hasRecentPresentationDeposit;
        // 只读的显示优先级提示，不拥有数量。新增命中点在压力下也应优先于早先的远处残留。
        public bool TryGetRecentPresentationDeposit(out Vector3 position)
        {position=_recentPresentationDeposit;return _hasRecentPresentationDeposit && Time.time-_recentPresentationDepositTime<30;}
        private void MarkPresentationDeposit(Vector3 position)
        {_recentPresentationDeposit=position;_recentPresentationDepositTime=Time.time;_hasRecentPresentationDeposit=true;}
        public uint TransportEpoch => _history != null ? _history.Epoch : 0;
        public uint TerrainFallbackCount=>_terrain!=null?_terrain.FallbackCount:0;
        public uint TransportHistoryOverflowCount=>_history!=null?_history.OverflowCount:0;
        public int MaximumActiveTransportCells => _profile != null ? _profile.MaximumActiveCells : 0;
        public int SleepingCellCount=>_activity!=null?_activity.SleepingCount:0;
        public void SetActivityDomain(ElementWorldActivityRegions regions)
        {_activity?.SetActivityDomain(_profile!=null && _profile.SleepDistantLiquids?regions:null,_origin,_cellSize);}
        public float TransportInterval => MotionHorizon;
        public bool TryGetTerrainSupport(Vector3Int cell, out LiquidTerrainSupport support)
        { support = default; return _terrain != null && _terrain.TryGet(cell, out support); }
        // Presentation的真实支撑读取不会消耗/预热Solver预算，避免相机和渲染负载影响Gameplay。
        public bool TryGetTerrainSupportForPresentation(Vector3Int cell,out LiquidTerrainSupport support)
        {support=default;return _terrain!=null && _terrain.TryGetForPresentation(cell,out support);}
        public float GetGameplayBaseHeight(Vector3Int cell) => (_terrain != null ? _terrain.BaseHeight(cell) : _origin.y + cell.y * _cellSize)+ResidueBaseHeight(cell);
        public float GetPresentationBaseHeight(Vector3Int cell)=>(_terrain!=null?_terrain.BaseHeightForPresentation(cell):_origin.y+cell.y*_cellSize)+ResidueBaseHeight(cell);
        internal LiquidCellPresentationSample ReadCurrentPresentation(Vector3Int cell, MaterialId material)
        {
            TryGetDynamicQuantity(cell, material, out uint quantity);
            return ReadCurrentPresentation(cell,material,quantity);
        }
        private LiquidCellPresentationSample ReadCurrentPresentation(Vector3Int cell,MaterialId material,uint quantity)
        {
            if (quantity == 0) return default;
            Vector3 offset = Vector3.zero, velocity = Vector3.zero;
            if (TryGetFallingMotion(cell, material, out LiquidFallingMotion fall))
            {
                offset=fall.HorizontalOffsetCells;offset.y=-fall.OffsetCells;
                // 显示Warp只在Solver已经验证的自由距离内前推；不能穿进下一块Collider。
                velocity=fall.HorizontalVelocityWorld;
                velocity.y=-Mathf.Min(fall.Velocity,fall.SafeDistance/Mathf.Max(.001f,TransportInterval));
            }
            else if (_solver != null) _solver.TryGetGroundVelocity(cell, material, out velocity);
            return new LiquidCellPresentationSample(quantity, offset, velocity);
        }
        // 只读批量快照：一页数量只做一次Chunk查找，一格History只做一次查找。
        // Rendering持有目标数组；这里不暴露Gameplay数组或让显示反写数量。
        public void CopyPresentationChunkQuantities(ElementChunkKey key,ushort[] destination)=>_store.CopyChunkQuantities(key,destination);
        public bool CopyPresentationRow(Vector3Int cell,ushort[] quantities,int offset,
            LiquidCellPresentationSample[] current,LiquidCellPresentationSample[] previous)
        {
            bool wet=false;for(int m=0;m<MaterialCount;m++)wet|=quantities[offset+m]>0;
            bool frozen=_history.CopyFrozenRow(cell,previous);
            if(!wet)
            {
                bool existed=false;if(frozen)for(int m=0;m<MaterialCount;m++)existed|=previous[m].Quantity>0;
                if(!existed)return false;
            }
            for(int m=0;m<MaterialCount;m++)current[m]=ReadCurrentPresentation(cell,GetLiquidMaterial(m),quantities[offset+m]);
            if(!frozen)Array.Copy(current,previous,MaterialCount);return true;
        }
        public bool TryGetPresentationSample(Vector3Int cell, MaterialId material, bool previous, out LiquidCellPresentationSample sample)
        { sample = previous && _history != null ? _history.ReadPrevious(cell, material) : ReadCurrentPresentation(cell, material); return sample.Quantity > 0; }
        public int CopyTransportChangedCells(Vector3Int[] destination) => _history != null ? _history.CopyChangedCells(destination) : 0;
        private StableLiquidRegionRuntime _stable;
        private StableLiquidRegionDetector _detector;
        private PendingDeposit[] _pending;
        private int _head, _tail, _count;
        private bool _sampleStableFirst;
        private ulong _pendingQuantity;
        private Vector3 _origin;
        private float _cellSize;
        private LiquidVolumeSettings _volumeSettings;
        private int _chunkSize;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private int _gcSampledTicks, _gcAllocationTicks;
        private long _gcAllocatedBytes, _gcMaximumTickBytes;
#endif

        public bool IsFluidInitialized => _store != null;
        public int MaterialCount => _layers != null ? _layers.MaterialCount : 0;
        public int DensityGroupCount => _layers != null ? _layers.DensityGroupCount : 0;
        public MaterialId GetLiquidMaterial(int slot) => _layers.GetMaterial(slot);
        public int GetDensityGroup(MaterialId material) => _layers != null ? _layers.GetDensityGroup(material) : -1;
        private void ComposeLayers(Vector3Int cell)
        {
            for (int i = 0; i < MaterialCount; i++)
                TryGetQuantity(cell, _layers.GetMaterial(i), out _layerQuantities[i]);
            _layers.Compose(_layerQuantities);
        }
        public bool TryGetLayer(Vector3Int cell, int densityGroup, out LiquidLayerSample layer)
        {
            layer = default;
            if (_layers == null || densityGroup < 0 || densityGroup >= DensityGroupCount) return false;
            ComposeLayers(cell); layer = _layers.GetLayer(densityGroup);
            return layer.ThicknessFill > 0f;
        }
        public float GetGroupMaterialShare(Vector3Int cell, MaterialId material)
        { if (_layers == null) return 0f; ComposeLayers(cell); return _layers.GetShare(material); }
        public ulong IgnitedStickyQuantity => _ignitedStickyQuantityGmu;
        internal void RecordStickyIgnition(uint quantity) => _ignitedStickyQuantityGmu += quantity;
        public float MotionHorizon => _solver != null ? _solver.MotionHorizon : .1f;
        public bool TryGetFallingMotion(Vector3Int cell, MaterialId material,
            out LiquidFallingMotion motion)
        {
            motion = default;
            return _solver != null && _solver.TryGetFallingMotion(cell, material, out motion);
        }
        public Vector3 Origin => _origin;
        public float CellSize => _cellSize;
        public int ChunkSize => _chunkSize;
        public int CellCapacityGmu => _store != null ? _store.CapacityGmu : 0;
        public double CubicMetersPerGmu => _volumeSettings.CubicMetersPerGmu;
        public double TotalVolumeCubicMeters => TotalQuantity * CubicMetersPerGmu;
        public Vector3Int WorldCellToLiquidCell(Vector3Int worldCell) =>
            _volumeSettings.WorldCellToLiquidCell(worldCell);
        public Vector3Int LiquidCellToWorldCell(Vector3Int liquidCell) =>
            _volumeSettings.LiquidCellToWorldCell(liquidCell);
        public int MaximumSurfaceCandidateKeys => _store != null ? _store.MaximumSurfaceCandidateKeys : 0;
        public int MaximumStableRegions => _profile != null ? _profile.MaximumStableRegions : 0;
        public int MaximumStableSurfacePatches =>
            _profile != null ? _profile.MaximumStableSurfacePatches : 0;
        public int MaximumStableRegionCells =>
            _profile != null ? _profile.MaximumStableRegionCells : 0;
        public uint SurfaceVersion => unchecked((_store?.SurfaceVersion ?? 0)
            + (_stable?.SurfaceVersion ?? 0));
        public uint DynamicSurfaceVersion => _store?.SurfaceVersion ?? 0;
        // 与候选扫描使用同一版本；诊断时区分动态写入重启和地形失效重启。
        public uint DynamicStaticVersion => _store?.StaticVersion ?? 0;
        public ulong TotalQuantity => (_store?.TotalQuantity ?? 0)
            + (_stable?.TotalQuantity ?? 0);
        public ulong PendingQuantity => _pendingQuantity;
        public int ResidentChunkCount => _store != null ? _store.ResidentChunkCount : 0;
        public int ActiveCellCount => _activity != null ? _activity.Count : 0;
        public int PendingDepositCount => _count;
        public int OldestPendingTicks => _oldestPendingTicks;
        public uint RejectedDepositCount => _rejectedDeposits;
        public int StableRegionCount => _stable != null ? _stable.RegionCount : 0;
        public ulong StableQuantity => _stable != null ? _stable.TotalQuantity : 0;
        public LiquidTransferLedger Transfers => _stable != null ? _stable.Ledger : null;
        public uint AutoCompressedRegions => _detector != null ? _detector.CompressedRegions : 0;
        public uint AutoPartialSurfaceCompressedRegions => _detector != null
            ? _detector.PartialSurfaceCompressedRegions : 0;
        public uint InvalidatedAutoCandidates => _detector != null ? _detector.InvalidatedCandidates : 0;
        public uint RejectedAutoCandidates => _detector != null ? _detector.RejectedCandidates : 0;
        public ulong AutoSeedInspections => _detector != null ? _detector.SeedInspections : 0;
        public int AutoCandidateAgeTicks => _detector != null ? _detector.CandidateAgeTicks : 0;
        public uint ActivityOverflowCount => _activity != null ? _activity.OverflowCount : 0;
        public uint UnsupportedInternalGeometryChanges =>
            _stable != null ? _stable.UnsupportedInternalGeometryChanges : 0;
        public uint ExpandedStableRegions => _stable != null ? _stable.ExpandedRegions : 0;
        public uint RejectedStableExpansions => _stable != null ? _stable.RejectedExpansions : 0;
        public StableLiquidExpansionFailure LastStableExpansionFailure =>
            _stable != null ? _stable.LastExpansionFailure : StableLiquidExpansionFailure.None;
        public int ResidentExpandedSectors => _stable != null
            ? _stable.ExpandedSectorCount : 0;
        public uint RecapturedStableRegions => _stable != null ? _stable.RecapturedRegions : 0;
        public uint RejectedStableRecaptures => _stable != null ? _stable.RejectedRecaptures : 0;
        public StableLiquidRecaptureFailure LastStableRecaptureFailure => _stable != null
            ? _stable.LastRecaptureFailure : StableLiquidRecaptureFailure.None;
        public bool Supports(MaterialId material) => _store != null && _store.Supports(material);

        public bool TryInitialize(Vector3 origin, float cellSize, int chunkSize,
            ElementFieldSolidBaker solidBaker)
        {
            if (_store != null) return true;
            if (_profile == null || solidBaker == null || !float.IsFinite(cellSize)
                || cellSize <= 0f || chunkSize < 1) return false;
            try
            {
                _profile.Validate();
                _volumeSettings = _profile.CreateVolumeSettings(cellSize);
                int count = _profile.MaterialCount;
                var materials = new MaterialId[count];
                var downRates = new int[count];
                var sideRates = new int[count];
                var sideDeadbands = new int[count];
                var spreadReserves = new int[count];
                var relativeDensities = new float[count];
                var drags = new float[count]; var speeds = new float[count];
                for (int i = 0; i < count; i++)
                {
                    LiquidBehaviorProfile behavior = _profile.GetMaterial(i);
                    materials[i] = behavior.Material;
                    relativeDensities[i]=behavior.RelativeDensity;
                    drags[i] = behavior.GroundDrag; speeds[i] = behavior.MaximumGroundSpeed;
                    // 流率按粗格容量缩放，避免分辨率变化后每 Tick 的填充比例突然变慢。
                    float rateScale = _volumeSettings.CapacityGmu / (float)_profile.CellCapacityGmu;
                    downRates[i] = Mathf.Max(1, Mathf.RoundToInt(behavior.MaximumDownFlowPerTick * rateScale));
                    sideRates[i] = Mathf.Max(1, Mathf.RoundToInt(behavior.MaximumSideFlowPerTick * rateScale));
                    sideDeadbands[i] = Mathf.RoundToInt(behavior.SideFlowDeadbandGmu * rateScale);
                    spreadReserves[i] = Mathf.RoundToInt(behavior.SideSpreadReserveFill
                        * _volumeSettings.CapacityGmu);
                }
                _origin = origin;
                _layers = new LiquidLayerComposer(materials, relativeDensities, _volumeSettings.CapacityGmu);
                _layerQuantities = new uint[count];
                // 编辑后立即进入 Play 时，Unity 的 Physics 场景可能尚未同步新 Cube。
                Physics.SyncTransforms();
                _cellSize = _volumeSettings.CellSize;
                _runtimeCellSizeMeters = _cellSize;
                _runtimeCellCapacityGmu = _volumeSettings.CapacityGmu;
                _chunkSize = chunkSize;
                _store = new LiquidVolumeStore(chunkSize, _profile.MaximumChunks,
                    _volumeSettings.CapacityGmu, materials,
                    cell => solidBaker.SampleSolidWorldCell(cell, origin, _cellSize),relativeDensities);
                _activity = new LiquidActivityScheduler(_profile.MaximumActiveCells, _store);
                _stable = new StableLiquidRegionRuntime(_profile.MaximumStableRegions,
                    _profile.MaximumStableOutlets, _profile.MaximumStableRegionCells,
                    _store, _activity, origin, _cellSize,
                    _profile.MaximumImmediateExpansionCells,
                    _profile.MaximumImmediateExpansionSurfaceChunks,
                    _profile.MaximumStableSurfacePatches);
                _store.SetExternalOccupancy(_stable.OccupancyAt);
                _store.SetDynamicOccupancyChangedCallback(_stable.OnDynamicOccupancyChanged);
                _terrain = new LiquidTerrainSupportCache(solidBaker, _store, origin, _cellSize, _profile.MaximumActiveCells);
                _history = new LiquidTransportHistory(this, _profile.MaximumActiveCells, count);
                _store.SetBeforeCellChanged(_history.CaptureBefore);
                _solver = new LiquidFluxSolver(_store, _activity, _stable,
                    materials, downRates, sideRates, sideDeadbands, _cellSize,
                    _profile.FallGravity, _profile.FallTerminalVelocity,
                    _profile.MaximumActiveCells, spreadReserves,relativeDensities, _terrain, drags, speeds, _history);
                if (_profile.EnableAutoStableDetection)
                {
                    int eligibleCount = 0;
                    for (int i = 0; i < count; i++)
                        if (_profile.GetMaterial(i).AllowsStableRegion) eligibleCount++;
                    var eligible = new MaterialId[eligibleCount];
                    int write = 0;
                    for (int i = 0; i < count; i++)
                        if (_profile.GetMaterial(i).AllowsStableRegion)
                            eligible[write++] = _profile.GetMaterial(i).Material;
                    _detector = new StableLiquidRegionDetector(_store, _stable, eligible,
                        _profile.MaximumAutoCandidateCells,
                        _profile.AutoValidationCellsPerTick, _profile.AutoQuietTicks);
                }
                _pending = new PendingDeposit[_profile.MaximumPendingDeposits];
                return true;
            }
            catch (Exception exception)
            {
                GameLog.Error($"Grid Liquid 初始化失败：{exception.Message}", "ElementField");
                _store = null;
                _activity = null;
                _solver = null;
                _stable = null;
                _detector = null;
                return false;
            }
        }

        public bool TryEnqueueDeposit(in ElementWriteRequest request)
        {
            if (_store == null || !_store.Supports(request.MaterialKind) || request.TotalAmount == 0
                || !float.IsFinite(request.Radius) || request.Radius < 0f
                || !float.IsFinite(request.WorldPosition.x)
                || !float.IsFinite(request.WorldPosition.y)
                || !float.IsFinite(request.WorldPosition.z)
                || !float.IsFinite(request.SurfaceNormal.x)
                || !float.IsFinite(request.SurfaceNormal.y)
                || !float.IsFinite(request.SurfaceNormal.z)
                || _count == _pending.Length)
            {
                _rejectedDeposits++;
                return false;
            }
            _pending[_tail] = new PendingDeposit { Request = request, Remaining = request.TotalAmount };
            MarkPresentationDeposit(request.WorldPosition);
            _tail = (_tail + 1) % _pending.Length;
            _count++;
            _pendingQuantity += request.TotalAmount;
            _conversionExternal?.Invoke(request.MaterialKind,request.TotalAmount);
            return true;
        }

        public void Step(float deltaTime)
        {
            if (_store == null || deltaTime <= 0f) return;
            _history.BeginTick(); _terrain.BeginTick();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // 只计 Grid Liquid 的 Tick；其它模块的分配不能混入液体 Solver 的结论。
            long gcBefore = GC.GetAllocatedBytesForCurrentThread();
#endif
            _oldestPendingTicks = 0;
            // 只查看本轮入队前已有的请求；无法落地的剩余量仍由 Pending 账本持有。
            using (PendingMarker.Auto())
            {
                int attempts = _count;
                for (int i = 0; i < attempts; i++)
                {
                    PendingDeposit pending = _pending[_head];
                    _head = (_head + 1) % _pending.Length;
                    _count--;
                    uint moved = PlacePending(ref pending);
                    pending.Remaining -= moved;
                    _pendingQuantity -= moved;
                    if (pending.Remaining == 0) continue;
                    pending.WaitingTicks++;
                    _oldestPendingTicks = Mathf.Max(_oldestPendingTicks, pending.WaitingTicks);
                    _pending[_tail] = pending;
                    _tail = (_tail + 1) % _pending.Length;
                    _count++;
                }
            }
            // 同一 Tick 的四项成本分别显示在 Profiler，方便区别写入排队、
            // 静水出流、局部格求解和自动压缩；Marker.Auto 不创建托管对象。
            using (StableMarker.Auto()) _stable.Step(deltaTime);
            using (SolverMarker.Auto()) _solver.Step(_profile.MaximumCellsPerTick, deltaTime, Time.time);
            // 先完成本 Tick 的局部 Solver，再判断分区是否安静，避免交接后立刻改写旧动态格。
            using (RecaptureMarker.Auto()) _stable.TryRecaptureQuietSector(
                _count == 0 && !_activity.HasPendingRecovery);
            using (DetectorMarker.Auto()) _detector?.Step();
            _residentChunks = _store.ResidentChunkCount;
            _activeCells = _activity.Count;
            _dynamicQuantityGmu = _store.TotalQuantity;
            _stableQuantityGmu = _stable.TotalQuantity;
            _pendingQuantityGmu = _pendingQuantity;
            _totalQuantityGmu = _dynamicQuantityGmu + _stableQuantityGmu;
            _totalVolumeCubicMeters = TotalVolumeCubicMeters;
            _accountedQuantityGmu = _totalQuantityGmu + _pendingQuantityGmu;
            _pendingDeposits = _count;
            _activityOverflows = _activity.OverflowCount;
            _stableRegionCount = _stable.RegionCount;
            _autoCompressedRegionCount = _detector != null ? _detector.CompressedRegions : 0;
            _autoRejectedCandidateCount = _detector != null ? _detector.RejectedCandidates : 0;
            _autoCandidateAgeTicks = _detector != null ? _detector.CandidateAgeTicks : 0;
            _unsupportedInternalGeometryChanges = _stable.UnsupportedInternalGeometryChanges;
            _expandedStableRegions = _stable.ExpandedRegions;
            _rejectedStableExpansions = _stable.RejectedExpansions;
            _lastStableExpansionFailure = _stable.LastExpansionFailure;
            _residentExpandedSectors = _stable.ExpandedSectorCount;
            _recapturedStableRegions = _stable.RecapturedRegions;
            _rejectedStableRecaptures = _stable.RejectedRecaptures;
            _lastStableRecaptureFailure = _stable.LastRecaptureFailure;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            long allocated = GC.GetAllocatedBytesForCurrentThread() - gcBefore;
            _gcSampledTicks++;
            if (allocated > 0)
            {
                _gcAllocationTicks++;
                _gcAllocatedBytes += allocated;
                if (allocated > _gcMaximumTickBytes) _gcMaximumTickBytes = allocated;
            }
#endif
        }

        private void OnDestroy()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_gcSampledTicks > 0)
                GameLog.Info($"Grid Runtime: GC {_gcAllocationTicks}/{_gcSampledTicks} ticks, "
                    + $"total {_gcAllocatedBytes} B, max {_gcMaximumTickBytes} B/tick.", "ElementField");
#endif
        }

        private uint PlacePending(ref PendingDeposit pending)
        {
            ElementWriteRequest request = pending.Request;
            uint remaining = pending.Remaining;
            Vector3 correctedPosition = request.WorldPosition;
            if (request.SurfaceNormal.sqrMagnitude > .01f)
                correctedPosition += request.SurfaceNormal.normalized * (_cellSize * .51f);
            Vector3Int center = ElementWorldCoordinates.WorldToGlobalCell(
                correctedPosition, _origin, _cellSize);
            uint moved = 0;
            int radiusCells = Mathf.CeilToInt(Mathf.Min(request.Radius / _cellSize, 128f));
            int side = radiusCells * 2 + 1;
            int candidateCount = side * side;
            int maximumExamined = Math.Min(64, candidateCount);
            // 从命中中心向外按方形螺旋找接收位置；同一请求保留游标，巨量写入分 Tick 落地。
            // Radius 限制范围而非总量上限，接受后的 GMU 始终在 Pending 或 Store 中。
            // 小半径请求一轮最多访问一次每个候选；重复扫描满格不会制造额外容量。
            for (int examined = 0; examined < maximumExamined && moved < remaining; examined++)
            {
                int index = pending.ScanIndex;
                pending.ScanIndex = index + 1 == candidateCount ? 0 : index + 1;
                SpiralOffset(index, out int dx, out int dz);
                if (dx * dx + dz * dz > radiusCells * radiusCells) continue;
                for (int y = 0; y < 4 && moved < remaining; y++)
                {
                    Vector3Int cell = center + new Vector3Int(dx, y, dz);
                    // Stable Region 拥有整片湖的剩余容量。满的命中格仍可把新水计入
                    // 湖泊水位；先以单格 FreeCapacity 截断会让法术误留在 Pending。
                    uint stableAccepted = _stable.TryDeposit(cell, request.MaterialKind,
                        remaining - moved);
                    if (stableAccepted > 0)
                    {
                        moved += stableAccepted;
                        continue;
                    }
                    int space = _store.FreeCapacity(cell);
                    uint amount = Math.Min((uint)space, remaining - moved);
                    if (amount == 0) continue;
                    if (!_stable.TryPrepareMaterialContact(cell, request.MaterialKind)) continue;
                    if (!_store.TryAdd(cell, request.MaterialKind, amount)) continue;
                    moved += amount;
                    _activity.WakeNeighbors(cell);
                }
            }
            return moved;
        }

        private static void SpiralOffset(int index, out int x, out int z)
        {
            if (index == 0) { x = z = 0; return; }
            int ring = Mathf.CeilToInt((Mathf.Sqrt(index + 1f) - 1f) * .5f);
            int edge = ring * 2;
            int onRing = index - (edge - 1) * (edge - 1);
            int side = onRing / edge;
            int along = onRing % edge;
            switch (side)
            {
                case 0: x = -ring + 1 + along; z = -ring; break;
                case 1: x = ring; z = -ring + 1 + along; break;
                case 2: x = ring - 1 - along; z = ring; break;
                default: x = -ring; z = ring - 1 - along; break;
            }
        }

        public bool TryAddInitial(Vector3Int cell, MaterialId material, uint quantity,
            bool wake = true)
        {
            if (_store == null || quantity == 0) return false;
            if (_stable.TryDeposit(cell, material, quantity, requireWhole: true) == quantity)
            {MarkPresentationDeposit(_origin+((Vector3)cell+Vector3.one*.5f)*_cellSize);_conversionExternal?.Invoke(material,quantity);_conversionObserver?.Invoke(cell);return true;}
            if (_store.FreeCapacity(cell) < quantity || !_store.Supports(material)
                || !_stable.TryPrepareMaterialContact(cell, material)) return false;
            if (!_store.TryAdd(cell, material, quantity)) return false;
            MarkPresentationDeposit(_origin+((Vector3)cell+Vector3.one*.5f)*_cellSize);
            if (wake) _activity.WakeNeighbors(cell);
            _conversionExternal?.Invoke(material,quantity);
            return true;
        }

        public bool TryConsume(Vector3Int cell, MaterialId material, uint quantity,bool explicitRuleConsumption=false)
        {
            if (_store == null || quantity == 0) return false;
            if (_store.TryGetQuantity(cell, material, out uint dynamicAmount)
                && dynamicAmount >= quantity)
            {
                if (!_store.TryRemove(cell, material, quantity)) return false;
            }
            else if (dynamicAmount == 0)
            {
                if (_stable == null || !_stable.TryConsume(cell, material, quantity)) return false;
            }
            else
            {
                uint fromStable = quantity - dynamicAmount;
                if (_stable == null || !_stable.TryConsume(cell, material, fromStable)) return false;
                if (!_store.TryRemove(cell, material, dynamicAmount))
                {
                    _stable.TryAddBack(cell, material, fromStable);
                    return false;
                }
            }
            _activity.WakeNeighbors(cell);
            if(explicitRuleConsumption) _conversionConsumed?.Invoke(material,quantity);
            return true;
        }

        public bool TryConvert(Vector3Int cell, MaterialId source, MaterialId target,
            uint quantity)
        {
            if (_store == null || quantity == 0) return false;
            if (_stable != null && !_stable.TryPrepareMaterialContact(cell, target)) return false;
            if (!_store.TryConvert(cell, source, target, quantity))
            {
                if (_stable == null || !_stable.TryConsume(cell, source, quantity)) return false;
                if (!_store.TryAdd(cell, target, quantity))
                {
                    Vector3Int above = cell + Vector3Int.up;
                    if (!_store.TryAdd(above, target, quantity))
                    {
                        _stable.TryAddBack(cell, source, quantity);
                        return false;
                    }
                    _activity.WakeNeighbors(above);
                }
            }
            _activity.WakeNeighbors(cell);
            return true;
        }

        public bool TryGetQuantity(Vector3Int cell, MaterialId material, out uint quantity)
        {
            quantity = 0;
            if (_store == null) return false;
            _store.TryGetQuantity(cell, material, out uint dynamicAmount);
            uint stableAmount = 0;
            if (_stable != null) _stable.TryGetQuantity(cell, material, out stableAmount);
            quantity = dynamicAmount + stableAmount;
            return quantity != 0;
        }

        public bool TryGetAmount(Vector3Int cell, MaterialId material, out byte amount)
        {
            amount = 0;
            bool found = TryGetQuantity(cell, material, out uint quantity);
            amount = (byte)Math.Min(byte.MaxValue, quantity);
            return found;
        }

        public bool TryGetWorldAmount(Vector3Int worldCell, MaterialId material, out byte amount)
        {
            amount = 0;
            Vector3Int cell = WorldCellToLiquidCell(worldCell);
            if (_layers == null) return false;
            float minimum = _origin.y + worldCell.y * _volumeSettings.WorldCellSize;
            float maximum = minimum + _volumeSettings.WorldCellSize, exposure = 0f;
            // 支撑面可在逻辑格下方；有限邻层搜索把真实Terrain修正同时用于Gameplay。
            // 深湖不额外扩张搜索范围，圆冠也不算入接触体积。
            for (int dy = -2; dy <= 2; dy++)
            {
                Vector3Int candidate = cell + Vector3Int.up * dy;
                if (!TryGetQuantity(candidate, material, out uint q) || q == 0) continue;
                ComposeLayers(candidate); LiquidLayerSample layer = _layers.GetLayer(material);
                float floor = GetGameplayBaseHeight(candidate);
                if (TryGetFallingMotion(candidate, material, out LiquidFallingMotion fall)) floor -= fall.OffsetCells * _cellSize;
                float overlap = Mathf.Max(0f, Mathf.Min(maximum, floor + layer.TopFill * _cellSize)
                    - Mathf.Max(minimum, floor + layer.BaseFill * _cellSize));
                exposure += overlap / _volumeSettings.WorldCellSize * _layers.GetShare(material);
            }
            amount = (byte)Mathf.Clamp(Mathf.RoundToInt(255f * exposure), 0, 255);
            return amount != 0;
        }

        public bool TryGetWorldQuantity(Vector3Int worldCell, MaterialId material, out uint quantity)
        {
            quantity = 0; if (_store == null) return false;
            // 当前液体格比 World 格粗：此处返回覆盖该 World 格的液体库存，用于保守的占据拒绝。
            // 相邻 World 格可能指向同一个粗液体格，不能逐 World 格相加作为世界账本；
            // 转换消费必须使用 WorldCellToLiquidCell 后的唯一 Liquid Cell 地址与 TryConsume。
            float ratio = _volumeSettings.WorldCellSize / _cellSize;
            Vector3Int min = Vector3Int.FloorToInt((Vector3)worldCell * ratio);
            Vector3Int max = Vector3Int.CeilToInt(((Vector3)worldCell + Vector3.one) * ratio) - Vector3Int.one;
            ulong total = 0;
            for (int z = min.z; z <= max.z; z++) for (int y = min.y; y <= max.y; y++) for (int x = min.x; x <= max.x; x++)
                if (TryGetQuantity(new Vector3Int(x, y, z), material, out uint q)) total += q;
            if (total > uint.MaxValue) return false;
            quantity = (uint)total; return quantity > 0;
        }

        public void NotifyMaterialTopologyChanged(Vector3Int worldCell)
        {
            if (_store == null) return;
            float ratio = _volumeSettings.WorldCellSize / _cellSize;
            Vector3Int min = Vector3Int.FloorToInt((Vector3)worldCell * ratio);
            Vector3Int max = Vector3Int.CeilToInt(((Vector3)worldCell + Vector3.one) * ratio) - Vector3Int.one;
            for (int z = min.z; z <= max.z; z++) for (int y = min.y; y <= max.y; y++) for (int x = min.x; x <= max.x; x++)
            {
                var cell = new Vector3Int(x, y, z); _store.InvalidateStaticCell(cell);
                _residueOccupancy?.Remove(cell);
                _terrain?.InvalidateSupportedColumn(cell); _stable?.OnStaticCellChanged(cell); _activity.WakeNeighbors(cell);
            }
        }

        public uint GetDynamicSurfaceVersion(MaterialId material) =>
            _store != null ? _store.GetMaterialSurfaceVersion(material) : 0;

        public uint GetSurfaceChunkVersion(ElementChunkKey key, MaterialId material) =>
            _store != null ? _store.GetMaterialChunkSurfaceVersion(key, material) : 0;

        public uint GetDynamicCandidateVersion(MaterialId material) =>
            _store != null ? _store.GetCandidateVersion(material) : 0;

        public bool HasDynamicMaterialInChunk(ElementChunkKey key, MaterialId material) =>
            _store != null && _store.HasMaterialInChunk(key, material);

        public int CopySurfaceChunkKeys(MaterialId material, ElementChunkKey[] destination) =>
            _store != null ? _store.CopyChunkKeys(material, destination) : 0;

        public int CopyStableSurfacePatches(MaterialId material,
            StableLiquidSurfacePatch[] destination) =>
            _stable != null ? _stable.CopySurfacePatches(material, destination) : 0;

        public bool IsStableSurfaceHandoffPending(MaterialId material, ElementChunkKey key) =>
            _stable != null && _stable.IsSurfaceHandoffPending(material, key);

        public bool IsActiveStableSurfaceOverlapping(MaterialId material, ElementChunkKey key) =>
            _stable != null && _stable.IsActiveSurfaceOverlapping(material, key);

        public bool IsSurfaceMaterialDominant(Vector3Int cell, MaterialId material) =>
            _store != null && _store.IsDominantMaterial(cell, material);

        public int CopyLiquidChunkKeys(ElementChunkKey[] destination) =>
            _store != null ? _store.CopyLiquidChunkKeys(destination) : 0;

        public float GetTotalFill(Vector3Int cell)
        {
            uint total=0;
            for(int i=0;i<_profile.MaterialCount;i++)
            {TryGetQuantity(cell,_profile.GetMaterial(i).Material,out uint q);total+=q;}
            return Mathf.Clamp01(total/(float)CellCapacityGmu);
        }

        public bool TryGetSurfaceFill(Vector3Int cell,MaterialId material,out float totalFill)
        {
            int selected=-1;uint total=0;
            for(int i=0;i<_profile.MaterialCount;i++)
            {
                LiquidBehaviorProfile behavior=_profile.GetMaterial(i);
                if(!TryGetQuantity(cell,behavior.Material,out uint q) || q==0)continue;
                total+=q;
                if(selected<0 || behavior.RelativeDensity<_profile.GetMaterial(selected).RelativeDensity
                    || behavior.RelativeDensity==_profile.GetMaterial(selected).RelativeDensity
                        && (byte)behavior.Material<(byte)_profile.GetMaterial(selected).Material)selected=i;
            }
            totalFill=Mathf.Clamp01(total/(float)CellCapacityGmu);
            return selected>=0 && _profile.GetMaterial(selected).Material==material;
        }

        public bool TryGetDynamicQuantity(Vector3Int cell, MaterialId material,
            out uint quantity)
        {
            quantity = 0;
            return _store != null && _store.TryGetQuantity(cell, material, out quantity);
        }

        public bool TryGetStableSurfaceHeight(Vector3Int cell, MaterialId material,
            out float heightCells)
        {
            heightCells = 0f;
            return _stable != null
                && _stable.TryGetSurfaceHeight(cell, material, out heightCells);
        }

        public uint GetStableNeighborhoodVersion(ElementChunkKey key,
            MaterialId material) => _stable != null
            ? _stable.GetSurfaceNeighborhoodVersion(key, material) : 0;

        public uint GetStableSurfaceVersion(MaterialId material) => _stable != null
            ? _stable.GetMaterialSurfaceVersion(material) : 0;

        public int CopyAllStableSurfacePatches(StableLiquidSurfacePatch[] destination) =>
            _stable != null ? _stable.CopyAllSurfacePatches(destination) : 0;

        public int CopyOccupiedCells(MaterialId material, LiquidMaterialCellSample[] destination)
        {
            if (_store == null || destination == null || destination.Length == 0) return 0;
            _sampleStableFirst = !_sampleStableFirst;
            int count = _sampleStableFirst
                ? _stable.CopySurfaceCells(material, destination)
                : _store.CopyOccupiedCells(material, destination);
            count += _sampleStableFirst
                ? _store.CopyOccupiedCells(material, destination, count)
                : _stable.CopySurfaceCells(material, destination, count);
            return count;
        }

        public bool IsSolidForSurface(Vector3Int cell) =>
            _store != null && _store.IsSolid(cell);

        public bool AllowsStableRegion(MaterialId material)
        {
            if (_profile == null) return false;
            for (int i = 0; i < _profile.MaterialCount; i++)
            {
                LiquidBehaviorProfile behavior = _profile.GetMaterial(i);
                if (behavior.Material == material) return behavior.AllowsStableRegion;
            }
            return false;
        }

        public bool TryRegisterStableRegion(MaterialId material, Vector3Int minimum,
            Vector3Int maximum, int stepX, int stepRise, ulong initialQuantity,
            out int regionId)
        {
            regionId = -1;
            bool registered=_stable != null && AllowsStableRegion(material)
                && _stable.TryRegister(material, minimum, maximum, stepX, stepRise,
                    initialQuantity, out regionId);
            if(registered) _conversionExternal?.Invoke(material,initialQuantity);
            return registered;
        }

        public bool TryRegisterStableFootprint(MaterialId material, Vector3Int minimum,
            Vector3Int maximum, bool[] footprint, ulong initialQuantity, out int regionId)
        {
            regionId = -1;
            bool registered=_stable != null && AllowsStableRegion(material)
                && _stable.TryRegisterFootprint(material, minimum, maximum, footprint,
                    initialQuantity, out regionId);
            if(registered) _conversionExternal?.Invoke(material,initialQuantity);
            return registered;
        }

        public bool TryRegisterStableHeightfield(MaterialId material, Vector3Int minimum,
            Vector3Int maximum, int[] floorByColumn, ulong initialQuantity,
            out int regionId)
        {
            regionId = -1;
            bool registered=_stable != null && AllowsStableRegion(material)
                && _stable.TryRegisterHeightfield(material, minimum, maximum,
                floorByColumn, initialQuantity, out regionId);
            if(registered) _conversionExternal?.Invoke(material,initialQuantity);
            return registered;
        }

        public bool TryRegisterSectorizedStableHeightfield(MaterialId material,
            Vector3Int minimum, Vector3Int maximum, int[] floorByColumn,
            ulong initialQuantity, out int groupId)
        {
            groupId = -1;
            bool registered=_stable != null && AllowsStableRegion(material)
                && _stable.TryRegisterSectorizedHeightfield(material, minimum, maximum,
                    floorByColumn, initialQuantity, out groupId);
            if(registered) _conversionExternal?.Invoke(material,initialQuantity);
            return registered;
        }

        public bool TryRegisterStableOutlet(int regionId, Vector3Int target,
            float heightCells, float areaCells, float conductance, bool open, out int outletId)
        {
            outletId = -1;
            return _stable != null && _stable.TryRegisterOutlet(regionId, target,
                heightCells, areaCells, conductance, open, out outletId);
        }

        public bool SetStableOutletOpen(int outletId, bool open) =>
            _stable != null && _stable.SetOutletOpen(outletId, open);

        public void InvalidateStaticCell(Vector3Int cell)
        {
            _terrain?.Invalidate();
            _store?.InvalidateStaticCell(cell);
            _stable?.OnStaticCellChanged(cell);
            // Solid 变化使邻格的流动边界改变；仅刷新缓存而不 Wake 会让水继续睡在旧墙边。
            _activity?.WakeNeighbors(cell);
        }

        public bool CanChangeStaticCell(Vector3Int cell) =>
            _stable == null || _stable.CanChangeStaticCell(cell);

        /// <summary>
        /// 地形调用方须在修改 Collider 之前调用；成功后完成地形操作并调用
        /// InvalidateStaticCell。超预算或受占用时维持原区域，不允许先改几何再补救。
        /// </summary>
        public bool TryPrepareStaticCellChange(Vector3Int cell, bool becomesSolid) =>
            _stable == null || _stable.TryPrepareStaticCellChange(cell, becomesSolid);
    }
}
