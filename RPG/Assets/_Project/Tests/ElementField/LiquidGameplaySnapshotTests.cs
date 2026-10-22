using Game.Materials;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class LiquidGameplaySnapshotTests
    {
        [Test]
        public void PackedLayout_UsesOneFloat4AndActiveMaterialPlusOneTag()
        {
            Assert.That(FluidGameplaySampleCodec.Stride, Is.EqualTo(16));
            Assert.That(FluidGameplaySampleCodec.EncodeTag(false, MaterialId.Water), Is.Zero);
            Assert.That(
                FluidGameplaySampleCodec.EncodeTag(true, MaterialId.Water),
                Is.EqualTo((float)((byte)MaterialId.Water + 1)));
        }

        [Test]
        public void Rebuild_UsesFloorForNegativeWorldCoordinates()
        {
            using var samples = Samples(
                new Vector4(-0.01f, 0.25f, 0.25f, WaterTag()));
            var snapshot = new LiquidGameplaySnapshot(maximumCellCount: 8);

            Assert.That(snapshot.TryRebuild(
                samples,
                Metadata(new Bounds(Vector3.zero, new Vector3(2f, 1f, 1f)))), Is.True);
            Assert.That(snapshot.TryGetAmount(
                new Vector3Int(-1, 0, 0), MaterialId.Water, out byte amount), Is.True);
            Assert.That(amount, Is.EqualTo(8));
        }

        [Test]
        public void Rebuild_MaxBoundsEdgeIsExclusive()
        {
            using var samples = Samples(
                new Vector4(0.9999f, 0.25f, 0.25f, WaterTag()),
                new Vector4(1f, 0.25f, 0.25f, WaterTag()));
            var snapshot = new LiquidGameplaySnapshot(maximumCellCount: 1);

            Assert.That(snapshot.TryRebuild(
                samples,
                Metadata(new Bounds(new Vector3(0.5f, 0.5f, 0.5f), Vector3.one))), Is.True);
            Assert.That(snapshot.TryGetAmount(
                Vector3Int.zero, MaterialId.Water, out byte amount), Is.True);
            Assert.That(amount, Is.EqualTo(8));
        }

        [Test]
        public void Rebuild_AccumulatesInFixedSlotOrderAndSaturatesAtByteMax()
        {
            var samples = new NativeArray<Vector4>(40, Allocator.Temp);
            try
            {
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = new Vector4(0.25f, 0.25f, 0.25f, WaterTag());
                var snapshot = new LiquidGameplaySnapshot(maximumCellCount: 1);

                Assert.That(snapshot.TryRebuild(
                    samples,
                    Metadata(new Bounds(new Vector3(0.5f, 0.5f, 0.5f), Vector3.one))), Is.True);
                Assert.That(snapshot.TryGetAmount(
                    Vector3Int.zero, MaterialId.Water, out byte amount), Is.True);
                Assert.That(amount, Is.EqualTo(byte.MaxValue));
            }
            finally
            {
                samples.Dispose();
            }
        }

        [Test]
        public void Rebuild_SameCellKeepsWaterAndPoisonWithIndependentGmuScales()
        {
            using var samples = Samples(
                new Vector4(0.25f, 0.25f, 0.25f, WaterTag()),
                new Vector4(0.25f, 0.25f, 0.25f, PoisonTag()),
                new Vector4(0.25f, 0.25f, 0.25f, PoisonTag()));
            var snapshot = new LiquidGameplaySnapshot(3);
            var scales = LiquidMaterialAmountScaleSnapshot.Create(new[]
            {
                new LiquidMaterialAmountScale(MaterialId.Water, 8u),
                new LiquidMaterialAmountScale(MaterialId.Poison, 4u),
            });

            Assert.That(snapshot.TryRebuild(samples, Metadata(UnitBounds(), scales: scales)), Is.True);
            Assert.That(snapshot.TryGetAmount(Vector3Int.zero, MaterialId.Water, out byte water), Is.True);
            Assert.That(snapshot.TryGetAmount(Vector3Int.zero, MaterialId.Poison, out byte poison), Is.True);
            Assert.That(water, Is.EqualTo(8));
            Assert.That(poison, Is.EqualTo(8));
        }

        [Test]
        public void CopyOccupiedCells_ReturnsOnlyRequestedStickyCellsWithoutAllocationContainer()
        {
            using var samples = Samples(
                new Vector4(0.25f, 0.25f, 0.25f, StickyTag()),
                new Vector4(1.25f, 0.25f, 0.25f, StickyTag()),
                new Vector4(0.25f, 0.25f, 0.25f, WaterTag()));
            var scales = LiquidMaterialAmountScaleSnapshot.Create(new[]
            {
                new LiquidMaterialAmountScale(MaterialId.Water, 8u),
                new LiquidMaterialAmountScale(MaterialId.Sticky, 4u),
            });
            var snapshot = new LiquidGameplaySnapshot(3);
            Assert.That(snapshot.TryRebuild(samples,
                Metadata(new Bounds(new Vector3(1f, .5f, .5f), new Vector3(2f, 1f, 1f)), scales: scales)), Is.True);
            var destination = new LiquidMaterialCellSample[4];

            int count = snapshot.CopyOccupiedCells(MaterialId.Sticky, destination);

            Assert.That(count, Is.EqualTo(2));
            Assert.That(destination[0].Amount + destination[1].Amount, Is.EqualTo(8));
        }

        [Test]
        public void AmountScaleSnapshotCopiesInputAndRejectsInvalidEntries()
        {
            var entries = new[] { new LiquidMaterialAmountScale(MaterialId.Water, 8u) };
            LiquidMaterialAmountScaleSnapshot snapshot = LiquidMaterialAmountScaleSnapshot.Create(entries);
            entries[0] = new LiquidMaterialAmountScale(MaterialId.Water, 99u);
            Assert.That(snapshot.TryGet(MaterialId.Water, out uint scale), Is.True);
            Assert.That(scale, Is.EqualTo(8u));
            Assert.That(snapshot.TryGet(MaterialId.Poison, out _), Is.False);
            Assert.That(
                () => LiquidMaterialAmountScaleSnapshot.Create(new[]
                {
                    new LiquidMaterialAmountScale(MaterialId.Empty, 1u),
                }),
                Throws.TypeOf<System.InvalidOperationException>());
        }

        [Test]
        public void AmountScaleSnapshot_AcceptsStickyAsConfiguredLiquid()
        {
            LiquidMaterialAmountScaleSnapshot snapshot =
                LiquidMaterialAmountScaleSnapshot.Create(new[]
                {
                    new LiquidMaterialAmountScale(MaterialId.Sticky, 4u),
                });

            Assert.That(snapshot.TryGet(MaterialId.Sticky, out uint scale), Is.True);
            Assert.That(scale, Is.EqualTo(4u));
        }

        [Test]
        public void RebuildAndCompositeQueryAllocateZeroManagedBytesAfterWarmup()
        {
            using var samples = Samples(
                new Vector4(0.25f, 0.25f, 0.25f, WaterTag()),
                new Vector4(0.25f, 0.25f, 0.25f, PoisonTag()));
            var snapshot = new LiquidGameplaySnapshot(2);
            var scales = LiquidMaterialAmountScaleSnapshot.Create(new[]
            {
                new LiquidMaterialAmountScale(MaterialId.Water, 8u),
                new LiquidMaterialAmountScale(MaterialId.Poison, 4u),
            });
            FluidGameplayReadbackMetadata metadata = Metadata(UnitBounds(), scales: scales);
            snapshot.TryRebuild(samples, in metadata);
            snapshot.TryGetAmount(Vector3Int.zero, MaterialId.Water, out _);

            long before = System.GC.GetAllocatedBytesForCurrentThread();
            bool rebuilt = snapshot.TryRebuild(samples, in metadata);
            bool queried = snapshot.TryGetAmount(Vector3Int.zero, MaterialId.Poison, out byte amount);
            long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(rebuilt && queried, Is.True);
            Assert.That(amount, Is.EqualTo(4));
            Assert.That(allocated, Is.Zero);
        }

        [Test]
        public void Rebuild_SkipsInactiveAndNonWaterSamples()
        {
            using var samples = Samples(
                new Vector4(0.25f, 0.25f, 0.25f, 0f),
                new Vector4(0.25f, 0.25f, 0.25f,
                    FluidGameplaySampleCodec.EncodeTag(true, MaterialId.Fire)));
            var snapshot = new LiquidGameplaySnapshot(maximumCellCount: 1);

            Assert.That(snapshot.TryRebuild(
                samples,
                Metadata(new Bounds(new Vector3(0.5f, 0.5f, 0.5f), Vector3.one))), Is.True);
            Assert.That(snapshot.TryGetAmount(
                Vector3Int.zero, MaterialId.Water, out byte amount), Is.True);
            Assert.That(amount, Is.Zero);
            Assert.That(snapshot.TryGetAmount(
                Vector3Int.zero, MaterialId.Fire, out _), Is.False);
        }

        [Test]
        public void Rebuild_SkipsNonFiniteParticlePositions()
        {
            using var samples = Samples(
                new Vector4(float.NaN, 0.25f, 0.25f, WaterTag()),
                new Vector4(float.PositiveInfinity, 0.25f, 0.25f, WaterTag()));
            var snapshot = new LiquidGameplaySnapshot(maximumCellCount: 1);

            Assert.That(snapshot.TryRebuild(
                samples,
                Metadata(new Bounds(new Vector3(0.5f, 0.5f, 0.5f), Vector3.one))), Is.True);
            Assert.That(snapshot.TryGetAmount(
                Vector3Int.zero, MaterialId.Water, out byte amount), Is.True);
            Assert.That(amount, Is.Zero);
        }

        [Test]
        public void Rebuild_AfterBoundsTranslationClearsOldCellsAndPublishesFrozenVersions()
        {
            using var first = Samples(new Vector4(0.25f, 0.25f, 0.25f, WaterTag()));
            using var second = Samples(new Vector4(4.25f, 0.25f, 0.25f, WaterTag()));
            var snapshot = new LiquidGameplaySnapshot(maximumCellCount: 1);
            snapshot.TryRebuild(first, Metadata(
                new Bounds(new Vector3(0.5f, 0.5f, 0.5f), Vector3.one), topology: 7u));

            Assert.That(snapshot.TryRebuild(second, Metadata(
                new Bounds(new Vector3(4.5f, 0.5f, 0.5f), Vector3.one), topology: 9u)), Is.True);
            Assert.That(snapshot.TryGetAmount(Vector3Int.zero, MaterialId.Water, out _), Is.False);
            Assert.That(snapshot.TryGetAmount(
                new Vector3Int(4, 0, 0), MaterialId.Water, out byte amount), Is.True);
            Assert.That(amount, Is.EqualTo(8));
            Assert.That(snapshot.TopologyVersion, Is.EqualTo(9u));
            Assert.That(snapshot.LayoutVersion, Is.EqualTo(FluidGpuLayout.LayoutVersion));
        }

        [Test]
        public void InvalidFrozenMetadataFailsWithoutReplacingPreviousSnapshot()
        {
            using var samples = Samples(new Vector4(0.25f, 0.25f, 0.25f, WaterTag()));
            var snapshot = new LiquidGameplaySnapshot(maximumCellCount: 1);
            Assert.That(snapshot.TryRebuild(samples, Metadata(
                new Bounds(new Vector3(0.5f, 0.5f, 0.5f), Vector3.one), topology: 3u)), Is.True);

            var invalid = new FluidGameplayReadbackMetadata(
                new Bounds(new Vector3(float.NaN, 0f, 0f), Vector3.one),
                new Vector3(float.PositiveInfinity, 0f, 0f),
                1f,
                64,
                WaterScales(),
                FluidGpuLayout.LayoutVersion,
                topologyVersion: 4u,
                snapshotVersion: 4u);
            Assert.That(FluidGameplayMetadataValidator.IsValid(in invalid), Is.False);
            Assert.That(snapshot.TryRebuild(samples, in invalid), Is.False);
            Assert.That(snapshot.TopologyVersion, Is.EqualTo(3u));
            Assert.That(snapshot.TryGetAmount(
                Vector3Int.zero, MaterialId.Water, out byte amount), Is.True);
            Assert.That(amount, Is.EqualTo(8));
        }

        private static NativeArray<Vector4> Samples(params Vector4[] values)
        {
            return new NativeArray<Vector4>(values, Allocator.Temp);
        }

        private static FluidGameplayReadbackMetadata Metadata(
            Bounds bounds,
            uint topology = 1u,
            LiquidMaterialAmountScaleSnapshot scales = null)
        {
            return new FluidGameplayReadbackMetadata(
                bounds,
                Vector3.zero,
                cellSize: 1f,
                particleCapacity: 64,
                scales ?? WaterScales(),
                FluidGpuLayout.LayoutVersion,
                topology,
                snapshotVersion: topology);
        }

        private static float WaterTag()
        {
            return FluidGameplaySampleCodec.EncodeTag(true, MaterialId.Water);
        }

        private static float PoisonTag()
        {
            return FluidGameplaySampleCodec.EncodeTag(true, MaterialId.Poison);
        }

        private static float StickyTag()
        {
            return FluidGameplaySampleCodec.EncodeTag(true, MaterialId.Sticky);
        }

        private static LiquidMaterialAmountScaleSnapshot WaterScales()
        {
            return LiquidMaterialAmountScaleSnapshot.Create(new[]
            {
                new LiquidMaterialAmountScale(MaterialId.Water, 8u),
            });
        }

        private static Bounds UnitBounds()
        {
            return new Bounds(new Vector3(0.5f, 0.5f, 0.5f), Vector3.one);
        }
    }
}
