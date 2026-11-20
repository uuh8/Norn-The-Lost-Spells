using System;
using Game.NetworkProtocol;
using NUnit.Framework;

namespace Game.Networking.Tests
{
    /// <summary>运动页使用独立语义，不能借用数量Revision或订阅控制绕过原数量校验。</summary>
    public sealed class WorldLiquidMotionPageCodecTests
    {
        private static WorldPageMessage Page(int words = 160) {
            var p = new WorldPageMessage { ProtocolVersion = ProtocolCompatibility.CurrentVersion,
                GameplayHash = 9, WorldEpoch = 7, ConnectionEpoch = 1, SnapshotId = 3,
                Kind = 8, Domain = 1, ServerTick = 5, Checksum = 1,
                TotalWords = (uint)words, PageCount = (uint)((words + 159) / 160) };
            for (int i = 0; i < Math.Min(words, 160); i++) p.Words.Add(uint.MaxValue);
            return p;
        }
        [Test] public void MotionRoundTripsWithoutOuterChunkIdentity() {
            var codec = new WorldPageCodec(); var b = new byte[900];
            Assert.True(codec.TryEncode(Page(), new ArraySegment<byte>(b), out int n));
            Assert.True(codec.TryDecode(new ArraySegment<byte>(b, 0, n), out var p));
            Assert.AreEqual(8, p.Kind); Assert.AreEqual(0, p.SubscriptionEpoch);
            Assert.AreEqual(160, p.Words.Count);
        }
        [Test] public void MaximumMotionFrameIsBoundedIndependentlyOfQuantity() {
            var codec = new WorldPageCodec(); var b = new byte[900];
            const int maxMotionWords=1607; // wire合同7+32*10+128*10；独立断言，不从实现反推期望。
            Assert.True(codec.TryEncode(Page(maxMotionWords), new ArraySegment<byte>(b), out _));
            Assert.False(codec.TryEncode(Page(maxMotionWords + 1), new ArraySegment<byte>(b), out _));
        }
        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)]
        public void MotionCannotSmuggleQuantityOrControlIdentity(int field) {
            var p = Page();
            switch(field) { case 0:p.Generation=1;break;case 1:p.Revision=1;break;
                case 2:p.BaseRevision=1;break;case 3:p.SubscriptionEpoch=1;break;
                case 4:p.ChunkX=1;break;case 5:p.Domain=0;break; }
            Assert.False(new WorldPageCodec().TryEncode(p, new ArraySegment<byte>(new byte[900]), out _));
        }
        [Test] public void QuantityStillRequiresItsOwnSubscriptionAndRevision() {
            var p = Page(); p.Kind=1;
            Assert.False(new WorldPageCodec().TryEncode(p,new ArraySegment<byte>(new byte[900]),out _));
        }
    }
}
