using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class MaterialSolidGasWorldTests
    {
        [Test]
        public void ThinGasFirstMovementHasNoFixedPreferredFaceAndConservesQuantity()
        {
            var seen=new bool[6]; int directions=0;
            for(int sample=0;sample<36;sample++)
            {
                var store=new GasVolumeStore(4,8,32,new[]{MaterialId.Steam});
                var activity=new MaterialCellActivityQueue(128,store);
                var solver=new GasFluxSolver(store,activity,new[]{new GasMaterialBehavior{Material=MaterialId.Steam,DiffusionPerSecond=1}},32,c=>false);
                var source=new Vector3Int(sample*13,7,-sample*11);
                store.TryAdd(source,MaterialId.Steam,1); activity.Wake(source);
                for(int tick=0;tick<90 && store.OccupancyAt(source)>0;tick++) solver.Step(.1f);
                Assert.That(store.TotalQuantity,Is.EqualTo(1UL));
                Vector3Int move=default;
                for(int i=0;i<store.ResidentCellCount;i++) if(store.TryGetCell(i,out var cell)) move=cell.Cell-source;
                int face=move.x>0?0:move.x<0?1:move.y>0?2:move.y<0?3:move.z>0?4:5;
                Assert.That(move.sqrMagnitude,Is.EqualTo(1));
                if(!seen[face]) { seen[face]=true; directions++; }
            }
            Assert.That(directions,Is.GreaterThanOrEqualTo(4),"整数余量不能令每个薄格都先向上流出；均匀气体没有固定风向。");
        }
        [Test]
        public void FractionalDiffusionEventuallyMovesOneGmuWithoutCreatingOrLosingMaterial()
        {
            var store=new GasVolumeStore(4,8,32,new[]{MaterialId.ToxicVapor});
            var activity=new MaterialCellActivityQueue(128,store);
            var solver=new GasFluxSolver(store,activity,new[]{new GasMaterialBehavior
                {Material=MaterialId.ToxicVapor,DiffusionPerSecond=1}},32,cell=>false);
            store.TryAdd(Vector3Int.zero,MaterialId.ToxicVapor,1); activity.Wake(Vector3Int.zero);
            bool moved=false;
            for(int tick=0;tick<90;tick++)
            {
                solver.Step(.1f);
                moved|=!store.TryGetQuantity(Vector3Int.zero,MaterialId.ToxicVapor,out _);
                Assert.That(store.TotalQuantity,Is.EqualTo(1UL));
            }
            Assert.That(moved,Is.True,"整数 floor 不能把稀薄气体永久休眠在出生格。");
        }
        [Test]
        public void FixedSolidPreservesLargeQuantityNegativeCoordinatesAndRejectsDifferentIdentity()
        {
            var store = new FixedSolidVolumeStore(4, 2, 4096, new[] { MaterialId.Ice, MaterialId.DriedSlime });
            var cell = new Vector3Int(-1, 0, -5);
            Assert.That(store.TryAdd(cell, MaterialId.Ice, 1024), Is.True);
            Assert.That(store.TryAdd(cell, MaterialId.DriedSlime, 10), Is.False);
            Assert.That(store.TryGetQuantity(cell, MaterialId.Ice, out uint quantity), Is.True);
            Assert.That(quantity, Is.EqualTo(1024));
            Assert.That(store.HeightAt(cell), Is.EqualTo(.25f));
            uint version = store.Version;
            Assert.That(store.TryRemove(cell, MaterialId.Ice, 512), Is.True);
            Assert.That(store.Version, Is.GreaterThan(version));
            Assert.That(store.TotalQuantity, Is.EqualTo(512UL));
        }
        [Test]
        public void ExhaustedChunkPoolRejectsWithoutLosingExistingQuantityAndCanReclaim()
        {
            var store = new GasVolumeStore(2, 1, 4096, new[] { MaterialId.Steam, MaterialId.ToxicVapor });
            Assert.That(store.TryAdd(Vector3Int.zero, MaterialId.Steam, 1024), Is.True);
            Assert.That(store.TryAdd(Vector3Int.right * 4, MaterialId.Steam, 100), Is.False);
            Assert.That(store.TotalQuantity, Is.EqualTo(1024UL));
            Assert.That(store.TryRemove(Vector3Int.zero, MaterialId.Steam, 1024), Is.True);
            store.ReclaimEmptyChunks();
            Assert.That(store.TryAdd(Vector3Int.right * 4, MaterialId.ToxicVapor, 100), Is.True);
            Assert.That(store.TotalQuantity, Is.EqualTo(100UL));
        }
        [Test]
        public void GasDoubleBufferSharesCapacityAndKeepsCurrentUnchangedUntilCommit()
        {
            var store = new GasVolumeStore(2, 4, 4096, new[] { MaterialId.Steam, MaterialId.ToxicVapor });
            Assert.That(store.TryAdd(Vector3Int.zero, MaterialId.Steam, 4000), Is.True);
            Assert.That(store.TryAdd(Vector3Int.right, MaterialId.ToxicVapor, 4000), Is.True);
            store.BeginFluxTick();
            Assert.That(store.Transfer(Vector3Int.zero, Vector3Int.right, MaterialId.Steam, 1000), Is.EqualTo(96u));
            store.TryGetQuantity(Vector3Int.zero, MaterialId.Steam, out uint before);
            Assert.That(before, Is.EqualTo(4000));
            store.CommitFluxTick();
            Assert.That(store.TotalQuantity, Is.EqualTo(8000UL));
            Assert.That(store.OccupancyAt(Vector3Int.right), Is.EqualTo(4096u));
        }
        [Test]
        public void ClosedRoomGasConservesQuantityDoesNotCrossWallAndStaticTicksDoNotAllocate()
        {
            var store = new GasVolumeStore(2, 8, 4096, new[] { MaterialId.ToxicVapor });
            var activity = new MaterialCellActivityQueue(64, store);
            var solver = new GasFluxSolver(store, activity,
                new[] { new GasMaterialBehavior { Material = MaterialId.ToxicVapor, DiffusionPerSecond = 2 } },
                32, c => c.x < 0 || c.x > 2 || c.y != 0 || c.z != 0);
            Assert.That(store.TryAdd(Vector3Int.zero, MaterialId.ToxicVapor, 3000), Is.True);
            activity.Wake(Vector3Int.zero);
            for (int i = 0; i < 300; i++) solver.Step(.1f);
            Assert.That(store.TotalQuantity, Is.EqualTo(3000UL));
            Assert.That(store.OccupancyAt(Vector3Int.right), Is.GreaterThan(0));
            Assert.That(store.OccupancyAt(Vector3Int.right * 3), Is.Zero);
            long allocated = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 30; i++) solver.Step(.1f);
            Assert.That(System.GC.GetAllocatedBytesForCurrentThread() - allocated, Is.Zero);
        }
        [Test]
        public void SteamRisesAndFrozenGasRestoresWithoutElapsedTimeCatchUp()
        {
            var store = new GasVolumeStore(4, 16, 4096, new[] { MaterialId.Steam });
            var activity = new MaterialCellActivityQueue(256, store);
            var solver = new GasFluxSolver(store, activity,
                new[] { new GasMaterialBehavior { Material = MaterialId.Steam, DiffusionPerSecond = 1, BuoyancyPerSecond = 1 } },
                128, c => c.x != 0 || c.z != 0 || c.y < 0 || c.y > 4);
            Assert.That(store.TryAdd(Vector3Int.zero, MaterialId.Steam, 2000), Is.True);
            var domain = new ElementWorldActivityRegions();
            domain.TryAdd(new Bounds(Vector3.one * 100, Vector3.one * 8));
            activity.SetDomain(domain, Vector3.zero, 1);
            activity.Wake(Vector3Int.zero);
            for (int i = 0; i < 600; i++) solver.Step(.1f);
            Assert.That(store.TryGetQuantity(Vector3Int.zero, MaterialId.Steam, out uint frozen), Is.True);
            Assert.That(frozen, Is.EqualTo(2000));
            domain.Clear(); domain.TryAdd(new Bounds(Vector3.zero, Vector3.one * 16));
            activity.SetDomain(domain, Vector3.zero, 1);
            solver.Step(.1f);
            Assert.That(store.OccupancyAt(Vector3Int.up), Is.GreaterThan(0));
            Assert.That(store.TotalQuantity, Is.EqualTo(2000UL));
            Assert.That(store.OccupancyAt(Vector3Int.zero), Is.GreaterThan(1000u));
        }
        [Test]
        public void ExplicitVentRemovesOnlyAccountedQuantity()
        {
            var store = new GasVolumeStore(2, 2, 4096, new[] { MaterialId.Steam });
            var activity = new MaterialCellActivityQueue(16, store);
            var solver = new GasFluxSolver(store, activity,
                new[] { new GasMaterialBehavior { Material = MaterialId.Steam } }, 8,
                c => c != Vector3Int.zero, (c, m, dt) => 100);
            Assert.That(store.TryAdd(Vector3Int.zero, MaterialId.Steam, 1000), Is.True);
            activity.Wake(Vector3Int.zero); solver.Step(.1f);
            Assert.That(store.TotalQuantity + solver.VentedQuantity, Is.EqualTo(1000UL));
            Assert.That(solver.VentedQuantity, Is.EqualTo(100UL));
        }
    }
}
