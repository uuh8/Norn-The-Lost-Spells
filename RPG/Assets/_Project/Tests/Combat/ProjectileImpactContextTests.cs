using NUnit.Framework;
using UnityEngine;

namespace Game.Combat.Tests
{
    public sealed class ProjectileImpactContextTests
    {
        [Test]
        public void Constructor_PreservesExactImpactSnapshotWithoutNormalization()
        {
            var point = new Vector3(1f, 2f, 3f);
            var incoming = new Vector3(3f, 0f, 4f);
            var normal = new Vector3(0f, 2f, 0f);

            var context = new ProjectileImpactContext(point, incoming, normal);

            Assert.That(context.Point, Is.EqualTo(point));
            Assert.That(context.IncomingDirection, Is.EqualTo(incoming));
            Assert.That(context.SurfaceNormal, Is.EqualTo(normal));
        }

        [Test]
        public void Constructor_PreservesTargetAndAttackerAttribution()
        {
            GameObject targetObject = new GameObject("impact-target");
            HealthComponent target = targetObject.AddComponent<HealthComponent>();

            var context = new ProjectileImpactContext(
                Vector3.one,
                Vector3.forward,
                Vector3.up,
                target,
                attackerId: 73,
                attackerTeam: 2);

            Assert.That(context.Target, Is.SameAs(target));
            Assert.That(context.AttackerId, Is.EqualTo(73));
            Assert.That(context.AttackerTeam, Is.EqualTo(2));
            Object.DestroyImmediate(targetObject);
        }

        [Test]
        public void LegacyConstructor_UsesNoTargetOrAttribution()
        {
            var context = new ProjectileImpactContext(Vector3.zero, Vector3.forward, Vector3.up);

            Assert.That(context.Target, Is.Null);
            Assert.That(context.AttackerId, Is.Zero);
            Assert.That(context.AttackerTeam, Is.Zero);
        }
    }
}
