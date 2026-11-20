using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class CellularMaterialRulesTests
    {
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
    }
}
