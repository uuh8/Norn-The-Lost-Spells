using NUnit.Framework;
using UnityEngine;

namespace Game.Combat.Tests
{
    public sealed class ProjectileStatusOnImpactTests
    {
        [Test]
        public void TryApply_AppliesConfiguredStatusToImpactTarget()
        {
            GameObject projectileObject = new GameObject("status-projectile");
            ProjectileStatusOnImpact applicator = projectileObject.AddComponent<ProjectileStatusOnImpact>();
            applicator.ConfigureForTests(StatusKind.Sticky, 20f);

            GameObject targetObject = new GameObject("status-target");
            HealthComponent health = targetObject.AddComponent<HealthComponent>();
            StatusController statuses = targetObject.AddComponent<StatusController>();
            StatusDefinition sticky = CreateStickyDefinition();
            statuses.SetDefinitionsForTests(sticky);
            var context = new ProjectileImpactContext(
                Vector3.zero,
                Vector3.forward,
                Vector3.up,
                health,
                attackerId: 91,
                attackerTeam: 3);

            bool applied = applicator.TryApply(in context);

            Assert.That(applied, Is.True);
            Assert.That(statuses.GetIntensity(StatusKind.Sticky), Is.EqualTo(20f).Within(1e-4f));
            Object.DestroyImmediate(sticky);
            Object.DestroyImmediate(targetObject);
            Object.DestroyImmediate(projectileObject);
        }

        [Test]
        public void TryApply_DoesNothingForEnvironmentImpact()
        {
            GameObject projectileObject = new GameObject("status-projectile");
            ProjectileStatusOnImpact applicator = projectileObject.AddComponent<ProjectileStatusOnImpact>();
            applicator.ConfigureForTests(StatusKind.Wet, 15f);
            var context = new ProjectileImpactContext(Vector3.zero, Vector3.forward, Vector3.up);

            Assert.That(applicator.TryApply(in context), Is.False);
            Object.DestroyImmediate(projectileObject);
        }

        [Test]
        public void TryApply_DoesNothingWhenAmountIsNotPositive()
        {
            GameObject projectileObject = new GameObject("status-projectile");
            ProjectileStatusOnImpact applicator = projectileObject.AddComponent<ProjectileStatusOnImpact>();
            applicator.ConfigureForTests(StatusKind.Poisoned, 0f);
            var context = new ProjectileImpactContext(Vector3.zero, Vector3.forward, Vector3.up);

            Assert.That(applicator.TryApply(in context), Is.False);
            Object.DestroyImmediate(projectileObject);
        }

        private static StatusDefinition CreateStickyDefinition()
        {
            StatusDefinition definition = ScriptableObject.CreateInstance<StatusDefinition>();
            definition.Kind = StatusKind.Sticky;
            definition.DisplayName = "Sticky";
            definition.AffectsMoveSpeed = true;
            definition.MaxMoveSpeedSlowRatio = 0.4f;
            return definition;
        }
    }
}
