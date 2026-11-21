using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class CellularMaterialRulesTests
    {
        [Test]
        public void SameHomeWaterDoseFillsDepthThenDrainsThroughAnOpenedBottomOutlet()
        {
            // 一根一格宽的槽接一个更低的出口。和截图同量的水在槽内必须积出四格多的真实水深；
            // 开洞只唤醒局部邻格，水依靠重力和侧向输运进入下方空间。
            bool outletOpen = false;
            bool Wall(Vector3Int p)
            {
                if (p.x == 0 && p.z == 0 && p.y >= 0) return false;
                if (outletOpen && p.x == 1 && p.z == 0 && p.y == 0) return false;
                return p.x != 2 || p.z != 0 || p.y < -8 || p.y > 0;
            }
            var cells = new ElementCellSimulation(255, 64, Wall);
            for (int i = 0; i < 5; i++)
                Assert.That(cells.TryDeposit(Vector3Int.up * (5 + i), MaterialId.Water, i < 4 ? 255u : 12u), Is.True);
            for (int i = 0; i < 100; i++) cells.Step(64);
            Assert.That(cells.TryGetQuantity(Vector3Int.up * 4, MaterialId.Water, out var top), Is.True);
            Assert.That(top, Is.EqualTo(12u));
            outletOpen = true;
            cells.WakeNeighbors(Vector3Int.right);
            for (int i = 0; i < 200; i++) cells.Step(64);
            Assert.That(cells.TryGetQuantity(new Vector3Int(2, -8, 0), MaterialId.Water, out var low), Is.True);
            Assert.That(low, Is.EqualTo(255u));
            Assert.That(cells.TryGetQuantity(Vector3Int.up * 4, MaterialId.Water, out _), Is.False);
            Assert.That(cells.GetTotalQuantity(MaterialId.Water), Is.EqualTo(1032ul));
        }

        [TestCase(MaterialId.Water, 1032, 40)]
        [TestCase(MaterialId.Poison, 298, 35)]
        [TestCase(MaterialId.Sticky, 472, 65)]
        [TestCase(MaterialId.Oil, 305, 20)]
        public void HomeQuantityPouredOntoDryGroundSettlesWithoutSpreadingToTraceCells(
            MaterialId material, int quantity, int minimumMeanGmu)
        {
            // 正式格 25 cm / 255 GMU。使用玩家 Home 中的四份实际数量，并让材料从空中自行落地。
            var world = new ElementCellSimulation(255, 1024, p => p.y < 0);
            int left = quantity;
            for (int y = 4; left > 0; y++)
            {
                int portion = System.Math.Min(left, 255);
                Assert.IsTrue(world.TryDeposit(new Vector3Int(0, y, 0), material, (uint)portion));
                left -= portion;
            }
            for (int step = 0; step < 256 && world.ActiveCellCount != 0; step++) world.Step(1024);
            Assert.Zero(world.ActiveCellCount);
            Assert.AreEqual((ulong)quantity, world.GetTotalQuantity(material));
            Assert.Greater(world.OccupiedCellCount, 1, "足量液体仍须沿地板向外铺展");
            Assert.GreaterOrEqual(quantity / world.OccupiedCellCount, minimumMeanGmu);
            for (int i = 0; i < world.OccupiedCellCount; i++)
            {
                Assert.IsTrue(world.TryGetOccupiedCoordinate(i, out var cell));
                Assert.Zero(cell.y);
            }
            TestContext.WriteLine($"{material} {quantity} GMU, {world.OccupiedCellCount} columns, mean depth " +
                $"{250d * quantity / (255d * world.OccupiedCellCount):F1} mm.");
        }

        [Test]
        public void ExcavatingBesideASleepingOneGmuDropLetsItDrainOverTheEdge()
        {
            var lip = Vector3Int.right;
            var sink = lip + Vector3Int.down;
            var cold = new CellularRestingLiquidStore(null, 255, 128);
            bool cut = false;
            var world = new ElementCellSimulation(255, 32, p =>
                cold.BlocksSimulation(p) || p != Vector3Int.zero && p != lip && !(cut && p == sink));
            world.BindRestingMaterials(cold);
            Assert.IsTrue(world.TryDeposit(Vector3Int.zero, MaterialId.Water, 1));
            Assert.IsTrue(cold.TryArchiveSettled(Vector3Int.zero, world, out _));
            cut = true;
            cold.WakeNeighbors(sink);
            for (int i = 0; i < 16; i++)
            {
                cold.Step(32, world);
                world.Step(32);
                while (world.TryDequeueChanged(out var moved)) cold.WakeNeighbors(moved);
            }
            Assert.IsTrue(world.TryGetQuantity(sink, MaterialId.Water, out uint remaining) ||
                cold.TryGetQuantity(sink, MaterialId.Water, out remaining));
            Assert.AreEqual(1u, remaining);
        }

        [Test]
        public void FallingSandFirstFillsArchivedSandAndOnlyExcessStaysAbove()
        {
            var cold = new CellularRestingLiquidStore(null, 255, 128);
            var world = new ElementCellSimulation(255, 32, p =>
                cold.BlocksSimulation(p) || p != Vector3Int.zero && p != Vector3Int.up);
            world.BindRestingMaterials(cold);
            Assert.IsTrue(world.TryDeposit(Vector3Int.zero, MaterialId.Sand, 200));
            Assert.IsTrue(cold.TryArchiveSettled(Vector3Int.zero, world, out _));
            Assert.IsTrue(world.TryDeposit(Vector3Int.up, MaterialId.Sand, 100));
            cold.WakeNeighbors(Vector3Int.up);
            for (int step = 0; step < 32; step++)
            {
                cold.Step(64, world);
                world.Step(64);
                while (world.TryDequeueChanged(out var changed)) cold.WakeNeighbors(changed);
            }
            Assert.IsTrue(world.TryGetQuantity(Vector3Int.zero, MaterialId.Sand, out uint bottom) ||
                cold.TryGetQuantity(Vector3Int.zero, MaterialId.Sand, out bottom));
            Assert.IsTrue(world.TryGetQuantity(Vector3Int.up, MaterialId.Sand, out uint top) ||
                cold.TryGetQuantity(Vector3Int.up, MaterialId.Sand, out top));
            Assert.AreEqual(255u, bottom);
            Assert.AreEqual(45u, top);
        }

        [Test]
        public void GrainPileBuildsAboveFlatGroundAndSinksThroughWaterWithoutLosingMass()
        {
            var world = new ElementCellSimulation(255, 128, p => p.y < 0);
            for (int y = 1; y <= 15; y++) Assert.IsTrue(world.TryDeposit(Vector3Int.up * y, MaterialId.Sand, 255));
            for (int i = 0; i < 96 && world.ActiveCellCount > 0; i++) world.Step(128);
            Assert.Zero(world.ActiveCellCount);
            Assert.IsTrue(world.TryGetQuantity(Vector3Int.up, MaterialId.Sand, out _));
            Assert.IsTrue(world.TryGetQuantity(Vector3Int.right, MaterialId.Sand, out _));
            Assert.AreEqual(15UL * 255, world.GetTotalQuantity(MaterialId.Sand));

            var tube = new ElementCellSimulation(255, 8, p => p != Vector3Int.zero && p != Vector3Int.up);
            Assert.IsTrue(tube.TryDeposit(Vector3Int.zero, MaterialId.Water, 83));
            Assert.IsTrue(tube.TryDeposit(Vector3Int.up, MaterialId.Sand, 17));
            tube.Step(8);
            Assert.IsTrue(tube.TryGetQuantity(Vector3Int.zero, MaterialId.Sand, out uint submerged));
            Assert.AreEqual(17u, submerged);
            Assert.IsTrue(tube.TryGetQuantity(Vector3Int.up, MaterialId.Water, out uint displaced));
            Assert.AreEqual(83u, displaced);
        }

        [Test]
        public void NewlyFixedIceArchivesWithoutAnActiveTickAndStillCondensesAdjacentSteam()
        {
            var cold = new CellularRestingLiquidStore(null, 64, 16);
            var cells = new ElementCellSimulation(64, 8, p => cold.BlocksSimulation(p) || p != Vector3Int.zero && p != Vector3Int.right);
            cells.BindRestingMaterials(cold);
            Assert.IsTrue(cells.TryDeposit(Vector3Int.zero, MaterialId.Ice, 13));
            Assert.AreEqual(0, cells.ActiveCellCount);
            Assert.AreEqual(1, cells.SleepingLiquidCandidateCount);
            cold.Step(8, cells);
            Assert.AreEqual(0, cells.OccupiedCellCount);
            Assert.AreEqual(13UL, cold.GetTotalQuantity(MaterialId.Ice));
            Assert.IsTrue(cells.TryDeposit(Vector3Int.right, MaterialId.Steam, 8));
            cells.Step(8);
            Assert.AreEqual(4UL, cells.GetTotalQuantity(MaterialId.Water));
            Assert.AreEqual(4UL, cells.GetTotalQuantity(MaterialId.Steam));
        }

        [Test]
        public void OverflowedColdGrainWakeProgressesBeforeUnrelatedMillionCellLakeBorderCompletes()
        {
            var lake = new CellularRestingLiquidRegion { Material = MaterialId.Water, Cells = new BoundsInt(0,0,0,1000000,1,1) };
            var cold = new CellularRestingLiquidStore(new[]{ lake },64,1);
            var grain = new Vector3Int(-400,0,0);
            var sink = grain + Vector3Int.down;
            bool open = false;
            var cells = new ElementCellSimulation(64,8,p => cold.BlocksSimulation(p) || p != grain && !(open && p == sink));
            cold.WakeNeighbors(new Vector3Int(100,0,0));   // 填满一个主队列项和另一个溢出页。
            Assert.IsTrue(cells.TryDeposit(grain,MaterialId.Sand,23));
            Assert.IsTrue(cold.TryArchiveSettled(grain,cells,out _));
            open = true;
            cold.WakeNeighbors(sink);
            for (int i = 0; i < 32 && !cells.TryGetQuantity(sink,MaterialId.Sand,out _); i++)
            { cold.Step(1,cells); cells.Step(8); }
            Assert.IsTrue(cells.TryGetQuantity(sink,MaterialId.Sand,out uint amount));
            Assert.AreEqual(23u,amount);
            Assert.AreEqual(64000000UL,cold.GetTotalQuantity(MaterialId.Water));
        }

        [Test]
        public void ExcavatingSupportWakesArchivedSandWithoutChangingItsQuantity()
        {
            var cold = new CellularRestingLiquidStore(null, 64, 128);
            bool open = false;
            var cells = new ElementCellSimulation(64, 8, p =>
                (p.y < 0 && !(open && p == Vector3Int.down)) || cold.BlocksSimulation(p));
            Assert.IsTrue(cells.TryDeposit(Vector3Int.zero, MaterialId.Sand, 37));
            Assert.IsTrue(cold.TryArchiveSettled(Vector3Int.zero, cells, out _));
            Assert.AreEqual(0, cells.OccupiedCellCount);
            Assert.AreEqual(37UL, cold.GetTotalQuantity(MaterialId.Sand));
            open = true;
            cold.WakeNeighbors(Vector3Int.down);
            cold.Step(128, cells);
            cells.Step(16);
            Assert.IsTrue(cells.TryGetQuantity(Vector3Int.down, MaterialId.Sand, out var quantity));
            Assert.AreEqual(37u, quantity);
            Assert.AreEqual(0UL, cold.GetTotalQuantity(MaterialId.Sand));
        }

        [Test]
        public void PartialColdWaterReleasesItsGasSpaceOnMaterialization()
        {
            var cold = new CellularRestingLiquidStore(null, 64, 128);
            var cells = new ElementCellSimulation(64, 8, p => p != Vector3Int.zero || cold.BlocksSimulation(p));
            Assert.IsTrue(cells.TryDeposit(Vector3Int.zero, MaterialId.Water, 23));
            Assert.IsTrue(cold.TryArchiveSettled(Vector3Int.zero, cells, out _));
            Assert.IsTrue(cold.TryMaterialize(Vector3Int.zero, cells));
            Assert.IsTrue(cells.TryDeposit(Vector3Int.zero, MaterialId.Steam, 41));
            Assert.IsFalse(cells.TryDeposit(Vector3Int.zero, MaterialId.ToxicVapor, 1));
            Assert.IsFalse(cold.TryArchiveSettled(Vector3Int.zero, cells, out _));
            Assert.AreEqual(23UL, cells.GetTotalQuantity(MaterialId.Water));
            Assert.AreEqual(41UL, cells.GetTotalQuantity(MaterialId.Steam));
        }

        [Test]
        public void RecipeCannotConsumeWhatItCannotPlace()
        {
            var cells = new ElementCellSimulation(64, 8, p => p != Vector3Int.zero);
            var ledger = new MaterialWorldQuantityLedger();
            var rows = new MaterialConversionPlacement[14];
            var ice = new MaterialConversionRule { Source = MaterialId.Water, FirstProduct = MaterialId.Ice,
                InputUnits = 1, FirstUnits = 1, InputGmuPerSecond = 32 };
            var source = new MaterialConversionAddress(Vector3Int.zero, MaterialId.Water);
            Assert.IsTrue(cells.TryDeposit(source.Cell, source.Material, 64));
            Assert.IsFalse(cells.TryReact(source, in ice, 1, rows, ledger, out _));
            Assert.AreEqual(64UL, cells.GetTotalQuantity(MaterialId.Water));
            Assert.AreEqual(0UL, ledger.CommittedTransactions);
            Assert.IsTrue(cells.TryReact(source, in ice, 64, rows, ledger, out _));
            Assert.AreEqual(64UL, cells.GetTotalQuantity(MaterialId.Ice));
            Assert.AreEqual(64UL, ledger.Consumed(MaterialId.Water));
        }

        [Test]
        public void DirectedPourMovesOnlyItsOwnQuantityBeforeGravity()
        {
            var cells = new ElementCellSimulation(64, 8, p => false);
            Assert.IsTrue(cells.TryDeposit(Vector3Int.zero, MaterialId.Water, 54));
            cells.GiveImpulse(Vector3Int.zero, MaterialId.Water, 14, Vector3.right, .2f);
            cells.Step(16);
            Assert.IsTrue(cells.TryGetQuantity(Vector3Int.right, MaterialId.Water, out var projected));
            Assert.AreEqual(14u, projected);
            Assert.AreEqual(54UL, cells.GetTotalQuantity(MaterialId.Water));
        }

        [Test]
        public void RestingHandoffLeavesAnUpwardPourWithItsTransportOwner()
        {
            var cold = new CellularRestingLiquidStore(null, 64, 8);
            var cells = new ElementCellSimulation(64, 4, p => p != Vector3Int.zero && p != Vector3Int.up || cold.BlocksSimulation(p));
            Assert.IsTrue(cells.TryDeposit(Vector3Int.zero, MaterialId.Water, 20));
            cells.GiveImpulse(Vector3Int.zero, MaterialId.Water, 20, Vector3.up, 0);
            Assert.IsFalse(cold.TryArchiveSettled(Vector3Int.zero, cells, out _));
            cells.Step(4);
            Assert.IsTrue(cells.TryGetQuantity(Vector3Int.up, MaterialId.Water, out uint amount));
            Assert.AreEqual(20u, amount);
        }

        [Test]
        public void AbsorptionQueueRetainsColdContactsBeyondOneTickAndDeduplicatesAcrossNegativePages()
        {
            var work = new CellularAbsorptionQueue();
            var expected = new System.Collections.Generic.HashSet<Vector3Int>();
            for (int i = 0; i < 2048; i++)
            {
                var cell = new Vector3Int(i % 32 - 16, i / 32 % 8 - 4, i / 256 - 4);
                expected.Add(cell);
                work.Enqueue(cell);
                work.Enqueue(cell);
            }
            Assert.AreEqual(expected.Count, work.Count);
            while (work.Count != 0) Assert.IsTrue(expected.Remove(work.Take()));
            Assert.AreEqual(0, expected.Count);
        }
    }
}
