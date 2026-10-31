using System;
using System.Reflection;
using Game.Combat;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.ElementField.Tests
{
    /// <summary>先通过 Reflection 固定缺失合同的 RED，避免缺类型令整个测试程序集无法编译。</summary>
    public sealed class WorldReplicationSourceTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static Type Contract(string name) => typeof(Game.Core.GameLog).Assembly.GetType("Game.Core." + name, true);
        private static object Read(object target, string name) => target.GetType().GetField(name)?.GetValue(target)
            ?? target.GetType().GetProperty(name).GetValue(target);
        private static void Set(object target, string name, object value)
        {
            Assert.That(target, Is.Not.Null, "Fixture Set target为空，字段=" + name);
            FieldInfo field = target.GetType().GetField(name, Hidden);
            Assert.That(field, Is.Not.Null, "Fixture缺字段 " + target.GetType().FullName + "." + name);
            field.SetValue(target, value);
        }
        private static object Buffer(int cells = 64, int pending = 16, int budget = 100000) =>
            Activator.CreateInstance(Contract("WorldReplicaBuffer"), cells, pending, budget);
        private static bool Capture(ElementWorldRuntime world, object buffer, out string failure)
        {
            var type = typeof(ElementWorldRuntime).Assembly.GetType("Game.ElementField.WorldReplicationSource", true);
            // UnityEngine.Object可隐式转bool；显式参数数组避免误选CreateInstance(Type,bool nonPublic)。
            var source = Activator.CreateInstance(type, new object[] { world });
            var args = new[] { buffer, null };
            bool result = (bool)type.GetMethod("TryCapture").Invoke(source, args);
            failure = args[1].ToString(); return result;
        }
        private static ulong Sum(object buffer, string domain, string storage = null)
        {
            ulong sum = 0; var cells = (Array)Read(buffer, "Cells");
            int count = (int)Read(Read(buffer, "Header"), "CellCount");
            for (int i = 0; i < count; i++)
            {
                object value = cells.GetValue(i);
                if (Read(value, "Domain").ToString() == domain && (storage == null || Read(value, "Storage").ToString() == storage))
                    sum += (uint)Read(value, "Quantity");
            }
            return sum;
        }

        [Test]
        public void FourDomainFixtureOwnsCompleteResidentAndPendingQuantitiesBeforeCapture()
        {
            using (var f = new Fixture())
            {
                Assert.That(f.Liquid.TotalQuantity, Is.EqualTo(1310UL));
                Assert.That(f.Liquid.StableQuantity, Is.EqualTo(900UL));
                Assert.That(f.Liquid.PendingQuantity, Is.EqualTo(77UL));
                Assert.That(f.World.FixedSolids.TotalQuantity, Is.EqualTo(1024UL));
                Assert.That(f.World.FixedSolids.PendingQuantity, Is.EqualTo(77UL));
                Assert.That(f.World.Gases.TotalQuantity, Is.EqualTo(2048UL));
                Assert.That(f.World.Gases.PendingQuantity, Is.EqualTo(77UL));
                Assert.That(f.World.PendingWriteCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void CapturesFourCpuDomainsAtNativeResolutionIncludingWholeStableDepth()
        {
            using (var f = new Fixture())
            {
                object buffer = Buffer(); Assert.That(Capture(f.World, buffer, out _), Is.True);
                Assert.That(Sum(buffer, "LegacyCell"), Is.EqualTo(173UL), "Fire 的 173 是强度，不混入 GMU。");
                Assert.That(Sum(buffer, "Liquid", "Dynamic"), Is.EqualTo(410UL), "一个粗液体格只导出一次。");
                Assert.That(Sum(buffer, "Liquid", "Stable"), Is.EqualTo(900UL), "必须包含深水，不是表面抽样。");
                Assert.That(Sum(buffer, "Solid"), Is.EqualTo(1024UL));
                Assert.That(Sum(buffer, "Gas"), Is.EqualTo(2048UL));
                Assert.That((int)Read(Read(buffer, "Header"), "GeometryCount"), Is.EqualTo(4));
                var geometry = ((Array)Read(buffer, "Geometries")).GetValue(1);
                Assert.That((float)Read(geometry, "CellSize"), Is.EqualTo(.5f));
                Assert.That((long)Read(Read(buffer, "Header"), "WorldTick"), Is.EqualTo(27));
                Assert.That(f.Host.GetComponentsInChildren<Renderer>(true), Is.Empty);
                // 这是空间数量采样；不承诺重建 Solver 速度、Outlet Fractional 或 Authority 规则。
                Assert.That((bool)Read(Read(buffer, "Header"), "CanRestoreAuthorityRules"), Is.False);
            }
        }

        [Test]
        public void PreservesCompletePendingRequestsAndDoesNotConsumeQueues()
        {
            using (var f = new Fixture())
            {
                object buffer = Buffer(); Assert.That(Capture(f.World, buffer, out _), Is.True);
                Assert.That((int)Read(Read(buffer, "Header"), "PendingCount"), Is.EqualTo(4));
                var pending = (Array)Read(buffer, "Pending");
                for (int i = 0; i < 4; i++)
                {
                    var value = pending.GetValue(i);
                    Assert.That(Read(value, "WorldPosition"), Is.EqualTo(f.Request.WorldPosition));
                    Assert.That(Read(value, "InitialVelocity"), Is.EqualTo(f.Request.InitialVelocity));
                    Assert.That(Read(value, "SurfaceNormal"), Is.EqualTo(f.Request.SurfaceNormal));
                    Assert.That(Read(value, "Radius"), Is.EqualTo(f.Request.Radius));
                    Assert.That(Read(value, "UseLinearFalloff"), Is.EqualTo(true));
                    Assert.That(Read(value, "TotalAmount"), Is.EqualTo((ushort)77));
                    Assert.That(Read(value, "Remaining"), Is.EqualTo(77u));
                    string domain = Read(value, "Domain").ToString();
                    MaterialId expected = domain == "LegacyCell" ? MaterialId.Fire : domain == "Liquid" ? MaterialId.Water
                        : domain == "Solid" ? MaterialId.Ice : MaterialId.Steam;
                    Assert.That(Read(value, "MaterialId"), Is.EqualTo((byte)expected), "每个Domain保留实际支持的材料身份。");
                }
                Assert.That(f.World.PendingWriteCount, Is.EqualTo(1));
                Assert.That(f.Liquid.PendingQuantity, Is.EqualTo(77UL));
            }
        }

        [Test]
        public void CallerBufferIsIndependentOfLaterLiveChanges()
        {
            using (var f = new Fixture())
            {
                object first = Buffer(), second = Buffer(); Assert.That(Capture(f.World, first, out _), Is.True);
                f.Liquid.TryAddInitial(new Vector3Int(-1, 0, 0), MaterialId.Water, 10, false);
                Assert.That(Capture(f.World, second, out _), Is.True);
                Assert.That(Sum(first, "Liquid", "Dynamic"), Is.EqualTo(410UL));
                Assert.That(Sum(second, "Liquid", "Dynamic"), Is.EqualTo(420UL));
            }
        }

        [Test]
        public void StableExpansionExportsNewOwnerWithoutCountingRetiredRegionAgain()
        {
            using (var f = new Fixture())
            {
                object before = Buffer(), after = Buffer(); Assert.That(Capture(f.World, before, out _), Is.True);
                // 在湖盆上层尚未进水的预留格预约air→solid，确实改变拓扑而不碰已有900GMU。
                // air→air会在正式API的幂等检查直接返回true，不能把该成功当成Owner交接。
                Assert.That(f.Liquid.TryPrepareStaticCellChange(new Vector3Int(4, 2, 0), true), Is.True);
                Assert.That(f.Liquid.StableQuantity, Is.Zero); Assert.That(f.Liquid.TotalQuantity, Is.EqualTo(1310UL));
                Assert.That(Capture(f.World, after, out _), Is.True);
                Assert.That(Sum(before, "Liquid", "Stable"), Is.EqualTo(900UL));
                Assert.That(Sum(after, "Liquid", "Stable"), Is.Zero);
                Assert.That(Sum(after, "Liquid", "Dynamic"), Is.EqualTo(1310UL));
            }
        }

        [TestCase("Solid")]
        [TestCase("Gas")]
        public void PendingPartialProgressAndFifoRemainValueCopies(string domain)
        {
            using (var f = new Fixture())
            {
                object owner = domain == "Solid" ? (object)f.World.FixedSolids : f.World.Gases;
                var queue = (MaterialPhaseDepositQueue)owner.GetType().GetField("_pending", Hidden).GetValue(owner);
                Assert.That(queue.TryDequeue(out var pending), Is.True);
                pending.Remaining = 31; pending.Cursor = 9; pending.WaitingTicks = 4; queue.Requeue(in pending);
                object buffer = Buffer(); Assert.That(Capture(f.World, buffer, out _), Is.True);
                bool found = false;
                for (int i = 0; i < (int)Read(Read(buffer, "Header"), "PendingCount"); i++)
                {
                    object sample = ((Array)Read(buffer, "Pending")).GetValue(i);
                    if (Read(sample, "Domain").ToString() != domain) continue;
                    found = true; Assert.That(Read(sample, "QueueIndex"), Is.EqualTo(0));
                    Assert.That(Read(sample, "Remaining"), Is.EqualTo(31u));
                    Assert.That(Read(sample, "Cursor"), Is.EqualTo(9));
                    Assert.That(Read(sample, "WaitingTicks"), Is.EqualTo(4u));
                    Assert.That(Read(sample, "TotalAmount"), Is.EqualTo((ushort)77));
                }
                Assert.That(found, Is.True); Assert.That(queue.PendingQuantity, Is.EqualTo(31UL));
            }
        }

        [Test]
        public void GpuParticleBackendExplicitlyRejectsIncompleteCpuCapture()
        {
            using (var f = new Fixture())
            {
                Set(f.World, "_effectiveWaterSimulationMode", WaterSimulationMode.GpuPbf);
                object buffer = Buffer();
                Assert.That(Capture(f.World, buffer, out string failure), Is.False);
                Assert.That(failure, Is.EqualTo("UnsupportedBackend"), "GPU粒子/Archive不属于四域CPU Store，不可静默遗漏。");
                Assert.That((int)Read(Read(buffer, "Header"), "CellCount"), Is.Zero);
            }
        }

        [TestCase(MaterialId.Poison)]
        [TestCase(MaterialId.Sticky)]
        public void AnyGpuMaterialRouteRejectsEvenWhenWaterUsesCpuGrid(MaterialId material)
        {
            using (var f = new Fixture())
            {
                var routes = ScriptableObject.CreateInstance<MaterialSimulationRoutingProfile>();
                try
                {
                    JsonUtility.FromJsonOverwrite("{\"_routes\":[{\"_material\":" + (int)material + ",\"_backend\":2}]}", routes);
                    var catalog = AssetDatabase.LoadAssetAtPath<MaterialCatalog>("Assets/_Project/ScriptableObjects/Materials/MaterialCatalog_Default.asset");
                    var decision = WaterSimulationCompatibilityPolicy.Resolve(WaterSimulationMode.GridLiquid, false);
                    var snapshot = MaterialSimulationRouteSnapshot.Create(catalog.CreateSnapshot(), routes, in decision);
                    Assert.That(snapshot.TryResolve(material, out var backend), Is.True);
                    Assert.That(backend, Is.EqualTo(MaterialSimulationBackendKind.GpuPbfLiquid));
                    Set(f.World, "_materialRoutes", snapshot); Set(f.World, "_effectiveWaterSimulationMode", WaterSimulationMode.GridLiquid);
                    object buffer = Buffer();
                    Assert.That(Capture(f.World, buffer, out string failure), Is.False);
                    Assert.That(failure, Is.EqualTo("UnsupportedBackend"));
                    Assert.That((int)Read(Read(buffer, "Header"), "CellCount"), Is.Zero);
                }
                finally { Object.DestroyImmediate(routes); }
            }
        }

        [TestCase("_worldStepInProgress", "SimulationInProgress")]
        [TestCase("_simulationFaulted", "SimulationFaulted")]
        public void UncommittedWorldCannotChangePreviouslyCapturedCallerData(string guardField, string reason)
        {
            using (var f = new Fixture())
            {
                object buffer = Buffer(); Assert.That(Capture(f.World, buffer, out _), Is.True);
                var oldCells = (Array)((Array)Read(buffer, "Cells")).Clone();
                var oldPending = (Array)((Array)Read(buffer, "Pending")).Clone();
                var oldGeometry = (Array)((Array)Read(buffer, "Geometries")).Clone();
                object oldHeader = Read(buffer, "Header");
                // 在已提交窗口获得快照后，再模拟同步规则回调/异常锁存时请求下一份快照。
                Set(f.World, guardField, true);
                Assert.That(Capture(f.World, buffer, out string failure), Is.False);
                Assert.That(failure, Is.EqualTo(reason));
                Assert.That(Read(buffer, "Cells"), Is.EqualTo(oldCells));
                Assert.That(Read(buffer, "Pending"), Is.EqualTo(oldPending));
                Assert.That(Read(buffer, "Geometries"), Is.EqualTo(oldGeometry));
                Assert.That(Read(buffer, "Header"), Is.EqualTo(oldHeader));
            }
        }

        [TestCase(1, 16, 100000, "CellCapacity")]
        [TestCase(64, 1, 100000, "PendingCapacity")]
        [TestCase(64, 16, 1, "InspectionBudget")]
        public void FailurePreservesEveryCallerArrayAndOldHeader(int cells, int pending, int budget, string reason)
        {
            using (var f = new Fixture())
            {
                object buffer = Buffer(cells, pending, budget);
                var cellArray = (Array)Read(buffer, "Cells"); var pendingArray = (Array)Read(buffer, "Pending");
                var geometryArray = (Array)Read(buffer, "Geometries");
                var oldCells = (Array)cellArray.Clone(); var oldPending = (Array)pendingArray.Clone();
                var oldGeometry = (Array)geometryArray.Clone(); object oldHeader = Read(buffer, "Header");
                Assert.That(Capture(f.World, buffer, out string failure), Is.False); Assert.That(failure, Is.EqualTo(reason));
                Assert.That(cellArray, Is.EqualTo(oldCells)); Assert.That(pendingArray, Is.EqualTo(oldPending));
                Assert.That(geometryArray, Is.EqualTo(oldGeometry)); Assert.That(Read(buffer, "Header"), Is.EqualTo(oldHeader));
            }
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly GameObject Host = new GameObject("CPU native world capture");
            internal readonly ElementWorldRuntime World;
            internal readonly LiquidWorldRuntime Liquid;
            internal readonly ElementWriteRequest Request = new ElementWriteRequest(new Vector3(8, 9, 10), MaterialId.Water,
                77, .75f, true, new Vector3(1, 2, 3), Vector3.up);
            private readonly ElementWorldProfile _worldProfile = ScriptableObject.CreateInstance<ElementWorldProfile>();
            private readonly LiquidWorldProfile _liquidProfile = ScriptableObject.CreateInstance<LiquidWorldProfile>();
            private readonly LiquidBehaviorProfile _behavior = ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            internal Fixture()
            {
                try
                {
                Host.SetActive(false); World = Host.AddComponent<ElementWorldRuntime>(); Liquid = Host.AddComponent<LiquidWorldRuntime>();
                JsonUtility.FromJsonOverwrite("{\"_chunkSize\":4,\"_maximumResidentChunks\":4}", _worldProfile);
                // CreateSimulationSettings 是正式配置验证入口，夹具也提供真实反应配置而非绕过其不变量。
                var reaction = AssetDatabase.LoadAssetAtPath<ElementReactionProfile>("Assets/_Project/ScriptableObjects/Combat/Status/ElementReactionProfile_Default.asset");
                Assert.That(reaction, Is.Not.Null); Set(_worldProfile, "_reactionProfile", reaction);
                var catalog = AssetDatabase.LoadAssetAtPath<MaterialCatalog>("Assets/_Project/ScriptableObjects/Materials/MaterialCatalog_Default.asset");
                Assert.That(catalog, Is.Not.Null); Set(_worldProfile, "_materialCatalog", catalog);
                Set(World, "_profile", _worldProfile); Set(World, "_settings", _worldProfile.CreateSimulationSettings());
                Set(World, "<IsInitialized>k__BackingField", true); Set(World, "_worldTick", 27L);
                var legacy = new ElementWorldStore(4, 4); legacy.TryGetOrCreateChunk(default, out var chunk);
                typeof(ElementWorldChunk).GetMethod("SetCell", Hidden).Invoke(chunk, new object[] { Vector3Int.zero, new ElementCell(MaterialId.Fire, 173) });
                Set(World, "_store", legacy); var writes = new ElementWriteQueue(4);
                var fireRequest = ForMaterial(MaterialId.Fire); writes.TryEnqueue(in fireRequest); Set(World, "_writeQueue", writes);
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":4,\"_maximumActiveCells\":64,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4}", _liquidProfile);
                Set(_liquidProfile, "_materials", new[] { _behavior }); Set(Liquid, "_profile", _liquidProfile);
                // ElementWorldRuntime 的 RequireComponent 已添加且 Baker 禁止重复添加。
                var baker = Host.GetComponent<ElementFieldSolidBaker>();
                Assert.That(baker, Is.Not.Null);
                // EditMode沿用当前Editor Physics Scene；此纯Store采样夹具明确不采外部场景Collider，
                // 防止打开的Hub/P7地板把合法三层湖盆判成实心。不是放宽Stable注册的占据验证。
                Set(baker, "_includeDefaultEnvironment", false);
                Assert.That(Liquid.TryInitialize(Vector3.zero, .25f, 4, baker), Is.True);
                Liquid.TryAddInitial(new Vector3Int(-1, 0, 0), MaterialId.Water, 410, false);
                Assert.That(Liquid.TryRegisterStableRegion(MaterialId.Water, new Vector3Int(4, 0, 0), new Vector3Int(5, 3, 1), 5, 0, 900, out _), Is.True);
                Liquid.TryEnqueueDeposit(in Request); Set(World, "_gridLiquidRuntime", Liquid);
                var solid = Host.AddComponent<FixedSolidWorldRuntime>(); var solidStore = new FixedSolidVolumeStore(4, 4, 4096, new[] { MaterialId.Ice });
                solidStore.TryAdd(new Vector3Int(-3, 0, 0), MaterialId.Ice, 1024); Set(solid, "_store", solidStore); Set(solid, "_size", .25f);
                var solidPending = new MaterialPhaseDepositQueue(4); var solidRequest = ForMaterial(MaterialId.Ice);
                solidPending.Enqueue(in solidRequest); Set(solid, "_pending", solidPending); Set(World, "_fixedSolidRuntime", solid);
                var gas = Host.AddComponent<GasWorldRuntime>(); var gasStore = new GasVolumeStore(4, 4, 4096, new[] { MaterialId.Steam });
                gasStore.TryAdd(new Vector3Int(3, 0, 0), MaterialId.Steam, 2048); Set(gas, "_store", gasStore); Set(gas, "_size", .25f);
                var gasPending = new MaterialPhaseDepositQueue(4); var gasRequest = ForMaterial(MaterialId.Steam);
                gasPending.Enqueue(in gasRequest); Set(gas, "_pending", gasPending); Set(World, "_gasRuntime", gas);
                }
                catch { Dispose(); throw; }
            }
            private ElementWriteRequest ForMaterial(MaterialId material) => new ElementWriteRequest(Request.WorldPosition,
                material, Request.TotalAmount, Request.Radius, Request.UseLinearFalloff, Request.InitialVelocity, Request.SurfaceNormal);
            public void Dispose() { Object.DestroyImmediate(Host); Object.DestroyImmediate(_worldProfile); Object.DestroyImmediate(_liquidProfile); Object.DestroyImmediate(_behavior); }
        }
    }
}
