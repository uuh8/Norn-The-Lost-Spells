using System;
using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementWorldRuntime
    {
        private WorldReplicaStore _replicaStore;
        private WorldReplicaLiquidReadModel _replicaLiquids;
        private WorldReplicaSolidReadModel _replicaSolids;
        private WorldReplicaVolumeReadModel _replicaGases;
        private LiquidVolumeSettings _replicaVolumeSettings;
        private bool _replicaPresentationSubscribed;
        private Transform _replicaPresentationInterest;
        private Camera _replicaPresentationCamera;
        public WorldReplicaStore ReplicaStore => _replicaStore;

        /// <summary>
        /// 表现距离中心与固定Geometry Origin分离。不授予模拟、网络Interest或资源写权限。
        /// Actor/Camera可能先销毁、后广播解绑，也可能跨Scene移动；查询时按Unity假null及Scene归属兜底，
        /// 绝不让旧世界继续跟随新图角色。无可信绑定时保持有限的世界位置回退，不搜索/猜测其他角色。
        /// </summary>
        private Vector3 ReplicaPresentationInterestPosition
        {
            get
            {
                if (!_replicaPresentationSubscribed || _replicaPresentationInterest == null
                    || _replicaPresentationCamera == null || !_replicaPresentationCamera.isActiveAndEnabled
                    || !_replicaPresentationInterest.gameObject.activeInHierarchy
                    || _replicaPresentationInterest.gameObject.scene != gameObject.scene)
                    return transform.position;
                return _replicaPresentationInterest.position;
            }
        }

        private void EnableReplicaPresentationInterest()
        {
            if (_replicaStore == null || !IsInitialized) return;
            if (!_replicaPresentationSubscribed)
            {
                _replicaPresentationSubscribed = true;
                EventBus<LocalActorPresentationEvent>.Subscribe(OnReplicaLocalActorChanged);
                EventBus<LocalActorCameraBoundEvent>.Subscribe(OnReplicaLocalCameraChanged);
            }
            // 场景、Actor和原Camera加载顺序不固定；晚启用必须补读当前注册而不是等下一次重连。
            RefreshReplicaPresentationInterest();
        }

        private void DisableReplicaPresentationInterest()
        {
            if (_replicaPresentationSubscribed)
            {
                EventBus<LocalActorPresentationEvent>.Unsubscribe(OnReplicaLocalActorChanged);
                EventBus<LocalActorCameraBoundEvent>.Unsubscribe(OnReplicaLocalCameraChanged);
                _replicaPresentationSubscribed = false;
            }
            // 消费者只清自己的缓存，不能Clear全局Owner，否则会影响HUD、相机或较晚的新Scene。
            _replicaPresentationInterest = null;
            _replicaPresentationCamera = null;
        }

        private void OnReplicaLocalActorChanged(LocalActorPresentationEvent _) => RefreshReplicaPresentationInterest();
        private void OnReplicaLocalCameraChanged(LocalActorCameraBoundEvent _) => RefreshReplicaPresentationInterest();

        private void RefreshReplicaPresentationInterest()
        {
            _replicaPresentationInterest = null;
            _replicaPresentationCamera = null;
            if (!_replicaPresentationSubscribed) return;
            // 事件只是刷新通知，不能凭任意Actor载荷接管本地目标。两个既有注册必须指向同一本地Actor。
            // Clear(owner)先更新注册再发布default同一Bound事件，因而原解绑合同无需修改。
            var actor = LocalActorPresentation.Actor;
            // 可信身份与瞬时active分开：晚订阅时Camera/Actor可能暂时失活，单纯恢复active不会再发绑定事件。
            // 仍缓存合法引用，由位置getter每次检查active/假null；否则恢复后会永久回退到世界原点。
            if (actor == null || actor.scene != gameObject.scene
                || !LocalActorCameraPresentation.TryGet(actor, out var camera)) return;
            _replicaPresentationInterest = actor.transform;
            _replicaPresentationCamera = camera;
        }

        /// <summary>
        /// inactive内容激活前装配只读缓存。只读Profile用于Geometry、Density、Mesh容量，
        /// 不调用TryInitialize，不建Solver/Reaction/Registry写Sink，也不运行Exposure。
        /// Exposure及库存Adapter必须由外层在同一个inactive边界剥除，不能依赖组件Awake顺序。
        /// </summary>
        public void ConfigureReplica(WorldReplicaStore store)
        {
            if (store == null || _profile == null || _gridLiquidRuntime == null || _fixedSolidRuntime == null || _gasRuntime == null)
                throw new ArgumentException("Replica缺少可信材料世界配置。");
            if (IsInitialized || _initializationStarted || _replicaStore != null)
                throw new InvalidOperationException("不能替换已初始化世界的所有权。");
            ConfigureExecutionMode(WorldExecutionMode.Replica);
            var liquid = _gridLiquidRuntime.ConfigurationProfile;
            var solid = _fixedSolidRuntime.ConfigurationProfile;
            var gas = _gasRuntime.ConfigurationProfile;
            if (liquid == null || solid == null || gas == null) throw new ArgumentException("Replica缺少三相Profile。");
            _origin = transform.position; _solidBaker = GetComponent<ElementFieldSolidBaker>();
            var volume = liquid.CreateVolumeSettings(_profile.CellSize);
            _replicaVolumeSettings = volume;
            var legacyGeometry = new WorldGridGeometry(WorldMaterialDomain.LegacyCell, _origin, _profile.CellSize, _profile.ChunkSize);
            var liquidGeometry = new WorldGridGeometry(WorldMaterialDomain.Liquid, _origin, volume.CellSize, _profile.ChunkSize);
            var solidGeometry = new WorldGridGeometry(WorldMaterialDomain.Solid, _origin, _profile.CellSize, _profile.ChunkSize);
            var gasGeometry = new WorldGridGeometry(WorldMaterialDomain.Gas, _origin, _profile.CellSize, _profile.ChunkSize);
            if (!store.ConfigureGeometry(legacyGeometry) || !store.ConfigureGeometry(liquidGeometry)
                || !store.ConfigureGeometry(solidGeometry) || !store.ConfigureGeometry(gasGeometry))
                throw new ArgumentException("Replica网格Geometry不一致。");
            var liquidMaterials = new byte[liquid.MaterialCount];
            var solidMaterials = new byte[solid.MaterialCount];
            var gasMaterials = new MaterialId[gas.MaterialCount]; var gasKeys = new byte[gas.MaterialCount];
            for (int i = 0; i < liquidMaterials.Length; i++) liquidMaterials[i] = (byte)liquid.GetMaterial(i).Material;
            for (int i = 0; i < solidMaterials.Length; i++) solidMaterials[i] = (byte)solid.GetMaterial(i).Material;
            for (int i = 0; i < gasMaterials.Length; i++) { gasMaterials[i] = gas.GetMaterial(i).Material; gasKeys[i] = (byte)gasMaterials[i]; }
            if (!store.ConfigureMaterials(WorldMaterialDomain.Liquid, (uint)volume.CapacityGmu, liquidMaterials)
                || !store.ConfigureMaterials(WorldMaterialDomain.Solid, solid.CellCapacityGmu, solidMaterials, true, solid.MaximumChunks)
                || !store.ConfigureMaterials(WorldMaterialDomain.Gas, gas.CellCapacityGmu, gasKeys, false, gas.MaximumChunks))
                throw new ArgumentException("Replica材料目录或容量配置冲突。");
            _replicaSolids = new WorldReplicaSolidReadModel(store, solidGeometry, solid, transform);
            _replicaLiquids = new WorldReplicaLiquidReadModel(store, liquidGeometry, liquid, _profile.CellSize, _solidBaker, _replicaSolids);
            _replicaGases = new WorldReplicaVolumeReadModel(store, gasGeometry, gas.CellCapacityGmu, gasMaterials);
            _effectiveWaterSimulationMode = WaterSimulationMode.GridLiquid;
            _replicaStore = store; IsInitialized = true;
        }
        private bool TryGetReplicaQuantity(Vector3Int worldCell, MaterialId material, out uint quantity)
        {
            quantity = 0;
            if (_replicaStore == null) return false;
            if (_replicaStore.TryGetQuantity(WorldMaterialDomain.Solid, worldCell, (byte)material, out quantity)
                || _replicaStore.TryGetQuantity(WorldMaterialDomain.Gas, worldCell, (byte)material, out quantity)) return true;
            // Runtime公共查询仍接受World细格；只读液体Renderer传native粗格，二者不能混用。
            return _replicaStore.TryGetQuantity(WorldMaterialDomain.Liquid, _replicaVolumeSettings.WorldCellToLiquidCell(worldCell), (byte)material, out quantity);
        }
        private bool TryGetReplicaLegacyCell(Vector3Int position, out ElementCell cell)
        {
            cell = default;
            if (_replicaStore == null) return false;
            // LegacyCell每格只允许一个主要身份；块内二分查询，不为每个Fire采样遍历255个身份。
            if (_replicaStore.TryGetFirstCell(WorldMaterialDomain.LegacyCell, position, out var sample))
            { cell = new ElementCell((MaterialId)sample.MaterialId, (byte)sample.Quantity); return true; }
            return false;
        }
        private int CopyReplicaLegacyChunk(ElementChunkKey key, ElementCell[] destination)
        {
            int required = checked(ChunkSize * ChunkSize * ChunkSize);
            if (destination == null || destination.Length < required) throw new ArgumentException(nameof(destination));
            for (int z = 0; z < ChunkSize; z++) for (int y = 0; y < ChunkSize; y++) for (int x = 0; x < ChunkSize; x++)
                TryGetReplicaLegacyCell(new Vector3Int(key.X * ChunkSize + x, key.Y * ChunkSize + y, key.Z * ChunkSize + z), out destination[(z * ChunkSize + y) * ChunkSize + x]);
            return required;
        }
    }
}
