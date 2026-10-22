using NUnit.Framework;

namespace Game.ElementField.Tests
{
    public sealed class FluidPerformanceDiagnosticsTests
    {
        [Test]
        public void PoolObservation_DistinguishesMissingFromFull()
        {
            Assert.That(default(GpuFluidPoolDiagnostics).HasConsistentCapacity(8192), Is.False);
            var full = new GpuFluidPoolDiagnostics(true, 1, 8192, 0, 256, 10, 10.02);
            Assert.That(full.HasConsistentCapacity(8192), Is.True);
            Assert.That(full.Dropped, Is.EqualTo(256u));
        }

        [Test]
        public void PoolObservation_RejectsBrokenConservationWithoutIntegerWraparound()
        {
            var broken = new GpuFluidPoolDiagnostics(true, 1, uint.MaxValue, 2, 0, 0, 1);
            Assert.That(broken.HasConsistentCapacity(1), Is.False);
            Assert.That(broken.HasConsistentCapacity(uint.MaxValue), Is.False);
            Assert.That(new GpuFluidPoolDiagnostics(true, 1, 6, 2, 3, 0, 1)
                .HasConsistentCapacity(8), Is.True);
            Assert.That(new GpuFluidPoolDiagnostics(true, 1, 0, 0, 0, 0, 1)
                .HasConsistentCapacity(0), Is.False);
        }

        [Test]
        public void PoolObservation_RejectsSnapshotFromEarlierTransferMutation()
        {
            var beforeArchiveCommit = new GpuFluidPoolDiagnostics(
                true, 3, 8, 0, 0, 10, 10.02, 7);

            Assert.That(beforeArchiveCommit.IsFreshFor(7), Is.True);
            Assert.That(beforeArchiveCommit.IsFreshFor(8), Is.False,
                "Archive/Restore 改写 Free List 后，旧 Alive 不能与新的 RAM Archive 计数相加。");
        }

        [Test]
        public void FailedReadback_KeepsLastSuccessfulSnapshotAgeAndVersion()
        {
            var published = new FluidGameplayDiagnostics(true, 9, 10, 10.01, 10.02, 0);
            FluidGameplayDiagnostics failed = published.WithReadbackErrorCount(2);
            Assert.That(failed.Valid, Is.True);
            Assert.That(failed.SnapshotVersion, Is.EqualTo(9));
            Assert.That(failed.RequestedAt, Is.EqualTo(10));
            Assert.That(failed.CompletedAt, Is.EqualTo(10.01));
            Assert.That(failed.PublishedAt, Is.EqualTo(10.02));
            Assert.That(failed.ReadbackErrorCount, Is.EqualTo(2));
            Assert.That(default(FluidGameplayDiagnostics).WithReadbackErrorCount(1).Valid, Is.False);
        }
    }
}
