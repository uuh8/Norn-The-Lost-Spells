using Game.Materials;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class FluidChunkArchiveBuilderTests
    {
        [Test]
        public void Rebuild_SameCellKeepsMaterialsSeparateAndPreservesAmount()
        {
            var samples = new NativeArray<FluidGpuArchiveSample>(3, Allocator.Temp);
            try
            {
                samples[0] = Sample(new Vector3(.1f, .1f, .1f), MaterialId.Water, new Vector3(2f, 0f, 0f));
                samples[1] = Sample(new Vector3(.2f, .1f, .1f), MaterialId.Water, new Vector3(4f, 0f, 0f));
                samples[2] = Sample(new Vector3(.1f, .1f, .1f), MaterialId.Poison, Vector3.zero);
                var builder = new FluidChunkArchiveBuilder(8);

                Assert.That(builder.TryRebuild(samples, Context()), Is.True);
                Assert.That(builder.TryGet(new ElementChunkKey(0, 0, 0), 0, MaterialId.Water, out var water), Is.True);
                Assert.That(water.Amount, Is.EqualTo(16u));
                Assert.That(water.AverageVelocity.x, Is.EqualTo(3f).Within(.0001f));
                Assert.That(builder.TryGet(new ElementChunkKey(0, 0, 0), 0, MaterialId.Poison, out var poison), Is.True);
                Assert.That(poison.Amount, Is.EqualTo(4u));
            }
            finally { samples.Dispose(); }
        }

        [Test]
        public void Rebuild_NegativePositionUsesFloorChunkAndLocalCell()
        {
            var samples = new NativeArray<FluidGpuArchiveSample>(1, Allocator.Temp);
            try
            {
                samples[0] = Sample(new Vector3(-.01f, .1f, .1f), MaterialId.Water, Vector3.zero);
                var builder = new FluidChunkArchiveBuilder(1);
                Assert.That(builder.TryRebuild(samples, Context()), Is.True);
                Assert.That(builder.TryGet(new ElementChunkKey(-1, 0, 0), 7, MaterialId.Water, out _), Is.True);
            }
            finally { samples.Dispose(); }
        }

        [Test]
        public void Rebuild_InvalidSamplePublishesNoPartialRecords()
        {
            var samples = new NativeArray<FluidGpuArchiveSample>(2, Allocator.Temp);
            try
            {
                samples[0] = Sample(Vector3.zero, MaterialId.Water, Vector3.zero);
                samples[1] = Sample(new Vector3(float.NaN, 0f, 0f), MaterialId.Water, Vector3.zero);
                var builder = new FluidChunkArchiveBuilder(2);
                Assert.That(builder.TryRebuild(samples, Context()), Is.False);
                Assert.That(builder.RecordCount, Is.Zero);
            }
            finally { samples.Dispose(); }
        }

        [Test]
        public void Rebuild_IgnoresAliveSamplesWithoutArchiveLock()
        {
            var samples = new NativeArray<FluidGpuArchiveSample>(2, Allocator.Temp);
            try
            {
                samples[0] = Sample(Vector3.zero, MaterialId.Water, Vector3.zero);
                samples[1] = new FluidGpuArchiveSample(
                    new Vector3(.2f, .1f, .1f), MaterialId.Water, Vector3.zero,
                    FluidGpuLayout.ActiveFlag);
                var builder = new FluidChunkArchiveBuilder(2);

                Assert.That(builder.TryRebuild(samples, Context()), Is.True);
                Assert.That(builder.TryGet(new ElementChunkKey(0, 0, 0), 0,
                    MaterialId.Water, out FluidArchiveCellRecord water), Is.True);
                Assert.That(water.Amount, Is.EqualTo(8u));
            }
            finally { samples.Dispose(); }
        }

        private static FluidGpuArchiveSample Sample(Vector3 p, MaterialId m, Vector3 v) =>
            new FluidGpuArchiveSample(p, m, v,
                FluidGpuLayout.AliveFlag | FluidActivityFlags.ArchiveLocked);
        private static FluidArchiveBuildContext Context() => new FluidArchiveBuildContext(
            Vector3.zero, 1f, 8, LiquidMaterialAmountScaleSnapshot.Create(new[]
            {
                new LiquidMaterialAmountScale(MaterialId.Water, 8u),
                new LiquidMaterialAmountScale(MaterialId.Poison, 4u),
            }));
    }
}
