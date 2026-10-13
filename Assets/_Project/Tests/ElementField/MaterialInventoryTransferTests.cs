using NUnit.Framework;
using System;
using System.Reflection;
using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class MaterialInventoryTransferTests
    {
        [Test]
        public void ProductionProvidesActualOwnerTransferPort()
        {
            Assert.That(typeof(ElementWorldRuntime).Assembly.GetType("Game.ElementField.MaterialInventoryTransferAdapter"), Is.Not.Null);
        }

        private sealed class Destination : IMaterialTransferDestination
        {
            public ushort AllowedMaterials { get; set; } = (1 << 1) | (1 << 3);
            public ulong FreeCapacity { get; set; } = 1024;
            public readonly ulong[] Quantities = new ulong[256];
            public bool Reject, Reserved, Committed;
            public Action OnReserve;
            private readonly MaterialAmount[] _amounts = new MaterialAmount[8]; private int _count;
            public bool TryReserve(MaterialAmount[] amounts, int count, out MaterialTransferFailure failure)
            { failure = Reject ? MaterialTransferFailure.Capacity : MaterialTransferFailure.None; if (Reject) return false;
                Array.Copy(amounts, _amounts, count); _count = count; Reserved = true; OnReserve?.Invoke(); return true; }
            public void Commit() { for (int i = 0; i < _count; i++) Quantities[_amounts[i].MaterialKey] += _amounts[i].Quantity; Committed = true; Reserved = false; }
            public void Cancel() => Reserved = false;
        }
        internal sealed class World : IDisposable
        {
            internal readonly GameObject Host = new GameObject("Inventory Real World");
            internal readonly ElementWorldRuntime Runtime;
            internal readonly LiquidWorldRuntime Liquid;
            internal readonly FixedSolidWorldRuntime Solid;
            internal readonly GasWorldRuntime Gas;
            internal readonly MaterialInventoryTransferAdapter Adapter;
            private readonly ElementWorldProfile _world = ScriptableObject.CreateInstance<ElementWorldProfile>();
            private readonly LiquidWorldProfile _liquid = ScriptableObject.CreateInstance<LiquidWorldProfile>();
            private readonly FixedSolidWorldProfile _solid = ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            private readonly GasWorldProfile _gas = ScriptableObject.CreateInstance<GasWorldProfile>();
            private readonly LiquidBehaviorProfile _water = ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            private readonly LiquidBehaviorProfile _poison = ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            internal static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
            internal World(int scale = 1, int navigationBoxes = 8)
            {
                Host.SetActive(false); Host.hideFlags = HideFlags.DontSave;
                JsonUtility.FromJsonOverwrite("{\"_cellSize\":1,\"_chunkSize\":2,\"_activeRadiusXZChunks\":2,\"_activeRadiusYChunks\":2}", _world);
                JsonUtility.FromJsonOverwrite("{\"_resolutionScale\":1,\"_unitVolumeMultiplier\":1,\"_maximumChunks\":8,\"_maximumActiveCells\":128,\"_maximumCellsPerTick\":32,\"_maximumStableRegions\":4,\"_maximumStableOutlets\":4,\"_maximumStableSurfacePatches\":16,\"_maximumAutoCandidateCells\":64}", _liquid);
                Set(_liquid, "_resolutionScale", scale); JsonUtility.FromJsonOverwrite("{\"_material\":3}", _poison);
                Set(_liquid, "_materials", new[] { _water, _poison }); _liquid.Validate();
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumNavigationBoxesPerChunk\":8}", _solid);
                Set(_solid, "_maximumNavigationBoxesPerChunk", navigationBoxes);
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":128,\"_maximumCellsPerTick\":32,\"_cellCapacityGmu\":4096}", _gas);
                Runtime = Host.AddComponent<ElementWorldRuntime>(); Set(Runtime, "_profile", _world); Set(Runtime, "_origin", Vector3.one * 100);
                Set(Runtime, "<IsInitialized>k__BackingField", true); Set(Runtime, "_hasInterestChunk", true);
                var backends = new MaterialSimulationBackendKind[256]; var present = new bool[256];
                foreach (var id in new[] { MaterialId.Water, MaterialId.Poison, MaterialId.Ice, MaterialId.DriedSlime, MaterialId.Steam, MaterialId.ToxicVapor })
                { present[(byte)id] = true; backends[(byte)id] = id == MaterialId.Water || id == MaterialId.Poison ? MaterialSimulationBackendKind.GridLiquid :
                    id == MaterialId.Ice || id == MaterialId.DriedSlime ? MaterialSimulationBackendKind.FixedSolid : MaterialSimulationBackendKind.GridGas; }
                var routes = (MaterialSimulationRouteSnapshot)Activator.CreateInstance(typeof(MaterialSimulationRouteSnapshot), BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new object[] { backends, present }, null); Set(Runtime, "_materialRoutes", routes);
                var baker = Host.GetComponent<ElementFieldSolidBaker>();
                Liquid = Host.AddComponent<LiquidWorldRuntime>(); Solid = Host.AddComponent<FixedSolidWorldRuntime>(); Gas = Host.AddComponent<GasWorldRuntime>();
                Set(Liquid, "_profile", _liquid); Set(Solid, "_profile", _solid); Set(Gas, "_profile", _gas);
                Set(Runtime, "_gridLiquidRuntime", Liquid); Set(Runtime, "_fixedSolidRuntime", Solid); Set(Runtime, "_gasRuntime", Gas); Set(Runtime, "_solidBaker", baker);
                Assert.That(Liquid.TryInitialize(Vector3.one * 100, 1, 2, baker), Is.True);
                Assert.That(Solid.TryInitialize(Runtime, baker, Vector3.one * 100, 1, 2), Is.True);
                Assert.That(Gas.TryInitialize(Runtime, baker, Vector3.one * 100, 1, 2), Is.True);
                baker.SetMaterialSolids(Solid, Vector3.one * 100, 1);
                Adapter = Host.AddComponent<MaterialInventoryTransferAdapter>(); Adapter.Configure(Runtime, "test-world");
            }
            public void Dispose()
            { UnityEngine.Object.DestroyImmediate(Host); foreach (var asset in new UnityEngine.Object[] { _world, _liquid, _solid, _gas, _water, _poison }) UnityEngine.Object.DestroyImmediate(asset); }
        }
        private static MaterialTransferRequestedEvent Request(Vector3 position, ulong budget = 30)
            => new MaterialTransferRequestedEvent { WorldId = "test-world", InventoryId = "inventory", SourceId = "source", RequestId = 1,
                CollectorPosition = position, SampleCenter = position, Radius = .1f, Budget = budget };
        [TestCase(1)] [TestCase(2)]
        public void RealMixedWaterPoisonConservesAtDifferentBackendResolution(int scale)
        {
            using var world = new World(scale);
            Assert.That(world.Liquid.TryAddInitial(Vector3Int.zero, MaterialId.Water, 100), Is.True);
            Assert.That(world.Liquid.TryAddInitial(Vector3Int.zero, MaterialId.Poison, 50), Is.True);
            var destination = new Destination(); var port = new MaterialInventoryWorldPort(world.Runtime);
            Vector3 position = world.Liquid.Origin + Vector3.one * world.Liquid.CellSize * .5f;
            Assert.That(port.Transfer(Request(position), destination, out ulong transferred), Is.EqualTo(MaterialTransferFailure.None));
            Assert.That(transferred, Is.EqualTo(30)); world.Liquid.TryGetQuantity(Vector3Int.zero, MaterialId.Water, out uint water);
            world.Liquid.TryGetQuantity(Vector3Int.zero, MaterialId.Poison, out uint poison);
            Assert.That(water, Is.EqualTo(80)); Assert.That(poison, Is.EqualTo(40));
            Assert.That(destination.Quantities[1], Is.EqualTo(20)); Assert.That(destination.Quantities[3], Is.EqualTo(10));
            Assert.That(world.Runtime.ExportedMaterialQuantity(MaterialId.Water), Is.EqualTo(20));
            Assert.That((ulong)water + poison + transferred, Is.EqualTo(150));
        }
        [TestCase(true)] [TestCase(false)]
        public void RejectedReservationAndStaleOwnerLeaveBothEndsUntouched(bool reject)
        {
            using var world = new World(); world.Liquid.TryAddInitial(Vector3Int.zero, MaterialId.Water, 100);
            var destination = new Destination { Reject = reject };
            if (!reject) destination.OnReserve = () => world.Liquid.TryAddInitial(Vector3Int.zero, MaterialId.Water, 1);
            var port = new MaterialInventoryWorldPort(world.Runtime);
            Assert.That(port.Transfer(Request(Vector3.one * 100.5f), destination, out _), Is.EqualTo(reject ? MaterialTransferFailure.Capacity : MaterialTransferFailure.StaleVersion));
            world.Liquid.TryGetQuantity(Vector3Int.zero, MaterialId.Water, out uint water);
            Assert.That(water, Is.EqualTo(reject ? 100 : 101)); Assert.That(destination.Committed, Is.False);
            Assert.That(destination.Reserved, Is.False); Assert.That(world.Runtime.ExportedMaterialQuantity(MaterialId.Water), Is.Zero);
        }
        [Test]
        public void StableLiquidRemainsTheRealOwnerAndPartialCapacityIsConserved()
        {
            using var world = new World();
            Assert.That(world.Liquid.TryRegisterStableRegion(MaterialId.Water, Vector3Int.zero, Vector3Int.one, 0, 0, 100, out _), Is.True);
            var destination = new Destination { FreeCapacity = 7 };
            var port = new MaterialInventoryWorldPort(world.Runtime);
            Assert.That(port.Transfer(Request(Vector3.one * 100.5f), destination, out ulong transferred), Is.EqualTo(MaterialTransferFailure.None));
            Assert.That(transferred, Is.EqualTo(7)); Assert.That(world.Liquid.StableQuantity, Is.EqualTo(93));
            Assert.That(world.Liquid.TotalQuantity + destination.Quantities[1], Is.EqualTo(100));
        }
        [TestCase(MaterialId.Steam)] [TestCase(MaterialId.ToxicVapor)] [TestCase(MaterialId.Ice)] [TestCase(MaterialId.DriedSlime)]
        public void GasAndStableSolidUseTheirTrueQuantity(MaterialId material)
        {
            using var world = new World();
            bool solid = material == MaterialId.Ice || material == MaterialId.DriedSlime;
            if (solid) { world.Solid.BeginConversionGeometry(); Assert.That(world.Solid.StageConversionAdd(Vector3Int.zero, material, 100), Is.True); }
            else Assert.That(world.Gas.StageConversionAdd(Vector3Int.zero, material, 100), Is.True);
            var destination = new Destination { AllowedMaterials = (ushort)(1 << (byte)material) };
            var port = new MaterialInventoryWorldPort(world.Runtime);
            Assert.That(port.Transfer(Request(Vector3.one * 100.5f), destination, out var transferred), Is.EqualTo(MaterialTransferFailure.None));
            world.Runtime.TryGetQuantity(Vector3Int.zero, material, out uint remaining);
            Assert.That(remaining, Is.EqualTo(70)); Assert.That(transferred, Is.EqualTo(30)); Assert.That(destination.Quantities[(byte)material], Is.EqualTo(30));
        }
        [Test]
        public void ExportIsAccountedSeparatelyFromDissipation()
        {
            var balanced = new MaterialQuantityFamilySnapshot(150, 120, 0, 0, 0, 30);
            Assert.That(balanced.IsBalanced, Is.True); Assert.That(balanced.RuleConsumed, Is.Zero);
        }
        [Test]
        public void GeometryFailureUndoesSolidAndReleasesDestination()
        {
            using var world = new World(navigationBoxes: 1);
            world.Solid.BeginConversionGeometry();
            Assert.That(world.Solid.StageConversionAdd(Vector3Int.zero, MaterialId.DriedSlime, 100), Is.True);
            Assert.That(world.Solid.StageConversionAdd(Vector3Int.right, MaterialId.DriedSlime, 100), Is.True);
            Assert.That(world.Solid.ValidateConversionGeometry(), Is.True); world.Solid.PublishConversionGeometry();
            var destination = new Destination { AllowedMaterials = 1 << 6 };
            var port = new MaterialInventoryWorldPort(world.Runtime);
            Assert.That(port.Transfer(Request(Vector3.one * 100.5f), destination, out _), Is.EqualTo(MaterialTransferFailure.GeometryRejected));
            world.Solid.TryGetQuantity(Vector3Int.zero, MaterialId.DriedSlime, out uint quantity);
            Assert.That(quantity, Is.EqualTo(100)); Assert.That(world.Solid.TotalQuantity, Is.EqualTo(200));
            Assert.That(destination.Reserved, Is.False); Assert.That(destination.Committed, Is.False);
        }
        [Test]
        public void RepeatedLiquidTransferHasNoManagedAllocationAfterWarmup()
        {
            using var world = new World(); world.Liquid.TryAddInitial(Vector3Int.zero, MaterialId.Water, 100);
            var destination = new Destination(); var port = new MaterialInventoryWorldPort(world.Runtime); var request = Request(Vector3.one * 100.5f, 1);
            for (int i = 0; i < 4; i++) port.Transfer(request, destination, out _);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 16; i++) port.Transfer(request, destination, out _);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.Zero, "只测预热后 Liquid World transfer 同步调用；不是整个场景的 Profiler 证据。");
        }
        [Test]
        public void SamplingOverflowRejectsWholeTransferInsteadOfTruncating()
        {
            using var world = new World(); world.Liquid.TryAddInitial(Vector3Int.zero, MaterialId.Water, 100);
            var destination = new Destination(); var request = Request(Vector3.one * 100.5f); request.Radius = 30;
            Assert.That(new MaterialInventoryWorldPort(world.Runtime).Transfer(request, destination, out _), Is.EqualTo(MaterialTransferFailure.SamplingLimit));
            Assert.That(world.Liquid.TotalQuantity, Is.EqualTo(100)); Assert.That(destination.Committed, Is.False);
        }
        [Test]
        public void UnsupportedProjectionBackendCannotPretendToSupplyGmu()
        {
            using var world = new World(); world.Liquid.TryAddInitial(Vector3Int.zero, MaterialId.Water, 100);
            var backends = new MaterialSimulationBackendKind[256]; var present = new bool[256];
            backends[1] = MaterialSimulationBackendKind.GpuPbfLiquid; present[1] = true;
            World.Set(world.Runtime, "_materialRoutes", (MaterialSimulationRouteSnapshot)Activator.CreateInstance(typeof(MaterialSimulationRouteSnapshot),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { backends, present }, null));
            var destination = new Destination();
            Assert.That(new MaterialInventoryWorldPort(world.Runtime).Transfer(Request(Vector3.one * 100.5f), destination, out _), Is.EqualTo(MaterialTransferFailure.UnsupportedBackend));
            Assert.That(world.Liquid.TotalQuantity, Is.EqualTo(100)); Assert.That(destination.Committed, Is.False);
        }
        [TestCase(false)] [TestCase(true)] public void IncompatibleBottleCannotPurifyMixedLiquidOrGas(bool gas)
        {
            using var world = new World();
            MaterialId first = gas ? MaterialId.Steam : MaterialId.Water, second = gas ? MaterialId.ToxicVapor : MaterialId.Poison;
            if (gas) { Assert.That(world.Gas.StageConversionAdd(Vector3Int.zero, first, 100), Is.True); Assert.That(world.Gas.StageConversionAdd(Vector3Int.zero, second, 50), Is.True); }
            else { Assert.That(world.Liquid.TryAddInitial(Vector3Int.zero, first, 100), Is.True); Assert.That(world.Liquid.TryAddInitial(Vector3Int.zero, second, 50), Is.True); }
            var destination = new Destination { AllowedMaterials = (ushort)(1 << (byte)first) };
            var result = new MaterialInventoryWorldPort(world.Runtime).Transfer(Request(Vector3.one * 100.5f), destination, out ulong transferred);
            Assert.That(result, Is.Not.EqualTo(MaterialTransferFailure.None), "相容筛选不能变成免费分离/净化。");
            Assert.That(transferred, Is.Zero); Assert.That(destination.Committed, Is.False);
            world.Runtime.TryGetQuantity(Vector3Int.zero, first, out uint a); world.Runtime.TryGetQuantity(Vector3Int.zero, second, out uint b);
            Assert.That(a, Is.EqualTo(100)); Assert.That(b, Is.EqualTo(50));
        }
    }
}
