using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Combat.Tests
{
    public sealed class CombatImpactFailureProjectile : ProjectileBase
    {
        protected override void OnImpact(Collision collision, IDamageable target, Vector3 point, bool damaged)
        { throw new InvalidOperationException("受控的子效果容量拒绝。"); }
    }

    /// <summary>
    /// 验证真实故障后的状态和事实，不比较源码文本。Reflection 只连接非公开业务接缝和 EditMode fixture；
    /// Physics 接触几何本身不属于这些“已接触后事务失败”的测试范围。
    /// </summary>
    public sealed class CombatAuthorityFailureTests
    {
        private readonly List<Object> _owned = new List<Object>();
        private readonly List<StatusController> _boundStatuses = new List<StatusController>();
        private readonly Vector3 _position = new Vector3(32451f, 12345f, 20987f);
        private int _reactionStarts;

        [SetUp]
        public void SetUp()
        {
            _reactionStarts = 0;
            EventBus<ElementReactionEvent>.Subscribe(OnReaction);
        }

        [TearDown]
        public void TearDown()
        {
            EventBus<ElementReactionEvent>.Unsubscribe(OnReaction);
            for (int i = 0; i < _boundStatuses.Count; i++)
                if (_boundStatuses[i] != null) Invoke(_boundStatuses[i], "OnDisable");
            _boundStatuses.Clear();
            for (int i = _owned.Count - 1; i >= 0; i--)
                if (_owned[i] != null) Object.DestroyImmediate(_owned[i]);
            _owned.Clear();
        }

        [Test]
        public void NovaCapacityRejection_DoesNotLeaveActiveSinglePlayerAreaOrApplyFreeDamage()
        {
            var world = new CombatSimulationWorld(1);
            NovaFireball meteor = Projectile<NovaFireball>();
            meteor.ConfigureExecutionMode(CombatExecutionMode.Authority, world);
            world.BeginTick(1);
            GameObject prefab = Root("Rejected Area Prefab");
            AreaDamage templateArea = prefab.AddComponent<AreaDamage>();
            HealthComponent target = Health();
            target.gameObject.AddComponent<BoxCollider>();
            Set(target, "_teamId", (byte)1);
            MethodInfo spawn = typeof(NovaFireball).GetMethod("SpawnEffect", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(spawn, Is.Not.Null);
            AreaDamage[] before = Object.FindObjectsByType<AreaDamage>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            try
            {
                TargetInvocationException failure = Assert.Throws<TargetInvocationException>(() =>
                    spawn.Invoke(meteor, new object[] { prefab, _position, Vector3.zero, 2f, 10f, 0f }));
                Assert.That(failure.InnerException, Is.TypeOf<InvalidOperationException>());
                Physics.SyncTransforms();
                AreaDamage[] after = Object.FindObjectsByType<AreaDamage>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                int liveUnexpectedAreas = 0;
                for (int i = 0; i < after.Length; i++)
                {
                    if (Array.IndexOf(before, after[i]) >= 0) continue;
                    _owned.Add(after[i].gameObject);
                    if (!after[i].gameObject.activeInHierarchy) continue;
                    liveUnexpectedAreas++;
                    // 模拟下一次真实 Unity Start；只有仍然活跃的默认 SinglePlayer 组件会走到这里。
                    Invoke(after[i], "Start");
                }
                Assert.That(liveUnexpectedAreas, Is.Zero, "容量拒绝后必须先停用新实例，再安排销毁。");
                Assert.That(target.CurrentHp, Is.EqualTo(100f), "未注入的默认Area不能免费攻击。");
                Assert.That(templateArea.gameObject.activeInHierarchy, Is.True, "失败不能删除Prefab来源。");
            }
            finally
            {
                // RED 同样清理故障实际遗留的 Clone，防止污染后面的 Physics 查询。
                var after = Object.FindObjectsByType<AreaDamage>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < after.Length; i++)
                    if (Array.IndexOf(before, after[i]) < 0 && !_owned.Contains(after[i].gameObject)) _owned.Add(after[i].gameObject);
            }
        }

        [Test]
        public void ImpactEffectFailure_CancelsExactlyOnceWithoutPublishingSuccessfulPayload()
        {
            CombatImpactFailureProjectile projectile = Projectile<CombatImpactFailureProjectile>();
            projectile.ConfigureExecutionMode(CombatExecutionMode.Authority, new CombatSimulationWorld());
            int cancelled = 0, successful = 0, impacts = 0;
            projectile.Terminated += (reason, point, direction) =>
            {
                if (reason == ProjectileTerminationReason.Cancelled) cancelled++;
                if (reason == ProjectileTerminationReason.Impact || reason == ProjectileTerminationReason.LifetimeExpired) successful++;
            };
            projectile.Impacted += (point, direction) => impacts++;
            MethodInfo resolve = typeof(ProjectileBase).GetMethod("ResolveImpact", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(resolve, Is.Not.Null, "接触后的事务需要统一完成/取消入口，不能因子效果异常跳过终止。");
            try { resolve.Invoke(projectile, new object[] { null, null, _position, Vector3.forward, Vector3.up, false }); }
            catch (TargetInvocationException error) { Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>()); }
            Assert.That(cancelled, Is.EqualTo(1));
            Assert.That(successful, Is.Zero);
            Assert.That(impacts, Is.Zero);
            Assert.That(projectile.gameObject.activeInHierarchy, Is.False, "失败对象不能留到下一次补跑Physics。");
        }

        [TestCase(CombatExecutionMode.SinglePlayer)]
        [TestCase(CombatExecutionMode.Authority)]
        public void DeadActor_CannotReceiveNewDirectOrSustainedStatus(CombatExecutionMode mode)
        {
            HealthComponent health = Health();
            StatusController status = Status(health, mode);
            DamageRequest lethal = Lethal(); health.ReceiveHit(in lethal);
            status.ApplyStatus(StatusKind.Burning, 45f, 99, 2);
            status.ApplySustainedStatus(StatusKind.Poisoned, 40f, 99, 2, 1f);
            Assert.That(health.IsAlive, Is.False);
            Assert.That(status.HasStatus(StatusKind.Burning), Is.False);
            Assert.That(status.HasStatus(StatusKind.Poisoned), Is.False);
        }

        [TestCase(CombatExecutionMode.SinglePlayer)]
        [TestCase(CombatExecutionMode.Authority)]
        public void LethalHit_FollowupBurnCannotRestartCancelledCorpseReaction(CombatExecutionMode mode)
        {
            HealthComponent health = Health();
            StatusController status = Status(health, mode);
            status.ApplyStatus(StatusKind.Poisoned, 40f, 99, 2);
            status.ApplyStatus(StatusKind.Burning, 45f, 99, 2);
            Assert.That(_reactionStarts, Is.EqualTo(1));
            DamageRequest lethal = Lethal(); health.ReceiveHit(in lethal);
            _reactionStarts = 0;
            // 火球真实顺序为 ReceiveHit -> Death -> OnImpact.ApplyBurn；尸体在死亡动画期间仍有 Collider。
            status.ApplyStatus(StatusKind.Burning, 45f, 99, 2);
            status.TickForTests(0.5f);
            Assert.That(_reactionStarts, Is.Zero, "OnDeath 已取消的Toxic不能被同次命中的附加燃烧重新启动。");
        }

        private HealthComponent Health()
        {
            HealthComponent health = Root("Actor").AddComponent<HealthComponent>();
            Set(health, "_currentHp", 100f); Set(health, "_id", health.gameObject.GetInstanceID());
            return health;
        }

        private StatusController Status(HealthComponent health, CombatExecutionMode mode)
        {
            StatusController status = health.gameObject.AddComponent<StatusController>();
            Set(status, "_health", health); Set(status, "_damageable", health);
            Set(status, "_targetId", health.Id);
            ElementReactionProfile profile = ScriptableObject.CreateInstance<ElementReactionProfile>(); _owned.Add(profile);
            profile.ToxicCombustion = new ToxicCombustionTuning { FireThreshold = 25f, PoisonThreshold = 20f,
                WindUpSeconds = 0.2f, MaxPoisonConsume = 40f, MinEffectiveConsume = 5f,
                BaseDamage = 8f, DamagePerPoison = 0.4f, Radius = 2.5f, CooldownSeconds = 1f };
            status.SetReactionProfileForTests(profile);
            status.ConfigureExecutionMode(mode);
            Invoke(status, "OnEnable");
            _boundStatuses.Add(status);
            return status;
        }

        private T Projectile<T>() where T : ProjectileBase
        {
            GameObject root = Root(typeof(T).Name); root.AddComponent<SphereCollider>();
            T projectile = root.AddComponent<T>();
            projectile.Init(1, 10, 10f, DamageType.True, Vector3.forward * 10f, null, false);
            return projectile;
        }

        private GameObject Root(string name)
        {
            GameObject value = new GameObject("Combat Failure " + name); value.transform.position = _position;
            _owned.Add(value); return value;
        }
        private void OnReaction(ElementReactionEvent reaction)
        { if (reaction.Reaction == ElementReactionId.ToxicCombustion && reaction.Phase == ElementReactionPhase.Started) _reactionStarts++; }
        private static DamageRequest Lethal() => new DamageRequest(99, 2, 200f, DamageType.True, Vector3.zero, Vector3.forward);
        private static void Set(object target, string field, object value)
        { target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value); }
        private static void Invoke(object target, string method)
        { target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null); }
    }
}
