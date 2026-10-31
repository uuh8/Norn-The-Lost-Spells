using System;
using System.Reflection;
using Game.Core;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 复用真实三相 Owner，先使真实材料 Tick 的写入回调抛异常，再尝试投放/收取。
    /// 这组测试只审查故障后的入口拒绝，不复制数量算法，也不手动把 Faulted 字段伪造成 true。
    /// </summary>
    public sealed class WorldFaultedWriteTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private MaterialInventoryTransferTests.World _fixture;
        private ElementWorldRuntime World => _fixture.Runtime;
        private Vector3 SamplePoint => _fixture.Liquid.Origin + Vector3.one * _fixture.Liquid.CellSize * .5f;

        [SetUp]
        public void SetUp()
        {
            _fixture = new MaterialInventoryTransferTests.World();
            Assert.That(_fixture.Liquid.TryAddInitial(Vector3Int.zero, MaterialId.Water, 100), Is.True);
            // 此夹具原本只需要数量材料；补 Fire 的真实 ElementCell 路由，避免写入测试因 Unsupported 假通过。
            var backends = new MaterialSimulationBackendKind[256];
            var present = new bool[256];
            present[(byte)MaterialId.Fire] = present[(byte)MaterialId.Water] = true;
            backends[(byte)MaterialId.Fire] = MaterialSimulationBackendKind.ElementCell;
            backends[(byte)MaterialId.Water] = MaterialSimulationBackendKind.GridLiquid;
            Set(World, "_materialRoutes", Activator.CreateInstance(typeof(MaterialSimulationRouteSnapshot), Private,
                null, new object[] { backends, present }, null));
            Set(World, "_settings", new ElementFieldSimulationSettings(1, 0, 0, 0, 2, false, default));
            Set(World, "_store", new ElementWorldStore(2, 8, chunk => throw new InvalidOperationException("Intentional fault during material write")));
            var writes = new ElementWriteQueue(8);
            var request = new ElementWriteRequest(SamplePoint, MaterialId.Fire, 1, 0, false);
            Assert.That(writes.TryEnqueue(in request), Is.True); Set(World, "_writeQueue", writes);
            var thrown = Assert.Throws<TargetInvocationException>(() => typeof(ElementWorldRuntime).GetMethod("SimulateOneTick", Private).Invoke(World, null));
            Assert.That(thrown.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(World.IsSimulationFaulted, Is.True);
            Assert.That(World.WorldTick, Is.EqualTo(1));
            Assert.That(World.IsInitialized, Is.True, "故障不等于回滚或销毁；因此入口不能只检查 IsInitialized。");
        }

        [TearDown]
        public void TearDown() => _fixture?.Dispose();

        [Test]
        public void FaultedWorldCannotAcceptEvenAValidPendingFireWrite()
        {
            int before = World.PendingWriteCount;
            var request = new ElementWriteRequest(SamplePoint, MaterialId.Fire, 1, 0, false);
            Assert.That(World.TryEnqueueWrite(in request), Is.False);
            Assert.That(World.PendingWriteCount, Is.EqualTo(before));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DepositRejectsBeforeSourceReservationForDirectPortAndEventBus(bool throughEvent)
        {
            var source = new Source();
            var exchange = new MaterialDepositExchange
            {
                WorldId = "test-world", Position = SamplePoint, Radius = .1f, Source = source,
                Amounts = new[] { new MaterialAmount(1, 20) }, Count = 1,
            };
            if (throughEvent)
            {
                var adapter = _fixture.Host.AddComponent<ContainerDepositAdapter>();
                adapter.Configure(World, "test-world");
                InvokeLifecycle(adapter, "OnEnable");
                try { EventBus<MaterialWorldDepositRequestedEvent>.Publish(new MaterialWorldDepositRequestedEvent { Exchange = exchange }); }
                finally { InvokeLifecycle(adapter, "OnDisable"); }
                Assert.That(exchange.Handled, Is.True);
            }
            else exchange.Failure = new MaterialInventoryDepositPort(World).Deposit(exchange, out exchange.Transferred);
            Assert.That(exchange.Failure, Is.EqualTo(MaterialTransferFailure.WorldUnavailable));
            Assert.That(exchange.Transferred, Is.Zero);
            Assert.That(source.Reservations, Is.Zero); Assert.That(source.Commits, Is.Zero);
            Assert.That(source.Quantity, Is.EqualTo(20));
            Assert.That(_fixture.Liquid.TotalQuantity, Is.EqualTo(100));
            Assert.That(World.ImportedMaterialQuantity(MaterialId.Water), Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CollectionRejectsBeforeDestinationReservationForDirectPortAndEventBus(bool throughEvent)
        {
            var destination = new Destination();
            var request = new MaterialTransferRequestedEvent
            {
                WorldId = "test-world", RequestId = 1, CollectorPosition = SamplePoint,
                SampleCenter = SamplePoint, Radius = .1f, Budget = 20,
            };
            var exchange = new MaterialTransferExchange { Request = request, Destination = destination };
            if (throughEvent)
            {
                InvokeLifecycle(_fixture.Adapter, "OnEnable");
                try { EventBus<MaterialWorldTransferRequestedEvent>.Publish(new MaterialWorldTransferRequestedEvent { Exchange = exchange }); }
                finally { InvokeLifecycle(_fixture.Adapter, "OnDisable"); }
                Assert.That(exchange.Handled, Is.True);
            }
            else
            {
                var result = exchange.Result;
                result.Failure = new MaterialInventoryWorldPort(World).Transfer(in request, destination, out result.Transferred);
                exchange.Result = result;
            }
            Assert.That(exchange.Result.Failure, Is.EqualTo(MaterialTransferFailure.WorldUnavailable));
            Assert.That(exchange.Result.Transferred, Is.Zero);
            Assert.That(destination.Reservations, Is.Zero); Assert.That(destination.Commits, Is.Zero);
            Assert.That(destination.Quantity, Is.Zero);
            Assert.That(_fixture.Liquid.TotalQuantity, Is.EqualTo(100));
            Assert.That(World.ExportedMaterialQuantity(MaterialId.Water), Is.Zero);
        }

        private sealed class Source : IMaterialTransferSource
        {
            public int Reservations, Commits;
            public ulong Quantity = 20;
            private ulong _reserved;
            public bool TryReserve(MaterialAmount[] amounts, int count, out MaterialTransferFailure failure)
            { Reservations++; _reserved = amounts[0].Quantity; failure = MaterialTransferFailure.None; return true; }
            public void Commit() { Commits++; Quantity -= _reserved; }
            public void Cancel() => _reserved = 0;
        }

        private sealed class Destination : IMaterialTransferDestination
        {
            public int Reservations, Commits;
            public ulong Quantity;
            public ushort AllowedMaterials => 1 << 1;
            public ulong FreeCapacity => 1024;
            private ulong _reserved;
            public bool TryReserve(MaterialAmount[] amounts, int count, out MaterialTransferFailure failure)
            { Reservations++; _reserved = amounts[0].Quantity; failure = MaterialTransferFailure.None; return true; }
            public void Commit() { Commits++; Quantity += _reserved; }
            public void Cancel() => _reserved = 0;
        }

        private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private).SetValue(target, value);
        private static void InvokeLifecycle(object target, string method) => target.GetType().GetMethod(method, Private).Invoke(target, null);
    }
}
