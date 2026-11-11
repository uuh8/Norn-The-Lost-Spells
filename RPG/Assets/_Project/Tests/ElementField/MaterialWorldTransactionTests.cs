using System;
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
    /// 在真实初始化的健康 CPU 世界上执行库存回调，证明 World 级互斥覆盖整个事务。
    /// 验证隔离、数量提交与异常锁存；失败 Undo 后的 Stable/Dynamic 字节一致性不属于本组承诺。
    /// </summary>
    public sealed class MaterialWorldTransactionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private Fixture _fixture;
        private ElementWorldRuntime World => _fixture.World;
        private Vector3 Point => _fixture.Liquid.Origin + Vector3.one * (_fixture.Liquid.CellSize * .5f);

        [TearDown]
        public void TearDown() => _fixture?.Dispose();

        [Test]
        public void HealthyCollectionControlReachesReservationBeforeTestingReentry()
        {
            _fixture = new Fixture(WorldExecutionMode.SinglePlayer);
            var destination = new Destination();
            var failure = new MaterialInventoryWorldPort(World).Transfer(Request(20), destination, out ulong moved);
            Assert.That(failure, Is.EqualTo(MaterialTransferFailure.None), "健康控制必须真正采到材料，隔离测试才能审查 Reserve 回调。");
            Assert.That(destination.Reserves, Is.EqualTo(1)); Assert.That(destination.Commits, Is.EqualTo(1));
            Assert.That(moved, Is.EqualTo(20UL)); Assert.That(_fixture.Liquid.TotalQuantity, Is.EqualTo(80UL));
        }

        [Test]
        public void SuspendedAuthorityRejectsNewPortTransactionsBeforeTouchingInventory()
        {
            _fixture = new Fixture(WorldExecutionMode.Authority);
            var source = new Source();
            var destination = new Destination();
            World.SetSimulationSuspended(true);

            var depositFailure = new MaterialInventoryDepositPort(World).Deposit(Deposit(source, 20), out ulong deposited);
            var collectionFailure = new MaterialInventoryWorldPort(World).Transfer(Request(20), destination, out ulong collected);
            Assert.That(depositFailure, Is.EqualTo(MaterialTransferFailure.WorldUnavailable));
            Assert.That(collectionFailure, Is.EqualTo(MaterialTransferFailure.WorldUnavailable),
                "直接Port必须在预约或Stage之前拒绝挂起世界，不能只依赖网络入口的前置检查。");
            Assert.That(deposited + collected, Is.Zero);
            Assert.That(source.Reserves + source.Commits + source.Cancels, Is.Zero);
            Assert.That(destination.Reserves + destination.Commits + destination.Cancels, Is.Zero);
            Assert.That(source.Removed + destination.Added, Is.Zero);
            Assert.That(_fixture.Liquid.TotalQuantity, Is.EqualTo(100UL));
            Assert.That(World.ImportedMaterialQuantity(MaterialId.Water), Is.Zero);
            Assert.That(World.ExportedMaterialQuantity(MaterialId.Water), Is.Zero);
            Assert.That(World.IsSimulationFaulted, Is.False, "拒绝新事务不把保留的健康现场标成部分执行故障。");
            AssertTransactionReleased();
        }

        [Test]
        public void DepositReservationIsolatesClockCaptureWritesAndNestedPortsUntilSuccess()
        {
            _fixture = new Fixture(WorldExecutionMode.Authority);
            var port = new MaterialInventoryDepositPort(World);
            var source = new Source();
            var nestedSource = new Source();
            Probe result = null;
            source.OnReserve = () => result = ProbeCallbacks(() => port.Deposit(Deposit(nestedSource, 7), out _));
            var failure = port.Deposit(Deposit(source, 20), out ulong moved);
            AssertProbe(result);
            Assert.That(failure, Is.EqualTo(MaterialTransferFailure.None));
            Assert.That(moved, Is.EqualTo(20UL)); Assert.That(source.Removed, Is.EqualTo(20UL));
            Assert.That(nestedSource.Reserves, Is.Zero);
            Assert.That(_fixture.Liquid.TotalQuantity, Is.EqualTo(120UL));
            Assert.That(World.ImportedMaterialQuantity(MaterialId.Water), Is.EqualTo(20UL));
            AssertReleasedAndHealthy();
        }

        [Test]
        public void CollectionReservationCannotOverwriteOuterSampleThroughSamePortReentry()
        {
            _fixture = new Fixture(WorldExecutionMode.SinglePlayer);
            var port = new MaterialInventoryWorldPort(World);
            var destination = new Destination();
            var nestedDestination = new Destination();
            Probe result = null;
            destination.OnReserve = () => result = ProbeCallbacks(() => port.Transfer(Request(7), nestedDestination, out _));
            var failure = port.Transfer(Request(20), destination, out ulong moved);
            AssertProbe(result);
            Assert.That(failure, Is.EqualTo(MaterialTransferFailure.None));
            Assert.That(moved, Is.EqualTo(20UL)); Assert.That(destination.Added, Is.EqualTo(20UL));
            Assert.That(nestedDestination.Reserves, Is.Zero, "同 Port 的 Sample scratch 不得被嵌套调用清写。");
            Assert.That(_fixture.Liquid.TotalQuantity, Is.EqualTo(80UL));
            Assert.That(World.ExportedMaterialQuantity(MaterialId.Water), Is.EqualTo(20UL));
            AssertReleasedAndHealthy();
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void ReservationAndCommitExceptionsLatchFaultAndReleaseWorldFence(bool deposit, bool duringReserve)
        {
            _fixture = new Fixture(WorldExecutionMode.Authority);
            bool callbackSawFence = false;
            Action fail = () =>
            {
                callbackSawFence = World.IsWorldStepInProgress;
                throw new InvalidOperationException("Intentional external inventory callback failure");
            };
            if (deposit)
            {
                var source = new Source();
                if (duringReserve) source.OnReserve = fail; else source.OnCommit = fail;
                Assert.Throws<InvalidOperationException>(() => new MaterialInventoryDepositPort(World).Deposit(Deposit(source, 20), out _));
            }
            else
            {
                var destination = new Destination();
                if (duringReserve) destination.OnReserve = fail; else destination.OnCommit = fail;
                Assert.Throws<InvalidOperationException>(() => new MaterialInventoryWorldPort(World).Transfer(Request(20), destination, out _));
            }
            Assert.That(callbackSawFence, Is.True);
            Assert.That(World.IsSimulationFaulted, Is.True, "外部 Commit 可能已改变库存，不能通过盲目 Undo 假装跨端回滚。");
            Assert.That(World.IsWorldStepInProgress, Is.False, "异常释放互斥，但 Faulted 必须继续禁止发布和写入。");
            AssertTransactionReleased();
            Assert.That(new WorldReplicationSource(World).TryCapture(new WorldReplicaBuffer(128, 32, 100000), out var failure), Is.False);
            Assert.That(failure, Is.EqualTo(WorldCaptureFailure.SimulationFaulted));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CapacityUndoAndCancelKeepFenceUntilExitAndReleaseEvenWhenCancelThrows(bool cancelThrows)
        {
            _fixture = new Fixture(WorldExecutionMode.SinglePlayer);
            var port = new MaterialInventoryDepositPort(World);
            var source = new Source();
            Probe result = null;
            source.OnCancel = () =>
            {
                result = ProbeCallbacks(() => port.Deposit(Deposit(new Source(), 7), out _));
                if (cancelThrows) throw new InvalidOperationException("Intentional cancel failure");
            };
            // 单格球超过真实容量，先 Stage 一部分，再走既有 Undo→Cancel；不用假造失败返回。
            if (cancelThrows)
                Assert.Throws<InvalidOperationException>(() => port.Deposit(Deposit(source, 10000), out _));
            else
                Assert.That(port.Deposit(Deposit(source, 10000), out ulong moved), Is.EqualTo(MaterialTransferFailure.Capacity));
            AssertProbe(result);
            Assert.That(source.Cancels, Is.EqualTo(1)); Assert.That(source.Commits, Is.Zero);
            Assert.That(source.Removed, Is.Zero); Assert.That(_fixture.Liquid.TotalQuantity, Is.EqualTo(100UL));
            Assert.That(World.ImportedMaterialQuantity(MaterialId.Water), Is.Zero);
            Assert.That(World.IsWorldStepInProgress, Is.False);
            AssertTransactionReleased();
            Assert.That(World.IsSimulationFaulted, Is.EqualTo(cancelThrows));
            if (!cancelThrows) AssertReleasedAndHealthy();
        }

        [TestCase(true, false, false)]
        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        [TestCase(true, true, true)]
        [TestCase(false, false, false)]
        [TestCase(false, false, true)]
        [TestCase(false, true, false)]
        [TestCase(false, true, true)]
        public void SuccessfulCommitCleanupStaysInsideWorldFenceForDirectPortAndEventBus(bool deposit, bool throughEvent, bool cancelThrows)
        {
            _fixture = new Fixture(deposit ? WorldExecutionMode.Authority : WorldExecutionMode.SinglePlayer);
            var source = new Source(); var destination = new Destination();
            var depositPort = new MaterialInventoryDepositPort(World);
            var collectionPort = new MaterialInventoryWorldPort(World);
            Probe result = null;
            Action cleanup = () =>
            {
                result = ProbeCallbacks(() => deposit
                    ? depositPort.Deposit(Deposit(new Source(), 7), out _)
                    : collectionPort.Transfer(Request(7), new Destination(), out _));
                if (cancelThrows) throw new InvalidOperationException("Intentional successful-commit cleanup failure");
            };
            if (deposit) source.OnCancel = cleanup; else destination.OnCancel = cleanup;
            var depositExchange = Deposit(source, 20);
            var collectionExchange = new MaterialTransferExchange { Request = Request(20), Destination = destination };
            MaterialTransferFailure directFailure = MaterialTransferFailure.None;
            ulong directQuantity = 0;
            Action execute;
            object adapter = null;
            if (throughEvent)
            {
                if (deposit)
                {
                    var view = _fixture.Host.AddComponent<ContainerDepositAdapter>(); view.Configure(World, "transaction-test"); adapter = view;
                    execute = () => EventBus<MaterialWorldDepositRequestedEvent>.Publish(new MaterialWorldDepositRequestedEvent { Exchange = depositExchange });
                }
                else
                {
                    var view = _fixture.Host.AddComponent<MaterialInventoryTransferAdapter>(); view.Configure(World, "transaction-test"); adapter = view;
                    execute = () => EventBus<MaterialWorldTransferRequestedEvent>.Publish(new MaterialWorldTransferRequestedEvent { Exchange = collectionExchange });
                }
                InvokeLifecycle(adapter, "OnEnable");
            }
            else if (deposit) execute = () => directFailure = depositPort.Deposit(depositExchange, out directQuantity);
            else execute = () => directFailure = collectionPort.Transfer(collectionExchange.Request, destination, out directQuantity);

            try
            {
                if (cancelThrows) Assert.Throws<InvalidOperationException>(() => execute());
                else execute();
            }
            finally { if (adapter != null) InvokeLifecycle(adapter, "OnDisable"); }

            AssertProbe(result);
            Assert.That(deposit ? source.Cancels : destination.Cancels, Is.EqualTo(1), "成功 Commit 后也只做一次受保护清理，Adapter 不得再调用外部 Cancel。");
            Assert.That(deposit ? source.Removed : destination.Added, Is.EqualTo(20UL), "清理异常不代表此前库存 Commit 已回滚。");
            Assert.That(deposit ? source.Commits : destination.Commits, Is.EqualTo(1));
            Assert.That(_fixture.Liquid.TotalQuantity, Is.EqualTo(deposit ? 120UL : 80UL));
            Assert.That(deposit ? World.ImportedMaterialQuantity(MaterialId.Water) : World.ExportedMaterialQuantity(MaterialId.Water), Is.EqualTo(20UL));
            Assert.That(World.IsSimulationFaulted, Is.EqualTo(cancelThrows));
            Assert.That(World.IsWorldStepInProgress, Is.False); AssertTransactionReleased();
            if (!cancelThrows)
            {
                if (!throughEvent) { Assert.That(directFailure, Is.EqualTo(MaterialTransferFailure.None)); Assert.That(directQuantity, Is.EqualTo(20UL)); }
                else if (deposit) { Assert.That(depositExchange.Handled, Is.True); Assert.That(depositExchange.Failure, Is.EqualTo(MaterialTransferFailure.None)); }
                else { Assert.That(collectionExchange.Handled, Is.True); Assert.That(collectionExchange.Result.Failure, Is.EqualTo(MaterialTransferFailure.None)); }
                AssertReleasedAndHealthy();
            }
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void CleanupFailurePreservesPrimaryExceptionIdentityAndRecordsSecondary(bool deposit, bool duringReserve)
        {
            _fixture = new Fixture(WorldExecutionMode.Authority);
            var primary = new InvalidOperationException("Intentional primary inventory failure");
            var secondary = new InvalidOperationException("Intentional secondary cleanup failure");
            bool cleanupSawFence = false;
            Action failPrimary = () => throw primary;
            Action failCleanup = () => { cleanupSawFence = World.IsWorldStepInProgress; throw secondary; };
            InvalidOperationException received;
            if (deposit)
            {
                var source = new Source { OnCancel = failCleanup };
                if (duringReserve) source.OnReserve = failPrimary; else source.OnCommit = failPrimary;
                received = Assert.Throws<InvalidOperationException>(() => new MaterialInventoryDepositPort(World).Deposit(Deposit(source, 20), out _));
            }
            else
            {
                var destination = new Destination { OnCancel = failCleanup };
                if (duringReserve) destination.OnReserve = failPrimary; else destination.OnCommit = failPrimary;
                received = Assert.Throws<InvalidOperationException>(() => new MaterialInventoryWorldPort(World).Transfer(Request(20), destination, out _));
            }
            Assert.That(received, Is.SameAs(primary), "finally 的清理错误不能覆盖更早的业务错误。");
            Assert.That(received.Data["Norn.MaterialCleanupException"], Is.SameAs(secondary));
            Assert.That(cleanupSawFence, Is.True);
            Assert.That(World.IsSimulationFaulted, Is.True);
            Assert.That(World.IsWorldStepInProgress, Is.False); AssertTransactionReleased();
        }

        private sealed class Probe
        {
            internal bool InProgress, TransactionInProgress, Captured, WriteAccepted;
            internal long TickBefore, TickAfter;
            internal int TestTicks;
            internal WorldCaptureFailure CaptureFailure;
            internal MaterialTransferFailure SamePortFailure, OtherPortFailure;
            internal int OtherPortReservations;
        }

        private Probe ProbeCallbacks(Func<MaterialTransferFailure> reenterSamePort)
        {
            var result = new Probe { InProgress = World.IsWorldStepInProgress, TickBefore = World.WorldTick };
            var transaction = typeof(ElementWorldRuntime).GetProperty("IsMaterialTransactionInProgress", Private);
            result.TransactionInProgress = transaction != null && (bool)transaction.GetValue(World);
            result.Captured = new WorldReplicationSource(World).TryCapture(new WorldReplicaBuffer(128, 32, 100000), out result.CaptureFailure);
            World.StepAuthority(.1f, 1);
            result.TestTicks = World.TickForTests(.1f);
            typeof(ElementWorldRuntime).GetMethod("Update", Private).Invoke(World, null);
            result.TickAfter = World.WorldTick;
            var fire = new ElementWriteRequest(Point, MaterialId.Fire, 1, 0, false);
            result.WriteAccepted = World.TryEnqueueWrite(in fire);
            result.SamePortFailure = reenterSamePort();
            var otherSource = new Source();
            result.OtherPortFailure = new MaterialInventoryDepositPort(World).Deposit(Deposit(otherSource, 3), out _);
            result.OtherPortReservations = otherSource.Reserves;
            return result;
        }

        private static void AssertProbe(Probe probe)
        {
            Assert.That(probe, Is.Not.Null, "必须真实执行 Reserve/Cancel 回调。");
            Assert.That(probe.InProgress, Is.True);
            Assert.That(probe.TransactionInProgress, Is.True);
            Assert.That(probe.Captured, Is.False); Assert.That(probe.CaptureFailure, Is.EqualTo(WorldCaptureFailure.SimulationInProgress));
            Assert.That(probe.TickAfter, Is.EqualTo(probe.TickBefore)); Assert.That(probe.TestTicks, Is.Zero);
            Assert.That(probe.WriteAccepted, Is.False);
            Assert.That(probe.SamePortFailure, Is.EqualTo(MaterialTransferFailure.ResourceBusy));
            Assert.That(probe.OtherPortFailure, Is.EqualTo(MaterialTransferFailure.ResourceBusy));
            Assert.That(probe.OtherPortReservations, Is.Zero);
        }

        private void AssertReleasedAndHealthy()
        {
            Assert.That(World.IsWorldStepInProgress, Is.False);
            AssertTransactionReleased();
            Assert.That(World.IsSimulationFaulted, Is.False);
            Assert.That(World.PendingWriteCount, Is.Zero);
            Assert.That(new WorldReplicationSource(World).TryCapture(new WorldReplicaBuffer(128, 32, 100000), out var failure), Is.True, failure.ToString());
        }

        private void AssertTransactionReleased()
        {
            var property = typeof(ElementWorldRuntime).GetProperty("IsMaterialTransactionInProgress", Private);
            Assert.That(property, Is.Not.Null, "库存事务需要独立进行中标志，不能把 Faulted 当作尚未释放的锁。");
            Assert.That((bool)property.GetValue(World), Is.False);
        }

        private MaterialDepositExchange Deposit(Source source, ulong amount) => new MaterialDepositExchange
        {
            WorldId = "transaction-test", Position = Point, Radius = .1f, Source = source,
            Amounts = new[] { new MaterialAmount(1, amount) }, Count = 1,
        };
        private MaterialTransferRequestedEvent Request(ulong budget) => new MaterialTransferRequestedEvent
        {
            WorldId = "transaction-test", RequestId = 1, SampleCenter = Point, CollectorPosition = Point,
            Radius = .1f, Budget = budget,
        };

        private sealed class Source : IMaterialTransferSource
        {
            internal Action OnReserve, OnCommit, OnCancel;
            internal int Reserves, Commits, Cancels;
            internal ulong Removed;
            private ulong _reserved;
            public bool TryReserve(MaterialAmount[] amounts, int count, out MaterialTransferFailure failure)
            { Reserves++; _reserved = amounts[0].Quantity; failure = MaterialTransferFailure.None; OnReserve?.Invoke(); return true; }
            public void Commit() { Commits++; Removed += _reserved; OnCommit?.Invoke(); }
            public void Cancel() { Cancels++; _reserved = 0; OnCancel?.Invoke(); }
        }
        private sealed class Destination : IMaterialTransferDestination
        {
            internal Action OnReserve, OnCommit, OnCancel;
            internal int Reserves, Commits, Cancels;
            internal ulong Added;
            private ulong _reserved;
            public ushort AllowedMaterials => 1 << 1;
            public ulong FreeCapacity => 1024;
            public bool TryReserve(MaterialAmount[] amounts, int count, out MaterialTransferFailure failure)
            { Reserves++; _reserved = amounts[0].Quantity; failure = MaterialTransferFailure.None; OnReserve?.Invoke(); return true; }
            public void Commit() { Commits++; Added += _reserved; OnCommit?.Invoke(); }
            public void Cancel() { Cancels++; _reserved = 0; OnCancel?.Invoke(); }
        }

        private static void InvokeLifecycle(object target, string method)
            => target.GetType().GetMethod(method, Private).Invoke(target, null);

        private sealed class Fixture : IDisposable
        {
            internal readonly GameObject Host;
            internal readonly ElementWorldRuntime World;
            internal readonly LiquidWorldRuntime Liquid;
            private readonly ElementWorldProfile _world;
            private readonly LiquidWorldProfile _liquid;
            private readonly FixedSolidWorldProfile _solid;
            private readonly GasWorldProfile _gas;
            internal Fixture(WorldExecutionMode mode)
            {
                var actualWorld = AssetDatabase.LoadAssetAtPath<ElementWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/ElementWorldProfile_P7_DemoRun_Grid.asset");
                var actualLiquid = AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset");
                Assert.That(actualWorld, Is.Not.Null); Assert.That(actualLiquid, Is.Not.Null);
                _world = Object.Instantiate(actualWorld); _liquid = Object.Instantiate(actualLiquid);
                JsonUtility.FromJsonOverwrite("{\"_maximumResidentChunks\":16,\"_maxPendingWrites\":16}", _world);
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":128,\"_maximumPendingDeposits\":16,\"_maximumCellsPerTick\":64,\"_maximumStableRegions\":4,\"_maximumStableOutlets\":4,\"_maximumStableRegionCells\":256,\"_maximumStableSurfacePatches\":16,\"_maximumImmediateExpansionCells\":64,\"_maximumImmediateExpansionSurfaceChunks\":2,\"_maximumAutoCandidateCells\":64,\"_autoValidationCellsPerTick\":32}", _liquid);
                _solid = ScriptableObject.CreateInstance<FixedSolidWorldProfile>(); _gas = ScriptableObject.CreateInstance<GasWorldProfile>();
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumPendingDeposits\":16,\"_maximumNavigationBoxesPerChunk\":4}", _solid);
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":128,\"_maximumCellsPerTick\":32,\"_maximumPendingDeposits\":16,\"_maximumTerrainSamplesPerTick\":16,\"_maximumBirthScansPerTick\":128}", _gas);
                Host = new GameObject("Healthy transaction callback world"); Host.SetActive(false);
                Host.transform.position = new Vector3(30000, 2000, 30000);
                World = Host.AddComponent<ElementWorldRuntime>(); Liquid = Host.AddComponent<LiquidWorldRuntime>();
                var solid = Host.AddComponent<FixedSolidWorldRuntime>(); var gas = Host.AddComponent<GasWorldRuntime>();
                var baker = Host.GetComponent<ElementFieldSolidBaker>(); Set(baker, "_solidLayerMask", (LayerMask)0); Set(baker, "_includeDefaultEnvironment", false);
                Set(Liquid, "_profile", _liquid); Set(solid, "_profile", _solid); Set(gas, "_profile", _gas);
                Set(World, "_profile", _world); Set(World, "_interestPoint", Host.transform);
                Set(World, "_gridLiquidRuntime", Liquid); Set(World, "_fixedSolidRuntime", solid); Set(World, "_gasRuntime", gas);
                World.ConfigureExecutionMode(mode);
                Assert.That((bool)typeof(ElementWorldRuntime).GetMethod("TryInitialize", Private).Invoke(World, null), Is.True);
                Assert.That(Liquid.TryAddInitial(Vector3Int.zero, MaterialId.Water, 100), Is.True);
                // inactive 夹具不运行 OnEnable（避免抢占 Editor 场景 Registry），所以显式执行它会触发的
                // 真实 Activity 刷新。P7 启用扩展活动域，空域会让收取提前 NoMaterial，无法到达 Reserve。
                typeof(ElementWorldRuntime).GetMethod("RefreshWorldActivity", Private).Invoke(World, null);
            }
            public void Dispose()
            {
                if (Host != null) Object.DestroyImmediate(Host);
                foreach (var asset in new ScriptableObject[] { _world, _liquid, _solid, _gas }) if (asset != null) Object.DestroyImmediate(asset);
            }
            private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private).SetValue(target, value);
        }
    }
}
