using Game.Materials;
using NUnit.Framework;

namespace Game.ElementField.Tests
{
    public sealed class LiquidDensityLayerTests
    {
        [Test]
        public void LayersUseIndividualQuantityAndAdditionOrderDoesNotOwnSurface()
        {
            var c = new LiquidLayerComposer(new[] { MaterialId.Poison, MaterialId.Water }, new[] { .9f, 1f }, 510);
            c.Compose(new uint[] { 102, 306 });
            Assert.That(c.GetLayer(MaterialId.Water).BaseFill, Is.Zero);
            Assert.That(c.GetLayer(MaterialId.Poison).BaseFill, Is.EqualTo(.6f).Within(1e-6f));
            Assert.That(c.GetLayer(MaterialId.Poison).ThicknessFill, Is.EqualTo(.2f).Within(1e-6f));
        }

        [Test]
        public void EqualDensitySharesOneLayerWithQuantityWeights()
        {
            var c = new LiquidLayerComposer(new[] { MaterialId.Water, MaterialId.Poison }, new[] { 1f, 1f }, 510);
            c.Compose(new uint[] { 50, 150 });
            Assert.That(c.DensityGroupCount, Is.EqualTo(1));
            Assert.That(c.GetShare(MaterialId.Water), Is.EqualTo(.25f));
            Assert.That(c.GetShare(MaterialId.Poison), Is.EqualTo(.75f));
            Assert.That(c.GetLayer(MaterialId.Poison).ThicknessFill, Is.EqualTo(200f / 510f));
            c.Compose(new uint[] { 0, 0 });
            Assert.That(c.GetShare(MaterialId.Poison), Is.Zero);
        }

        [Test]
        public void ThreeLayersAndWarmQueriesKeepFixedMemory()
        {
            var c = new LiquidLayerComposer(new[] { MaterialId.Water, MaterialId.Poison, MaterialId.Sticky },
                new[] { 1f, .9f, 1.15f }, 510);
            uint[] q = { 102, 204, 204 }; c.Compose(q);
            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) c.Compose(q);
            Assert.That(System.GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
            Assert.That(c.GetLayer(MaterialId.Poison).BaseFill, Is.EqualTo(.6f).Within(1e-6f));
            Assert.That(c.GetLayer(MaterialId.Poison).TopFill, Is.EqualTo(1f).Within(1e-6f));
            Assert.Throws<System.ArgumentException>(() => new LiquidLayerComposer(
                new[] { MaterialId.Water }, new[] { float.NaN }, 510));
        }
    }
}
