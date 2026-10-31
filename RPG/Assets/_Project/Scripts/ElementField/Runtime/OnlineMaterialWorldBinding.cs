using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 同一份正式P7/P8材料内容按端装配。此组件放在导出世界Root，所有配置发生在inactive边界；
    /// 不能等Awake后仅禁用组件，因为禁用Behaviour仍可能Awake并建立Compute/Solver/写Port。
    /// 它只管理场景Owner与生命周期，不解析网络、不裁决玩家身份、不复制另一套材料Rendering。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OnlineMaterialWorldBinding : MonoBehaviour, IOnlineMaterialWorldBinding
    {
        [SerializeField] private ElementWorldRuntime _world;
        [Header("Bounded replication workspace - target-machine validation required")]
        [SerializeField, Min(1)] private int _captureCells = 65536;
        [SerializeField, Min(1)] private int _capturePending = 1024;
        [SerializeField, Min(1)] private int _inspectionBudget = 2000000;
        [SerializeField, Min(1)] private int _replicaChunks = 128;
        [SerializeField, Min(64)] private int _maximumChunkWords = 16384;
        private bool _configured, _detached, _configurationFaulted;
        private WorldExecutionMode _mode;
        private ulong _epoch;
        private IWorldReplicationSource _source;
        private WorldReplicaBuffer _buffer;
        private WorldReplicaStore _replica;
        public bool Faulted => _configurationFaulted || (_world != null && _world.IsSimulationFaulted);
        public bool IsReady => _configured && !_detached && !Faulted && _world != null && _world.IsInitialized;
        public IWorldAuthoritySimulation AuthoritySimulation => _configured && !_detached && _mode == WorldExecutionMode.Authority ? _world : null;
        public IWorldReplicationSource ReplicationSource => _detached ? null : _source;
        public WorldReplicaBuffer CaptureBuffer => _detached ? null : _buffer;
        public IWorldChunkPayloadInstaller ReplicaInstaller => _detached ? null : _replica;

        public bool TryConfigure(WorldExecutionMode mode, ulong epoch)
        {
            if (_detached || _configurationFaulted || gameObject.activeInHierarchy || epoch == 0
                || (mode != WorldExecutionMode.Authority && mode != WorldExecutionMode.Replica)) return false;
            if (_configured) return _mode == mode && _epoch == epoch;
            if (_world == null)
            {
                var worlds = GetComponentsInChildren<ElementWorldRuntime>(true);
                if (worlds.Length != 1) return false;
                _world = worlds[0];
            }
            if (_world.IsInitialized || _captureCells < 1 || _capturePending < 1 || _inspectionBudget < 1
                || _replicaChunks < 1 || _maximumChunkWords < 64) return false;
            try
            {
                // 先剥所有GPU玩法Owner；两个端的材料事实都必须来自CPU DS，而客户端可继续用GPU绘制外壳。
                StripOwners(mode, false);
                if (mode == WorldExecutionMode.Authority)
                {
                    _world.ConfigureExecutionMode(mode);
                    _source = new WorldReplicationSource(_world);
                    _buffer = new WorldReplicaBuffer(_captureCells, _capturePending, _inspectionBudget);
                    foreach (var exposure in GetComponentsInChildren<ElementWorldExposureSystem>(true)) exposure.enabled = true;
                    // Renderer脚本已剥除，保留CPU MeshCollider；静态关卡的Renderer不属于材料子树，不在这里改动。
                    foreach (var renderer in _world.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
                }
                else
                {
                    _replica = new WorldReplicaStore(epoch, _replicaChunks, _captureCells, _capturePending, _maximumChunkWords);
                    _world.ConfigureReplica(_replica);
                    // ConfigureReplica先读取三相可信Profile建立readmodel，再删除数量Owner，避免留下可被误调用的写对象。
                    StripOwners(mode, true);
                }
                _mode = mode; _epoch = epoch; _configured = true;
                return true;
            }
            catch (Exception)
            {
                // 配置可能已经剥过组件，不能装作原样继续或退回单机；调用方必须销毁这棵候选Root。
                _configurationFaulted = true;
                if (_world != null) { _world.SetSimulationSuspended(true); _world.enabled = false; }
                return false;
            }
        }

        private void Awake()
        {
            if (_configured) return;
            _configurationFaulted = true;
            if (_world != null) { _world.SetSimulationSuspended(true); _world.enabled = false; }
        }
        public bool TrySetActor(int slot, ulong actorId, Vector3 position) => _configured && !_detached
            && _mode == WorldExecutionMode.Authority && _world != null && _world.TrySetActor(slot, actorId, position);
        public void ClearActor(int slot) { if (_world != null) _world.ClearActor(slot); }
        public void SetSuspended(bool suspended) { if (_world != null) _world.SetSimulationSuspended(suspended); }
        public void Detach()
        {
            if (_detached) return;
            _detached = true;
            if (_world != null)
            {
                _world.SetSimulationSuspended(true);
                for (int i = 0; i < 4; i++) _world.ClearActor(i);
                _world.gameObject.SetActive(false);
            }
            _source = null; _buffer = null; _replica = null;
        }
        private void OnDestroy() => Detach();

        private void StripOwners(WorldExecutionMode mode, bool afterReplica)
        {
            var pending = new List<MonoBehaviour>();
            foreach (var behaviour in GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;
                bool remove = behaviour is GpuPbfFluidRuntime || behaviour is FluidGameplayOccupancyBridge
                    || behaviour is FluidChunkStreamingRuntime;
                if (mode == WorldExecutionMode.Authority)
                    remove |= behaviour.GetType().Namespace == "Game.Rendering";
                else
                {
                    remove |= behaviour is ElementWorldExposureSystem || behaviour is MaterialInventoryTransferAdapter
                        || behaviour is ContainerDepositAdapter || behaviour is MaterialEnvironmentAuthoring;
                    if (afterReplica) remove |= behaviour is LiquidWorldRuntime || behaviour is FixedSolidWorldRuntime || behaviour is GasWorldRuntime;
                }
                if (remove) pending.Add(behaviour);
            }
            // Unity在Editor和Player都执行RequireComponent约束；按依赖关系先拆依赖者，不能赌组件数组的顺序。
            while (pending.Count != 0)
            {
                bool removed = false;
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var candidate = pending[i];
                    if (candidate == null) { pending.RemoveAt(i); removed = true; continue; }
                    if (HasDependent(candidate)) continue;
                    // Root仍inactive，且只销毁实例组件；不触碰Prefab资产。延后Destroy会让本帧激活时仍执行Awake。
                    DestroyImmediate(candidate);
                    pending.RemoveAt(i); removed = true;
                }
                if (!removed) throw new InvalidOperationException("材料Owner仍被保留组件RequireComponent依赖。");
            }
        }
        private static bool HasDependent(MonoBehaviour candidate)
        {
            Type target = candidate.GetType();
            foreach (var component in candidate.GetComponents<Component>())
            {
                if (component == null || component == candidate) continue;
                foreach (RequireComponent requirement in component.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                    if ((requirement.m_Type0 != null && requirement.m_Type0.IsAssignableFrom(target))
                        || (requirement.m_Type1 != null && requirement.m_Type1.IsAssignableFrom(target))
                        || (requirement.m_Type2 != null && requirement.m_Type2.IsAssignableFrom(target))) return true;
            }
            return false;
        }
    }
}
