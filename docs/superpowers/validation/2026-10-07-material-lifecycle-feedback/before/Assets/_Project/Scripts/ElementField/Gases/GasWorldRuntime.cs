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
        private Dictionary<ElementChunkKey,bool> _terrainTiles;
        private ElementChunkKey[] _tileKeys;
        private int _tileCursor,_tileCount;
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
                || _profile.MaximumTerrainSamplesPerTick < 1
                || _profile.MaximumBirthScansPerTick < 1
                || !float.IsFinite(_profile.InitialConcentration) || _profile.InitialConcentration<=0 || _profile.InitialConcentration>1
                || !FluidSpawnRequestValidator.IsFinite(_profile.WindVelocity)) return false;
            try
            {
                var materials = new MaterialId[_profile.MaterialCount];
                var behaviors = new GasMaterialBehavior[materials.Length];
                for (int i = 0; i < materials.Length; i++) { behaviors[i] = _profile.GetMaterial(i); materials[i] = behaviors[i].Material; }
                _world = world; _baker = baker; _origin = origin; _size = size;
                _store = new GasVolumeStore(chunkSize, _profile.MaximumChunks, _profile.CellCapacityGmu, materials);
                _activity = new MaterialCellActivityQueue(_profile.MaximumActiveCells, _store);
                _terrain = new Dictionary<Vector3Int, bool>(_profile.MaximumActiveCells);
                _terrainTiles=new Dictionary<ElementChunkKey,bool>(_profile.MaximumActiveCells);
                _tileKeys=new ElementChunkKey[_profile.MaximumActiveCells];
                _terrainKeys = new Vector3Int[_profile.MaximumActiveCells];
                _terrainKeyValid=new bool[_profile.MaximumActiveCells];
                _pending = new MaterialPhaseDepositQueue(_profile.MaximumPendingDeposits);
                _solver = new GasFluxSolver(_store, _activity, behaviors, _profile.MaximumCellsPerTick, IsBlocked, VentAmount,
                    _profile.WindVelocity,size,_profile.MaximumActiveCells);
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
            int depositScans=_profile.MaximumBirthScansPerTick;
            uint depositCapacity=_profile.DepositCellAmountGmu;
            int pendingCount = Math.Min(8, _pending.Count);
            for (int i = 0; i < pendingCount; i++)
            {
                _pending.TryDequeue(out MaterialPhasePendingDeposit pending);
                var center = ElementWorldCoordinates.WorldToGlobalCell(pending.Request.WorldPosition
                    + pending.Request.SurfaceNormal * (_size * .51f), _origin, _size);
                // 半格偏移可能仍处于与地板相交的体素。锚点必须先在外法线方向找到真实空气，
                // 否则 IsBirthReachable 的末端永远实心，整团 Pending 会永久不能落位。
                Vector3 normal=pending.Request.SurfaceNormal;
                var outward=new Vector3Int(Mathf.RoundToInt(normal.x),Mathf.RoundToInt(normal.y),Mathf.RoundToInt(normal.z));
                for(int n=0;n<2 && IsBlocked(center);n++) center+=outward;
                // 用初始质量估算出生体积，整个 Pending 生命周期保持同一半径/游标。
                // 多预留半个球供墙面命中；大气团不再被旧固定半径的总容量永久卡住。
                double cells=Math.Ceiling(pending.Request.TotalAmount/(double)depositCapacity);
                int massRadius=cells<=1 ? 0 : (int)Math.Ceiling(Math.Pow(cells*2*3/(4*Math.PI),1d/3d))+1;
                int radius=Mathf.Clamp(Math.Max(Mathf.CeilToInt(pending.Request.Radius/_size),massRadius),0,MaterialPhaseDepositQueue.MaximumRadius);
                int samples=MaterialPhaseDepositQueue.SampleCount(radius);
                // 每请求最多用一半出生预算，另一半可服务同时命中的气体；总 Physics 仍共用原上限。
                for(int scan=0;scan<Math.Min(Math.Max(128,_profile.MaximumBirthScansPerTick/2),samples) && depositScans>0 && pending.Remaining>0;scan++)
                {
                    depositScans--;
                    Vector3Int offset = MaterialPhaseDepositQueue.Offset(pending.Cursor++, radius);
                    if (pending.Cursor >= samples) pending.Cursor = 0;
                    // 命中表面后的首团放在外侧半空间。背向法线的格留给后续真实扩散，
                    // 避免地面命中把出生 Physics 预算耗在地板下面。
                    if(normal.sqrMagnitude>0 && Vector3.Dot((Vector3)offset,normal)<0) continue;
                    Vector3Int cell = center + offset;
                    uint occupied=_store.OccupancyAt(cell);
                    if(occupied>=depositCapacity || !IsBirthReachable(center,cell)) continue;
                    uint amount=Math.Min(pending.Remaining,depositCapacity-occupied);
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
        private bool IsBirthReachable(Vector3Int center,Vector3Int cell)
        {
            // 大出生球不能只验证目标是空气，否则会越过薄墙/地板直接在另一侧造气体。
            // 沿中心方向的六面阶梯路径保守预检；路径仍共用有限 Terrain Cache/Budget。
            // 不搜索绕墙路径，遇阻挡保留 Pending，后续真实扩散可沿开放通道绕行。
            for(int step=0;step<=MaterialPhaseDepositQueue.MaximumRadius*3;step++)
            {
                if(IsBlocked(cell)) return false;
                if(cell==center) return true;
                Vector3Int delta=center-cell;
                int x=Mathf.Abs(delta.x),y=Mathf.Abs(delta.y),z=Mathf.Abs(delta.z);
                if(x>=y && x>=z) cell.x+=delta.x>0?1:-1;
                else if(y>=z) cell.y+=delta.y>0?1:-1;
                else cell.z+=delta.z>0?1:-1;
            }
            return false;
        }
        public void NotifyTopologyChanged(Vector3Int cell)
        {
            _terrain?.Remove(cell);
            ElementChunkKey tile=TerrainTile(cell);
            _terrainTiles?.Remove(tile); _activity?.WakeNeighbors(cell);
        }
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
            if (_world != null && _world.FixedSolids != null && _world.FixedSolids.BlocksGasCell(cell)) return true;
            if (_terrain.TryGetValue(cell, out bool solid)) return solid;
            ElementChunkKey tile=TerrainTile(cell);
            if(!_terrainTiles.TryGetValue(tile,out bool tileBlocked))
            {
                if(_samplesRemaining==0) { _activity.WakeNeighbors(cell); return true; }
                _samplesRemaining--;
                tileBlocked=_baker!=null && _baker.SampleEnvironmentSolidWorldBox(
                    _origin+new Vector3(tile.X*4,tile.Y,tile.Z*4)*_size,new Vector3(4,1,4)*_size);
                if(_tileCount==_tileKeys.Length) _terrainTiles.Remove(_tileKeys[_tileCursor]); else _tileCount++;
                _tileKeys[_tileCursor]=tile; _tileCursor=(_tileCursor+1)%_tileKeys.Length;
                _terrainTiles[tile]=tileBlocked;
            }
            // 4x1x4 空气 Tile 一次证明 16 格；垂直仅一层，避免地板交叉让上方整层空气退回逐格查询。
            // Solid Store 仍在上面逐格检查，空气缓存不允许穿过新冰或凝固粘液。
            if(!tileBlocked) return false;
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
        private static ElementChunkKey TerrainTile(Vector3Int cell)
        {
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell,4,out ElementChunkKey tile,out _);
            return new ElementChunkKey(tile.X,cell.y,tile.Z);
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
