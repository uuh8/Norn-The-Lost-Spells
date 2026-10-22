using System.Runtime.InteropServices;
using NUnit.Framework;

namespace Game.Rendering.Tests
{
    public sealed class FireParcelGpuLayoutTests
    {
        [Test]
        public void CSharpGpuRecordsKeepTheirHlslStrideContract()
        {
            Assert.That(Marshal.SizeOf<FireParcelSpawnGpu>(), Is.EqualTo(32));
            Assert.That(Marshal.SizeOf<FireParcelStateGpu>(), Is.EqualTo(64));
        }

        [Test]
        public void RingCursorAlwaysStaysInsideCapacity()
        {
            Assert.That(FireParcelPoolMath.ResolveRingSlot(0u, 4), Is.EqualTo(0));
            Assert.That(FireParcelPoolMath.ResolveRingSlot(5u, 4), Is.EqualTo(1));
            Assert.That(FireParcelPoolMath.ResolveRingSlot(uint.MaxValue, 4), Is.InRange(0, 3));
        }
    }
}
