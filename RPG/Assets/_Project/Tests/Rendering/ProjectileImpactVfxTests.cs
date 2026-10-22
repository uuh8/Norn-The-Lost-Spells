using System.Reflection;
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
