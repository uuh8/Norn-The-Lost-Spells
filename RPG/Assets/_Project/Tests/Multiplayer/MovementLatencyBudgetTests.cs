using Game.Networking;
using Mirror;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 固定本轮允许的调度与缓冲预算：收发主循环可以比玩法模拟更频繁，不能顺手把Physics/AI提到60Hz。
    /// 这些用例读取真实Profile、调用真实Manager配置和FixedStepClock，不把配置值冒充Native端到端延迟实测。
    /// </summary>
    public sealed class MovementLatencyBudgetTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void Profile_StablePlaybackKeepsSuccessorHistoryWithinFiftyMilliseconds(bool runtimeAsset)
        {
            var profile = runtimeAsset ? Resources.Load<OnlineMovementProfile>("Online/MovementProfile") :
                ScriptableObject.CreateInstance<OnlineMovementProfile>();
            Assert.That(profile, Is.Not.Null);
            try
            {
                // 30Hz至少保留一个后继快照周期；50ms目标比原两周期预算更低，但不取消插值历史。
                Assert.That(profile.InterpolationDelay, Is.InRange(1f / 30f, .05f));
                Assert.That(profile.MaximumInterpolationDelay, Is.EqualTo(.2f).Within(.000001f),
                    "降低稳定链路预算时，抖动恢复仍须保留200ms有界余量。");
                Assert.That(profile.SnapshotInterval, Is.EqualTo(1f / 30f).Within(.000001f),
                    "本轮只增加网络泵频率，运动快照仍按30Hz导出。");
            }
            finally
            {
                if (!runtimeAsset) Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void RoomConfiguration_UsesSixtyHzNetworkPumpWithoutStartingTransport()
        {
            Assert.That(NetworkServer.active, Is.False, "隔离配置用例不能接管已运行的Server。");
            Assert.That(NetworkClient.active, Is.False, "隔离配置用例不能接管已运行的Client。");
            var root = new GameObject("N4 inactive network latency budget"); root.SetActive(false);
            try
            {
                // inactive层级使Awake/OnEnable不启动网络生命周期；RequireComponent仍会装配真实KcpTransport。
                // Configure只写本机参数，不监听端口、不连接业务服，也不替换Mirror单例。
                var manager = root.AddComponent<RoomNetworkManager>();
                manager.Configure(17861);
                Assert.That(manager.sendRate, Is.EqualTo(60),
                    "Mirror用sendRate同时配置Server flush与Headless帧循环；30Hz会保留更粗的收发相位。");
                Assert.That(NetworkServer.active, Is.False);
                Assert.That(NetworkClient.active, Is.False);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void SixtyRenderUpdates_StillProduceThirtyAuthoritySimulationTicks()
        {
            var clock = new FixedStepClock();
            int authorityTicks = 0;
            // 使用Unity实际传入Driver的float帧间隔精度；60次1/60秒主循环只能积累30次1/30秒玩法步。
            // 若未来把FixedStepClock改成60Hz，本用例会实际得到60而失败，避免仅断言某个常量文字。
            for (int frame = 0; frame < 60; frame++) authorityTicks += clock.Advance(1f / 60f, true);
            Assert.That(authorityTicks, Is.EqualTo(30));
            Assert.That(clock.DroppedSteps, Is.Zero);
        }
    }
}
