using System;
using Mirror;
using NUnit.Framework;

namespace Game.Networking.Tests
{
    /// <summary>抖动可能让插值缓存超过预热时的数量；连接边界预留容量，不能运行中扩容。</summary>
    public sealed class MirrorTimeBufferTests
    {
        [Test]
        public void BatcherHandlesMultipleFrameTimestampsWithoutFirstQueueAllocation()
        {
            // 平常一个 Timestamp 直接 Flush 不会碰队列；抖动使多个 Timestamp 等待同一轮发送时才暴露懒分配。
            var payload = new ArraySegment<byte>(new byte[] { 42 });
            var warmup = new Batcher(1200);
            var writer = new NetworkWriter();
            for (int i = 0; i < 3; i++) warmup.AddMessage(payload, i + 1);
            Drain(warmup, writer);
            var batcher = new Batcher(1200);
            using var meter = new CodecAllocationMeter();
            meter.Begin();
            for (int i = 0; i < 3; i++) batcher.AddMessage(payload, i + 1);
            long events = meter.End();
            Assert.That(meter.IsValid, Is.True);
            Assert.That(events, Is.Zero, "首次使用批次队列不应在收包回调内分配 backing array。");
            Assert.That(Drain(batcher, writer), Is.EqualTo(3), "容量调整必须保持三个 Timestamp 的批次边界。");
        }

        private static int Drain(Batcher batcher, NetworkWriter writer)
        {
            int count = 0;
            while (true)
            {
                writer.Reset();
                if (!batcher.GetBatch(writer)) return count;
                count++;
            }
        }

        [Test]
        public void ServerTimeSnapshotBufferDoesNotGrowWhileReceivingBoundedBurst()
        {
            var warmup = new NetworkConnectionToClient(1);
            for (int i = 0; i < 32; i++) warmup.OnTimeSnapshot(new TimeSnapshot(i + 1, i + 1));
            var connection = new NetworkConnectionToClient(2);
            connection.OnTimeSnapshot(new TimeSnapshot(1, 1));
            connection.OnTimeSnapshot(new TimeSnapshot(2, 2));
            using var meter = new CodecAllocationMeter();
            meter.Begin();
            for (int i = 2; i < 32; i++) connection.OnTimeSnapshot(new TimeSnapshot(i + 1, i + 1));
            long events = meter.End();
            Assert.That(meter.IsValid, Is.True);
            Assert.That(events, Is.Zero, "默认 32 项上限内也不应出现 SortedList 扩容。");
        }
    }
}
