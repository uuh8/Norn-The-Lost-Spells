using System.Reflection;
using Game.Combat;
using Game.Core;
using Game.ElementField;
using NUnit.Framework;
using UnityEngine;

namespace Game.Rendering.Tests
{
    public sealed class ProjectileImpactVfxTests
    {
        private GameObject _host;
        private GameObject _prefab;
        private GameObject _instance;

        [TearDown]
        public void TearDown()
        {
            if (_instance != null)
                Object.DestroyImmediate(_instance);
            if (_prefab != null)
                Object.DestroyImmediate(_prefab);
            if (_host != null)
                Object.DestroyImmediate(_host);
        }

        [Test]
        public void SpawnImpact_CreatesCloneAtHitPointAndPreservesPrefabRotation()
        {
            _host = new GameObject("ProjectileImpactVfxTests_Host");
            _prefab = new GameObject("WaterExplosion_TestPrefab");
            Quaternion authoredRotation = Quaternion.Euler(-90f, 25f, 0f);
            _prefab.transform.rotation = authoredRotation;
            ProjectileImpactVfx presenter = _host.AddComponent<ProjectileImpactVfx>();
            SetPrivateField(presenter, "_impactPrefab", _prefab);
            SetPrivateField(presenter, "_lifetime", 2f);
            var hitPoint = new Vector3(1.25f, 2.5f, -3.75f);

            _instance = presenter.SpawnImpact(hitPoint);

            Assert.That(_instance, Is.Not.Null);
            Assert.That(_instance.transform.position, Is.EqualTo(hitPoint));
            Assert.That(
                Quaternion.Angle(_instance.transform.rotation, authoredRotation),
                Is.LessThan(0.001f),
                "命中特效必须保留 Prefab 根节点的美术旋转，不能被 Quaternion.identity 覆盖。");
        }

        [Test]
        public void SpawnImpact_WithoutPrefab_ReturnsNull()
        {
            _host = new GameObject("ProjectileImpactVfxTests_Host");
            ProjectileImpactVfx presenter = _host.AddComponent<ProjectileImpactVfx>();

            _instance = presenter.SpawnImpact(Vector3.one);

            Assert.That(_instance, Is.Null);
        }

        [Test]
        public void ReplicaTermination_PresentsOnlyTheFirstRealImpactWithoutGameplayEvents()
        {
            _host = new GameObject("ReplicaImpact_Host", typeof(Rigidbody), typeof(BoxCollider));
            var projectile = _host.AddComponent<ElementOrbProjectile>();
            _prefab = new GameObject("PoisonReplicaImpact_TestPrefab");
            _prefab.transform.rotation = Quaternion.Euler(-90, 18, 0);
            var presenter = _host.AddComponent<ProjectileImpactVfx>();
            SetPrivateField(presenter, "_impactPrefab", _prefab);
            EnablePresenterInEditMode(presenter);
            int gameplay = 0;
            projectile.Impacted += (_, _) => gameplay++;
            projectile.SurfaceImpacted += _ => gameplay++;
            projectile.Terminated += (_, _, _) => gameplay++;
            projectile.Finished += () => gameplay++;
            var api = typeof(ProjectileBase).GetMethod("TryPresentReplicaTermination", BindingFlags.Public | BindingFlags.Instance);
            Assert.That(api, Is.Not.Null, "Replica必须有独立纯表现入口，不能调用会展开Payload/伤害/沉积的Impact。");
            var point = new Vector3(3, 2, -1);
            Assert.That(api.Invoke(projectile, new object[] { ProjectileTerminationReason.Impact, point }), Is.False,
                "普通单机/Authority不能经网络表现入口再次生成特效。");
            projectile.ConfigureExecutionMode(CombatExecutionMode.Replica);
            Assert.That(api.Invoke(projectile, new object[] { ProjectileTerminationReason.Impact, point }), Is.True);
            Assert.That(api.Invoke(projectile, new object[] { ProjectileTerminationReason.Impact, point }), Is.False);
            _instance = GameObject.Find(_prefab.name + "(Clone)");
            Assert.That(_instance, Is.Not.Null);
            Assert.That(_instance.transform.position, Is.EqualTo(point));
            Assert.That(Quaternion.Angle(_instance.transform.rotation, _prefab.transform.rotation), Is.LessThan(.001f));
            Assert.That(gameplay, Is.Zero, "纯视觉重放不得广播任何原玩法命中/终止/Payload事件。");

            var expiredHost = new GameObject("ReplicaExpired_Host", typeof(Rigidbody), typeof(BoxCollider));
            try
            {
                var expired = expiredHost.AddComponent<ElementOrbProjectile>();
                var expiredPresenter = expiredHost.AddComponent<ProjectileImpactVfx>();
                SetPrivateField(expiredPresenter, "_impactPrefab", _prefab);
                EnablePresenterInEditMode(expiredPresenter);
                expired.ConfigureExecutionMode(CombatExecutionMode.Replica);
                Assert.That(api.Invoke(expired, new object[] { ProjectileTerminationReason.LifetimeExpired, point }), Is.False);
                Assert.That(api.Invoke(expired, new object[] { ProjectileTerminationReason.Impact, point }), Is.False,
                    "超时已结束该实例，迟到命中不能再把它伪装为碰撞特效。");
                int clones = 0;
                foreach (var value in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
                    if (value.name == _prefab.name + "(Clone)") clones++;
                Assert.That(clones, Is.EqualTo(1), "重复或超时终止不得多生成特效。");
            }
            finally { Object.DestroyImmediate(expiredHost); }
        }

        private static void EnablePresenterInEditMode(ProjectileImpactVfx presenter)
        {
            // 普通MonoBehaviour不以ExecuteAlways运行；EditMode构造不能假定AddComponent已跑生命周期。
            // 复用真实启停方法建立订阅，先退订确保即使Editor曾启用也不会双订阅；不直接调用VFX或伪造事件。
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(ProjectileImpactVfx).GetMethod("OnDisable", flags).Invoke(presenter, null);
            typeof(ProjectileImpactVfx).GetMethod("OnEnable", flags).Invoke(presenter, null);
        }

        private static void SetPrivateField(
            ProjectileImpactVfx target,
            string fieldName,
            object value)
        {
            FieldInfo field = typeof(ProjectileImpactVfx).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(target, value);
        }
    }
}
