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
        private int _depositCellLimit;
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
        public bool TryInitialize(ElementWorldRuntime world, ElementFieldSolidBaker baker, Vector3 origin, float size, int chunkSize)
        {
            if (_store != null) return true;
            if (_profile == null || !float.IsFinite(size) || size <= 0
                || !FluidSpawnRequestValidator.IsFinite(origin) || _profile.MaterialCount == 0
                || _profile.MaximumNavigationBoxesPerChunk < 1 || _profile.MaximumCellsPerTick < 1
                || _profile.MaximumCellsPerTick > _depositWork.Length || _profile.MaximumColliderChunksPerTick < 1
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
                _conversionDirty = new ElementChunkKey[_profile.MaximumChunks];
                _pending = new MaterialPhaseDepositQueue(_profile.MaximumPendingDeposits);
                _depositCellLimit=_profile.MaximumCellsPerTick;
                _physics = new FixedSolidPhysicsAdapter(transform, _store, BlocksMaterial,
                    _profile.MaximumNavigationBoxesPerChunk, origin, size);
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
            int count=Math.Min(_profile.MaximumCellsPerTick,_pending.Count),requests=0,writes=0,scanBudget=256;
            // Journal 只覆盖本批；全部几何可准备后才发布 Collider。提高“写格数”仍只产生
            // 有界的 Chunk Cooking，避免把生成延迟换成一帧十几次 MeshCollider 重建。
            for(int i=0;i<count && writes<_depositCellLimit && scanBudget>0;i++)
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
                int massRadius=pending.Request.TotalAmount<=CellCapacity?0:(int)Math.Ceiling(Math.Pow(pending.Request.TotalAmount/(double)CellCapacity*2*3/(4*Math.PI),1d/3d))+1;
                int radius = Mathf.Clamp(Math.Max(Mathf.CeilToInt(pending.Request.Radius / _size),massRadius), 0, 4);
                int samples=MaterialPhaseDepositQueue.SampleCount(radius);
                bool placed = false;
                for(int scan=0;scan<Math.Min(128,samples) && scanBudget>0 && pending.Remaining>0 && writes<_depositCellLimit;scan++)
                {
                    scanBudget--;
                    Vector3Int offset = MaterialPhaseDepositQueue.Offset(pending.Cursor++, radius);
                    if (pending.Cursor >= samples) pending.Cursor = 0;
                    Vector3Int cell = center + offset;
                    uint free = CellCapacity - _store.OccupancyAt(cell);
                    uint amount = Math.Min(free, pending.Remaining);
                    var material=pending.Request.MaterialKind;
                    if(amount==0 || !CanTrackDepositCell(cell)
                        || _baker!=null && _baker.SampleEnvironmentSolidWorldCell(cell,_origin,_size)
                        || BlocksMaterial(material) && IntersectsActor(cell,amount)
                        || _world!=null && _world.HasFluidOrGasAt(cell)) continue;
                    if(BlocksWholeCell(material) && _world!=null && !_world.TryPrepareMaterialTopologyChange(cell,true)) continue;
                    if(!_store.TryAdd(cell,material,amount)) continue;
                    TrackConversionCell(cell);
                    _depositCells[writes]=cell; _depositMaterials[writes]=material; _depositAmounts[writes++]=amount;
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
                _depositCellLimit=Math.Max(1,_depositCellLimit/2);
            }
            else if(writes>0)
            {
                PublishConversionGeometry();
                for(int i=0;i<writes;i++) _world?.NotifyMaterialTopologyChanged(_depositCells[i]);
                _depositCellLimit=Math.Min(_profile.MaximumCellsPerTick,_depositCellLimit+1);
            }
            for(int i=0;i<requests;i++)
            {
                var pending=_depositWork[i];
                if(!ready) { pending.Remaining=_depositOriginal[i]; pending.Cursor=0; pending.WaitingTicks++; DeferredDeposits++; }
                _pending.Requeue(in pending);
            }
            _store.ReclaimEmptyChunks(); _physics.ReleaseUnused(_store);
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
            { var hit = _actorHits[i]; _actorHits[i] = null; blocked |= hit is CharacterController || hit != null && hit.GetComponentInParent<StatusController>() != null; }
            return blocked;
        }
        private void OnDestroy() => _physics?.Dispose();
    }
}
