using System;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 坐标测试把 World Space、Grid Space 和线性数组索引分开验证。
    /// 这能尽早发现最危险的错误：不同系统各自实现一套换算公式，最终把同一世界位置写进不同 Cell。
    /// </summary>
    public sealed class ElementFieldCoordinatesTests
    {
        [Test]
        public void WorldToCell_UsesFloorAndOriginOffset()
        {
            bool inside = ElementFieldCoordinates.TryWorldToCell(
                new Vector3(10.49f, 2.24f, -0.01f),
                new Vector3(10f, 2f, -0.5f),
                0.25f,
                new Vector3Int(4, 4, 4),
                out Vector3Int cell);

            Assert.That(inside, Is.True);
            Assert.That(cell, Is.EqualTo(new Vector3Int(1, 0, 1)));
        }

        [Test]
        public void WorldToCell_NegativeLocalCoordinate_IsOutsideInsteadOfCellZero()
        {
            bool inside = ElementFieldCoordinates.TryWorldToCell(
                new Vector3(-0.01f, 0.1f, 0.1f),
                Vector3.zero,
                0.25f,
                new Vector3Int(4, 4, 4),
                out Vector3Int cell);

            Assert.That(inside, Is.False);
            Assert.That(cell.x, Is.EqualTo(-1));
        }

        [Test]
        public void WorldToCell_ExactUpperBoundary_IsOutside()
        {
            bool inside = ElementFieldCoordinates.TryWorldToCell(
                new Vector3(1f, 0.1f, 0.1f),
                Vector3.zero,
                0.25f,
                new Vector3Int(4, 4, 4),
                out Vector3Int cell);

            Assert.That(inside, Is.False);
            Assert.That(cell.x, Is.EqualTo(4));
        }

        [Test]
        public void WorldToCellUnchecked_PreservesOutOfBoundsCoordinateForLaterClamp()
        {
            Vector3Int cell = ElementFieldCoordinates.WorldToCellUnchecked(
                new Vector3(-0.01f, 0.1f, 1.01f),
                Vector3.zero,
                0.25f);

            Assert.That(cell, Is.EqualTo(new Vector3Int(-1, 0, 4)));
        }

        [Test]
        public void WorldToCell_NonPositiveCellSize_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ElementFieldCoordinates.WorldToCellUnchecked(
                    Vector3.zero,
                    Vector3.zero,
                    0f));
        }

        [Test]
        public void IndexRoundTrip_ReturnsOriginalCoordinate()
        {
            var size = new Vector3Int(32, 16, 32);
            var coordinate = new Vector3Int(7, 5, 11);

            int index = ElementFieldCoordinates.ToIndex(coordinate, size);

            Assert.That(
                ElementFieldCoordinates.FromIndex(index, size),
                Is.EqualTo(coordinate));
        }

        [Test]
        public void ToIndex_UsesXFastestContiguousLayout()
        {
            var size = new Vector3Int(4, 3, 2);

            Assert.That(ElementFieldCoordinates.ToIndex(new Vector3Int(0, 0, 0), size), Is.EqualTo(0));
            Assert.That(ElementFieldCoordinates.ToIndex(new Vector3Int(1, 0, 0), size), Is.EqualTo(1));
            Assert.That(ElementFieldCoordinates.ToIndex(new Vector3Int(0, 1, 0), size), Is.EqualTo(4));
            Assert.That(ElementFieldCoordinates.ToIndex(new Vector3Int(0, 0, 1), size), Is.EqualTo(12));
        }

        [Test]
        public void IndexConversion_OutOfBounds_Throws()
        {
            var size = new Vector3Int(4, 3, 2);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ElementFieldCoordinates.ToIndex(new Vector3Int(-1, 0, 0), size));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ElementFieldCoordinates.FromIndex(24, size));
        }
    }
}
