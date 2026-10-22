using System;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 连续世界不能依赖“只有正坐标”的实验房假设。本组测试先锁定数学 Floor Division：
    /// 负方向世界位置必须进入负 Chunk，同时 Chunk 内 Local Cell 仍保持 0～7。
    /// </summary>
    public sealed class ElementWorldCoordinatesTests
    {
        [TestCase(-9, -2, 7)]
        [TestCase(-8, -1, 0)]
        [TestCase(-1, -1, 7)]
        [TestCase(0, 0, 0)]
        [TestCase(7, 0, 7)]
        [TestCase(8, 1, 0)]
        public void SplitAxis_UsesFloorDivisionAndPositiveLocalCoordinate(
            int globalCell,
            int expectedChunk,
            int expectedLocal)
        {
            ElementWorldCoordinates.SplitAxis(
                globalCell,
                chunkSize: 8,
                out int chunk,
                out int local);

            Assert.That(chunk, Is.EqualTo(expectedChunk));
            Assert.That(local, Is.EqualTo(expectedLocal));
        }

        [Test]
        public void WorldToGlobalCell_NegativeFractionMapsToNegativeCell()
        {
            Vector3Int cell = ElementWorldCoordinates.WorldToGlobalCell(
                new Vector3(-0.01f, 0.24f, -0.26f),
                Vector3.zero,
                cellSize: 0.25f);

            Assert.That(cell, Is.EqualTo(new Vector3Int(-1, 0, -2)));
        }

        [Test]
        public void GlobalCellSplitAndCompose_RoundTripsAcrossNegativeChunks()
        {
            var globalCell = new Vector3Int(-17, 9, 24);

            ElementWorldCoordinates.GlobalCellToChunkAndLocal(
                globalCell,
                chunkSize: 8,
                out ElementChunkKey chunk,
                out Vector3Int localCell);

            Assert.That(chunk, Is.EqualTo(new ElementChunkKey(-3, 1, 3)));
            Assert.That(localCell, Is.EqualTo(new Vector3Int(7, 1, 0)));
            Assert.That(
                ElementWorldCoordinates.ComposeGlobalCell(chunk, localCell, chunkSize: 8),
                Is.EqualTo(globalCell));
        }

        [Test]
        public void InvalidCellOrChunkSize_ThrowsBeforeDivision()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ElementWorldCoordinates.WorldToGlobalCell(
                    Vector3.zero,
                    Vector3.zero,
                    cellSize: 0f));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ElementWorldCoordinates.SplitAxis(
                    globalCell: 0,
                    chunkSize: 0,
                    out _,
                    out _));
        }

        [Test]
        public void ChunkKey_UsesValueEqualityForDictionaryIdentity()
        {
            var first = new ElementChunkKey(-2, 1, 5);
            var same = new ElementChunkKey(-2, 1, 5);
            var different = new ElementChunkKey(-2, 1, 6);

            Assert.That(first, Is.EqualTo(same));
            Assert.That(first.GetHashCode(), Is.EqualTo(same.GetHashCode()));
            Assert.That(first, Is.Not.EqualTo(different));
        }
    }
}
