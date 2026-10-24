using System.Reflection;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 真实 Registry/Prefab/死亡事实/Physics Query 的行为回归。World 保持 inactive，Actor Parent 单独激活供查询；
    /// Reflection 仅连接现有事实入口和 ServerTick，不替换死亡/计时/Collider 逻辑。
    /// </summary>
    public sealed class OnlineCorpseLifecycleTests
    {
        private GameObject _worldRoot, _actorsRoot, _networkRoot;
        private OnlineWorldBootstrap _world;
        private OnlineCombatWorld _combat;
        private NetworkActorRegistry _registry;
        private OnlineCombatDefinition _definition;
        private uint _sequence;
        private float _prefabDeathDelay;
        private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly MethodInfo Receive = typeof(OnlineCombatWorld).GetMethod("OnFact", Flags);
        private readonly Vector3 _anchor = new Vector3(23000f, 3000f, 17000f);

        [SetUp]
        public void SetUp()
        {
            _sequence = 0;
            var profile = Resources.Load<OnlineMovementProfile>("Online/MovementProfile");
            _definition = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
            Assert.That(profile, Is.Not.Null); Assert.That(_definition, Is.Not.Null);
            var feedback = profile.PlayerPrefab.GetComponent<CharacterCombatFeedback>(); Assert.That(feedback, Is.Not.Null);
            _prefabDeathDelay = (float)typeof(CharacterCombatFeedback).GetField("_destroyDelay", Flags).GetValue(feedback);
            _worldRoot = new GameObject("N4 inactive corpse world"); _worldRoot.SetActive(false);
            _actorsRoot = new GameObject("N4 queryable corpse actors");
            _networkRoot = new GameObject("N4 inactive corpse channel"); _networkRoot.SetActive(false);
            _world = _worldRoot.AddComponent<OnlineWorldBootstrap>();
            var simulation = new CombatSimulationWorld();
            _registry = new NetworkActorRegistry(profile, _actorsRoot.transform, _definition, simulation);
            Set(_world, "<Registry>k__BackingField", _registry);
            Set(_world, "_stopped", true);
            var manager = _networkRoot.AddComponent<RoomNetworkManager>();
            var channel = _networkRoot.AddComponent<CombatNetworkChannel>();
            _combat = new OnlineCombatWorld(_world, _definition, simulation, channel, manager, null);
            SetTick(100);
        }

        [TearDown]
        public void TearDown()
        {
            // Replica 不创建本机 InputAction；先清 Actor 再 Dispose，防止 EditMode 走运行时延迟 Destroy。
            if (_actorsRoot != null) Object.DestroyImmediate(_actorsRoot);
            _combat?.Dispose(); _combat = null;
            if (_worldRoot != null) Object.DestroyImmediate(_worldRoot);
            if (_networkRoot != null) Object.DestroyImmediate(_networkRoot);
        }

        [Test]
        public void AuthorityLethalDamage_RetiresBodyAtPrefabDeadlineWithoutDeletingRunIdentity()
        {
            Set(_world, "<IsAuthority>k__BackingField", true);
            var slot = _registry.EnsurePlayer(0, PlayerExecutionMode.Authority);
            var health = slot.Health;
            typeof(HealthComponent).GetMethod("Awake", Flags).Invoke(health, null);
            _combat.RegisterActorHierarchy(slot, 1);
            Assert.That(slot.Body.GetComponent<CharacterController>().enabled, Is.True);
            health.ReceiveHit(new DamageRequest(99, 2, 10000, DamageType.True, slot.Body.position, Vector3.forward));
            Assert.That(health.IsAlive, Is.False);
            Assert.That(slot.Body.GetComponent<CharacterController>().enabled, Is.True,
                "单机原DestroyDelay内仍有尸体碰撞；不能提前引入不同的线上手感。");
            uint deadline = 100 + (uint)Mathf.CeilToInt(DeathDelay(slot) / FixedStepClock.DeltaTime);
            SetTick(deadline + 1); _combat.StepAuthority(FixedStepClock.DeltaTime, deadline + 1);
            AssertBodyRetired(slot);
            Assert.That(slot.Root != null && slot.Root.activeSelf && slot.Body != null, Is.True);
            Assert.That(slot.Health, Is.SameAs(health), "死亡不能抹掉同 Run 重连仍需要的身份/Health。");
        }

        [TestCase(7)]
        [TestCase(8)]
        [TestCase(9)]
        public void EachAuthoritativeDeathPath_RetiresReplicaAtOriginalPrefabDeadline(int kind)
        {
            Install(Fact(7, 1, 100));
            var slot = _registry[0]; var health = slot.Health; var cast = slot.Cast;
            var death = Fact((uint)kind, 1, 0);
            death.Amount = kind == 8 ? 100 : DeathDelay(slot); // Amount 按 Kind 解释：伤害值或尸体剩余秒数。
            Install(death);
            Assert.That(slot.Health.CurrentHp, Is.Zero);
            Assert.That(slot.Body.GetComponent<CharacterController>().enabled, Is.True,
                "先撤销玩法权限，尸体碰撞依原两秒Deadline清理。");
            uint deadline = 100 + (uint)Mathf.CeilToInt(DeathDelay(slot) / FixedStepClock.DeltaTime);
            SetTick(deadline + 1); _combat.Present(5);
            AssertBodyRetired(slot);
            Assert.That(slot.Root.activeSelf && slot.Body != null, Is.True);
            Assert.That(slot.Health, Is.SameAs(health)); Assert.That(slot.Cast, Is.SameAs(cast));
        }

        [Test]
        public void RealRayPassesRetiredCorpseAndHitsLivingActorBehindIt()
        {
            Install(Fact(7, 1, 100)); Install(Fact(7, 2, 100));
            var front = _registry[0]; var back = _registry[1];
            front.Body.SetPositionAndRotation(_anchor, Quaternion.identity);
            back.Body.SetPositionAndRotation(_anchor + Vector3.forward * 4, Quaternion.identity);
            var frontBody = front.Body.GetComponent<CharacterController>();
            var backBody = back.Body.GetComponent<CharacterController>();
            Vector3 center = front.Body.TransformPoint(frontBody.center);
            Ray ray = new Ray(center + Vector3.back * 3, Vector3.forward);
            Physics.SyncTransforms();
            Assert.That(Nearest(ray), Is.SameAs(frontBody), "正控：死亡前真实身体必须遮住后方目标。");
            Install(Fact(9, 1, 0)); Physics.SyncTransforms();
            Assert.That(Nearest(ray), Is.SameAs(frontBody), "原两秒尸体期间的遮挡属于合法单机行为。");
            uint deadline = 100 + (uint)Mathf.CeilToInt(DeathDelay(front) / FixedStepClock.DeltaTime);
            SetTick(deadline + 1); _combat.Present(5); Physics.SyncTransforms();
            Assert.That(Nearest(ray), Is.SameAs(backBody), "死亡后真实Physics Query应穿过尸体命中活目标。");
            Assert.That(front.Root.activeSelf && front.Body != null, Is.True);
        }

        [Test]
        public void ServerTickLifetime_HidesVisualOnlyAfterPrefabDelay_AndFrozenCannotAgeIt()
        {
            Install(Fact(7, 1, 100));
            var slot = _registry[0]; float delay = DeathDelay(slot);
            Assert.That(VisibleCount(slot), Is.GreaterThan(0));
            Install(Fact(9, 1, 0));
            Assert.That(slot.Body.GetComponent<CharacterController>().enabled, Is.True);
            Set(_world, "_frozen", true);
            _combat.Present(1000); _combat.Present(5000);
            Assert.That(VisibleCount(slot), Is.GreaterThan(0), "真实时间过去不会推进Frozen尸体时钟。");
            Assert.That(slot.Body.GetComponent<CharacterController>().enabled, Is.True);
            Set(_world, "_frozen", false);
            uint ticks = (uint)Mathf.CeilToInt(delay / FixedStepClock.DeltaTime);
            SetTick(100 + ticks - 1); _combat.Present(5001);
            Assert.That(VisibleCount(slot), Is.GreaterThan(0));
            Assert.That(slot.Body.GetComponent<CharacterController>().enabled, Is.True);
            SetTick(100 + ticks + 1); _combat.Present(5002);
            Assert.That(VisibleCount(slot), Is.Zero);
            AssertBodyRetired(slot);
            Assert.That(slot.Root.activeSelf && slot.Body != null && slot.Health != null && slot.Cast != null, Is.True,
                "期满只隐藏表现，不能Destroy角色网络身份。");
        }

        [Test]
        public void RepeatedDeathAndNewConnectionEpoch_DoNotRestartRetiredBodyOrVisualTimer()
        {
            Install(Fact(7, 1, 100)); Install(Fact(9, 1, 0));
            var slot = _registry[0]; var body = slot.Body; var health = slot.Health;
            SetTick(130); _combat.Present(1);
            var duplicateState = Fact(7, 1, 0); duplicateState.Amount = DeathDelay(slot); Install(duplicateState);
            SetTick(170); _combat.Present(2);
            Assert.That(VisibleCount(slot), Is.Zero, "后续零血快照不能不断重启两秒尸体表现。");
            var rejoined = Fact(7, 1, 0); rejoined.ConnectionEpoch = 2; rejoined.Amount = DeathDelay(slot); Install(rejoined);
            _combat.Present(3);
            Assert.That(slot.Epoch, Is.EqualTo(2)); Assert.That(slot.Body, Is.SameAs(body)); Assert.That(slot.Health, Is.SameAs(health));
            AssertBodyRetired(slot); Assert.That(VisibleCount(slot), Is.Zero, "ConnectionEpoch不是NewLife。");
        }

        [Test]
        public void LateJoinDeadActor_WithZeroRemainingVisualLifetime_IsImmediatelyHidden()
        {
            var snapshot = Fact(7, 1, 0); snapshot.Amount = 0;
            Install(snapshot); _combat.Present(0);
            var slot = _registry[0]; AssertBodyRetired(slot);
            Assert.That(VisibleCount(slot), Is.Zero, "晚加入不应重播已在服务器清理完的尸体表现。");
            Assert.That(slot.Root.activeSelf && slot.Health != null && slot.Cast != null, Is.True);
        }

        [Test]
        public void NewerExpiredActorState_RetiresImmediatelyEvenWhenMovementTickIsStillBehind()
        {
            Install(Fact(7, 1, 100));
            var lethal = Fact(8, 1, 0); lethal.Amount = 100; Install(lethal);
            var slot = _registry[0];
            Assert.That(slot.Body.GetComponent<CharacterController>().enabled, Is.True);
            Assert.That(VisibleCount(slot), Is.GreaterThan(0));
            // Combat 与 Movement 没有跨通道总顺序；明确已清理的可信事实不能等旧 Movement Tick 补上。
            var expired = Fact(7, 1, 0); expired.ServerTick = 200; expired.Amount = 0; Install(expired);
            Assert.That(_world.ServerTick, Is.EqualTo(100));
            AssertBodyRetired(slot);
            Assert.That(VisibleCount(slot), Is.Zero);
        }

        [Test]
        public void AuthoritativeShorterRemainingWindow_CanShortenButNeverRestartExistingDeadline()
        {
            Install(Fact(7, 1, 100));
            var lethal = Fact(8, 1, 0); lethal.Amount = 100; Install(lethal);
            var corrected = Fact(7, 1, 0); corrected.ServerTick = 130; corrected.Amount = .25f; Install(corrected);
            SetTick(138); _combat.Present(0);
            AssertBodyRetired(_registry[0]);
            Assert.That(VisibleCount(_registry[0]), Is.Zero,
                "可信剩余窗口若更短，应收敛到更早Deadline；重复较长窗口不得重启已由其他case覆盖。");
        }

        [Test]
        public void RendererCreatedAfterActorBirth_IsIncludedWhenDeathDeadlineRetiresVisuals()
        {
            Install(Fact(7, 1, 100));
            var slot = _registry[0];
            var lateVisual = new GameObject("N4 late-created blood or boss visual");
            lateVisual.transform.SetParent(slot.Body, false);
            var renderer = lateVisual.AddComponent<MeshRenderer>();
            Assert.That(renderer.enabled, Is.True);
            // 对应真实 Awake 的Boss子Visual，以及致死DamageEvent在Death之前创建的BloodDrip。
            Install(Fact(9, 1, 0)); SetTick(170); _combat.Present(0);
            Assert.That(renderer.enabled, Is.False, "不能只隐藏出生时的模型，留下后来添加的子表现悬空。");
        }

        [Test]
        public void BurningSnapshotAfterDeathBeforeDeadline_IsHiddenWhenCorpseRetiresWithoutDroppingStatusReadModel()
        {
            Install(Fact(7, 1, 100));
            var slot = _registry[0]; var status = InitializeActualStatus(slot);
            Install(Fact(9, 1, 0));
            SetTick(130);
            var burning = Fact(7, 1, 0); burning.Burning = 45; burning.Amount = 1; Install(burning);
            GameObject fire = BurningVfx(status);
            Assert.That(fire, Is.Not.Null, "正控：正式Database的Burning必须实际生成OnFire子表现。");
            Assert.That(fire.activeInHierarchy, Is.True);
            Assert.That(status.GetIntensity(StatusKind.Burning), Is.EqualTo(45));
            SetTick(170); _combat.Present(0);
            Assert.That(fire == null || !fire.activeInHierarchy, Is.True,
                "死亡后才收到状态事实所创建的OnFire，也不能越过尸体Deadline继续悬空。");
            Assert.That(status.GetIntensity(StatusKind.Burning), Is.EqualTo(45), "纯表现退休不能丢弃服务器的只读状态数据。");
        }

        [Test]
        public void BurningSnapshotAfterCorpseAlreadyRetired_UpdatesIntensityWithoutCreatingNewVfx()
        {
            Install(Fact(7, 1, 100));
            var slot = _registry[0]; var status = InitializeActualStatus(slot);
            Install(Fact(9, 1, 0)); SetTick(170); _combat.Present(0);
            Assert.That(slot.BodyLifetime.Retired, Is.True);
            Assert.That(BurningVfx(status), Is.Null);
            var burning = Fact(7, 1, 0); burning.Burning = 45; burning.Amount = 0; Install(burning);
            Assert.That(status.GetIntensity(StatusKind.Burning), Is.EqualTo(45));
            Assert.That(BurningVfx(status), Is.Null,
                "已退休角色的重复15Hz死态快照不能再Instantiate状态VFX；保留HP/Intensity等只读事实。");
        }

        private CombatFactMessage Fact(uint kind, ulong actor, float hp) => new CombatFactMessage
        {
            ProtocolVersion = ProtocolCompatibility.CurrentVersion, GameplayHash = _definition.WireHash,
            Kind = kind, ActorId = actor, ConnectionEpoch = 1, Sequence = ++_sequence, ServerTick = _world.ServerTick,
            CurrentHp = hp, MaxHp = 100, Team = 1, MoveMultiplier = 1, RotationW = 1,
            Amount = kind == 9 ? _prefabDeathDelay : 0, // Kind9沿用Prefab时长，Kind7 dead由case显式给剩余值。
            PositionX = _anchor.x, PositionY = _anchor.y, PositionZ = _anchor.z
        };
        private void Install(CombatFactMessage fact) => Receive.Invoke(_combat, new object[] { fact });
        private void SetTick(uint tick) => Set(_world, "<ServerTick>k__BackingField", tick);
        private static void Set(object target, string field, object value) => target.GetType().GetField(field, Flags).SetValue(target, value);
        private static void AssertBodyRetired(NetworkActorRegistry.Slot slot)
        {
            var colliders = slot.Body.GetComponentsInChildren<Collider>(true);
            Assert.That(colliders.Length, Is.GreaterThan(0));
            for (int i = 0; i < colliders.Length; i++) Assert.That(colliders[i].enabled, Is.False, colliders[i].name);
        }
        private static int VisibleCount(NetworkActorRegistry.Slot slot)
        {
            int count = 0; var renderers = slot.Body.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++) if (renderers[i].enabled) count++;
            return count;
        }
        private static float DeathDelay(NetworkActorRegistry.Slot slot)
        {
            var feedback = slot.Body.GetComponent<CharacterCombatFeedback>(); Assert.That(feedback, Is.Not.Null);
            return (float)typeof(CharacterCombatFeedback).GetField("_destroyDelay", Flags).GetValue(feedback);
        }
        private static StatusController InitializeActualStatus(NetworkActorRegistry.Slot slot)
        {
            var status = slot.Body.GetComponent<StatusController>(); Assert.That(status, Is.Not.Null);
            // EditMode 夹具仅补正式Awake，使用Prefab实际引用的StatusDatabase，不临时造一个近似VFX。
            typeof(StatusController).GetMethod("Awake", Flags).Invoke(status, null);
            var definitions = (StatusDefinition[])typeof(StatusController).GetField("_definitions", Flags).GetValue(status);
            Assert.That(definitions[(int)StatusKind.Burning], Is.Not.Null);
            Assert.That(definitions[(int)StatusKind.Burning].VfxPrefab, Is.Not.Null);
            return status;
        }
        private static GameObject BurningVfx(StatusController status) =>
            ((GameObject[])typeof(StatusController).GetField("_vfxInstances", Flags).GetValue(status))[(int)StatusKind.Burning];
        private static Collider Nearest(Ray ray)
        {
            var hits = new RaycastHit[64]; int count = Physics.RaycastNonAlloc(ray, hits, 20, ~0, QueryTriggerInteraction.Ignore);
            Collider nearest = null; float distance = float.PositiveInfinity;
            for (int i = 0; i < count; i++) if (hits[i].distance < distance) { distance = hits[i].distance; nearest = hits[i].collider; }
            return nearest;
        }
    }
}
