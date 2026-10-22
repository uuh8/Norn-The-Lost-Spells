using kcp2k;
using NUnit.Framework;

namespace Game.Networking.Tests
{
    /// <summary>局部上游修正必须保留所有字节的合法性判断，不能为零分配放松输入校验。</summary>
    public sealed class KcpHeaderTests
    {
        [Test]
        public void EveryWireByteKeepsItsOriginalMeaning()
        {
            for (int i = 0; i <= byte.MaxValue; i++)
            {
                bool reliable = KcpHeader.ParseReliable((byte)i, out KcpHeaderReliable r);
                bool unreliable = KcpHeader.ParseUnreliable((byte)i, out KcpHeaderUnreliable u);
                Assert.That(reliable, Is.EqualTo(i >= 1 && i <= 3));
                Assert.That(unreliable, Is.EqualTo(i == 4 || i == 5));
                if (reliable) Assert.That((byte)r, Is.EqualTo((byte)i));
                if (unreliable) Assert.That((byte)u, Is.EqualTo((byte)i));
            }
        }

        [Test]
        public void ValidHeadersDoNotBoxOnTheHotPath()
        {
            for (int i = 0; i < 32; i++) { KcpHeader.ParseReliable(3, out _); KcpHeader.ParseUnreliable(4, out _); }
            using var meter = new CodecAllocationMeter();
            meter.Begin();
            for (int i = 0; i < 8; i++) { KcpHeader.ParseReliable(3, out _); KcpHeader.ParseUnreliable(4, out _); }
            long events = meter.End();
            Assert.That(meter.IsValid, Is.True);
            Assert.That(events, Is.Zero);
        }
    }
}
