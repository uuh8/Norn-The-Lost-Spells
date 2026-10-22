using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class FluidChunkRestorePlannerTests
    {
        [Test]
        public void ExpandArchiveRecord_PreservesParticleCountAndIsDeterministic()
        {
            var record = new FluidArchiveCellRecord(
                new ElementChunkKey(1, -1, 2), 3, MaterialId.Water, 24u, Vector3.right);
            var first = new FluidGpuRestoreParticle[3];
            var second = new FluidGpuRestoreParticle[3];

            Assert.That(FluidChunkRestorePlanner.Expand(
                in record, 8u, .25f, 7u, first, 0), Is.EqualTo(3));
            Assert.That(FluidChunkRestorePlanner.Expand(
                in record, 8u, .25f, 7u, second, 0), Is.EqualTo(3));
            CollectionAssert.AreEqual(first, second);
            Assert.That(first[0].Velocity, Is.EqualTo(Vector3.right));
            Assert.That(first[0].MaterialId, Is.EqualTo((uint)MaterialId.Water));
        }

        [Test]
        public void Expand_RejectsFractionalParticleAndLeavesDestinationUntouched()
        {
            var record = new FluidArchiveCellRecord(
                default, 0, MaterialId.Poison, 10u, Vector3.zero);
            var destination = new[]
            {
                new FluidGpuRestoreParticle(Vector3.one, MaterialId.Water, Vector3.up, 9u)
            };

            Assert.That(FluidChunkRestorePlanner.Expand(
                in record, 8u, .25f, 7u, destination, 0), Is.EqualTo(-1));
            Assert.That(destination[0].Position, Is.EqualTo(Vector3.one));
            Assert.That(destination[0].TransactionId, Is.EqualTo(9u));
        }

        [Test]
        public void Expand_FullOverloadUsesWorldOriginChunkSizeAndTransaction()
        {
            var record = new FluidArchiveCellRecord(
                new ElementChunkKey(-1, 0, 1), 0, MaterialId.Sticky, 8u, Vector3.zero);
            var destination = new FluidGpuRestoreParticle[1];

            Assert.That(FluidChunkRestorePlanner.Expand(
                in record, 8u, new Vector3(10f, 2f, -4f), .25f, 4,
                3u, 12u, destination, 0), Is.EqualTo(1));

            Assert.That(destination[0].TransactionId, Is.EqualTo(12u));
            Assert.That(destination[0].Position.x, Is.InRange(9f, 9.25f));
            Assert.That(destination[0].Position.y, Is.InRange(2f, 2.25f));
            Assert.That(destination[0].Position.z, Is.InRange(-3f, -2.75f));
        }

        [Test]
        public void RestoreContractsRemainThirtyTwoAndSixteenBytes()
        {
            Assert.That(System.Runtime.InteropServices.Marshal.SizeOf<FluidGpuRestoreParticle>(),
                Is.EqualTo(FluidGpuRestoreParticle.Stride));
            Assert.That(System.Runtime.InteropServices.Marshal.SizeOf<FluidGpuTransferStatus>(),
                Is.EqualTo(FluidGpuTransferStatus.Stride));
        }

        [Test]
        public void ExpandRange_MatchesSameSliceOfFullExpansion()
        {
            var record = new FluidArchiveCellRecord(
                new ElementChunkKey(1, 0, -1), 5, MaterialId.Water, 40u, Vector3.forward);
            var full = new FluidGpuRestoreParticle[5];
            var slice = new FluidGpuRestoreParticle[2];
            Assert.That(FluidChunkRestorePlanner.Expand(
                in record, 8u, Vector3.one, .25f, 8, 9u, 17u, full, 0), Is.EqualTo(5));

            Assert.That(FluidChunkRestorePlanner.ExpandRange(
                in record, 8u, Vector3.one, .25f, 8, 9u, 17u,
                sourceParticleOffset: 2, maximumParticleCount: 2, slice, 0), Is.EqualTo(2));

            Assert.That(slice[0], Is.EqualTo(full[2]));
            Assert.That(slice[1], Is.EqualTo(full[3]));
        }
    }
}
