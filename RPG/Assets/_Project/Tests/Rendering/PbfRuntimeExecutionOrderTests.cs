using System;
using Game.ElementField;
using NUnit.Framework;
using UnityEngine;

namespace Game.Rendering.Tests
{
    public sealed class PbfRuntimeExecutionOrderTests
    {
        [Test]
        public void PbfRuntimeInitializesBeforeWorldAndPresentationAdapters()
        {
            Assert.That(ReadOrder<GpuPbfFluidRuntime>(), Is.EqualTo(-200));
            Assert.That(ReadOrder<ElementWorldRuntime>(), Is.EqualTo(0));
            Assert.That(ReadOrder<FluidGameplayOccupancyBridge>(), Is.EqualTo(100));
            Assert.That(ReadOrder<GpuLiquidSurfaceRenderer>(), Is.EqualTo(200));
        }

        private static int ReadOrder<T>()
        {
            var attribute = Attribute.GetCustomAttribute(
                typeof(T),
                typeof(DefaultExecutionOrder)) as DefaultExecutionOrder;
            return attribute != null ? attribute.order : 0;
        }
    }
}
