using System;
using System.Collections.Generic;
using Game.Core;
using Game.Materials;
using UnityEngine;
using Unity.Profiling;

namespace Game.ElementField
{
    /// <summary>真实气体唯一 Owner，由 ElementWorld Tick 驱动；圈外停止推进且不补算经过时间。</summary>
    [DisallowMultipleComponent]
    public sealed partial class GasWorldRuntime : MonoBehaviour, IMaterialDepositSink, IGasVolumeReadOnly
    {
        private static readonly ProfilerMarker SimulateMarker = new ProfilerMarker("GridGas.Simulate");
        [SerializeField] private GasWorldProfile _profile;
        private GasVolumeStore _store;
        private MaterialCellActivityQueue _activity;
        private GasFluxSolver _solver;
        private MaterialPhaseDepositQueue _pending;
        private ElementWorldRuntime _world;
        private ElementFieldSolidBaker _baker;
        private Dictionary<Vector3Int, bool> _terrain;
        private Vector3Int[] _terrainKeys;
        private bool[] _terrainKeyValid;
        private int _terrainCursor, _samplesRemaining;
        private Vector3 _origin;
        private float _size;
        private readonly GasVentAuthoring[] _vents = new GasVentAuthoring[64];
        private int _ventCount;
        public bool IsMaterialInitialized => _store != null;
        public int ResidentCellCount => _store != null ? _store.ResidentCellCount : 0;
        public uint Version => _store != null ? _store.Version : 0;
        public uint CellCapacity => _store != null ? _store.CellCapacity : 0;
        public ulong TotalQuantity => _store != null ? _store.TotalQuantity : 0;
        public ulong PendingQuantity => _pending != null ? _pending.PendingQuantity : 0;
        public ulong VentedQuantity => _solver != null ? _solver.VentedQuantity : 0;
        public int ProcessedCellsLastTick => _solver != null ? _solver.ProcessedCellsLastTick : 0;
        // 都是已有队列/预算的 O(1) 计数；诊断不能每帧扫描 Resident 数据重新统计。
        public int ActiveCellCount => _activity != null ? _activity.Count : 0;
        public bool HasPendingRecovery => _activity != null && _activity.HasPendingRecovery;
        public int PendingDepositCount => _pending != null ? _pending.Count : 0;
        public int TerrainSamplesLastTick { get; private set; }
        public int ChunkSize => _store != null ? _store.ChunkSize : 0;
        public int MaximumChunks => _store != null ? _store.MaximumChunks : 0;
        public int ResidentChunkCount => _store != null ? _store.ResidentChunkCount : 0;
        public ElementChunkKey GetChunkKey(int index) => _store.GetChunkKey(index);
        public uint GetChunkVersion(ElementChunkKey key) => _store.GetChunkVersion(key);
        public void CopyMaterialBox(ElementChunkKey key,Vector3Int localMinimum,Vector3Int size,MaterialId material,
            uint[] destination,int destinationOffset,int rowStride,int sliceStride)
            => _store.CopyMaterialBox(key,localMinimum,size,material,destination,destinationOffset,rowStride,sliceStride);
        public int MaterialCount => _store != null ? _store.MaterialCount : 0;
        public MaterialId GetMaterial(int index) => _store.GetMaterial(index);
        public bool TryInitialize(ElementWorldRuntime world, ElementFieldSolidBaker baker, Vector3 origin, float size, int chunkSize)
        {
            if (_store != null) return true;
            if (_profile == null || !float.IsFinite(size) || size <= 0
                || !FluidSpawnRequestValidator.IsFinite(origin) || _profile.MaterialCount == 0
                || _profile.CellCapacityGmu == 0 || _profile.CellCapacityGmu > int.MaxValue
                || _profile.MaximumActiveCells < 1 || _profile.MaximumCellsPerTick < 1
                || _profile.MaximumTerrainSamplesPerTick < 1) return false;
            try
            {
                var materials = new MaterialId[_profile.MaterialCount];
                var behaviors = new GasMaterialBehavior[materials.Length];
                for (int i = 0; i < materials.Length; i++) { behaviors[i] = _profile.GetMaterial(i); materials[i] = behaviors[i].Material; }
                _world = world; _baker = baker; _origin = origin; _size = size;
                _store = new GasVolumeStore(chunkSize, _profile.MaximumChunks, _profile.CellCapacityGmu, materials);
                _activity = new MaterialCellActivityQueue(_profile.MaximumActiveCells, _store);
                _terrain = new Dictionary<Vector3Int, bool>(_profile.MaximumActiveCells);
                _terrainKeys = new Vector3Int[_profile.MaximumActiveCells];
                _terrainKeyValid=new bool[_profile.MaximumActiveCells];
                _pending = new MaterialPhaseDepositQueue(_profile.MaximumPendingDeposits);
                _solver = new GasFluxSolver(_store, _activity, behaviors, _profile.MaximumCellsPerTick, IsBlocked, VentAmount);
                return true;
            }
            catch (Exception ex) { GameLog.Error($"Gas 初始化失败：{ex.Message}", "ElementField"); _store = null; return false; }
        }
        public bool Supports(MaterialId material) => _store != null && _store.Supports(material);
        public bool TryEnqueueDeposit(in ElementWriteRequest request)
        {
            if(!Supports(request.MaterialKind) || !_pending.Enqueue(in request)) return false;
            _world?.NotifyExternalMaterialInjection(request.MaterialKind,request.TotalAmount); return true;
        }
        public bool TryGetQuantity(Vector3Int cell, MaterialId material, out uint quantity)
        { quantity = 0; return _store != null && _store.TryGetQuantity(cell, material, out quantity); }
        public bool TryGetAmount(Vector3Int cell, MaterialId material, out byte amount)
        { amount = 0; return _store != null && _store.TryGetAmount(cell, material, out amount); }
        public bool TryGetCell(int index, out MaterialVolumeCell cell)
        { cell = default; return _store != null && _store.TryGetCell(index, out cell); }
        public void SetActivityDomain(ElementWorldActivityRegions regions) => _activity?.SetDomain(regions, _origin, _size);
        public void Step(float deltaTime)
        {
            if (_store == null || deltaTime <= 0) return;
            using var simulationScope = SimulateMarker.Auto();
            _samplesRemaining = _profile.MaximumTerrainSamplesPerTick;
            int pendingCount = Math.Min(8, _pending.Count);
            for (int i = 0; i < pendingCount; i++)
            {
                _pending.TryDequeue(out MaterialPhasePendingDeposit pending);
                var center = ElementWorldCoordinates.WorldToGlobalCell(pending.Request.WorldPosition
                    + pending.Request.SurfaceNormal * (_size * .51f), _origin, _size);
                int radius = Mathf.Clamp(Mathf.CeilToInt(pending.Request.Radius / _size), 0, 4);
                int samples = 1 + (radius * 2 + 1) * (radius * 2 + 1) * (radius * 2 + 1);
                for (int scan = 0; scan < 16 && pending.Remaining > 0; scan++)
                {
                    Vector3Int offset = MaterialPhaseDepositQueue.Offset(pending.Cursor++, radius);
                    if (pending.Cursor >= samples) pending.Cursor = 0;
                    if (offset.sqrMagnitude > radius * radius) continue;
                    Vector3Int cell = center + offset;
                    if (IsBlocked(cell)) continue;
                    uint amount = Math.Min(pending.Remaining, CellCapacity - _store.OccupancyAt(cell));
                    if (amount == 0 || !_store.TryAdd(cell, pending.Request.MaterialKind, amount)) continue;
                    pending.Remaining -= amount; _activity.WakeNeighbors(cell);
                }
                pending.WaitingTicks++; _pending.Requeue(in pending);
            }
            _solver.Step(deltaTime);
            TerrainSamplesLastTick = _profile.MaximumTerrainSamplesPerTick - _samplesRemaining;
            int chunks = _store.ResidentChunkCount;
            if (_solver.ProcessedCellsLastTick > 0) _store.ReclaimEmptyChunks();
            if (chunks != _store.ResidentChunkCount) _activity.RequestRecovery();
        }
        public void NotifyTopologyChanged(Vector3Int cell)
        { _terrain?.Remove(cell); _activity?.WakeNeighbors(cell); }
        public bool RegisterVent(GasVentAuthoring vent)
        {
            for (int i = 0; i < _ventCount; i++) if (_vents[i] == vent) return true;
            if (_ventCount == _vents.Length) return false;
            _vents[_ventCount++] = vent; _activity?.RequestRecovery(); return true;
        }
        public void UnregisterVent(GasVentAuthoring vent)
        {
            for (int i = 0; i < _ventCount; i++) if (_vents[i] == vent)
            { _vents[i] = _vents[--_ventCount]; _vents[_ventCount] = null; return; }
        }
        private bool IsBlocked(Vector3Int cell)
        {
            if (_world != null && _world.FixedSolids != null && _world.FixedSolids.BlocksCell(cell)) return true;
            if (_terrain.TryGetValue(cell, out bool solid)) return solid;
            if (_samplesRemaining == 0)
            {
                // 未知地形不能当作空气漏过墙；把邻域留给后续 Tick 继续预热。
                _activity.WakeNeighbors(cell); return true;
            }
            _samplesRemaining--; solid = _baker != null && _baker.SampleEnvironmentSolidWorldCell(cell, _origin, _size);
            // Ice 拓扑失效会删除缓存键；覆盖 Ring 槽位时始终淘汰旧键，防止失效后容量越界。
            if (_terrainKeyValid[_terrainCursor]) _terrain.Remove(_terrainKeys[_terrainCursor]);
            _terrainKeyValid[_terrainCursor]=true;
            _terrainKeys[_terrainCursor] = cell; _terrainCursor = (_terrainCursor + 1) % _terrainKeys.Length;
            _terrain[cell] = solid; return solid;
        }
        private uint VentAmount(Vector3Int cell, MaterialId material, float deltaTime)
        {
            Vector3 center = _origin + ((Vector3)cell + Vector3.one * .5f) * _size;
            double amount = 0;
            for (int i = 0; i < _ventCount; i++)
            {
                var vent = _vents[i]; if (vent == null || !vent.isActiveAndEnabled) continue;
                if ((vent.Material == MaterialId.Empty || vent.Material == material) && vent.Bounds.Contains(center))
                    amount += Math.Max(0, vent.Rate) * deltaTime;
            }
            return (uint)Math.Min(uint.MaxValue, Math.Floor(amount));
        }
    }
}
