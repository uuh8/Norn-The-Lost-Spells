using Game.Core;
using Game.Networking;
using NUnit.Framework;
using UnityEngine;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 以可重复网络故障驱动真正的播放器，而非只断言某个Lerp中点。
    /// 模拟30Hz权威快照与60Hz渲染，统计稳定窗口连续移动帧、播放滞后和欠载恢复；不等待真实20秒。
    /// RTT参数仅表示这里的注入配置，不是对公网或底层UDP的测量。
    /// </summary>
    public sealed class MovementPlaybackQualityTests
    {
        private static MovementStateFrame State(uint tick, uint sequence) => new MovementStateFrame
        {
            ActorId = 2, ConnectionEpoch = 1, SnapshotSequence = sequence, ServerTick = tick,
            Connected = true, Targetable = true,
            Movement = new PlayerMovementSnapshot
            {
                Position = new Vector3((tick - 30) / 30f * 5, 0, 0), Rotation = Quaternion.identity,
                IsGrounded = true, GroundNormal = Vector3.up
            }
        };

        [TestCase(0, 0, 0, 2f / 30f)]
        [TestCase(100, 20, 1, 2f / 30f)]
        [TestCase(200, 40, 3, 2f / 30f)]
        [TestCase(0, 0, 0, .05f)]
        [TestCase(100, 20, 1, .05f)]
        [TestCase(200, 40, 3, .05f)]
        public void ThirtyHzSnapshotsRenderContinuouslyWithBoundedDelay(int rtt, int jitter, int loss, float minimumDelay)
        {
            var playback = new RemoteMovementInterpolation();
            playback.Configure(1f / 30f, minimumDelay, .2f);
            var delivery = new MovementFaultQueue<MovementStateFrame>();
            delivery.Configure(rtt, jitter, loss, 2701);
            int measured = 0, moving = 0, animating = 0;
            float previous = 0, largestStep = 0;
            PlayerMovementSnapshot pose = default;
            for (int frame = 0; frame <= 1200; frame++)
            {
                double now = 5 + frame / 60d;
                if (frame % 2 == 0)
                {
                    uint tick = (uint)(30 + frame / 2);
                    delivery.Schedule(State(tick, (uint)(frame / 2 + 1)), now);
                }
                while (delivery.TryPop(now, out var state)) playback.TryAdd(state, now);
                playback.TrySample(now, minimumDelay, out pose);
                if (frame > 180)
                {
                    float delta = pose.Position.x - previous;
                    Assert.That(delta, Is.GreaterThanOrEqualTo(-.0001f), "正常直线移动不能因重缓冲倒退。");
                    measured++;
                    if (delta > .001f) moving++;
                    if (playback.PlanarSpeed > 4) animating++;
                    largestStep = Mathf.Max(largestStep, delta);
                }
                previous = pose.Position.x;
            }
            float movingRatio = moving / (float)measured;
            TestContext.Out.WriteLine($"Minimum={minimumDelay:F4}s; Injected={rtt}/{jitter}/{loss}; moving={movingRatio:P2}; animation={animating / (float)measured:P2}; largestStep={largestStep:F4}m; finalLag={100 - pose.Position.x:F4}m; underflows={playback.UnderflowCount}; buffer={playback.EffectiveDelay:F4}s");
            Assert.That(movingRatio, Is.GreaterThan(rtt == 0 ? .98f : .90f));
            Assert.That(animating / (float)measured, Is.GreaterThan(.95f));
            Assert.That(largestStep, Is.LessThan(rtt == 0 ? .12f : .8f));
            Assert.That(100 - pose.Position.x, Is.LessThan(rtt == 0 ? .7f : 2f));
            Assert.That(playback.BufferedCount, Is.InRange(1, 16));
        }

        [Test]
        public void SnapshotPlaybackHotPathHasPositivelyCalibratedZeroAllocationEvents()
        {
            var playback = new RemoteMovementInterpolation();
            playback.Configure(1f / 30f, 2f / 30f, .2f);
            var clock = new SnapshotSendClock();
            for (uint tick = 30; tick < 90; tick++)
            {
                double now = 5 + tick / 30d;
                clock.TrySend(now, 1f / 30f); playback.TryAdd(State(tick, tick), now);
                playback.TrySample(now + .01, 2f / 30f, out _);
            }
            using var meter = new CodecAllocationMeter(256);
            Assert.True(meter.IsValid, "必须由真实1KiB分配证明GC.Alloc记录器有效。");
            meter.Begin();
            for (uint tick = 90; tick < 1090; tick++)
            {
                double now = 5 + tick / 30d;
                clock.TrySend(now, 1f / 30f); playback.TryAdd(State(tick, tick), now);
                playback.TrySample(now + .01, 2f / 30f, out _);
            }
            long events = meter.End();
            Assert.That(events, Is.Zero, "仅覆盖发送调度与插值算法调用，不等于整场战斗或全帧零分配。");
            Assert.True(meter.IsValid);
        }
    }
}
