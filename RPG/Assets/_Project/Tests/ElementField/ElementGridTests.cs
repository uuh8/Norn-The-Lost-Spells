using Game.Materials;
using System;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// ElementGrid 是 Gameplay 数据的所有者。本组测试验证容量保护、连续存储和 Cell/Solid 两类数据互不污染，
    /// 不创建 GameObject，也不依赖 MonoBehaviour 生命周期。
    /// </summary>
    public sealed class ElementGridTests
    {
        [Test]
        public void SerializedMaterialValues_AreStable()
        {
            Assert.That((byte)MaterialId.Empty, Is.EqualTo(0));
            Assert.That((byte)MaterialId.Water, Is.EqualTo(1));
            Assert.That((byte)MaterialId.Fire, Is.EqualTo(2));
            Assert.That((byte)MaterialId.Poison, Is.EqualTo(3));
            Assert.That((byte)MaterialId.Sticky, Is.EqualTo(4));
        }

        [Test]
        public void ElementCell_IsEmptyWhenMaterialOrAmountIsEmpty()
        {
            Assert.That(new ElementCell(MaterialId.Empty, 255).IsEmpty, Is.True);
            Assert.That(new ElementCell(MaterialId.Water, 0).IsEmpty, Is.True);
            Assert.That(new ElementCell(MaterialId.Water, 1).IsEmpty, Is.False);
        }

        [Test]
        public void Constructor_PreallocatesAllSimulationBuffersOnce()
        {
            var grid = new ElementGrid(new Vector3Int(4, 3, 2), maximumCellCount: 24);

            Assert.That(grid.CellCount, Is.EqualTo(24));
            Assert.That(grid.CurrentCells.Length, Is.EqualTo(24));
            Assert.That(grid.NextCells.Length, Is.EqualTo(24));
            Assert.That(grid.SolidMask.Length, Is.EqualTo(24));
            Assert.That(grid.AmountDelta.Length, Is.EqualTo(24));
        }

        [TestCase(0, 1, 1)]
        [TestCase(1, 0, 1)]
        [TestCase(1, 1, 0)]
        [TestCase(-1, 1, 1)]
        public void Constructor_NonPositiveDimension_Throws(int x, int y, int z)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ElementGrid(new Vector3Int(x, y, z), maximumCellCount: 128));
        }

        [Test]
        public void Constructor_CellCountAboveConfiguredMaximum_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ElementGrid(new Vector3Int(5, 5, 5), maximumCellCount: 100));
        }

        [Test]
        public void Constructor_UsesLongBeforeMultiplicationCanOverflow()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ElementGrid(
                    new Vector3Int(int.MaxValue, int.MaxValue, 2),
                    maximumCellCount: int.MaxValue));
        }

        [Test]
        public void CellAndSolidStorage_AreIndependent()
        {
            var grid = new ElementGrid(new Vector3Int(2, 2, 2), maximumCellCount: 8);
            var coordinate = new Vector3Int(1, 0, 1);

            grid.SetCell(coordinate, new ElementCell(MaterialId.Water, 128));
            Assert.That(grid.GetCell(coordinate).MaterialKind, Is.EqualTo(MaterialId.Water));
            Assert.That(grid.GetCell(coordinate).Amount, Is.EqualTo(128));
            Assert.That(grid.IsSolid(coordinate), Is.False);

            grid.SetSolid(coordinate, true);
            Assert.That(grid.IsSolid(coordinate), Is.True);
            Assert.That(grid.GetCell(coordinate).MaterialKind, Is.EqualTo(MaterialId.Water));
            Assert.That(grid.GetCell(coordinate).Amount, Is.EqualTo(128));
        }

        [Test]
        public void PublicReads_OutOfBounds_ThrowInsteadOfReadingAnotherCell()
        {
            var grid = new ElementGrid(new Vector3Int(2, 2, 2), maximumCellCount: 8);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                grid.GetCell(new Vector3Int(2, 0, 0)));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                grid.IsSolid(new Vector3Int(0, -1, 0)));
        }

        [Test]
        public void ChunkStorage_UsesCeilingDivisionAndStartsAtZero()
        {
            var grid = new ElementGrid(
                new Vector3Int(5, 3, 4),
                maximumCellCount: 60,
                chunkSize: 2);

            Assert.That(grid.ChunkCounts, Is.EqualTo(new Vector3Int(3, 2, 2)));
            Assert.That(grid.GetChunkVersion(2, 1, 1), Is.Zero);
        }

        [Test]
        public void DebugClearCannotResurrectElementFromNextBuffer()
        {
            var grid = new ElementGrid(Vector3Int.one, maximumCellCount: 1, chunkSize: 1);
            grid.SetCell(Vector3Int.zero, new ElementCell(MaterialId.Water, 30));
            grid.PrepareNextFromCurrent();

            Assert.That(grid.ClearElementsAndCommitVersions(), Is.EqualTo(1));
            grid.CommitPreparedStage();

            Assert.That(grid.GetCell(Vector3Int.zero).IsEmpty, Is.True);
            Assert.That(grid.GetChunkVersion(0, 0, 0), Is.EqualTo(1u));
        }
    }
}
