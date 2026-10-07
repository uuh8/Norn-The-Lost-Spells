using Game.Materials;
using NUnit.Framework;

namespace Game.ElementField.Tests
{
    public sealed class FluidMaterialAuditTests
    {
        [Test]
        public void SameEpoch_GpuAndRamExactlyExplainExpectedGmu()
        {
            var audit = new FluidMaterialAudit();
            audit.Begin(12, 7, 3);
            Assert.That(audit.TryAddExpected(MaterialId.Water, 64), Is.True);
            Assert.That(audit.TryAddGpu(MaterialId.Water, 32), Is.True);
            Assert.That(audit.TryAddRam(MaterialId.Water, 32), Is.True);

            FluidMaterialAuditSnapshot snapshot = audit.GetSnapshot(MaterialId.Water, 12, 7, 3);

            Assert.That(snapshot.Valid, Is.True);
            Assert.That(snapshot.IsBalanced, Is.True);
            Assert.That(snapshot.UnexplainedGmu, Is.Zero);
        }

        [Test]
        public void DifferentEpochOrMutationCannotBePresentedAsConservation()
        {
            var audit = new FluidMaterialAudit();
            audit.Begin(12, 7, 3);
            audit.TryAddExpected(MaterialId.Water, 64);
            audit.TryAddGpu(MaterialId.Water, 32);
            audit.TryAddRam(MaterialId.Water, 32);

            Assert.That(audit.GetSnapshot(MaterialId.Water, 13, 7, 3).Valid, Is.False);
            Assert.That(audit.GetSnapshot(MaterialId.Water, 12, 7, 4).Valid, Is.False);
        }

        [Test]
        public void OverflowInvalidatesAuditInsteadOfWrappingToBalanced()
        {
            var audit = new FluidMaterialAudit();
            audit.Begin(1, 1, 1);
            Assert.That(audit.TryAddExpected(MaterialId.Water, ulong.MaxValue), Is.True);
            Assert.That(audit.TryAddExpected(MaterialId.Water, 1), Is.False);

            FluidMaterialAuditSnapshot snapshot = audit.GetSnapshot(MaterialId.Water, 1, 1, 1);
            Assert.That(snapshot.Valid, Is.False);
        }

        [Test]
        public void ExplicitInvalidationCannotBeReportedAsBalanced()
        {
            var audit = new FluidMaterialAudit();
            audit.Begin(4, 9, 2);
            audit.TryAddExpected(MaterialId.Water, 8);
            audit.TryAddGpu(MaterialId.Water, 8);

            audit.Invalidate();

            Assert.That(audit.GetSnapshot(MaterialId.Water, 4, 9, 2).Valid, Is.False);
        }

        [Test]
        public void Freshness_RequiresLatestSubmittedWatermarkAndPostWriteRequest()
        {
            var audit = new FluidMaterialAudit();
            audit.Begin(8, 2, 3, 1024, 12.5, 12.7);
            audit.TryAddExpected(MaterialId.Water, 8192);
            audit.TryAddGpu(MaterialId.Water, 8192);
            FluidMaterialAuditSnapshot snapshot = audit.GetSnapshot(MaterialId.Water, 8, 2, 3);

            Assert.That(snapshot.IsFreshFor(1024, 12.4), Is.True);
            Assert.That(snapshot.IsFreshFor(1280, 12.4), Is.False);
            Assert.That(snapshot.IsFreshFor(1024, 12.6), Is.False);
        }
    }
}
