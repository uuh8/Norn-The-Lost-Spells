using System;
using Game.Combat;
using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>固定固体唯一数量 Owner；与 Physics 在 World Tick 内发布，静止时没有 Update 或重建。</summary>
    [DisallowMultipleComponent]
    public sealed partial class FixedSolidWorldRuntime : MonoBehaviour, IMaterialDepositSink, IFixedSolidSurfaceReadOnly
    {
        [SerializeField] private FixedSolidWorldProfile _profile;
        private FixedSolidVolumeStore _store;
        private FixedSolidPhysicsAdapter _physics;
        private MaterialPhaseDepositQueue _pending;
        private ElementWorldRuntime _world;
        private ElementFieldSolidBaker _baker;
        private Vector3 _origin;
        private float _size;
        private readonly bool[] _blocks = new bool[256];
        private readonly bool[] _partialVolume = new bool[256];
        private readonly Collider[] _actorHits = new Collider[32];
        private readonly ElementChunkKey[] _dirty = new ElementChunkKey[7];
        private int _dirtyCount;
        private readonly MaterialPhasePendingDeposit[] _depositWork = new MaterialPhasePendingDeposit[64];
        private readonly uint[] _depositOriginal = new uint[64];
        private readonly Vector3Int[] _depositCells = new Vector3Int[64];
        private readonly MaterialId[] _depositMaterials = new MaterialId[64];
        private readonly uint[] _depositAmounts = new uint[64];
        private int _depositRequestLimit;
        private readonly Vector3Int[] _grainCells = new Vector3Int[8];
        private readonly uint[] _grainFree = new uint[8];
        private readonly uint[] _grainAmounts = new uint[8];
        public bool IsMaterialInitialized => _store != null;
        public int ResidentCellCount => _store != null ? _store.ResidentCellCount : 0;
        public int ResidentChunkCount => _store != null ? _store.ResidentChunkCount : 0;
        public int PendingDepositCount => _pending != null ? _pending.Count : 0;
        public uint Version => _store != null ? _store.Version : 0;
        public uint CellCapacity => _store != null ? _store.CellCapacity : 0;
        public ulong TotalQuantity => _store != null ? _store.TotalQuantity : 0;
        public ulong PendingQuantity => _pending != null ? _pending.PendingQuantity : 0;
        public int SurfaceCapacity => _physics != null ? _physics.Capacity : 0;
        public uint SurfaceRebuildCount => _physics != null ? _physics.RebuildCount : 0;
        public uint DeferredDeposits { get; private set; }
        public int MaximumChunks => _store != null ? _store.MaximumChunks : 0;
        public uint ChunkCapacityRejections { get; private set; }
        public uint GeometryCapacityRejections { get; private set; }
        public bool TryInitialize(ElementWorldRuntime world, ElementFieldSolidBaker baker, Vector3 origin, float size, int chunkSize)
        {
            if (_store != null) return true;
            if (_profile == null || !float.IsFinite(size) || size <= 0
                || !FluidSpawnRequestValidator.IsFinite(origin) || _profile.MaterialCount == 0
                || _profile.MaximumNavigationBoxesPerChunk < 1 || _profile.MaximumCellsPerTick < 1
                || _profile.MaximumCellsPerTick > _depositWork.Length || _profile.MaximumColliderChunksPerTick < 1
                || _profile.DepositGrainCells < 1 || _profile.DepositGrainCells > 2
                || _profile.CellCapacityGmu == 0 || _profile.CellCapacityGmu > int.MaxValue) return false;
            try
            {
                var materials = new MaterialId[_profile.MaterialCount];
                for (int i = 0; i < materials.Length; i++)
                {
                    var behavior = _profile.GetMaterial(i); materials[i] = behavior.Material;
                    _blocks[(byte)behavior.Material] = behavior.BlocksMovement;
                    _partialVolume[(byte)behavior.Material] = behavior.UsesPartialVolume;
                    // 现有部分容量/底面适配器只描述 DriedSlime，不能静默接受其它材料后漏算体积。
                    if (behavior.UsesPartialVolume && behavior.Material != MaterialId.DriedSlime)
                        throw new ArgumentException("UsesPartialVolume 当前只支持固化粘液的体积占据。");
                }
                _world = world; _baker = baker; _origin = origin; _size = size;
                _store = new FixedSolidVolumeStore(chunkSize, _profile.MaximumChunks, _profile.CellCapacityGmu, materials);
                // 重力跨Chunk平移会同时失效旧/新两组页；数量Owner上限仍不变。
                _conversionDirty = new ElementChunkKey[_profile.MaximumChunks*2];
                _pending = new MaterialPhaseDepositQueue(_profile.MaximumPendingDeposits);
                _depositRequestLimit=_profile.MaximumCellsPerTick;
                _physics = new FixedSolidPhysicsAdapter(transform, _store, BlocksMaterial,
                    _profile.MaximumNavigationBoxesPerChunk, origin, size);
                InitializeGravity();
                return true;
            }
            catch (Exception ex) { GameLog.Error($"FixedSolid 初始化失败：{ex.Message}", "ElementField"); _store = null; return false; }
        }
        public bool Supports(MaterialId material) => _store != null && _store.Supports(material);
        public bool BlocksMaterial(MaterialId material) => _blocks[(byte)material];
        internal bool BlocksWholeCell(MaterialId material) => _blocks[(byte)material] && !_partialVolume[(byte)material];
        internal bool BlocksGasCell(Vector3Int cell)
        {
            // Gas 使用细格：整格残留已没有气体空间；薄残留仍允许上方的空气共存。
            // Liquid 使用粗格和数量占据适配器，不能把这个判定当作整个粗格的墙。
            return BlocksCell(cell) || _store != null && _store.OccupancyAt(cell)>=CellCapacity;
        }
        public bool BlocksCell(Vector3Int cell)
        {
            if (_store == null) return false;
            for (int i = 0; i < _store.MaterialCount; i++)
            { var material = _store.GetMaterial(i); if (BlocksWholeCell(material) && _store.TryGetQuantity(cell, material, out _)) return true; }
            return false;
        }
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
        public bool TryGetSurface(int index, out Mesh mesh, out Vector3 position)
        { mesh = null; position = default; return _physics != null && _physics.TryGetSurface(index, out mesh, out position); }
        public void Step()
        {
            if (_store == null || _pending.Count == 0) return;
            BeginConversionGeometry();
            int count=Math.Min(_depositRequestLimit,_pending.Count),requests=0,writes=0,scanBudget=256;
            int cellLimit=_profile.MaximumCellsPerTick,grain=_profile.DepositGrainCells;
            ulong grainCapacity=(ulong)CellCapacity*(uint)(grain*grain*grain);
            // Journal 只覆盖本批；全部几何可准备后才发布 Collider。提高“写格数”仍只产生
            // 有界的 Chunk Cooking，避免把生成延迟换成一帧十几次 MeshCollider 重建。
            for(int i=0;i<count && writes<cellLimit && scanBudget>0;i++)
            {
                _pending.TryDequeue(out MaterialPhasePendingDeposit pending);
                _depositOriginal[requests]=pending.Remaining;
                int requestStart=writes,dirtyStart=_conversionDirtyCount;
                Vector3 position = pending.Request.WorldPosition + pending.Request.SurfaceNormal * (_size * .51f);
                var center = ElementWorldCoordinates.WorldToGlobalCell(position, _origin, _size);
                // 命中点上方半格仍可能与厚地板相交；沿外法线寻找空气锚点，不能从墙内铺实体。
                Vector3 normal=pending.Request.SurfaceNormal;
                var outward=new Vector3Int(Mathf.RoundToInt(normal.x),Mathf.RoundToInt(normal.y),Mathf.RoundToInt(normal.z));
                for(int n=0;n<2 && _baker!=null && _baker.SampleEnvironmentSolidWorldCell(center,_origin,_size);n++) center+=outward;
                // X/Z 在负坐标同样向下对齐；Y 以地面空气锚点为块底，不能对齐后嵌回地板。
                center.x=Mathf.FloorToInt(center.x/(float)grain)*grain;
                center.z=Mathf.FloorToInt(center.z/(float)grain)*grain;
                int massRadius=pending.Request.TotalAmount<=grainCapacity?0:(int)Math.Ceiling(Math.Pow(pending.Request.TotalAmount/(double)grainCapacity*2*3/(4*Math.PI),1d/3d))+1;
                int radius = Mathf.Clamp(Math.Max(Mathf.CeilToInt(pending.Request.Radius / (_size*grain)),massRadius), 0, 4);
                int samples=MaterialPhaseDepositQueue.SampleCount(radius);
                bool placed = false;
                for(int scan=0;scan<Math.Min(128,samples) && scanBudget>0 && pending.Remaining>0 && writes<cellLimit;scan++)
                {
                    scanBudget--;
                    Vector3Int offset = MaterialPhaseDepositQueue.Offset(pending.Cursor++, radius);
                    if (pending.Cursor >= samples) pending.Cursor = 0;
                    Vector3Int cell = center + offset*grain;
                    if(!TryDepositGrain(cell,pending.Request.MaterialKind,pending.Remaining,ref writes,out uint amount)) continue;
                    pending.Remaining -= amount; placed = true;
                }
                // 普通固体的一发 <= 完整 Tick 写格预算。空间/同帧其它命中挤占预算时，
                // 整发延后而不显露半个实体；撤回这一请求，保留本批其它完整请求。
                if(pending.Remaining>0 && pending.Request.TotalAmount<=(ulong)CellCapacity*(uint)_profile.MaximumCellsPerTick)
                {
                    for(int n=writes-1;n>=requestStart;n--) UndoConversionAdd(_depositCells[n],_depositMaterials[n],_depositAmounts[n]);
                    writes=requestStart; _conversionDirtyCount=dirtyStart;
                    pending.Remaining=_depositOriginal[requests]; pending.Cursor=0; placed=false;
                }
                if (!placed) { DeferredDeposits++; pending.WaitingTicks++; }
                _depositWork[requests++]=pending;
            }
            bool ready=writes==0 || ValidateConversionGeometry();
            if(!ready)
            {
                for(int i=writes-1;i>=0;i--) UndoConversionAdd(_depositCells[i],_depositMaterials[i],_depositAmounts[i]);
                CancelConversionGeometry();
                // 导航段容量拒绝复杂批次时下次缩小批次重试；真实 Pending 不丢失，
                // 也不能永久重复一个必然失败的大批次。
                GeometryCapacityRejections++;
                // 只缩小请求批数。单发写格预算不得小于完整实体所需格数，否则整发回滚
                // 与“成功才恢复预算”互相等待，导致以后所有普通法术永久无法生成。
                _depositRequestLimit=Math.Max(1,requests/2);
            }
            else if(writes>0)
            {
                PublishConversionGeometry();
                for(int i=0;i<writes;i++) _world?.NotifyMaterialTopologyChanged(_depositCells[i]);
                _depositRequestLimit=Math.Min(_profile.MaximumCellsPerTick,_depositRequestLimit+1);
            }
            for(int i=0;i<requests;i++)
            {
                var pending=_depositWork[i];
                if(!ready) { pending.Remaining=_depositOriginal[i]; pending.Cursor=0; pending.WaitingTicks++; DeferredDeposits++; }
                _pending.Requeue(in pending);
            }
            _store.ReclaimEmptyChunks(); _physics.ReleaseUnused(_store);
        }
        private bool TryDepositGrain(Vector3Int minimum,MaterialId material,uint remaining,ref int writes,out uint added)
        {
            added=0;
            int grain=_profile.DepositGrainCells,layerCells=grain*grain,cellCount=layerCells*grain;
            int writeStart=writes,dirtyStart=_conversionDirtyCount;
            uint left=remaining;
            // 每层按剩余空位均分，少量尾量仅产生对应高度；不能把 1 GMU 放大为满块。
            // 所有数组初始化时预约，出生、预检与回滚均没有堆分配。
            for(int y=0;y<grain;y++)
            {
                ulong layerFree=0; int slots=0;
                for(int z=0;z<grain;z++) for(int x=0;x<grain;x++)
                {
                    int index=y*layerCells+z*grain+x;
                    var cell=minimum+new Vector3Int(x,y,z); _grainCells[index]=cell;
                    uint occupied=_store.OccupancyAt(cell);
                    if(occupied>0 && !_store.TryGetQuantity(cell,material,out _)) return false;
                    uint free=CellCapacity-occupied; _grainFree[index]=free; _grainAmounts[index]=0;
                    layerFree+=free; if(free>0) slots++;
                }
                uint layerLeft=(uint)Math.Min((ulong)left,layerFree); left-=layerLeft;
                for(int n=y*layerCells;n<(y+1)*layerCells && layerLeft>0;n++)
                {
                    if(_grainFree[n]==0) continue;
                    uint share=(uint)(((ulong)layerLeft+(uint)slots-1)/(uint)slots);
                    uint amount=Math.Min(_grainFree[n],share);
                    _grainAmounts[n]=amount; layerLeft-=amount; slots--;
                }
            }
            if(left==remaining) return false;
            int needed=0; for(int n=0;n<cellCount;n++) if(_grainAmounts[n]>0) needed++;
            if(writes+needed>_profile.MaximumCellsPerTick) return false;
            for(int n=0;n<cellCount;n++)
            {
                uint amount=_grainAmounts[n]; if(amount==0) continue;
                var cell=_grainCells[n];
                ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell,_store.ChunkSize,out var candidateKey,out _);
                if(_store.GetChunkVersion(candidateKey)==0 && _store.ResidentChunkCount>=_store.MaximumChunks)
                { ChunkCapacityRejections++; _conversionDirtyCount=dirtyStart; return false; }
                if(!CanTrackDepositCell(cell)
                    || _baker!=null && _baker.SampleEnvironmentSolidWorldCell(cell,_origin,_size)
                    || BlocksMaterial(material) && IntersectsActor(cell,amount)
                    || _world!=null && _world.HasFluidOrGasAt(cell)
                    || BlocksWholeCell(material) && _world!=null && !_world.TryPrepareMaterialTopologyChange(cell,true))
                { _conversionDirtyCount=dirtyStart; return false; }
                // 尚未写入的中心 Chunk 同样占有几何预约，整块跨 Chunk 时不能逐格漏算预算。
                for(int d=0;d<_dirtyCount;d++)
                {
                    bool found=false; for(int i=0;i<_conversionDirtyCount;i++) found|=_conversionDirty[i].Equals(_dirty[d]);
                    if(!found) _conversionDirty[_conversionDirtyCount++]=_dirty[d];
                }
            }
            for(int n=0;n<cellCount;n++)
            {
                uint amount=_grainAmounts[n]; if(amount==0) continue;
                var cell=_grainCells[n];
                if(!_store.TryAdd(cell,material,amount))
                {
                    ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell,_store.ChunkSize,out var key,out _);
                    if(_store.GetChunkVersion(key)==0 && _store.ResidentChunkCount>=_store.MaximumChunks) ChunkCapacityRejections++;
                    for(int i=writes-1;i>=writeStart;i--) UndoConversionAdd(_depositCells[i],material,_depositAmounts[i]);
                    writes=writeStart; _conversionDirtyCount=dirtyStart; return false;
                }
                _depositCells[writes]=cell; _depositMaterials[writes]=material; _depositAmounts[writes++]=amount;
                added+=amount;
            }
            return true;
        }
        private bool CanTrackDepositCell(Vector3Int cell)
        {
            CollectDirty(cell);
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell,_store.ChunkSize,out ElementChunkKey key,out _);
            // 尚未创建的中心 Chunk 也占几何预算，不能先写后发现超预算。
            bool found=false; for(int i=0;i<_dirtyCount;i++) found|=_dirty[i].Equals(key);
            if(!found) _dirty[_dirtyCount++]=key;
            int count=_conversionDirtyCount;
            for(int i=0;i<_dirtyCount;i++)
            {
                bool tracked=false; for(int n=0;n<_conversionDirtyCount;n++) tracked|=_conversionDirty[n].Equals(_dirty[i]);
                if(!tracked) count++;
            }
            return count<=Math.Min(_profile.MaximumColliderChunksPerTick,_conversionDirty.Length);
        }
        public bool TryRemove(Vector3Int cell, MaterialId material, uint quantity) => TryApply(cell, material, quantity, true);
        private bool TryApply(Vector3Int cell, MaterialId material, uint quantity, bool remove)
        {
            if (_store == null || quantity == 0) return false;
            if (!remove)
            {
                // 地形与已有液体先保留；水→冰的源消耗由后续转换事务明确处理。
                if (_baker != null && _baker.SampleEnvironmentSolidWorldCell(cell, _origin, _size)) return false;
                if (BlocksMaterial(material) && IntersectsActor(cell, quantity)) return false;
                if (_world != null && _world.HasFluidOrGasAt(cell)) return false;
            }
            if (remove && (!_store.TryGetQuantity(cell, material, out uint existing) || existing < quantity)) return false;
            if (BlocksWholeCell(material) && _world != null
                && !_world.TryPrepareMaterialTopologyChange(cell, !remove || _store.OccupancyAt(cell) > quantity)) return false;
            bool changed = remove ? _store.TryRemove(cell, material, quantity) : _store.TryAdd(cell, material, quantity);
            if (!changed) return false;
            CollectDirty(cell);
            for (int i = 0; i < _dirtyCount; i++)
                if (!_physics.CanPrepare(_dirty[i]))
                {
                    // 主线程内尚未发布物理/查询结果；预约失败完整恢复原数量。
                    if (remove) _store.TryAdd(cell, material, quantity); else _store.TryRemove(cell, material, quantity);
                    _store.ReclaimEmptyChunks(); return false;
                }
            for (int i = 0; i < _dirtyCount; i++) _physics.Publish(_dirty[i]);
            _world?.NotifyMaterialTopologyChanged(cell);
            _store.ReclaimEmptyChunks(); _physics.ReleaseUnused(_store);
            return true;
        }
        private void CollectDirty(Vector3Int cell)
        {
            _dirtyCount = 0; AddDirty(cell); AddDirty(cell + Vector3Int.left); AddDirty(cell + Vector3Int.right);
            AddDirty(cell + Vector3Int.up); AddDirty(cell + Vector3Int.down);
            AddDirty(cell + Vector3Int.forward); AddDirty(cell + Vector3Int.back);
        }
        private void AddDirty(Vector3Int cell)
        {
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell, _store.ChunkSize, out ElementChunkKey key, out _);
            if (_store.GetChunkVersion(key) == 0) return;
            for (int i = 0; i < _dirtyCount; i++) if (_dirty[i].Equals(key)) return;
            _dirty[_dirtyCount++] = key;
        }
        private bool IntersectsActor(Vector3Int cell, uint quantity)
        {
            float height = Mathf.Min(1f, (_store.OccupancyAt(cell) + quantity) / (float)CellCapacity);
            Vector3 size = new Vector3(_size * .98f, _size * height * .98f, _size * .98f);
            Vector3 center = _origin + (Vector3)cell * _size + new Vector3(_size, _size * height, _size) * .5f;
            int count = Physics.OverlapBoxNonAlloc(center, size * .5f, _actorHits, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
            bool blocked = count == _actorHits.Length;
            for (int i = 0; i < count; i++)
            { var hit = _actorHits[i]; _actorHits[i] = null; blocked |= hit is CharacterController || hit != null && ElementFieldSolidBaker.HasStatusOwner(hit); }
            return blocked;
        }
        private void OnDestroy() => _physics?.Dispose();
    }
}
