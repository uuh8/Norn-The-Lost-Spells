using System;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class FluidChunkStreamingPlannerTests
    {
        [TestCase(false, .5d)]
        [TestCase(true, .1d)]
        public void ArchiveRetryDelay_BackpressureUsesFasterBoundedCadence(
            bool hasPendingWrites, double expected)
        {
            Assert.That(FluidChunkStreamingPlanner.ArchiveRetryDelay(hasPendingWrites),
                Is.EqualTo(expected));
        }

        [Test]
        public void BuildWarmRegion_ExpandsActiveChunkBoxByPadding()
        {
            FluidChunkRegion region = FluidChunkStreamingPlanner.BuildWarmRegion(
                new ElementChunkKey(5, -2, 7), 2, 1, 1);

            Assert.That(region.Min, Is.EqualTo(new ElementChunkKey(2, -4, 4)));
            Assert.That(region.Max, Is.EqualTo(new ElementChunkKey(8, 0, 10)));
            Assert.That(region.Contains(new ElementChunkKey(8, 0, 10)), Is.True);
            Assert.That(region.Contains(new ElementChunkKey(9, 0, 10)), Is.False);
        }

        [Test]
        public void ToWorldBounds_MaxChunkAddsOneWholeChunk()
        {
            var region = new FluidChunkRegion(
                new ElementChunkKey(-1, 0, 1), new ElementChunkKey(0, 1, 2));
            Bounds bounds = region.ToWorldBounds(new Vector3(10f, 2f, -4f), .25f, 8);

            Assert.That(bounds.min, Is.EqualTo(new Vector3(8f, 2f, -2f)));
            Assert.That(bounds.max, Is.EqualTo(new Vector3(12f, 6f, 2f)));
        }

        [Test]
        public void ShouldStartArchive_PressureBypassesGraceButTransferSerializes()
        {
            Assert.That(FluidChunkStreamingPlanner.ShouldStartArchive(
                true, .1f, 2f, 400u, 512, false), Is.True);
            Assert.That(FluidChunkStreamingPlanner.ShouldStartArchive(
                true, 3f, 2f, 400u, 512, true), Is.False);
            Assert.That(FluidChunkStreamingPlanner.ShouldStartArchive(
                false, 0f, 2f, 400u, 512, false), Is.True,
                "容量压力不能依赖玩家恰好在这一帧跨 Chunk。");
        }

        [Test]
        public void Settings_RejectsCorruptSerializedValues()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new FluidChunkStreamingSettings(
                true, -1, 2f, 1, 1, 1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FluidChunkStreamingSettings(
                true, 1, float.NaN, 1, 1, 1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FluidChunkStreamingSettings(
                true, 1, 2f, 1, 1, 1, 0, true, 0));
        }

        [Test]
        public void LocalDormancy_RequiresPressureAndAtLeastOneSleepingCandidate()
        {
            Assert.That(FluidChunkStreamingPlanner.ShouldIncludeSleepingRetained(
                true, 511u, 512, true, 1u), Is.True);
            Assert.That(FluidChunkStreamingPlanner.ShouldIncludeSleepingRetained(
                true, 513u, 512, true, 1u), Is.False);
            Assert.That(FluidChunkStreamingPlanner.ShouldIncludeSleepingRetained(
                false, 0u, 512, true, 1u), Is.False);
            Assert.That(FluidChunkStreamingPlanner.ShouldIncludeSleepingRetained(
                true, 0u, 512, false, 1u), Is.False);
            Assert.That(FluidChunkStreamingPlanner.ShouldIncludeSleepingRetained(
                true, 0u, 512, true, 0u), Is.False,
                "没有 Sleeping 候选时不应付出整池 Readback 成本。");
        }
    }
}
