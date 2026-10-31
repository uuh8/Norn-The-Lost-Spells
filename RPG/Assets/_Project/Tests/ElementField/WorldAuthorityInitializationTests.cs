using System.Reflection;
using Game.Core;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 使用正式 P7 CPU Grid 配置的内存副本走完整初始化，不手置 IsInitialized。
    /// 验证 Profile→真实四域 Owner→写入→固定 Tick→Source 的最小组合；这不是 Native/headless 或场景玩家路径验收。
    /// </summary>
    public sealed class WorldAuthorityInitializationTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _host;
        private ElementWorldRuntime _world;
        private LiquidWorldRuntime _liquid;
        private ElementWorldProfile _worldProfile;
        private LiquidWorldProfile _liquidProfile;
        private FixedSolidWorldProfile _solidProfile;
        private GasWorldProfile _gasProfile;

        [SetUp]
        public void SetUp()
        {
            var actualWorld = AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(
                "Assets/_Project/ScriptableObjects/ElementField/Liquids/ElementWorldProfile_P7_DemoRun_Grid.asset");
            var actualLiquid = AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>(
                "Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset");
            Assert.That(actualWorld, Is.Not.Null); Assert.That(actualLiquid, Is.Not.Null);
            // Instantiate 只克隆测试拥有的 SO；保留正式 Catalog/Route/Behavior 引用及原生分辨率，
            // 只收紧容量，让一次水写入可在有界成本下走同一生产初始化链，不修改磁盘 Asset。
            _worldProfile = Object.Instantiate(actualWorld);
            _liquidProfile = Object.Instantiate(actualLiquid);
            JsonUtility.FromJsonOverwrite("{\"_maximumResidentChunks\":16,\"_maxPendingWrites\":16}", _worldProfile);
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":128,\"_maximumPendingDeposits\":16,\"_maximumCellsPerTick\":64,\"_maximumStableRegions\":4,\"_maximumStableOutlets\":4,\"_maximumStableRegionCells\":256,\"_maximumStableSurfacePatches\":16,\"_maximumImmediateExpansionCells\":64,\"_maximumImmediateExpansionSurfaceChunks\":2,\"_maximumAutoCandidateCells\":64,\"_autoValidationCellsPerTick\":32}", _liquidProfile);
            _solidProfile = ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            _gasProfile = ScriptableObject.CreateInstance<GasWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumPendingDeposits\":16,\"_maximumNavigationBoxesPerChunk\":4}", _solidProfile);
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":128,\"_maximumCellsPerTick\":32,\"_maximumPendingDeposits\":16,\"_maximumTerrainSamplesPerTick\":16,\"_maximumBirthScansPerTick\":128}", _gasProfile);

            _host = new GameObject("Real P7 Grid initialization fixture");
            _host.SetActive(false);
            _world = _host.AddComponent<ElementWorldRuntime>();
            _liquid = _host.AddComponent<LiquidWorldRuntime>();
            var solid = _host.AddComponent<FixedSolidWorldRuntime>();
            var gas = _host.AddComponent<GasWorldRuntime>();
            var baker = _host.GetComponent<ElementFieldSolidBaker>();
            // 显式关闭外部场景采样，避免当前 Editor 开着哪张场景影响空世界的水落地结果。
            Set(baker, "_solidLayerMask", (LayerMask)0);
            Set(baker, "_includeDefaultEnvironment", false);
            Set(_liquid, "_profile", _liquidProfile);
            Set(solid, "_profile", _solidProfile);
            Set(gas, "_profile", _gasProfile);
            Set(_world, "_profile", _worldProfile);
            Set(_world, "_interestPoint", _host.transform);
            Set(_world, "_gridLiquidRuntime", _liquid);
            Set(_world, "_fixedSolidRuntime", solid);
            Set(_world, "_gasRuntime", gas);
            Assert.That(_world.IsInitialized, Is.False);
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null) Object.DestroyImmediate(_host);
            foreach (var asset in new ScriptableObject[] { _worldProfile, _liquidProfile, _solidProfile, _gasProfile })
                if (asset != null) Object.DestroyImmediate(asset);
        }

        [TestCase(WorldExecutionMode.SinglePlayer)]
        [TestCase(WorldExecutionMode.Authority)]
        public void ActualGridProfilesInitializeAndCaptureCommittedWaterWithoutGpuComponents(WorldExecutionMode mode)
        {
            _world.ConfigureExecutionMode(mode);
            Assert.That((bool)typeof(ElementWorldRuntime).GetMethod("TryInitialize", Private).Invoke(_world, null), Is.True);
            Assert.That(_world.IsInitialized, Is.True);
            Assert.That(_world.EffectiveWaterSimulationMode, Is.EqualTo(WaterSimulationMode.GridLiquid));
            Assert.That(_liquid.IsFluidInitialized, Is.True);
            Assert.That(_world.FixedSolids.IsMaterialInitialized, Is.True);
            Assert.That(_world.Gases.IsMaterialInitialized, Is.True);

            Vector3 point = _liquid.Origin + Vector3.one * (_liquid.CellSize * .5f);
            var request = new ElementWriteRequest(point, MaterialId.Water, 64, 0, false);
            Assert.That(_world.TryEnqueueWrite(in request), Is.True);
            Assert.That(_liquid.PendingQuantity, Is.EqualTo(64UL));
            float interval = 1f / _worldProfile.TickRate;
            if (mode == WorldExecutionMode.Authority) _world.StepAuthority(interval, 1);
            else Assert.That(_world.TickForTests(interval), Is.EqualTo(1));
            Assert.That(_world.WorldTick, Is.EqualTo(1));
            Assert.That(_world.IsSimulationFaulted, Is.False);
            Assert.That(_liquid.PendingQuantity, Is.Zero, "水必须真实落地，不能仅复制待办请求当作模拟通过。");
            Assert.That(_liquid.TotalQuantity, Is.EqualTo(64UL));

            var buffer = new WorldReplicaBuffer(128, 32, 100000);
            Assert.That(new WorldReplicationSource(_world).TryCapture(buffer, out var failure), Is.True, failure.ToString());
            Assert.That(buffer.Header.WorldTick, Is.EqualTo(1));
            Assert.That(buffer.Header.GeometryCount, Is.EqualTo(4));
            int domains = 0;
            for (int i = 0; i < buffer.Header.GeometryCount; i++) domains |= 1 << (int)buffer.Geometries[i].Domain;
            Assert.That(domains, Is.EqualTo(15), "LegacyCell、Liquid、Solid、Gas 都必须有真实初始化的 Geometry。");
            ulong water = 0;
            for (int i = 0; i < buffer.Header.CellCount; i++)
                if (buffer.Cells[i].Domain == WorldMaterialDomain.Liquid && buffer.Cells[i].MaterialId == (byte)MaterialId.Water)
                    water += buffer.Cells[i].Quantity;
            Assert.That(water, Is.EqualTo(64UL));
            Assert.That(buffer.Header.CanRestoreAuthorityRules, Is.False, "空间样本不能冒充规则状态恢复。");
            Assert.That(_host.GetComponentsInChildren<Renderer>(true), Is.Empty);
            Assert.That(_host.GetComponentsInChildren<GpuPbfFluidRuntime>(true), Is.Empty);
            Assert.That(_host.GetComponentsInChildren<FluidChunkStreamingRuntime>(true), Is.Empty);
            Assert.That(_host.GetComponentsInChildren<FluidGameplayOccupancyBridge>(true), Is.Empty);
        }

        private static void Set(object target, string field, object value)
            => target.GetType().GetField(field, Private).SetValue(target, value);
    }
}
