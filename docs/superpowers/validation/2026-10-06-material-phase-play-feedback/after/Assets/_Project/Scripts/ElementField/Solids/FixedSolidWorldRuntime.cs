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
            int count = Math.Min(_profile.MaximumCellsPerTick, _pending.Count);
            for (int i = 0; i < count; i++)
            {
                _pending.TryDequeue(out MaterialPhasePendingDeposit pending);
                Vector3 position = pending.Request.WorldPosition + pending.Request.SurfaceNormal * (_size * .51f);
                var center = ElementWorldCoordinates.WorldToGlobalCell(position, _origin, _size);
                int radius = Mathf.Clamp(Mathf.CeilToInt(pending.Request.Radius / _size), 0, 4);
                int samples = 1 + (radius * 2 + 1) * (radius * 2 + 1) * (radius * 2 + 1);
                bool placed = false;
                // 每条请求只检查有界邻域；被角色占据或池满时保留 Pending，而非强行创建隐形物质。
                for (int scan = 0; scan < 16 && !placed; scan++)
                {
                    Vector3Int offset = MaterialPhaseDepositQueue.Offset(pending.Cursor++, radius);
                    if (pending.Cursor >= samples) pending.Cursor = 0;
                    if (offset.sqrMagnitude > radius * radius) continue;
                    Vector3Int cell = center + offset;
                    uint free = CellCapacity - _store.OccupancyAt(cell);
                    uint amount = Math.Min(free, pending.Remaining);
                    if (amount == 0 || !TryApply(cell, pending.Request.MaterialKind, amount, false)) continue;
                    pending.Remaining -= amount; placed = true;
                }
                if (!placed) { DeferredDeposits++; pending.WaitingTicks++; }
                _pending.Requeue(in pending);
            }
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
