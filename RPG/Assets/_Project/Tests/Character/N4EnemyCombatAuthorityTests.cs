using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Combat;
using Game.Core;
using Game.Skills;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace Game.Character.Tests
{
    /// <summary>实际控制器、FSM 与法术生成边界；没有 NavMesh 的用例直接进入攻击态，避免把寻路告警冒充战斗失败。</summary>
    public sealed class N4EnemyCombatAuthorityTests
    {
        private readonly List<Object> _objects = new List<Object>();
        [TearDown] public void Cleanup()
        { for (int i = _objects.Count - 1; i >= 0; i--) if (_objects[i] != null) Object.DestroyImmediate(_objects[i]); _objects.Clear(); }

        [Test]
        public void ConfiguredOnlineCombatRunsFullFsmOncePerAuthorityTick()
        {
            var enemy = Enemy<MeleeEnemyController>(); enemy.ConfigureOnlineAuthority(new Roster());
            Invoke(enemy, "ConfigureOnlineCombat", new CombatSimulationWorld(), 5ul, .2f);
            var state = new CountingState(enemy); enemy.StateMachine.ChangeState(state);
            enemy.StepAuthority(.05f, 10); enemy.StepAuthority(.05f, 10); enemy.StepAuthority(.05f, 9);
            Assert.That(state.Updates, Is.EqualTo(1));
            Assert.That(state.Seconds, Is.EqualTo(.05f).Within(.00001f));
        }

        [Test]
        public void N3LocomotionModeRemainsUntilCombatIsExplicitlyConfigured()
        {
            var enemy = Enemy<MeleeEnemyController>(); enemy.ConfigureOnlineAuthority(new Roster());
            var state = new CountingState(enemy); enemy.StateMachine.ChangeState(state);
            enemy.StepAuthority(.1f, 1);
            Assert.That(state.Updates, Is.Zero);
        }

        [Test]
        public void RangedAuthorityReleasesAtExportedTimelineWithoutAnimatorAndOnlyOnce()
        {
            var enemy = Enemy<RangedEnemyController>(); enemy.ConfigureOnlineAuthority(new Roster());
            Invoke(enemy, "ConfigureOnlineCombat", new CombatSimulationWorld(), 6ul, .2f);
            Assert.That(enemy.Animator, Is.Null);
            enemy.StateMachine.ChangeState(enemy.RangedAttackState);
            int spawned = 0; Action<CombatOutputSpawnedEvent> receive = value => { spawned++; _objects.Add(value.Output); };
            EventBus<CombatOutputSpawnedEvent>.Subscribe(receive);
            try
            {
                enemy.StepAuthority(.05f, 1); Assert.That(spawned, Is.Zero);
                enemy.StepAuthority(.05f, 2); Assert.That(spawned, Is.EqualTo(1));
                enemy.StepAuthority(.05f, 2); enemy.StepAuthority(.05f, 3);
                Assert.That(spawned, Is.EqualTo(1));
            }
            finally { EventBus<CombatOutputSpawnedEvent>.Unsubscribe(receive); }
        }

        [Test]
        public void RangedReplicaCannotSubmitNpcProgramDirectly()
        {
            var enemy = Enemy<RangedEnemyController>(); enemy.ConfigureOnlineReplica();
            Assert.That(enemy.CastSelectedProgram(), Is.False);
        }

        [Test]
        public void SinglePlayerNpcKeepsExistingDirectProgramCastWithoutAuthoritySetup()
        {
            var enemy = Enemy<RangedEnemyController>();
            Assert.That(enemy.IsOnlineAuthority, Is.False); Assert.That(enemy.IsOnlineReplica, Is.False);
            Assert.That(enemy.CastSelectedProgram(), Is.True);
            int spawned = 0;
            // 单机没有网络出生事实，因此检查真实 Instantiate 结果；只回收本用例专属命名的 Clone。
            var projectiles = Object.FindObjectsByType<Arrow>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < projectiles.Length; i++)
                if (projectiles[i].gameObject.name == "N4 test projectile(Clone)") { spawned++; _objects.Add(projectiles[i].gameObject); }
            Assert.That(spawned, Is.EqualTo(1));
        }

        [Test]
        public void SkyfallDelayStartsAfterReleaseTickInsteadOfCountingThatTickTwice()
        {
            var enemy = Enemy<RangedEnemyController>(); enemy.ConfigureOnlineAuthority(new Roster());
            var programs = (WandLoadout[])typeof(RangedEnemyController).GetField("_spellPrograms", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(enemy);
            programs[0].Spells[0].SpawnMode = SpellSpawnMode.SkyfallAtPoint;
            programs[0].Spells[0].LandingSiteDuration = .1f;
            Invoke(enemy, "ConfigureOnlineCombat", new CombatSimulationWorld(), 6ul, .2f);
            enemy.StateMachine.ChangeState(enemy.RangedAttackState);
            int spawned = 0; Action<CombatOutputSpawnedEvent> receive = value => { spawned++; _objects.Add(value.Output); };
            EventBus<CombatOutputSpawnedEvent>.Subscribe(receive);
            try
            {
                enemy.StepAuthority(.05f, 1); enemy.StepAuthority(.05f, 2);
                Assert.That(spawned, Is.Zero, "Tick 2 只达到施法释放点，此刻才开始预警等待");
                enemy.StepAuthority(.05f, 3); Assert.That(spawned, Is.Zero, "预警只经过 0.05 秒，不能借释放 Tick 提前落下");
                enemy.StepAuthority(.05f, 4); Assert.That(spawned, Is.EqualTo(1));
            }
            finally { EventBus<CombatOutputSpawnedEvent>.Unsubscribe(receive); }
        }

        [TestCase(false)] [TestCase(true)]
        public void BossAuthorityReplacesDeadOrDisconnectedTargetWithoutResettingEncounter(bool disconnected)
        {
            var a = Actor(1, Vector3.right * 3);
            var b = Actor(2, Vector3.right * 5);
            var roster = new Roster(a, b); var boss = Boss();
            Invoke(boss, "ConfigureAuthority", roster, new CombatSimulationWorld(), 7ul);
            Assert.That((bool)Invoke(boss, "TryActivateAuthorityEncounter"), Is.True);
            boss.StepAuthorityForTest(.05f, 1);
            Assert.That((ulong)Read(boss, "TargetActorId"), Is.EqualTo(1));
            if (disconnected) a.IsConnected = false; else a.IsAlive = false;
            roster.Actors[0] = a; boss.StepAuthorityForTest(.05f, 2);
            Assert.That((ulong)Read(boss, "TargetActorId"), Is.EqualTo(2));
            Assert.That(boss.IsEncounterActive, Is.True);
        }

        [Test]
        public void BossAuthorityOrdinaryUpdateAndDuplicateTickDoNotAdvanceCooldown()
        {
            var boss = Boss(); Invoke(boss, "ConfigureAuthority", new Roster(Actor(1, Vector3.right * 3)), new CombatSimulationWorld(), 7ul);
            Assert.That((bool)Invoke(boss, "TryActivateAuthorityEncounter"), Is.True);
            Set(boss, "_globalCastIntervalRemaining", 1f);
            typeof(WizardBossController).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(boss, null);
            Assert.That(boss.GlobalCastIntervalRemaining, Is.EqualTo(1));
            boss.StepAuthorityForTest(.1f, 10); boss.StepAuthorityForTest(.1f, 10);
            Assert.That(boss.GlobalCastIntervalRemaining, Is.EqualTo(.9f).Within(.00001f));
        }

        [Test]
        public void BossReplicaCannotActivateGameplayEncounter()
        {
            var boss = Boss(); Invoke(boss, "ConfigureReplica");
            Assert.That(boss.TryActivate(Actor(1, Vector3.right).Transform), Is.False);
        }

        [Test]
        public void BossAuthorityPhaseTransitionUsesTickTimeWithoutAnimator()
        {
            var boss = Boss(); boss.Definition.PhaseTransitionDuration = .1f;
            Invoke(boss, "ConfigureAuthority", new Roster(Actor(1, Vector3.right * 3)), new CombatSimulationWorld(), 7ul);
            Assert.That((bool)Invoke(boss, "TryActivateAuthorityEncounter"), Is.True);
            boss.Health.ReceiveHit(new DamageRequest(42, 1, 75, DamageType.Physical, Vector3.zero, Vector3.forward));
            boss.StepAuthorityForTest(.05f, 1); boss.StepAuthorityForTest(.05f, 2); boss.StepAuthorityForTest(.05f, 3);
            Assert.That(boss.CurrentPhase, Is.EqualTo(BossPhase.Phase3));
            Assert.That(boss.Health.CurrentHp, Is.EqualTo(25));
        }

        [Test]
        public void BossTargetReturningAfterAllDisconnectsReusesHealthAndPhaseState()
        {
            var actor = Actor(1, Vector3.right * 3); var roster = new Roster(actor); var boss = Boss();
            Invoke(boss, "ConfigureAuthority", roster, new CombatSimulationWorld(), 7ul);
            Assert.That((bool)Invoke(boss, "TryActivateAuthorityEncounter"), Is.True);
            actor.IsConnected = false; roster.Actors[0] = actor; boss.StepAuthorityForTest(.05f, 1);
            Assert.That((ulong)Read(boss, "TargetActorId"), Is.Zero);
            Assert.That(boss.IsActionLocked, Is.False);
            actor.IsConnected = true; roster.Actors[0] = actor; boss.StepAuthorityForTest(.05f, 2);
            Assert.That((ulong)Read(boss, "TargetActorId"), Is.EqualTo(1));
            Assert.That(boss.Health.CurrentHp, Is.EqualTo(100)); Assert.That(boss.CurrentPhase, Is.EqualTo(BossPhase.Phase1));
        }

        private T Enemy<T>() where T : EnemyControllerBase
        {
            var go = Track(new GameObject("N4 enemy")); go.SetActive(false);
            T enemy = go.AddComponent<T>(); go.GetComponent<NavMeshAgent>().enabled = false;
            var definition = Track(ScriptableObject.CreateInstance<EnemyDefinition>());
            var attack = Track(ScriptableObject.CreateInstance<AttackDefinition>()); attack.AnimationStateName = "Attack"; attack.ArrowSpawnTime = .4f;
            definition.Attack = attack; Set(enemy, "_definition", definition, typeof(EnemyControllerBase));
            if (enemy is RangedEnemyController)
            {
                var spell = Track(ScriptableObject.CreateInstance<SpellDefinition>()); spell.Kind = SpellKind.Emit;
                spell.ProjectilePrefab = Track(new GameObject("N4 test projectile", typeof(Rigidbody), typeof(BoxCollider), typeof(Arrow)));
                var wand = Track(ScriptableObject.CreateInstance<WandLoadout>()); wand.Spells = new[] { spell }; wand.BaseDraws = 1;
                Set(enemy, "_spellPrograms", new[] { wand }); Set(enemy, "_castOrigin", go.transform);
            }
            go.SetActive(true);
            InitializeHealth(go);
            if (enemy.Perception == null) typeof(T).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(enemy, null);
            return enemy;
        }
        private WizardBossController Boss()
        {
            var go = Track(new GameObject("N4 boss")); go.SetActive(false);
            var boss = go.AddComponent<WizardBossController>(); go.GetComponent<NavMeshAgent>().enabled = false;
            var definition = Track(ScriptableObject.CreateInstance<BossDefinition>()); definition.Programs = Array.Empty<BossSpellProgramDefinition>();
            Set(boss, "_definition", definition); go.SetActive(true);
            InitializeHealth(go);
            if (boss.Health == null) typeof(WizardBossController).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(boss, null);
            return boss;
        }
        private CombatActorSnapshot Actor(ulong id, Vector3 position)
        {
            var go = Track(new GameObject("N4 target")); go.transform.position = position;
            return new CombatActorSnapshot { ActorId = id, Transform = go.transform, Position = position,
                TeamId = 1, IsAlive = true, IsConnected = true, Targetable = true };
        }
        private T Track<T>(T value) where T : Object { _objects.Add(value); return value; }
        private static void InitializeHealth(GameObject owner)
        {
            // EditMode 不保证普通 MonoBehaviour 的 Awake 自动运行；由真实生命周期入口建立 HP，不直接篡改 Runtime 字段。
            var health = owner.GetComponent<HealthComponent>();
            if (health != null && !health.IsAlive)
                typeof(HealthComponent).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(health, null);
        }
        private static void Set(object owner, string field, object value, Type declaringType = null)
            => (declaringType ?? owner.GetType()).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(owner, value);
        private static object Read(object owner, string property)
        { var info = owner.GetType().GetProperty(property); Assert.That(info, Is.Not.Null, "缺少权威只读属性：" + property); return info.GetValue(owner); }
        private static object Invoke(object owner, string method, params object[] args)
        {
            MethodInfo selected = null;
            foreach (var value in owner.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
                if (value.Name == method && value.GetParameters().Length == args.Length) { selected = value; break; }
            Assert.That(selected, Is.Not.Null, "缺少 N4 敌人权威入口：" + method);
            return selected.Invoke(owner, args);
        }
        private sealed class CountingState : EnemyStateBase
        {
            internal int Updates; internal float Seconds;
            internal CountingState(EnemyControllerBase enemy) : base(enemy) { }
            public override void Enter() { }
            public override void Exit() { }
            public override void Update() { Updates++; Seconds += _enemy.SimulationDeltaTime; }
        }
        private sealed class Roster : ICombatActorRosterReadOnly
        {
            internal CombatActorSnapshot[] Actors;
            public int Count => Actors.Length;
            internal Roster(params CombatActorSnapshot[] actors) => Actors = actors;
            public bool TryGetActor(int index, out CombatActorSnapshot actor) { actor = Actors[index]; return true; }
        }
    }

    internal static class N4BossAuthorityTestAccess
    {
        internal static void StepAuthorityForTest(this WizardBossController boss, float delta, uint tick)
        {
            var method = typeof(WizardBossController).GetMethod("StepAuthority", BindingFlags.Public | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "Boss 必须由显式 Authority Tick 驱动");
            method.Invoke(boss, new object[] { delta, tick });
        }
    }
}
