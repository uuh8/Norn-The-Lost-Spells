using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Combat.Tests
{
    public sealed class CombatExecutionTestProjectile : ProjectileBase
    {
        protected override void OnImpact(Collision collision, IDamageable target, Vector3 point, bool damaged) { }
    }

    /// <summary>
    /// 这些测试只反射尚未落地的公开契约，因此 RED 阶段依旧可编译；断言针对实际伤害、资源和时间，
    /// 不靠源码字符串证明权限。EditMode 不推进 PlayerLoop，私有生命周期通过 MethodInfo 调用。
    /// </summary>
    public sealed class CombatExecutionModeTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private int _damageEvents;

        [SetUp]
        public void SetUp()
        {
            _damageEvents = 0;
            EventBus<DamageReceivedEvent>.Subscribe(OnDamage);
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
            EventBus<DamageReceivedEvent>.Unsubscribe(OnDamage);
        }

        [Test]
        public void DefaultSinglePlayer_ReceiveHitStillUsesDamageFunnel()
        {
            HealthComponent health = Health();
            DamageRequest request = Hit();
            health.ReceiveHit(in request);
            Assert.That(health.CurrentHp, Is.EqualTo(75f));
            Assert.That(_damageEvents, Is.EqualTo(1));
        }

        [Test]
        public void Replica_ReceiveHitDoesNotChangeHpOrPublishDamage()
        {
            HealthComponent health = Health();
            Configure(health, "Replica");
            DamageRequest request = Hit();
            health.ReceiveHit(in request);
            Assert.That(health.CurrentHp, Is.EqualTo(100f));
            Assert.That(_damageEvents, Is.Zero);
        }

        [Test]
        public void Replica_ManaSpendAndAutomaticRegenAreRejected()
        {
            ManaComponent mana = Root("Mana").AddComponent<ManaComponent>();
            Set(mana, "_currentMana", 50f);
            Configure(mana, "Replica");
            Assert.That(mana.Spend(10f), Is.False);
            Invoke(mana, "Update");
            Assert.That(mana.CurrentMana, Is.EqualTo(50f));
        }

        [Test]
        public void Replica_DirectAndSustainedStatusDoNotActivateReactionState()
        {
            StatusController status = Root("Status").AddComponent<StatusController>();
            Configure(status, "Replica");
            status.ApplyStatus(StatusKind.Burning, 45f, 1, 2);
            status.ApplySustainedStatus(StatusKind.Wet, 20f, 1, 2, 1f);
            Assert.That(status.HasStatus(StatusKind.Burning), Is.False);
            Assert.That(status.GetIntensity(StatusKind.Wet), Is.Zero);
        }

        [Test]
        public void Replica_AreaStartCannotApplyItsImmediateFirstHit()
        {
            HealthComponent health = Health();
            health.gameObject.AddComponent<BoxCollider>();
            AreaDamage area = Root("Area").AddComponent<AreaDamage>();
            area.transform.position = health.transform.position;
            area.Init(99, 2);
            area.ConfigureDamage(10f, DamageType.True);
            Configure(area, "Replica");
            Physics.SyncTransforms();
            Invoke(area, "Start");
            Assert.That(health.CurrentHp, Is.EqualTo(100f));
        }

        [Test]
        public void Replica_ShieldTriggerCannotInitializeOrReflectProjectile()
        {
            GameObject shieldRoot = Root("Shield");
            shieldRoot.AddComponent<SphereCollider>().isTrigger = true;
            ProjectileShield shield = shieldRoot.AddComponent<ProjectileShield>();
            Configure(shield, "Replica");
            Invoke(shield, "OnTriggerEnter", new object[] { null });
            Assert.That(Get<bool>(shield, "_initialized"), Is.False);

            CombatExecutionTestProjectile projectile = Projectile();
            Configure(projectile, "Replica");
            Assert.That(projectile.ReflectByShield(Vector3.back, 2, 99), Is.False);
        }

        [Test]
        public void Authority_WorldAndAutomaticCallbacksDoNotDoubleAdvanceProjectileLifetime()
        {
            object world = World();
            CombatExecutionTestProjectile projectile = Projectile();
            Configure(projectile, "Authority", world);
            float initial = Get<float>(projectile, "_lifetimeRemaining");
            Step(world, "StepBeforePhysics", 0.25f, 1);
            Step(world, "StepBeforePhysics", 0.25f, 1);
            Invoke(projectile, "Update");
            Invoke(projectile, "FixedUpdate");
            Assert.That(Get<float>(projectile, "_lifetimeRemaining"), Is.EqualTo(initial - 0.25f).Within(0.0001f));
        }

        [Test]
        public void Authority_AreaFirstHitBelongsToWorldAndEachServerTickRunsOnce()
        {
            object world = World();
            HealthComponent health = Health();
            health.gameObject.AddComponent<BoxCollider>();
            AreaDamage area = Root("Authority Area").AddComponent<AreaDamage>();
            area.transform.position = health.transform.position;
            area.Init(99, 2);
            area.ConfigureDamage(10f, DamageType.True);
            area.ConfigureTickInterval(0.5f);
            Configure(area, "Authority", world);
            Physics.SyncTransforms();
            Invoke(area, "Start");
            Assert.That(health.CurrentHp, Is.EqualTo(100f), "Unity Start 不得替 Authority 先打首跳。");
            Step(world, "StepAfterPhysics", 0.25f, 1);
            Step(world, "StepAfterPhysics", 0.25f, 1);
            Assert.That(health.CurrentHp, Is.EqualTo(90f));
            Step(world, "StepAfterPhysics", 0.25f, 2);
            Step(world, "StepAfterPhysics", 0.25f, 3);
            Assert.That(health.CurrentHp, Is.EqualTo(80f));
        }

        [Test]
        public void Authority_ManaAndStatusExplicitStepAreIdempotent()
        {
            ManaComponent mana = Root("Authority Mana").AddComponent<ManaComponent>();
            Set(mana, "_currentMana", 20f);
            Set(mana, "_manaRegenPerSecond", 10f);
            Configure(mana, "Authority");
            Step(mana, "StepAuthority", 0.5f, 7);
            Step(mana, "StepAuthority", 0.5f, 7);
            Assert.That(mana.CurrentMana, Is.EqualTo(25f));

            StatusController status = Root("Authority Status").AddComponent<StatusController>();
            StatusDefinition definition = ScriptableObject.CreateInstance<StatusDefinition>();
            try
            {
                definition.Kind = StatusKind.Burning;
                definition.NaturalDecayPerSecond = 10f;
                status.SetDefinitionsForTests(definition);
                Configure(status, "Authority");
                status.ApplyStatus(StatusKind.Burning, 40f, 1, 2);
                Step(status, "StepAuthority", 0.5f, 7);
                Step(status, "StepAuthority", 0.5f, 7);
                Assert.That(status.GetIntensity(StatusKind.Burning), Is.EqualTo(35f));
            }
            finally { Object.DestroyImmediate(definition); }
        }

        [Test]
        public void ConfigureHierarchy_IncludesInactiveChildrenBeforeTheirFirstStart()
        {
            object world = World();
            GameObject parent = Root("Hierarchy");
            GameObject child = Root("Inactive Area");
            child.transform.SetParent(parent.transform);
            child.SetActive(false);
            AreaDamage area = child.AddComponent<AreaDamage>();
            HealthComponent health = child.AddComponent<HealthComponent>();
            Set(health, "_currentHp", 100f);
            Type mode = ModeType();
            MethodInfo configure = world.GetType().GetMethod("ConfigureHierarchy", new[] { typeof(GameObject), mode });
            Assert.That(configure, Is.Not.Null, "缺少 CombatSimulationWorld.ConfigureHierarchy(GameObject, CombatExecutionMode)。");
            configure.Invoke(world, new[] { (object)parent, Enum.Parse(mode, "Replica") });
            DamageRequest request = Hit();
            health.ReceiveHit(in request);
            Assert.That(health.CurrentHp, Is.EqualTo(100f));
            Invoke(area, "Start");
        }

        [Test]
        public void Authority_CancelDoesNotBecomeLifetimeExpiryOrTriggerPayload()
        {
            object world = World();
            CombatExecutionTestProjectile projectile = Projectile();
            Configure(projectile, "Authority", world);
            int expired = 0;
            projectile.Terminated += (reason, point, direction) =>
            {
                if (reason == ProjectileTerminationReason.LifetimeExpired) expired++;
            };
            Invoke(projectile, "OnDisable");
            Step(world, "StepBeforePhysics", 60f, 1);
            Assert.That(expired, Is.Zero);
        }

        [Test]
        public void BeginTick_NewProjectileWaitsUntilNextTickButNewAreaResolvesFirstHitAfterPhysics()
        {
            object world = World();
            Begin(world, 10);
            CombatExecutionTestProjectile projectile = Projectile();
            Configure(projectile, "Authority", world);
            float lifetime = Get<float>(projectile, "_lifetimeRemaining");
            HealthComponent health = Health(); health.gameObject.AddComponent<BoxCollider>();
            AreaDamage area = Root("Born During Tick Area").AddComponent<AreaDamage>();
            area.transform.position = health.transform.position;
            area.Init(99, 2); area.ConfigureDamage(10f, DamageType.True); area.ConfigureTickInterval(0.5f);
            Configure(area, "Authority", world);
            Physics.SyncTransforms();
            Step(world, "StepBeforePhysics", 0.25f, 10);
            Step(world, "StepAfterPhysics", 0.25f, 10);
            Assert.That(Get<float>(projectile, "_lifetimeRemaining"), Is.EqualTo(lifetime));
            Assert.That(health.CurrentHp, Is.EqualTo(90f), "爆炸首跳属于本次真实碰撞后的受控阶段。");
            Step(world, "StepBeforePhysics", 0.25f, 11);
            Step(world, "StepAfterPhysics", 0.25f, 11);
            Assert.That(Get<float>(projectile, "_lifetimeRemaining"), Is.EqualTo(lifetime - 0.25f).Within(0.0001f));
            Assert.That(health.CurrentHp, Is.EqualTo(90f), "出生Tick不得额外消费持续伤害间隔。");
        }

        [Test]
        public void Authority_CapacityRejectionRollsBackRegistrationAndAllowsExplicitRetry()
        {
            Type type = typeof(ProjectileBase).Assembly.GetType("Game.Combat.CombatSimulationWorld");
            Assert.That(type, Is.Not.Null);
            object world = Activator.CreateInstance(type, new object[] { 1 });
            ManaComponent first = Root("First Capacity").AddComponent<ManaComponent>();
            ManaComponent second = Root("Second Capacity").AddComponent<ManaComponent>();
            Set(second, "_currentMana", 20f); Set(second, "_manaRegenPerSecond", 10f);
            Configure(first, "Authority", world);
            TargetInvocationException error = Assert.Throws<TargetInvocationException>(() => Configure(second, "Authority", world));
            Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
            Configure(first, "Replica", world);
            Configure(second, "Authority", world);
            Step(world, "StepAfterPhysics", 0.5f, 1);
            Assert.That(second.CurrentMana, Is.EqualTo(25f), "容量拒绝不能留下已标记Authority却未登记的幽灵组件。");
        }

        [Test]
        public void BeginTick_OlderAnnouncementCannotMakeNewObjectsConsumeCurrentTick()
        {
            object world = World();
            Begin(world, 12); Begin(world, 11); Begin(world, 12);
            CombatExecutionTestProjectile projectile = Projectile();
            Configure(projectile, "Authority", world);
            float lifetime = Get<float>(projectile, "_lifetimeRemaining");
            Step(world, "StepBeforePhysics", 0.25f, 12);
            Step(world, "StepBeforePhysics", 0.25f, 12);
            Assert.That(Get<float>(projectile, "_lifetimeRemaining"), Is.EqualTo(lifetime));
        }

        private static void Begin(object world, uint tick)
        {
            MethodInfo method = world.GetType().GetMethod("BeginTick", new[] { typeof(uint) });
            Assert.That(method, Is.Not.Null, "Driver必须在角色/ActionTimeline产生对象前宣告BirthTick。");
            method.Invoke(world, new object[] { tick });
        }

        [Test]
        public void MovingAreaToAnotherWorld_OldWorldCannotResolveItsFirstHit()
        {
            object first = World(), second = World(); Begin(first, 10); Begin(second, 10);
            HealthComponent health = Health(); health.gameObject.AddComponent<BoxCollider>();
            AreaDamage area = Root("Transferred Area").AddComponent<AreaDamage>();
            area.transform.position = health.transform.position; area.Init(99, 2); area.ConfigureDamage(10, DamageType.True);
            Configure(area, "Authority", first); Configure(area, "Authority", second); Physics.SyncTransforms();
            Step(first, "StepAfterPhysics", .1f, 10);
            Assert.That(health.CurrentHp, Is.EqualTo(100), "旧世界不能越过新世界的 Frozen/Tick 权限提前首跳。");
            Step(second, "StepAfterPhysics", .1f, 10);
            Assert.That(health.CurrentHp, Is.EqualTo(90), "首跳应由当前归属的 World 唯一执行。");
        }

        private GameObject Root(string name)
        {
            var root = new GameObject("Combat Execution " + name);
            root.transform.position = new Vector3(12345f, 54321f, 9876f);
            _objects.Add(root);
            return root;
        }

        private HealthComponent Health()
        {
            HealthComponent health = Root("Target").AddComponent<HealthComponent>();
            Set(health, "_currentHp", 100f);
            Set(health, "_id", health.gameObject.GetInstanceID());
            return health;
        }

        private CombatExecutionTestProjectile Projectile()
        {
            GameObject root = Root("Projectile");
            root.AddComponent<SphereCollider>();
            CombatExecutionTestProjectile projectile = root.AddComponent<CombatExecutionTestProjectile>();
            projectile.Init(1, 1, 10f, DamageType.True, Vector3.forward * 10f, null, false);
            return projectile;
        }

        private static DamageRequest Hit() => new DamageRequest(2, 2, 25f, DamageType.True, Vector3.zero, Vector3.forward);
        private void OnDamage(DamageReceivedEvent damage) => _damageEvents++;

        private static Type ModeType()
        {
            Type type = typeof(IGameEvent).Assembly.GetType("Game.Core.CombatExecutionMode");
            Assert.That(type, Is.Not.Null, "缺少 Gameplay 战斗权限契约 CombatExecutionMode；当前组件仍默认具备本地伤害权限。");
            return type;
        }

        private static object World()
        {
            Type type = typeof(ProjectileBase).Assembly.GetType("Game.Combat.CombatSimulationWorld");
            Assert.That(type, Is.Not.Null, "缺少唯一固定步协调器 CombatSimulationWorld。");
            return Activator.CreateInstance(type);
        }

        private static void Configure(object component, string modeName, object world = null)
        {
            Type mode = ModeType();
            Type worldType = typeof(ProjectileBase).Assembly.GetType("Game.Combat.CombatSimulationWorld");
            Assert.That(worldType, Is.Not.Null, "缺少唯一固定步协调器 CombatSimulationWorld。");
            MethodInfo method = component.GetType().GetMethod("ConfigureExecutionMode", new[] { mode, worldType });
            Assert.That(method, Is.Not.Null, component.GetType().Name + " 缺少战斗权限配置入口。");
            method.Invoke(component, new[] { Enum.Parse(mode, modeName), world });
        }

        private static void Step(object target, string method, float deltaTime, uint tick)
        {
            MethodInfo info = target.GetType().GetMethod(method, new[] { typeof(float), typeof(uint) });
            Assert.That(info, Is.Not.Null, target.GetType().Name + " 缺少 " + method + " 固定步入口。");
            info.Invoke(target, new object[] { deltaTime, tick });
        }

        private static FieldInfo Field(object target, string name)
        {
            Type type = target.GetType();
            while (type != null)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null) return field;
                type = type.BaseType;
            }
            Assert.Fail("Fixture 找不到字段 " + name);
            return null;
        }

        private static void Set(object target, string field, object value) => Field(target, field).SetValue(target, value);
        private static T Get<T>(object target, string field) => (T)Field(target, field).GetValue(target);

        private static void Invoke(object target, string name, object[] args = null)
        {
            Type type = target.GetType();
            while (type != null)
            {
                MethodInfo method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (method != null) { method.Invoke(target, args); return; }
                type = type.BaseType;
            }
            Assert.Fail("Fixture 找不到生命周期 " + name);
        }
    }
}
