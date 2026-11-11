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
                LogAssert.Expect(LogType.Error, new Regex("输出超出受控 Prefab 表或复制容量"));
                LogAssert.Expect(LogType.Error, new Regex("Destroy may not be called from edit mode"));
                typeof(AuthorityTickDriver).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(driver, null);

                Assert.That(combat.Faulted, Is.True, "必须实际走过生产输出拒绝路径，而不是预先冻结世界。");
                Assert.That(output != null && output.activeSelf, Is.True, "已提交的新输出留在现场，不以销毁代替故障保护。");
                Assert.That(world.Frozen, Is.True);
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

        /// <summary>
        /// 仅在外部材料Owner边界注入同步Stop；Bootstrap、Combat、Driver与Physics仍是真实生产实现。
        /// 断言实际生命周期和异常，不以mock调用次数替代清理；不声称覆盖了真实Solver反应。
        /// </summary>
        private sealed class StoppingMaterialOwner : IOnlineMaterialWorldBinding, IWorldAuthoritySimulation
        {
            private readonly System.Action _stop;
            private bool _suspended;
            public bool Detached { get; private set; }
            public StoppingMaterialOwner(System.Action stop) { _stop = stop; }
            public bool TryConfigure(WorldExecutionMode mode, ulong epoch) => false;
            public IWorldAuthoritySimulation AuthoritySimulation => Detached ? null : this;
            public IWorldReplicationSource ReplicationSource => null;
            public WorldReplicaBuffer CaptureBuffer => null;
            public IWorldChunkPayloadInstaller ReplicaInstaller => null;
            public bool TrySetActor(int slot, ulong actor, Vector3 position) => !Detached;
            public void ClearActor(int slot) { }
            public void SetSuspended(bool suspended) { _suspended = suspended; }
            public bool IsReady => !Detached;
            public bool Faulted => false;
            public WorldExecutionMode ExecutionMode => WorldExecutionMode.Authority;
            public bool IsSimulationFaulted => false;
            public void StepAuthority(float dt, uint tick) { if (!_suspended && !Detached) _stop(); }
            public void Detach() { Detached = true; _suspended = true; }
        }

        [TestCase(false)][TestCase(true)]
        public void MaterialStepCanStopWorldSynchronouslyWithoutDereferencingReleasedOwners(bool diagnosticDriver)
        {
            Assert.That(NetworkServer.active, Is.False);
            var profile = Resources.Load<OnlineMovementProfile>("Online/MovementProfile");
            var definition = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
            var root = new GameObject("Inactive material stop composition"); root.SetActive(false);
            var network = new GameObject("Inactive material stop transport"); network.SetActive(false);
            var previousMode = Physics.simulationMode; float previousDelta = Time.fixedDeltaTime;
            var world = root.AddComponent<OnlineWorldBootstrap>();
            var driver = root.AddComponent<AuthorityTickDriver>();
            var serverActive = typeof(NetworkServer).GetProperty("active", BindingFlags.Static | BindingFlags.Public);
            try
            {
                var manager = network.AddComponent<RoomNetworkManager>();
                var channel = network.AddComponent<CombatNetworkChannel>();
                var simulation = new CombatSimulationWorld();
                var registry = new NetworkActorRegistry(profile, root.transform, definition, simulation);
                Set(world, "<Registry>k__BackingField", registry); Set(world, "<IsAuthority>k__BackingField", true);
                Set(world, "<WorldReady>k__BackingField", true); Set(world, "_manager", manager);
                Set(world, "_combatSimulation", simulation); Set(world, "_driver", driver);
                Set(world, "_combat", new OnlineCombatWorld(world, definition, simulation, channel, manager, null));
                var material = new StoppingMaterialOwner(world.StopWorld); Set(world, "_materialWorld", material);
                manager.SetAuthorityReady(true); serverActive.GetSetMethod(true).Invoke(null, new object[] { true });
                Assert.That(world.CanAdvance, Is.True);
                if (diagnosticDriver)
                {
                    driver.Initialize(world, true); driver.StartDiagnosticMeasurement();
                    var clock = typeof(AuthorityTickDriver).GetField("_clock", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(driver);
                    Set(clock, "_accumulator", (double)FixedStepClock.DeltaTime * 1.1);
                    Assert.DoesNotThrow(() => typeof(AuthorityTickDriver).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(driver, null));
                    Assert.That(driver.Tick, Is.EqualTo(1));
                }
                else
                {
                    simulation.BeginTick(1);
                    Assert.DoesNotThrow(() => world.AfterPhysics(FixedStepClock.DeltaTime, 1));
                }
                Assert.That(material.Detached, Is.True); Assert.That(world.WorldReady, Is.False);
                Assert.That(world.CanAdvance, Is.False); Assert.That(Physics.simulationMode, Is.EqualTo(previousMode));
            }
            finally
            {
                driver.StopDriver(); world.StopWorld();
                serverActive.GetSetMethod(true).Invoke(null, new object[] { false });
                Object.DestroyImmediate(root); Object.DestroyImmediate(network);
                Physics.simulationMode = previousMode; Time.fixedDeltaTime = previousDelta;
            }
        }

        /// <summary>材料Owner是受控边界，Capture使用真实CommitOwner容量失败，不操作正式场景/资产或网络端口。</summary>
        private sealed class ReplicationMaterialOwner : IOnlineMaterialWorldBinding, IWorldAuthoritySimulation, IWorldReplicationSource
        {
            internal bool Suspended, Detached;
            internal int Steps;
            internal readonly WorldReplicaBuffer Buffer = new WorldReplicaBuffer(2, 1, 1000);
            public bool TryConfigure(WorldExecutionMode mode, ulong epoch) => false;
            public IWorldAuthoritySimulation AuthoritySimulation => this;
            public IWorldReplicationSource ReplicationSource => this;
            public WorldReplicaBuffer CaptureBuffer => Buffer;
            public IWorldChunkPayloadInstaller ReplicaInstaller => null;
            public bool IsReady => !Detached;
            public bool Faulted => MaterialFaulted;
            internal bool MaterialFaulted;
            public WorldExecutionMode ExecutionMode => WorldExecutionMode.Authority;
            public bool IsSimulationFaulted => false;
            public bool TrySetActor(int slot, ulong actor, Vector3 position) => !Suspended;
            public void ClearActor(int slot) { }
            public void SetSuspended(bool suspended) { Suspended = suspended; }
            public void Detach() { Detached = true; }
            public void StepAuthority(float dt, uint tick) { if (!Suspended) Steps++; }
            public bool TryCapture(WorldReplicaBuffer target, out WorldCaptureFailure failure)
            {
                target.Geometries[0] = new WorldGridGeometry(WorldMaterialDomain.Liquid, Vector3.zero, 1, 16);
                target.Cells[0] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid, WorldMaterialStorage.Dynamic,
                    new Vector3Int(16, 0, 0), 2, 7);
                target.Header = new WorldReplicaHeader(Steps, 1, 0, 1);
                failure = default; return true;
            }
        }

        [TestCase(1, true, false)] [TestCase(2, false, false)] [TestCase(2, false, true)]
        public void ReplicationCapacityFaultPreservesWorldAndStopsNewStepsAcrossLeaseRenewal(int capacity, bool shouldFault, bool materialFault)
        {
            Assert.That(NetworkServer.active, Is.False, "不接管正在运行的Server。");
            var root = new GameObject("N5 inactive replication fault gate"); root.SetActive(false);
            var network = new GameObject("N5 inactive replication fault network"); network.SetActive(false);
            var active = typeof(NetworkServer).GetProperty("active", BindingFlags.Static | BindingFlags.Public);
            var world = root.AddComponent<OnlineWorldBootstrap>();
            var material = new ReplicationMaterialOwner();
            var coordinator = new WorldReplicationCoordinator(7, 99, capacity, 64, 2, 1, 1000);
            try
            {
                var profile = Resources.Load<OnlineMovementProfile>("Online/MovementProfile");
                var definition = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
                var manager = network.AddComponent<RoomNetworkManager>();
                var channel = network.AddComponent<CombatNetworkChannel>(); var simulation = new CombatSimulationWorld();
                var registry = new NetworkActorRegistry(profile, root.transform, definition, simulation);
                Set(world, "<Registry>k__BackingField", registry); Set(world, "<IsAuthority>k__BackingField", true);
                Set(world, "<WorldReady>k__BackingField", true); Set(world, "_manager", manager);
                Set(world, "_combatSimulation", simulation); Set(world, "_materialWorld", material); Set(world, "_materialReplication", coordinator);
                var combat = new OnlineCombatWorld(world, definition, simulation, channel, manager, null); Set(world, "_combat", combat);
                manager.SetAuthorityReady(true); active.GetSetMethod(true).Invoke(null, new object[] { true });
                Assert.That(world.CanAdvance, Is.True); Assert.That(world.Frozen, Is.False);
                if (shouldFault) LogAssert.Expect(LogType.Error, new Regex("材料复制.*暂停"));
                world.AfterPhysics(FixedStepClock.DeltaTime, 3);
                Assert.That(coordinator.Faulted, Is.EqualTo(shouldFault), "几何基址与非原点Cell实际占两个Key。");
                Assert.That(world.CanAdvance, Is.EqualTo(!shouldFault)); Assert.That(world.Frozen, Is.EqualTo(shouldFault));
                Assert.That(world.WorldReady, Is.True, "暂停不等于卸载或撤销当前世界。");
                Assert.That(material.Detached, Is.False); Assert.That(registry, Is.SameAs(world.Registry));
                Assert.That(typeof(OnlineWorldBootstrap).GetField("_materialReplication", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(world), Is.SameAs(coordinator));
                Assert.That(typeof(OnlineWorldBootstrap).GetField("_combat", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(world), Is.SameAs(combat));
                Assert.That(material.Suspended, Is.EqualTo(shouldFault), "故障当次同步挂起材料边界，不等下一次玩法Tick。");
                manager.SetFrozen(true); manager.SetFrozen(false);
                Assert.That(world.CanAdvance, Is.EqualTo(!shouldFault), "Heartbeat解除业务Frozen不能清掉复制故障。");
                int steps = material.Steps; world.AfterPhysics(FixedStepClock.DeltaTime, 6);
                Assert.That(material.Steps, Is.EqualTo(steps + (shouldFault ? 0 : 1)));
                Assert.That(coordinator.Captures, Is.EqualTo(shouldFault ? 0 : 2));
                if (!shouldFault)
                {
                    // 同一保护也覆盖输出复制故障：暂停事实必须广播，不能只停DS而让Client继续预测。
                    if (materialFault) material.MaterialFaulted = true;
                    else typeof(OnlineCombatWorld).GetProperty("Faulted").SetValue(combat, true);
                    Assert.That(world.CanAdvance, Is.False);
                    Assert.That(world.Frozen, Is.True, "复制故障的暂停必须进入既有Frozen表现合同。");
                    if (materialFault) LogAssert.Expect(LogType.Error, new Regex("材料复制.*已暂停"));
                    world.AfterPhysics(FixedStepClock.DeltaTime, 9);
                    Assert.That(material.Suspended, Is.True);
                    Assert.That(material.Steps, Is.EqualTo(steps + 1));
                    Assert.That(world.WorldReady, Is.True);
                    Assert.That(material.Detached, Is.False, "故障保留原材料现场，不能调用StopWorld/Detach。");
                }
            }
            finally
            {
                world.StopWorld(); active.GetSetMethod(true).Invoke(null, new object[] { false });
                Object.DestroyImmediate(root); Object.DestroyImmediate(network);
            }
        }

        [Test]
        public void MaterialStepExceptionPreservesCommittedQuantityAndWorldAndLatchesWritesAcrossHeartbeat()
            => AssertMaterialBoundaryFaultRetainsScene(MaterialBoundaryFault.CommitThenThrow);

        [Test]
        public void TargetableActorRejectedByMaterialBindingPreservesSceneAndLatchesWritesAcrossHeartbeat()
            => AssertMaterialBoundaryFaultRetainsScene(MaterialBoundaryFault.RejectActor);

        [Test]
        public void SuspensionCallbackExceptionLatchesWritesWithoutRetryingOrDestroyingTheMaterialOwner()
            => AssertMaterialBoundaryFaultRetainsScene(MaterialBoundaryFault.SuspendThenThrow);

        private enum MaterialBoundaryFault { CommitThenThrow, RejectActor, SuspendThenThrow }

        /// <summary>
        /// 只在 Core 材料 Owner 接口注入故障，世界/Combat/Registry 与复制 Capture 都走生产入口。
        /// GMU 是该 Owner 已提交事实的见证，不用反射伪造 Faulted，也不声称在这里验证真实 Solver 的原子性。
        /// </summary>
        private sealed class BoundaryFaultMaterialOwner : IOnlineMaterialWorldBinding, IWorldAuthoritySimulation, IWorldReplicationSource
        {
            internal readonly WorldReplicaBuffer Buffer = new WorldReplicaBuffer(2, 1, 1000);
            internal MaterialBoundaryFault Fault;
            internal bool Armed, Suspended, Detached;
            internal int Steps, Captures, ActorSyncs, SuspendCalls, BoundaryFailures;
            internal ulong CommittedGmu = 256;
            internal string ExceptionMessage => Fault == MaterialBoundaryFault.CommitThenThrow
                ? "Intentional exception after a committed material step" : "Intentional exception in material suspension callback";
            public bool TryConfigure(WorldExecutionMode mode, ulong epoch) => false;
            public IWorldAuthoritySimulation AuthoritySimulation => this;
            public IWorldReplicationSource ReplicationSource => this;
            public WorldReplicaBuffer CaptureBuffer => Buffer;
            public IWorldChunkPayloadInstaller ReplicaInstaller => null;
            public bool IsReady => !Detached;
            // 故障不能依靠 stub 自己报 Faulted 兜底；必须由真实组合层锁存异常/拒绝这个事实。
            public bool Faulted => false;
            public WorldExecutionMode ExecutionMode => WorldExecutionMode.Authority;
            public bool IsSimulationFaulted => false;
            public bool TrySetActor(int slot, ulong actor, Vector3 position)
            {
                ActorSyncs++;
                if (Armed && Fault == MaterialBoundaryFault.RejectActor) { BoundaryFailures++; return false; }
                return !Suspended && !Detached;
            }
            public void ClearActor(int slot) { }
            public void SetSuspended(bool suspended)
            {
                SuspendCalls++;
                if (Armed && Fault == MaterialBoundaryFault.SuspendThenThrow)
                { BoundaryFailures++; throw new System.InvalidOperationException(ExceptionMessage); }
                Suspended = suspended;
            }
            public void Detach() { Detached = true; }
            public void StepAuthority(float dt, uint tick)
            {
                if (Suspended || Detached) return;
                Steps++; CommittedGmu = checked(CommittedGmu + 7);
                if (Armed && Fault == MaterialBoundaryFault.CommitThenThrow)
                { BoundaryFailures++; throw new System.InvalidOperationException(ExceptionMessage); }
            }
            public bool TryCapture(WorldReplicaBuffer target, out WorldCaptureFailure failure)
            {
                Captures++;
                target.Geometries[0] = new WorldGridGeometry(WorldMaterialDomain.Liquid, Vector3.zero, 1, 16);
                target.Cells[0] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid, WorldMaterialStorage.Dynamic,
                    new Vector3Int(16, 0, 0), 2, 7);
                target.Header = new WorldReplicaHeader(Steps, 1, 0, 1);
                failure = default; return true;
            }
        }

        private static void AssertMaterialBoundaryFaultRetainsScene(MaterialBoundaryFault fault)
        {
            Assert.That(NetworkServer.active, Is.False, "不接管正在运行的 Transport/Server。");
            var root = new GameObject("N5 inactive material boundary fault"); root.SetActive(false);
            var network = new GameObject("N5 inactive material boundary network"); network.SetActive(false);
            var content = new GameObject("Committed world content witness"); content.transform.SetParent(root.transform, false);
            var world = root.AddComponent<OnlineWorldBootstrap>();
            var material = new BoundaryFaultMaterialOwner { Fault = fault };
            // 两个真实 chunk key，字数预算足够容纳完整页面；健康对照不能被容量错误抢先变成 RED。
            var coordinator = new WorldReplicationCoordinator(7, 99, 2, 1024, 2, 1, 1000);
            var active = typeof(NetworkServer).GetProperty("active", BindingFlags.Static | BindingFlags.Public);
            var previousMode = Physics.simulationMode; float previousDelta = Time.fixedDeltaTime;
            GameObject actorRoot = null;
            try
            {
                var profile = Resources.Load<OnlineMovementProfile>("Online/MovementProfile");
                var definition = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
                Assert.That(profile, Is.Not.Null); Assert.That(definition, Is.Not.Null);
                var manager = network.AddComponent<RoomNetworkManager>();
                var channel = network.AddComponent<CombatNetworkChannel>();
                var simulation = new CombatSimulationWorld();
                var registry = new NetworkActorRegistry(profile, root.transform, definition, simulation);
                Set(world, "<Registry>k__BackingField", registry); Set(world, "<IsAuthority>k__BackingField", true);
                Set(world, "<WorldReady>k__BackingField", true); Set(world, "_manager", manager);
                Set(world, "_combatSimulation", simulation); Set(world, "_materialWorld", material);
                Set(world, "_materialReplication", coordinator); Set(world, "_worldContent", content);
                var combat = new OnlineCombatWorld(world, definition, simulation, channel, manager, null); Set(world, "_combat", combat);
                simulation.ConfigureAuthorityWriteGate(() => world.CanAdvance);
                manager.SetAuthorityReady(true); active.GetSetMethod(true).Invoke(null, new object[] { true });
                if (fault == MaterialBoundaryFault.RejectActor)
                {
                    // 空 Roster 只会 ClearActor，不能证明 TrySetActor=false 分支。使用正式角色与真实资格。
                    var slot = registry.EnsurePlayer(0, PlayerExecutionMode.Authority);
                    Assert.That(slot.Health, Is.Not.Null); Set(slot.Health, "_currentHp", slot.Health.MaxHp);
                    slot.Connected = slot.SimulationReady = true; actorRoot = slot.Root;
                    Assert.That(slot.Targetable && slot.Body != null, Is.True);
                }
                Assert.That(world.CanAdvance, Is.True); Assert.That(world.Frozen, Is.False);
                world.AfterPhysics(FixedStepClock.DeltaTime, 3);
                Assert.That(material.Steps, Is.EqualTo(1)); Assert.That(material.Captures, Is.EqualTo(1));
                Assert.That(coordinator.Captures, Is.EqualTo(1)); Assert.That(coordinator.Faulted, Is.False);
                if (fault == MaterialBoundaryFault.RejectActor) Assert.That(material.ActorSyncs, Is.EqualTo(1));
                ulong committedBefore = material.CommittedGmu; int suspendBefore = material.SuspendCalls;
                material.Armed = true;
                LogAssert.Expect(LogType.Error, new Regex("世界同步故障，已暂停"));
                if (fault == MaterialBoundaryFault.RejectActor)
                    Assert.DoesNotThrow(() => world.AfterPhysics(FixedStepClock.DeltaTime, 6));
                else
                {
                    var error = Assert.Throws<System.InvalidOperationException>(() => world.AfterPhysics(FixedStepClock.DeltaTime, 6));
                    Assert.That(error.Message, Is.EqualTo(material.ExceptionMessage), "保护先锁存，再保留原异常，不以第二次挂起异常覆盖它。");
                }
                Assert.That(material.BoundaryFailures, Is.EqualTo(1), "实际进入指定边界，不能由另一处提前拒绝伪造通过。");
                Assert.That(material.CommittedGmu, Is.EqualTo(committedBefore + (fault == MaterialBoundaryFault.CommitThenThrow ? 7UL : 0UL)),
                    "部分提交是现存事实；暂停不能回滚、补偿或再次执行它。");
                Assert.That(world.CanAdvance, Is.False); Assert.That(world.Frozen, Is.True);
                Assert.That(world.WorldReady, Is.True); Assert.That(material.Detached, Is.False);
                Assert.That(world.Registry, Is.SameAs(registry)); Assert.That(world.Combat, Is.SameAs(combat));
                Assert.That(world.MaterialReplication, Is.SameAs(coordinator));
                Assert.That(typeof(OnlineWorldBootstrap).GetField("_materialWorld", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(world), Is.SameAs(material));
                Assert.That(content != null && content.activeSelf, Is.True, "已提交的世界模型保持原实例，不能经 StopWorld 销毁。");
                if (fault == MaterialBoundaryFault.RejectActor)
                    Assert.That(actorRoot != null && actorRoot.activeSelf, Is.True, "真实Actor不能被故障清理；Unity已销毁对象也必须使断言失败。");
                if (fault != MaterialBoundaryFault.SuspendThenThrow) Assert.That(material.Suspended, Is.True);
                int stoppedSteps = material.Steps, stoppedCaptures = material.Captures;
                manager.SetFrozen(true); manager.SetFrozen(false);
                Assert.That(world.CanAdvance, Is.False, "Heartbeat/业务Frozen恢复不能清掉本世界的同步故障。");
                Assert.That(world.Frozen, Is.True);
                Assert.DoesNotThrow(() => world.AfterPhysics(FixedStepClock.DeltaTime, 9));
                Assert.That(material.Steps, Is.EqualTo(stoppedSteps)); Assert.That(material.Captures, Is.EqualTo(stoppedCaptures));
                Assert.That(coordinator.Captures, Is.EqualTo(1)); Assert.That(material.BoundaryFailures, Is.EqualTo(1));
                if (fault == MaterialBoundaryFault.SuspendThenThrow)
                    Assert.That(material.SuspendCalls, Is.EqualTo(suspendBefore + 1), "失败的挂起回调不能每帧重试或在catch里递归重入。");
            }
            finally
            {
                // 所有现场保留断言结束后，EditMode夹具先立即释放自己创建的Actor。
                // 生产Registry.Dispose仍使用运行期Destroy；不能改生产规则或忽略其日志来替测试清场。
                if (actorRoot != null) Object.DestroyImmediate(actorRoot);
                world.StopWorld(); active.GetSetMethod(true).Invoke(null, new object[] { false });
                Object.DestroyImmediate(root); Object.DestroyImmediate(network);
                Physics.simulationMode = previousMode; Time.fixedDeltaTime = previousDelta;
            }
        }

        private static void Set(object target, string field, object value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }
}
