using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class CellularMaterialRulesTests
    {
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
