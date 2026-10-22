using System;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// Spatial Hash 的 CPU Pure Contract。它不替代 GPU Dispatch，而是把 C#/HLSL 共享的
    /// cell、hash 与 Bitonic pass 规则锁成可在没有 Graphics Device 时执行的测试。
    /// </summary>
    public sealed class FluidSpatialHashTests
    {
        [Test]
        public void PositionToCellUsesFloorForNegativeCoordinates()
        {
            Vector3Int cell = FluidSpatialHash.PositionToCell(
                new Vector3(-0.01f, -1.01f, 1.99f),
                smoothingRadius: 1f);

            Assert.That(cell, Is.EqualTo(new Vector3Int(-1, -2, 1)));
        }

        [Test]
        public void PositionToCellRejectsNonPositiveSmoothingRadius()
        {
            Assert.That(
                () => FluidSpatialHash.PositionToCell(Vector3.zero, smoothingRadius: 0f),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void HashUsesMaskForPowerOfTwoTableAndKeepsSameCellInSameBucket()
        {
            Vector3Int cell = new Vector3Int(-3, 5, -7);
            uint first = FluidSpatialHash.HashCell(cell, hashTableCapacity: 16);
            uint second = FluidSpatialHash.HashCell(cell, hashTableCapacity: 16);

            Assert.That(first, Is.EqualTo(second));
            Assert.That(first, Is.LessThan(16u));
        }

        [Test]
        public void NeighborOffsetsCoverExactlyTwentySevenUniqueCells()
        {
            var uniqueOffsets = new System.Collections.Generic.HashSet<Vector3Int>();
            for (int i = 0; i < FluidSpatialHash.NeighborOffsetCount; i++)
            {
                Vector3Int offset = FluidSpatialHash.GetNeighborOffset(i);
                Assert.That(offset.x, Is.InRange(-1, 1));
                Assert.That(offset.y, Is.InRange(-1, 1));
                Assert.That(offset.z, Is.InRange(-1, 1));
                Assert.That(uniqueOffsets.Add(offset), Is.True, $"Duplicate neighbor offset at {i}.");
            }

            Assert.That(uniqueOffsets.Count, Is.EqualTo(27));
            Assert.That(uniqueOffsets.Contains(Vector3Int.zero), Is.True);
        }

        [Test]
        public void InactiveEntriesSortAfterEveryActiveEntryLexicographically()
        {
            var active = new FluidGpuUInt2(3u, 99u);
            var inactive = new FluidGpuUInt2(FluidSpatialHash.InactiveBucket, 0u);
            var laterSameBucket = new FluidGpuUInt2(3u, 100u);

            Assert.That(FluidSpatialHash.CompareEntries(active, inactive), Is.LessThan(0));
            Assert.That(FluidSpatialHash.CompareEntries(active, laterSameBucket), Is.LessThan(0));
            Assert.That(FluidSpatialHash.CompareEntries(inactive, active), Is.GreaterThan(0));
        }

        [Test]
        public void HashCollisionIsRejectedByRecheckingCandidateRealCell()
        {
            const float smoothingRadius = 1f;
            const int hashTableCapacity = 2;
            Vector3Int targetCell = Vector3Int.zero;
            Vector3Int collidingCell = new Vector3Int(2, 0, 0);

            Assert.That(
                FluidSpatialHash.HashCell(targetCell, hashTableCapacity),
                Is.EqualTo(FluidSpatialHash.HashCell(collidingCell, hashTableCapacity)),
                "Fixture must exercise a hash collision, not particle overlap.");

            Assert.That(
                FluidSpatialHash.IsCandidateInCell(new Vector3(0.2f, 0.2f, 0.2f), targetCell, smoothingRadius),
                Is.True);
            Assert.That(
                FluidSpatialHash.IsCandidateInCell(new Vector3(2.2f, 0.2f, 0.2f), targetCell, smoothingRadius),
                Is.False,
                "A matching bucket is only a candidate list; PBF must compare the real floored cell.");
        }

        [Test]
        public void BitonicPassSequenceForCapacityEightIsCompleteAndOrdered()
        {
            Assert.That(FluidSpatialHash.GetBitonicPassCount(particleCapacity: 8), Is.EqualTo(6));

            var expected = new[]
            {
                new FluidBitonicPass(2, 1),
                new FluidBitonicPass(4, 2),
                new FluidBitonicPass(4, 1),
                new FluidBitonicPass(8, 4),
                new FluidBitonicPass(8, 2),
                new FluidBitonicPass(8, 1)
            };

            for (int i = 0; i < expected.Length; i++)
            {
                Assert.That(
                    FluidSpatialHash.TryGetBitonicPass(8, i, out FluidBitonicPass actual),
                    Is.True);
                Assert.That(actual, Is.EqualTo(expected[i]));
                Assert.That(FluidSpatialHash.IsValidBitonicPass(8, in actual), Is.True);
            }

            Assert.That(FluidSpatialHash.TryGetBitonicPass(8, expected.Length, out _), Is.False);
        }

        [Test]
        public void BitonicPassPlanningRejectsNonPowerOfTwoParticleCapacity()
        {
            Assert.That(
                () => FluidSpatialHash.GetBitonicPassCount(particleCapacity: 6),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void BitonicPassPlanningTreatsOneSlotCapacityAsAlreadySorted()
        {
            Assert.That(FluidSpatialHash.GetBitonicPassCount(particleCapacity: 1), Is.Zero);
            Assert.That(FluidSpatialHash.TryGetBitonicPass(1, 0, out _), Is.False);
        }
    }
}
