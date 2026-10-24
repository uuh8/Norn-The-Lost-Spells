using System.Reflection;
using System.Text.RegularExpressions;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.Networking;
using Mirror;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 用真实投射物的定时 Trigger 在 BeforePhysics 中制造受控输出失败，观察 Driver 是否仍推进物理。
    /// Reflection 仅装配 inactive 世界与预置补步债，不替换 World.Step、Combat Tick 或 Physics.Simulate。
    /// </summary>
    public sealed class AuthorityTickFailureTests
    {
        [Test]
        public void OutputFaultDuringFirstCatchupTick_StopsPhysicsAndRemainingCatchupTicks()
        {
            Assert.That(NetworkServer.active, Is.False, "这个隔离测试不能抢占已运行的 Server。");
            var profile = Resources.Load<OnlineMovementProfile>("Online/MovementProfile");
            var definition = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
            Assert.That(profile, Is.Not.Null); Assert.That(definition, Is.Not.Null);
            var worldRoot = new GameObject("N4 tick fault fixture"); worldRoot.SetActive(false);
            var networkRoot = new GameObject("N4 inactive fault channel"); networkRoot.SetActive(false);
            var output = new GameObject("N4 rejected output");
            var parent = new GameObject("N4 triggering projectile"); parent.SetActive(false);
            var bodyRoot = new GameObject("N4 physics motion witness");
            bodyRoot.transform.position = new Vector3(31000f, 17000f, 23000f);
            var body = bodyRoot.AddComponent<Rigidbody>(); body.useGravity = false;
            body.linearVelocity = Vector3.right * 30f;
            Vector3 originalPosition = body.position;
            var previousMode = Physics.simulationMode;
            float previousFixedDelta = Time.fixedDeltaTime;
            var world = worldRoot.AddComponent<OnlineWorldBootstrap>();
            var driver = worldRoot.AddComponent<AuthorityTickDriver>();
            OnlineCombatWorld combat = null;
            var activeProperty = typeof(NetworkServer).GetProperty("active", BindingFlags.Static | BindingFlags.Public);
            try
            {
                var manager = networkRoot.AddComponent<RoomNetworkManager>();
                var channel = networkRoot.AddComponent<CombatNetworkChannel>();
                var simulation = new CombatSimulationWorld();
                var registry = new NetworkActorRegistry(profile, worldRoot.transform, definition, simulation);
                Set(world, "<Registry>k__BackingField", registry);
                Set(world, "<IsAuthority>k__BackingField", true);
                Set(world, "<WorldReady>k__BackingField", true);
                Set(world, "_manager", manager); Set(world, "_combatSimulation", simulation);
                combat = new OnlineCombatWorld(world, definition, simulation, channel, manager, null);
                Set(world, "_combat", combat); Set(world, "_driver", driver);
                manager.SetAuthorityReady(true);
                // 只打开真实 CanSimulate 的布尔门；不监听端口、不注册账号，也不替换网络单例。
                activeProperty.GetSetMethod(true).Invoke(null, new object[] { true });

                // Collider 是抽象基型，RequireComponent 不能替测试选择形状；显式提供与火球一致的球形碰撞体。
                parent.AddComponent<SphereCollider>();
                parent.AddComponent<Rigidbody>();
                var projectile = parent.AddComponent<Fireball>();
                projectile.ConfigureExecutionMode(CombatExecutionMode.Authority, simulation);
                projectile.ArmTimedTrigger(.001f);
                projectile.TimedTriggerElapsed += (point, direction) => EventBus<CombatOutputSpawnedEvent>.Publish(
                    new CombatOutputSpawnedEvent { ActorId = 1, CommandId = 1, Output = output, DefinitionPrefab = null });
                parent.SetActive(true);
                driver.Initialize(world, true);
                var clock = typeof(AuthorityTickDriver).GetField("_clock", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(driver);
                // 一帧至少三个待执行 Tick；第一个 Tick 内失败后，旧预算不能继续成为物理推进权限。
                Set(clock, "_accumulator", (double)FixedStepClock.DeltaTime * 3.1);
                Assert.That(world.CanAdvance, Is.True);
                LogAssert.Expect(LogType.Error, new Regex("Destroy may not be called from edit mode"));
                LogAssert.Expect(LogType.Error, new Regex("输出超出受控 Prefab 表或复制容量"));
                LogAssert.Expect(LogType.Error, new Regex("Destroy may not be called from edit mode"));
                typeof(AuthorityTickDriver).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(driver, null);

                Assert.That(combat.Faulted, Is.True, "必须实际走过生产输出拒绝路径，而不是预先冻结世界。");
                Assert.That(driver.Tick, Is.EqualTo(1), "失败后的同帧补步应立即停止。");
                Assert.That(body.position, Is.EqualTo(originalPosition), "BeforePhysics 失败后，这一 Tick 的 Physics 也不能推进。");
            }
            finally
            {
                driver.StopDriver();
                activeProperty.GetSetMethod(true).Invoke(null, new object[] { false });
                // 先销毁 fixture 后 Dispose，避免 EditMode 由运行时 Dispose 调用延迟 Destroy。
                Object.DestroyImmediate(worldRoot); combat?.Dispose();
                Object.DestroyImmediate(networkRoot); Object.DestroyImmediate(parent);
                Object.DestroyImmediate(output); Object.DestroyImmediate(bodyRoot);
                Physics.simulationMode = previousMode; Time.fixedDeltaTime = previousFixedDelta;
            }
        }

        private static void Set(object target, string field, object value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }
}
