using NUnit.Framework;
using UnityEngine;
using Game.Skills;

namespace Game.Skills.Tests
{
    public class CastModifierStateTests
    {
        private static SpellDefinition Modify(float dmgMul = 1f, float speedMul = 1f, float dmgAdd = 0f, float spread = 0f,
                                               int bounce = 0, bool useGravity = false,
                                               float homingRadius = 0f, float homingDuration = 0f, float homingTurnRate = 0f,
                                               float orbitRadius = 0f, float orbitAngularSpeed = 0f,
                                               float orbitPhaseOffset = 0f, float orbitPlaneTilt = 0f)
        {
            var s = ScriptableObject.CreateInstance<SpellDefinition>();
            s.Kind = SpellKind.Modify;
            s.ModDamageMul = dmgMul;
            s.ModSpeedMul = speedMul;
            s.ModDamageAddFlat = dmgAdd;
            s.ModSpreadAddDegrees = spread;
            s.ModBounceAdd = bounce;
            s.ModUseGravity = useGravity;
            s.ModHomingRadius = homingRadius;
            s.ModHomingDuration = homingDuration;
            s.ModHomingTurnRateDegrees = homingTurnRate;
            s.ModOrbitRadius = orbitRadius;
            s.ModOrbitAngularSpeedDegrees = orbitAngularSpeed;
            s.ModOrbitPhaseOffsetDegrees = orbitPhaseOffset;
            s.ModOrbitPlaneTiltDegrees = orbitPlaneTilt;
            return s;
        }

        [Test]
        public void Default_IsIdentity()
        {
            var d = CastModifierState.Default;
            Assert.AreEqual(0f, d.DamageAddFlat, 1e-4f);
            Assert.AreEqual(1f, d.DamageMul, 1e-4f);
            Assert.AreEqual(1f, d.SpeedMul, 1e-4f);
            Assert.AreEqual(0f, d.SpreadDegrees, 1e-4f);
            Assert.AreEqual(0, d.BounceCount);
            Assert.IsFalse(d.UseGravity);
            Assert.AreEqual(0f, d.HomingRadius, 1e-4f);
            Assert.AreEqual(0f, d.HomingDuration, 1e-4f);
            Assert.AreEqual(0f, d.HomingTurnRateDegrees, 1e-4f);
            Assert.AreEqual(0f, d.OrbitRadius, 1e-4f);
            Assert.AreEqual(0f, d.OrbitAngularSpeedDegrees, 1e-4f);
            Assert.AreEqual(0f, d.OrbitPhaseOffsetDegrees, 1e-4f);
            Assert.AreEqual(0f, d.OrbitPlaneTiltDegrees, 1e-4f);
            Assert.AreEqual(ProjectileMotionMode.None, d.MotionMode);
        }

        [Test]
        public void Apply_DamageMul_Multiplies_LeavesSpeedUnchanged()
        {
            var s = CastModifierState.Default.Apply(Modify(dmgMul: 1.5f));
            Assert.AreEqual(1.5f, s.DamageMul, 1e-4f);
            Assert.AreEqual(1f, s.SpeedMul, 1e-4f);   // 恒等保持：只改伤害不影响速度
        }

        [Test]
        public void Apply_RepeatedDamageMultipliers_StackTheirBonusAdditively()
        {
            var s = CastModifierState.Default
                .Apply(Modify(dmgMul: 1.5f))
                .Apply(Modify(dmgMul: 1.5f));

            // 每张“1.5 倍”提供 +0.5 倍增量：1 + 0.5 + 0.5 = 2，防止 1.5^n 指数膨胀。
            Assert.AreEqual(2f, s.DamageMul, 1e-4f);
        }

        [Test]
        public void Apply_SpreadAndFlat_AreAdditive()
        {
            var s = CastModifierState.Default.Apply(Modify(dmgAdd: 5f, spread: 15f)).Apply(Modify(dmgAdd: 5f, spread: 15f));
            Assert.AreEqual(10f, s.DamageAddFlat, 1e-4f);
            Assert.AreEqual(30f, s.SpreadDegrees, 1e-4f);
        }

        [Test]
        public void Apply_Bounce_IsAdditive()
        {
            var s = CastModifierState.Default.Apply(Modify(bounce: 1)).Apply(Modify(bounce: 2));
            Assert.AreEqual(3, s.BounceCount);
        }

        [Test]
        public void Apply_Gravity_IsEnabledByAnyModifier()
        {
            var s = CastModifierState.Default
                .Apply(Modify())
                .Apply(Modify(useGravity: true));

            Assert.IsTrue(s.UseGravity);
        }

        [Test]
        public void Apply_Homing_UsesMaxRadiusAndAddsDurationAndTurnRate()
        {
            var s = CastModifierState.Default
                .Apply(Modify(homingRadius: 6f, homingDuration: 0.5f, homingTurnRate: 90f))
                .Apply(Modify(homingRadius: 8f, homingDuration: 0.4f, homingTurnRate: 60f));

            Assert.AreEqual(8f, s.HomingRadius, 1e-4f);
            Assert.AreEqual(0.9f, s.HomingDuration, 1e-4f);
            Assert.AreEqual(150f, s.HomingTurnRateDegrees, 1e-4f);
        }

        [Test]
        public void Apply_Orbit_UsesMaxRadiusAddsAngularSpeedAndPhaseOffsetAndUsesMaxAbsPlaneTilt()
        {
            var s = CastModifierState.Default
                .Apply(Modify(orbitRadius: 0.6f, orbitAngularSpeed: 180f, orbitPhaseOffset: 15f, orbitPlaneTilt: 25f))
                .Apply(Modify(orbitRadius: 1.0f, orbitAngularSpeed: 90f, orbitPhaseOffset: 30f, orbitPlaneTilt: -35f));

            Assert.AreEqual(1.0f, s.OrbitRadius, 1e-4f);
            Assert.AreEqual(270f, s.OrbitAngularSpeedDegrees, 1e-4f);
            Assert.AreEqual(45f, s.OrbitPhaseOffsetDegrees, 1e-4f);
            Assert.AreEqual(-35f, s.OrbitPlaneTiltDegrees, 1e-4f);
            Assert.AreEqual(ProjectileMotionMode.Orbit, s.MotionMode);
        }

        [Test]
        public void Apply_MotionControl_LaterHomingOverridesEarlierOrbit()
        {
            var s = CastModifierState.Default
                .Apply(Modify(orbitRadius: 0.8f, orbitAngularSpeed: 360f, orbitPhaseOffset: 0f, orbitPlaneTilt: 35f))
                .Apply(Modify(homingRadius: 8f, homingDuration: 0.6f, homingTurnRate: 120f));

            Assert.AreEqual(ProjectileMotionMode.Homing, s.MotionMode);
        }

        [Test]
        public void Apply_MotionControl_LaterOrbitOverridesEarlierHoming()
        {
            var s = CastModifierState.Default
                .Apply(Modify(homingRadius: 8f, homingDuration: 0.6f, homingTurnRate: 120f))
                .Apply(Modify(orbitRadius: 0.8f, orbitAngularSpeed: 360f, orbitPhaseOffset: 0f, orbitPlaneTilt: 35f));

            Assert.AreEqual(ProjectileMotionMode.Orbit, s.MotionMode);
        }
    }
}
