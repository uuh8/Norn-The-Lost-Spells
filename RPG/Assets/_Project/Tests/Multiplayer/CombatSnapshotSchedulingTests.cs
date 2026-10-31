using System.Reflection;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.Networking;
using Mirror;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 驱动真实 Combat.Pump 并观察 NewFact 的序号推进，不另写一份发送调度逻辑。
    /// 夹具只打开 CanAdvance 的真实权限门；没有端口、连接或 Wire 发送，网络生命周期仍由主控串行执行。
    /// </summary>
    public sealed class CombatSnapshotSchedulingTests
    {
        private GameObject _worldRoot, _networkRoot;
        private OnlineWorldBootstrap _world;
        private OnlineCombatWorld _combat;
        private bool _previousServerActive;
        private static readonly PropertyInfo ServerActive = typeof(NetworkServer).GetProperty("active", BindingFlags.Static | BindingFlags.Public);
        private static readonly FieldInfo FactSequence = typeof(OnlineCombatWorld).GetField("_sequence", BindingFlags.Instance | BindingFlags.NonPublic);

        [SetUp]
        public void SetUp()
        {
            _previousServerActive = NetworkServer.active;
            Assert.That(_previousServerActive, Is.False, "隔离调度回归不能抢占已经运行的 Server。");
            try
            {
                var profile = Resources.Load<OnlineMovementProfile>("Online/MovementProfile");
                var definition = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
                Assert.That(profile, Is.Not.Null); Assert.That(definition, Is.Not.Null);
                Assert.That(FactSequence, Is.Not.Null);
                _worldRoot = new GameObject("N4 combat snapshot scheduling fixture"); _worldRoot.SetActive(false);
                _networkRoot = new GameObject("N4 inactive combat snapshot channel"); _networkRoot.SetActive(false);
                _world = _worldRoot.AddComponent<OnlineWorldBootstrap>();
                var previousManager = NetworkManager.singleton;
                var manager = _networkRoot.AddComponent<RoomNetworkManager>();
                var channel = _networkRoot.AddComponent<CombatNetworkChannel>();
                Assert.That(NetworkManager.singleton, Is.SameAs(previousManager));
                // inactive Owner 不运行 Manager.Awake；只补齐其中的空 Session 状态，不能调用 base.Awake 抢单例。
                // ActorConnection 会读取这些席位来判断是否存在接收者；所有 ConnectionId 保持 -1，不建立连接。
                var sessions = (ConnectionSession[])typeof(RoomNetworkManager)
                    .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
                for (int i = 0; i < sessions.Length; i++) sessions[i] = new ConnectionSession(10, 4);
                var simulation = new CombatSimulationWorld();
                var registry = new NetworkActorRegistry(profile, _worldRoot.transform, definition, simulation);
                // 恰好一个有 Health 的生产 Actor，无 Cast/连接/输出：每次 State 批次恰好生成一份真实 Kind7。
                // 因此 NewFact 序号增长就是批次数，不会把 OwnerSetup、出生或每个 Actor 的包数误算成 Hz。
                registry.EnsurePlayer(0, PlayerExecutionMode.Authority);
                Set(_world, "<Registry>k__BackingField", registry);
                Set(_world, "<IsAuthority>k__BackingField", true);
                Set(_world, "<WorldReady>k__BackingField", true);
                Set(_world, "_manager", manager);
                _combat = new OnlineCombatWorld(_world, definition, simulation, channel, manager, null);
                Set(_world, "_combat", _combat);
                manager.SetAuthorityReady(true);
                ServerActive.GetSetMethod(true).Invoke(null, new object[] { true });
                Assert.That(_world.CanAdvance, Is.True, "Pump 必须真正进入权威生成路径，不能把早退误当调度结果。");
            }
            catch
            {
                Cleanup();
                throw;
            }
        }

        [TearDown]
        public void Cleanup()
        {
            // 即使断言或 Pump 抛异常，也先恢复 Mirror 静态门，避免后面的测试误认为有 Server 正在运行。
            ServerActive.GetSetMethod(true).Invoke(null, new object[] { _previousServerActive });
            if (_world != null) Set(_world, "_stopped", true);
            if (_worldRoot != null) Object.DestroyImmediate(_worldRoot);
            _combat?.Dispose(); _combat = null;
            if (_networkRoot != null) Object.DestroyImmediate(_networkRoot);
            _world = null; _worldRoot = _networkRoot = null;
        }

        [TestCase(50)]
        [TestCase(60)]
        [TestCase(144)]
        public void RealPump_StableRenderFramesPreserveFifteenHz(int renderFps)
        {
            int batches = 0;
            // 覆盖 [0,20) 秒。首帧可立即生成；允许一个边界样本，不能把 50FPS 下 12.5Hz 当作 15Hz。
            for (int frame = 0; frame < renderFps * 20; frame++) batches += PumpAndCount(frame / (double)renderFps);
            Assert.That(batches, Is.EqualTo(300).Within(1),
                "真实 Combat.Pump 应保留晚帧余数，使资源/HP/弹体状态长期保持声明的 15Hz。");
        }

        [Test]
        public void RealPump_HitchKeepsDeadlinePhaseAndNeverGeneratesCatchupBurst()
        {
            Assert.That(PumpAndCount(0), Is.EqualTo(1));
            // 跳过许多周期只生成最新状态；同一时刻重复 Pump 也没有第二份资格。
            Assert.That(PumpAndCount(1.03), Is.EqualTo(1));
            Assert.That(PumpAndCount(1.03), Is.Zero);
            Assert.That(PumpAndCount(1.066666666666667), Is.EqualTo(1),
                "恢复后仍沿原 15Hz deadline，不应从 1.03 秒重新等待完整周期。");
            Assert.That(PumpAndCount(1.066666666666667), Is.Zero);
        }

        private int PumpAndCount(double now)
        {
            uint before = (uint)FactSequence.GetValue(_combat);
            _combat.Pump(now);
            uint delta = (uint)FactSequence.GetValue(_combat) - before;
            Assert.That(delta, Is.LessThanOrEqualTo(1u), "一帧最多生成一批当前状态，不补发并不存在的历史快照。");
            return (int)delta;
        }

        private static void Set(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }
}
