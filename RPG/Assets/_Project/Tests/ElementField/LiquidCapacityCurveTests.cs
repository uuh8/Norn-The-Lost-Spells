using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class LiquidCapacityCurveTests
    {
        [Test]
        public void HeightfieldDistributesExactQuantityOnlyAboveEachColumnFloor()
        {
            var curve = new LiquidCapacityCurve(Vector3Int.zero,
                new Vector3Int(3, 3, 1), new[] { 0, 1, int.MaxValue }, 10);

            Assert.That(curve.Capacity, Is.EqualTo(50ul));
            Assert.That(curve.Contains(new Vector3Int(1, 0, 0)), Is.False);
            Assert.That(curve.Contains(new Vector3Int(2, 2, 0)), Is.False);
            Assert.That(curve.SurfaceHeightInCells(15), Is.EqualTo(1.25f).Within(.001f));
            ulong total = 0;
            for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
                total += curve.QuantityAt(new Vector3Int(x, y, 0), 15);
            Assert.That(total, Is.EqualTo(15ul));
            Assert.That(curve.QuantityAt(new Vector3Int(0, 0, 0), 15), Is.EqualTo(10u));
            Assert.That(curve.QuantityAt(new Vector3Int(1, 0, 0), 15), Is.Zero);
        }

        [Test]
        public void CrossingRaisedFloorChangesShorelineEvenWhenPatchCountStaysOne()
        {
            var curve = new LiquidCapacityCurve(Vector3Int.zero,
                new Vector3Int(2, 3, 1), new[] { 0, 1 }, 10);

            curve.RefreshSurfaceRuns(10);
            Assert.That(curve.FootprintRunCount, Is.EqualTo(1));
            Assert.That(curve.GetFootprintRun(0).MaximumX, Is.EqualTo(1));
            uint before = curve.SurfaceTopologyVersion;

            curve.RefreshSurfaceRuns(11);
            Assert.That(curve.FootprintRunCount, Is.EqualTo(1));
            Assert.That(curve.GetFootprintRun(0).MaximumX, Is.EqualTo(2));
            Assert.That(curve.SurfaceTopologyVersion, Is.GreaterThan(before));

            uint unchanged = curve.SurfaceTopologyVersion;
            curve.RefreshSurfaceRuns(30);
            Assert.That(curve.SurfaceTopologyVersion, Is.EqualTo(unchanged),
                "水位进入没有新岸线的层时不应重建相同拓扑。");
            curve.RefreshSurfaceRuns(0);
            Assert.That(curve.FootprintRunCount, Is.Zero);
        }
    }
}
