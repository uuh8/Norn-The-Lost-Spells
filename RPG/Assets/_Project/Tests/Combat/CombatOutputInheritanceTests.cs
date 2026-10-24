using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Combat.Tests
{
    /// <summary>检查命中后的真实子实例与出生事实；不把服务器生成了火场等同于客户端已经能看到火场。</summary>
    public sealed class CombatOutputInheritanceTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<CombatOutputSpawnedEvent> _facts = new List<CombatOutputSpawnedEvent>();
        private readonly Vector3 _point = new Vector3(41370f, 17000f, 34180f);

        [SetUp] public void SetUp() => EventBus<CombatOutputSpawnedEvent>.Subscribe(OnBorn);
        [TearDown]
        public void TearDown()
        {
            EventBus<CombatOutputSpawnedEvent>.Unsubscribe(OnBorn);
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear(); _facts.Clear();
        }

        [Test]
        public void FireballImpact_PublishesControlledExplosionWithOriginalActorAndCommand()
        {
            Fireball projectile = Projectile<Fireball>(new CombatSimulationWorld());
            GameObject explosion = Root("Fireball explosion template");
            Field(projectile, "_explosionPrefab", explosion);
            ConfigureIdentity(projectile.gameObject);
            Impact(projectile);
            Assert.That(_facts.Count, Is.EqualTo(1));
            Assert.That(_facts[0].DefinitionPrefab, Is.SameAs(explosion));
            Assert.That(_facts[0].Output, Is.Not.SameAs(explosion));
            Assert.That(_facts[0].ActorId, Is.EqualTo(2));
            Assert.That(_facts[0].CommandId, Is.EqualTo(12345));
            Assert.That(_facts[0].Output.transform.position, Is.EqualTo(_point));
        }

        [Test]
        public void NovaImpact_PublishesBothConfiguredAreasWithDistinctOrdinals()
        {
            NovaFireball projectile = Projectile<NovaFireball>(new CombatSimulationWorld());
            GameObject explosion = Root("Nova explosion template"); explosion.AddComponent<AreaDamage>();
            GameObject field = Root("Fire field template"); field.AddComponent<AreaDamage>();
            Field(projectile, "_explosionPrefab", explosion); Field(projectile, "_fireFieldPrefab", field);
            projectile.ConfigureImpactDamage(21f, 7f, .5f, 3f);
            ConfigureIdentity(projectile.gameObject);
            Impact(projectile);
            Assert.That(_facts.Count, Is.EqualTo(2));
            Assert.That(_facts[0].DefinitionPrefab, Is.SameAs(explosion));
            Assert.That(_facts[1].DefinitionPrefab, Is.SameAs(field));
            Assert.That(_facts[0].ActorId, Is.EqualTo(2));
            Assert.That(_facts[1].CommandId, Is.EqualTo(12345));
            Assert.That(_facts[0].OutputOrdinal, Is.Not.EqualTo(_facts[1].OutputOrdinal));
            Assert.That(_facts[0].Output.GetComponent<AreaDamage>().DamagePerHit, Is.EqualTo(21f));
            Assert.That(_facts[1].Output.GetComponent<AreaDamage>().DamagePerHit, Is.EqualTo(7f));
            Assert.That(_facts[1].Output.GetComponent<AreaDamage>().ExecutionMode, Is.EqualTo(CombatExecutionMode.Authority));
        }

        [Test]
        public void NovaSecondAreaCapacityFailure_PublishesNeitherChild()
        {
            NovaFireball projectile = Projectile<NovaFireball>(new CombatSimulationWorld(2));
            GameObject explosion = Root("Nova explosion template"); explosion.AddComponent<AreaDamage>();
            GameObject field = Root("Fire field template"); field.AddComponent<AreaDamage>();
            Field(projectile, "_explosionPrefab", explosion); Field(projectile, "_fireFieldPrefab", field);
            projectile.ConfigureImpactDamage(21f, 7f, .5f, 3f);
            ConfigureIdentity(projectile.gameObject);
            var before = Object.FindObjectsByType<AreaDamage>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            try
            {
                var failure = Assert.Throws<TargetInvocationException>(() => Impact(projectile));
                Assert.That(failure.InnerException, Is.TypeOf<InvalidOperationException>());
                Assert.That(_facts, Is.Empty, "同一命中的两个产出必须在全部配置成功后才公开出生。");
                var after = Object.FindObjectsByType<AreaDamage>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                foreach (var area in after)
                    if (Array.IndexOf(before, area) < 0)
                        Assert.That(area.gameObject.activeInHierarchy, Is.False);
            }
            finally
            {
                foreach (var area in Object.FindObjectsByType<AreaDamage>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (Array.IndexOf(before, area) < 0) _objects.Add(area.gameObject);
            }
        }

        private void ConfigureIdentity(GameObject output)
        {
            // Reflection 仅隔离新组件缺失的 RED；断言的是对外事实与实例状态，不读取内部实现。
            Type identity = typeof(ProjectileBase).Assembly.GetType("Game.Combat.CombatOutputIdentity");
            Assert.That(identity, Is.Not.Null, "后续命中产出需要保存原始 Actor/Command，不能读后来一次施法的上下文。");
            MethodInfo configure = identity.GetMethod("ConfigureRoot", BindingFlags.Public | BindingFlags.Static);
            Assert.That(configure, Is.Not.Null);
            configure.Invoke(null, new object[] { output, 2UL, 12345UL });
        }

        private T Projectile<T>(CombatSimulationWorld world) where T : ProjectileBase
        {
            GameObject root = Root(typeof(T).Name); root.AddComponent<SphereCollider>();
            T projectile = root.AddComponent<T>();
            projectile.Init(41, 1, 10f, DamageType.True, Vector3.forward, null, false);
            projectile.ConfigureExecutionMode(CombatExecutionMode.Authority, world);
            return projectile;
        }
        private void Impact(ProjectileBase projectile) => projectile.GetType().GetMethod("OnImpact", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(projectile, new object[] { null, null, _point, false });
        private GameObject Root(string name)
        { var root = new GameObject("Output inheritance " + name); root.transform.position = _point; _objects.Add(root); return root; }
        private static void Field(object instance, string field, object value) => instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, value);
        private void OnBorn(CombatOutputSpawnedEvent fact) { _facts.Add(fact); if (fact.Output != null) _objects.Add(fact.Output); }
    }
}
