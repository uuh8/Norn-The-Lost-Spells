using System.Reflection;
using Game.Combat;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class MaterialPhaseIntegrationTests
    {
        private sealed class Routes : IMaterialSimulationRouteReadOnly
        {
            public bool TryResolve(MaterialId material, out MaterialSimulationBackendKind backend)
            { backend = material == MaterialId.Ice ? MaterialSimulationBackendKind.FixedSolid : MaterialSimulationBackendKind.GridGas; return true; }
        }
        private sealed class Sink : IMaterialDepositSink, IElementWriteSink
        {
            public bool IsMaterialInitialized => true;
            public int Count;
            public bool Accept = true;
            public bool TryEnqueueDeposit(in ElementWriteRequest request) { Count++; return Accept; }
            public bool TryEnqueueWrite(in ElementWriteRequest request) { Count++; return Accept; }
        }
        private sealed class Amount : IMaterialAmountReadOnly
        {
            public int Y; public byte Value;
            public bool TryGetAmount(Vector3Int cell, MaterialId material, out byte amount)
            { amount = cell.y == Y ? Value : (byte)0; return amount > 0; }
        }
        [Test]
        public void PhaseWriteRoutesToSingleOwnerAndFailureNeverTouchesLegacyCells()
        {
            var cell = new Sink(); var solid = new Sink(); var gas = new Sink(); var routes = new Routes();
            var ice = new ElementWriteRequest(Vector3.zero, MaterialId.Ice, 1024, 0, false);
            var steam = new ElementWriteRequest(Vector3.zero, MaterialId.Steam, 1024, 0, false);
            Assert.That(ElementWorldWriteRouter.TryRoute(routes, in ice, cell, null, null, solid, gas), Is.True);
            Assert.That(ElementWorldWriteRouter.TryRoute(routes, in steam, cell, null, null, solid, gas), Is.True);
            solid.Accept = false; gas.Accept = false;
            Assert.That(ElementWorldWriteRouter.TryRoute(routes, in ice, cell, null, null, solid, gas), Is.False);
            Assert.That(ElementWorldWriteRouter.TryRoute(routes, in steam, cell, null, null, solid, gas), Is.False);
            Assert.That(cell.Count, Is.Zero); Assert.That(solid.Count, Is.EqualTo(2)); Assert.That(gas.Count, Is.EqualTo(2));
        }
        [Test]
        public void PhaseQueryUsesSameRouteAndPreservesSeparateOwners()
        {
            var query = new MaterialStateQueryRouter(new Routes(), new Amount { Value = 255 }, null, null,
                new Amount { Value = 64 }, new Amount { Value = 192 });
            Assert.That(query.TryGetAmount(Vector3Int.zero, MaterialId.Ice, out byte ice), Is.True);
            Assert.That(ice, Is.EqualTo(64));
            Assert.That(query.TryGetAmount(Vector3Int.zero, MaterialId.Steam, out byte steam), Is.True);
            Assert.That(steam, Is.EqualTo(192));
        }
        [Test]
        public void GasExposureSamplesBreathingHeightAndKeepsMaximumAcrossRepeatedColliders()
        {
            var host = new GameObject("Gas Exposure Test"); host.SetActive(false);
            var profile = ScriptableObject.CreateInstance<ElementWorldProfile>();
            try
            {
                var world = host.AddComponent<ElementWorldRuntime>();
                var gas = new Amount { Y = 0, Value = 192 }; var routes = new Routes();
                Set(world, "_profile", profile); Set(world, "_settings", new ElementFieldSimulationSettings(.25f, 0, 0, 0, 2, false, default));
                Set(world, "<IsInitialized>k__BackingField", true);
                // World 持有最终路由的具体 Snapshot，因此使用实际生产配置冻结它。
                var catalog = AssetDatabase.LoadAssetAtPath<MaterialCatalog>("Assets/_Project/ScriptableObjects/Materials/MaterialCatalog_Default.asset").CreateSnapshot();
                var routing = AssetDatabase.LoadAssetAtPath<MaterialSimulationRoutingProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/MaterialSimulationRouting_P7_DemoRun_Grid.asset");
                var decision = WaterSimulationCompatibilityPolicy.Resolve(WaterSimulationMode.GridLiquid, true);
                var actualRoutes = MaterialSimulationRouteSnapshot.Create(catalog, routing, in decision); Set(world, "_materialRoutes", actualRoutes);
                Set(world, "_materialQuery", new MaterialStateQueryRouter(actualRoutes, null, null, null, null, gas));
                var exposure = host.AddComponent<ElementWorldExposureSystem>(); Set(exposure, "_runtime", world);
                Set(exposure, "_statusProjection", new MaterialStatusProjectionSnapshot(catalog, new[] {
                    new MaterialStatusProjectionBinding { Material = MaterialId.ToxicVapor, Status = StatusKind.Poisoned, MaximumApplyPerExposureTick = 10 } }));
                var maximum = new byte[64]; Set(exposure, "_maximumAmounts", maximum);
                var sample = typeof(ElementWorldExposureSystem).GetMethod("SampleBounds", BindingFlags.NonPublic | BindingFlags.Instance);
                object[] arguments = { new Bounds(new Vector3(.5f, .9f, .5f), new Vector3(1, 1.8f, 1)), new Bounds(Vector3.one, Vector3.one * 10), 0 };
                sample.Invoke(exposure, arguments); Assert.That(maximum[0], Is.Zero, "脚下毒气不等于吸入。");
                gas.Y = 6; sample.Invoke(exposure, arguments); Assert.That(maximum[0], Is.EqualTo(192));
                gas.Y = 3; gas.Value = 64; sample.Invoke(exposure, arguments); Assert.That(maximum[0], Is.EqualTo(192), "多 Collider 只保留最大浓度。");
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(profile); }
        }
        [Test]
        public void LocalSolidSupportInvalidationKeepsUnrelatedTerrainCache()
        {
            var store = new LiquidVolumeStore(2, 2, 255, new[] { MaterialId.Water }, _ => false);
            var cache = new LiquidTerrainSupportCache(null, store, Vector3.zero, 1, 32);
            var entries = (System.Collections.Generic.Dictionary<Vector3Int, LiquidTerrainSupport>)typeof(LiquidTerrainSupportCache)
                .GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cache);
            entries.Add(Vector3Int.up, new LiquidTerrainSupport(1, Vector3.up, true));
            entries.Add(Vector3Int.one * 10, new LiquidTerrainSupport(10, Vector3.up, true));
            cache.InvalidateSupportedColumn(Vector3Int.zero);
            Assert.That(entries.ContainsKey(Vector3Int.up), Is.False);
            Assert.That(entries.ContainsKey(Vector3Int.one * 10), Is.True);
        }
        private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }
}
