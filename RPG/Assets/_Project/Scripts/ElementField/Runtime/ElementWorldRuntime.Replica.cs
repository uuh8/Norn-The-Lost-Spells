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
        public WorldReplicaStore ReplicaStore => _replicaStore;

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
