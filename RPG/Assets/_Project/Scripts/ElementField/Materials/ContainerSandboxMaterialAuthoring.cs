using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>测试供给通过真实 Owner 显式注入；一次启动，不用 Update 持续补量掩盖采集损失。</summary>
    public sealed class ContainerSandboxMaterialAuthoring : MonoBehaviour
    {
        [SerializeField] private ElementWorldRuntime _world;
        [SerializeField, Tooltip("独立数量验收可暂停 Solver；Owner/表面/Collider 仍保留，采集写入仍正常。")]
        private bool _pauseWorldSimulation = true;
        public Vector3 MixedSamplePosition => new Vector3(.25f, .25f, .25f);
        public Vector3 StableSamplePosition => new Vector3(2.25f, .25f, .25f);
        public Vector3 GasSamplePosition => new Vector3(2.25f, 1.25f, .25f);
        public Vector3 SolidSamplePosition => new Vector3(-1.75f, .25f, .25f);
        public bool Initialized { get; private set; }
        private void Start()
        {
            if (_world == null || !_world.IsInitialized) { GameLog.Error("容器测试世界没有初始化。", "ContainerSandbox"); return; }
            var liquid = _world.InventoryLiquid;
            var mixed = Cell(MixedSamplePosition, liquid.Origin, liquid.CellSize);
            if (!liquid.TryAddInitial(mixed, MaterialId.Water, 100) || !liquid.TryAddInitial(mixed, MaterialId.Poison, 50))
            { GameLog.Error("混合测试材料注入失败。", "ContainerSandbox"); return; }
            var stable = Cell(StableSamplePosition, liquid.Origin, liquid.CellSize);
            // stepX 使用全局格坐标，平底也必须位于 [minimum.x, maximum.x]，不能默认传世界原点 0。
            if (!liquid.TryRegisterStableRegion(MaterialId.Water, stable, stable + Vector3Int.one, stable.x, 0, 100, out _))
            { GameLog.Error($"稳定液池测试供给失败：cell={stable}, origin={liquid.Origin}, size={liquid.CellSize}, capacity={liquid.CellCapacityGmu}, solid={_world.GetComponent<ElementFieldSolidBaker>().SampleEnvironmentSolidWorldCell(stable, liquid.Origin, liquid.CellSize)}。", "ContainerSandbox"); return; }
            var gas = Cell(GasSamplePosition, _world.ConversionOrigin, _world.ConversionCellSize);
            if (!_world.Gases.StageConversionAdd(gas, MaterialId.Steam, 30)) { GameLog.Error("气体测试供给失败。", "ContainerSandbox"); return; }
            _world.NotifyExternalMaterialInjection(MaterialId.Steam, 30); _world.Gases.PublishConversionChange(gas);
            var solid = Cell(SolidSamplePosition, _world.ConversionOrigin, _world.ConversionCellSize);
            _world.FixedSolids.BeginConversionGeometry();
            if (!_world.FixedSolids.StageConversionAdd(solid, MaterialId.Ice, 200) || !_world.FixedSolids.ValidateConversionGeometry())
            { GameLog.Error("冰测试供给失败。", "ContainerSandbox"); return; }
            _world.NotifyExternalMaterialInjection(MaterialId.Ice, 200); _world.FixedSolids.PublishConversionGeometry(); _world.NotifyMaterialTopologyChanged(solid);
            _world.SetSimulationPausedForDebug(_pauseWorldSimulation); Initialized = true;
        }
        private static Vector3Int Cell(Vector3 position, Vector3 origin, float size) => Vector3Int.FloorToInt((position - origin) / size);
    }
}
