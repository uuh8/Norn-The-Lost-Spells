using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// Deposit 测试把“世界命中范围如何变成 Cell 数量”单独验证，
    /// 避免流动、衰减等后续 Stage 掩盖写入错误。
    /// </summary>
    public sealed class ElementFieldWriteProcessorTests
    {
        [Test]
        public void DepositDistributesTotalAmountWithinRadius()
        {
            ElementGrid grid = Grid(new Vector3Int(3, 1, 1));
            var request = new ElementWriteRequest(
                new Vector3(1.5f, 0.5f, 0.5f),
                MaterialId.Water,
                totalAmount: 100,
                radius: 1.5f,
                useLinearFalloff: true);

            bool applied = ElementFieldWriteProcessor.TryApply(
                grid, in request, Vector3.zero, cellSize: 1f, out int changedCells);

            Assert.That(applied, Is.True);
            Assert.That(changedCells, Is.EqualTo(3));
            Assert.That(Sum(grid, MaterialId.Water), Is.EqualTo(100));
            Assert.That(grid.GetCell(1, 0, 0).Amount, Is.GreaterThan(grid.GetCell(0, 0, 0).Amount));
        }

        [Test]
        public void ZeroRadiusDepositsIntoSingleContainingCell()
        {
            ElementGrid grid = Grid(new Vector3Int(2, 1, 1));
            var request = new ElementWriteRequest(
                new Vector3(1.25f, 0.5f, 0.5f),
                MaterialId.Water,
                totalAmount: 80,
                radius: 0f,
                useLinearFalloff: true);

            Assert.That(ElementFieldWriteProcessor.TryApply(
                grid, in request, Vector3.zero, 1f, out int changedCells), Is.True);
            Assert.That(changedCells, Is.EqualTo(1));
            Assert.That(grid.GetCell(1, 0, 0).Amount, Is.EqualTo(80));
        }

        [Test]
        public void DepositIntoSameMaterialAccumulatesAndClamps()
        {
            ElementGrid grid = Grid(Vector3Int.one);
            grid.SetCell(Vector3Int.zero, new ElementCell(MaterialId.Water, 200));
            ElementWriteRequest request = PointWrite(MaterialId.Water, 100);

            Assert.That(ElementFieldWriteProcessor.TryApply(
                grid, in request, Vector3.zero, 1f, out _), Is.True);
            Assert.That(grid.GetCell(Vector3Int.zero).Amount, Is.EqualTo(byte.MaxValue));
        }

        [Test]
        public void WaterDepositIntoFireConsumesOldMaterialBeforeReplacing()
        {
            ElementGrid grid = Grid(Vector3Int.one);
            grid.SetCell(Vector3Int.zero, new ElementCell(MaterialId.Fire, 100));
            ElementWriteRequest request = PointWrite(MaterialId.Water, 160);

            Assert.That(ElementFieldWriteProcessor.TryApply(
                grid, in request, Vector3.zero, 1f, out _), Is.True);
            ElementCell result = grid.GetCell(Vector3Int.zero);
            Assert.That(result.MaterialKind, Is.EqualTo(MaterialId.Water));
            Assert.That(result.Amount, Is.EqualTo(60));
        }

        [Test]
        public void UnsupportedMaterialDoesNotChangeGrid()
        {
            ElementGrid grid = Grid(Vector3Int.one);
            ElementWriteRequest request = PointWrite(MaterialId.Poison, 100);

            Assert.That(ElementFieldWriteProcessor.TryApply(
                grid, in request, Vector3.zero, 1f, out int changedCells), Is.False);
            Assert.That(changedCells, Is.Zero);
            Assert.That(grid.GetCell(Vector3Int.zero).IsEmpty, Is.True);
        }

        private static ElementWriteRequest PointWrite(MaterialId kind, ushort amount)
        {
            return new ElementWriteRequest(
                new Vector3(0.5f, 0.5f, 0.5f),
                kind,
                amount,
                radius: 0f,
                useLinearFalloff: false);
        }

        private static ElementGrid Grid(Vector3Int dimensions)
        {
            return new ElementGrid(dimensions, maximumCellCount: 128, chunkSize: 2);
        }

        private static int Sum(ElementGrid grid, MaterialId kind)
        {
            int total = 0;
            for (int i = 0; i < grid.CellCount; i++)
            {
                ElementCell cell = grid.GetCell(ElementFieldCoordinates.FromIndex(i, grid.Dimensions));
                if (cell.MaterialKind == kind)
                    total += cell.Amount;
            }

            return total;
        }
    }
}
