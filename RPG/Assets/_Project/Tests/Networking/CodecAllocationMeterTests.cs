using System;
using NUnit.Framework;

namespace Game.Networking.Tests
{
    /// <summary>计数器本身也要校准：若连明确的数组分配都读不到，不能用零值证明性能。</summary>
    public sealed class CodecAllocationMeterTests
    {
        [Test]
        public void NestedRecordingDoesNotAllocateIntoTheParentScope()
        {
            using var outer = new CodecAllocationMeter();
            using var inner = new CodecAllocationMeter();
            for (int i = 0; i < 32; i++) { inner.Begin(); inner.End(); }
            outer.Begin();
            for (int i = 0; i < 16; i++) { inner.Begin(); inner.End(); }
            long events = outer.End();
            Assert.That(outer.IsValid, Is.True);
            Assert.That(events, Is.Zero, "测量自身不能在外围范围产生持续分配。");
        }

        [Test]
        public void DetectsKnownAllocationAndEmptyScopeIsZero()
        {
            using var meter = new CodecAllocationMeter();
            meter.Begin();
            byte[] allocation = new byte[1024];
            GC.KeepAlive(allocation);
            long events = meter.End();
            Assert.That(meter.IsValid, Is.True);
            Assert.That(events, Is.GreaterThanOrEqualTo(1), "这里测事件数，不能与数组字节数混用。");
            meter.Begin();
            long empty = meter.End();
            Assert.That(empty, Is.Zero);
        }
    }
}
