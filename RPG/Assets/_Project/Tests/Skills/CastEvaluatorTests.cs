using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Game.Skills;
using Game.Combat;

namespace Game.Skills.Tests
{
    public class CastEvaluatorTests
    {
        private readonly List<EmitCommand> _out = new List<EmitCommand>();

        // ── 构造测试用法术 ──
        private static SpellDefinition Emit(float dmg = 10f, float speed = 20f, float mana = 0f,
                                            PayloadTriggerMode trigger = PayloadTriggerMode.None,
                                            float delay = 1f)
        {
            var s = ScriptableObject.CreateInstance<SpellDefinition>();
            s.Kind = SpellKind.Emit;
            s.BaseDamage = dmg; s.BaseSpeed = speed; s.DamageType = DamageType.Magical; s.ManaCost = mana;
            s.PayloadTrigger = trigger;
            s.PayloadDelaySeconds = delay;
            return s;
        }

        private static SpellDefinition Static(
            float dmg = 10f,
            float speed = 20f,
            float mana = 0f,
            float explosionDamage = 0f,
            float fireFieldDamagePerTick = 0f,
            float fireFieldTickInterval = 0.5f,
            float fireFieldDuration = 0f)
        {
            var s = ScriptableObject.CreateInstance<SpellDefinition>();
            s.Kind = SpellKind.StaticProjectile;
            s.SpawnMode = SpellSpawnMode.SkyfallAtPoint;
            s.BaseDamage = dmg;
            s.ExplosionDamage = explosionDamage;
            s.FireFieldDamagePerTick = fireFieldDamagePerTick;
            s.FireFieldTickInterval = fireFieldTickInterval;
            s.FireFieldDuration = fireFieldDuration;
            s.BaseSpeed = speed;
            s.DamageType = DamageType.Magical;
            s.ManaCost = mana;
            s.SkyfallHeight = 12f;
            s.SkyfallBackOffset = 0f;
            s.LandingSiteDuration = 0.8f;
            return s;
        }

        private static SpellDefinition Shield(int reflectCount = 3, float mana = 0f)
        {
            var s = ScriptableObject.CreateInstance<SpellDefinition>();
            s.Kind = SpellKind.StaticProjectile;
            s.SpawnMode = SpellSpawnMode.StaticAtPoint;
            s.ManaCost = mana;
            s.ShieldReflectCount = reflectCount;
            return s;
        }

        private static SpellDefinition ShieldEmit(int reflectCount = 3, float speed = 10f, float mana = 0f)
        {
            var s = ScriptableObject.CreateInstance<SpellDefinition>();
            s.Kind = SpellKind.Emit;
            s.SpawnMode = SpellSpawnMode.ForwardProjectile;
            s.BaseDamage = 0f;
            s.BaseSpeed = speed;
            s.DamageType = DamageType.Magical;
            s.ManaCost = mana;
            s.ShieldReflectCount = reflectCount;
            return s;
        }

        private static SpellDefinition DamageMod(float mul, float mana = 0f)
        {
            var s = ScriptableObject.CreateInstance<SpellDefinition>();
            s.Kind = SpellKind.Modify; s.ModDamageMul = mul; s.ManaCost = mana;
            return s;
        }
        private static SpellDefinition Multi(int extra, float mana = 0f)
        {
            var s = ScriptableObject.CreateInstance<SpellDefinition>();
            s.Kind = SpellKind.Multicast; s.ExtraDraws = extra; s.ManaCost = mana;
            return s;
        }

        private CastSummary Run(int baseDraws, float mana, params SpellDefinition[] wand)
            => CastEvaluator.Evaluate(wand, baseDraws, mana, CastModifierState.Default, _out);

        [Test]
        public void SpellKind_StaticProjectile_AppendsAfterExistingKinds()
        {
            Assert.AreEqual(0, (int)SpellKind.Emit);
            Assert.AreEqual(1, (int)SpellKind.Modify);
            Assert.AreEqual(2, (int)SpellKind.Multicast);
            Assert.AreEqual(3, (int)SpellKind.StaticProjectile);
        }

        [Test]
        public void SpellSpawnMode_StaticAtPoint_AppendsAfterExistingModes()
        {
            Assert.AreEqual(0, (int)SpellSpawnMode.ForwardProjectile);
            Assert.AreEqual(1, (int)SpellSpawnMode.SkyfallAtPoint);
            Assert.AreEqual(2, (int)SpellSpawnMode.StaticAtPoint);
        }

        [Test]
        public void EmptyWand_EmitsNothing()
        {
            Run(1, 999f);
            Assert.AreEqual(0, _out.Count);
        }

        [Test]
        public void SingleFireball_EmitsOne_WithBaseValues()
        {
            Run(1, 999f, Emit(dmg: 15f, speed: 20f));
            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(15f, _out[0].Damage, 1e-4f);
            Assert.AreEqual(20f, _out[0].Speed, 1e-4f);
        }

        [Test]
        public void StaticProjectile_EmitsOneCommand_WithSkyfallSpawnMode()
        {
            Run(1, 999f, Static(dmg: 25f, speed: 18f));

            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(25f, _out[0].Damage, 1e-4f);
            Assert.AreEqual(18f, _out[0].Speed, 1e-4f);
            Assert.AreEqual(SpellSpawnMode.SkyfallAtPoint, _out[0].SpawnMode);
            Assert.AreEqual(12f, _out[0].SkyfallHeight, 1e-4f);
            Assert.AreEqual(0.8f, _out[0].LandingSiteDuration, 1e-4f);
        }

        [Test]
        public void StaticProjectile_BakesThreeStageDamageSnapshot()
        {
            Run(1, 999f, Static(
                dmg: 10f,
                explosionDamage: 15f,
                fireFieldDamagePerTick: 2f,
                fireFieldTickInterval: 0.5f,
                fireFieldDuration: 5f));

            Assert.AreEqual(10f, _out[0].Damage, 1e-4f);
            Assert.AreEqual(15f, _out[0].ExplosionDamage, 1e-4f);
            Assert.AreEqual(2f, _out[0].FireFieldDamagePerTick, 1e-4f);
            Assert.AreEqual(0.5f, _out[0].FireFieldTickInterval, 1e-4f);
            Assert.AreEqual(5f, _out[0].FireFieldDuration, 1e-4f);
        }

        [Test]
        public void DamageMultiplier_ScalesEveryMeteorDamageChannel()
        {
            Run(1, 999f,
                DamageMod(1.5f),
                Static(
                    dmg: 10f,
                    explosionDamage: 15f,
                    fireFieldDamagePerTick: 2f));

            Assert.AreEqual(15f, _out[0].Damage, 1e-4f);
            Assert.AreEqual(22.5f, _out[0].ExplosionDamage, 1e-4f);
            Assert.AreEqual(3f, _out[0].FireFieldDamagePerTick, 1e-4f);
        }

        [Test]
        public void RepeatedDamageMultipliers_ProduceLinearFinalDamage()
        {
            Run(1, 999f,
                DamageMod(1.5f),
                DamageMod(1.5f),
                DamageMod(1.5f),
                Emit(dmg: 10f));

            // 三张增幅各贡献 +0.5 倍，最终倍率 2.5；旧乘法规则会错误地产生 33.75 伤害。
            Assert.AreEqual(25f, _out[0].Damage, 1e-4f);
        }

        [Test]
        public void FlatDamageBonus_DoesNotRepeatOnExplosionOrEveryFireFieldTick()
        {
            SpellDefinition flat = ScriptableObject.CreateInstance<SpellDefinition>();
            flat.Kind = SpellKind.Modify;
            flat.ModDamageAddFlat = 5f;
            flat.ModDamageMul = 1f;

            Run(1, 999f,
                flat,
                Static(
                    dmg: 10f,
                    explosionDamage: 15f,
                    fireFieldDamagePerTick: 2f));

            Assert.AreEqual(15f, _out[0].Damage, 1e-4f);
            Assert.AreEqual(15f, _out[0].ExplosionDamage, 1e-4f);
            Assert.AreEqual(2f, _out[0].FireFieldDamagePerTick, 1e-4f);
        }

        [Test]
        public void StaticProjectile_ConsumesDrawBudget()
        {
            Run(1, 999f, Static(), Emit());

            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(SpellSpawnMode.SkyfallAtPoint, _out[0].SpawnMode);
        }

        [Test]
        public void StaticProjectile_Shield_BakesStaticAtPointAndReflectCount()
        {
            Run(1, 999f, Shield(reflectCount: 4));

            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(SpellSpawnMode.StaticAtPoint, _out[0].SpawnMode);
            Assert.AreEqual(4, _out[0].ShieldReflectCount);
        }

        [Test]
        public void ShieldStaticProjectile_ConsumesDrawBudgetLikeOtherStaticProjectiles()
        {
            Run(1, 999f, Shield(reflectCount: 3), Emit());

            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(SpellSpawnMode.StaticAtPoint, _out[0].SpawnMode);
        }

        [Test]
        public void DamageMod_BeforeEmit_BoostsIt()
        {
            Run(1, 999f, DamageMod(1.5f), Emit(dmg: 15f));
            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(22.5f, _out[0].Damage, 1e-4f);
        }

        [Test]
        public void DamageMod_AfterEmit_DoesNotBoostIt()
        {
            Run(1, 999f, Emit(dmg: 15f), DamageMod(1.5f));
            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(15f, _out[0].Damage, 1e-4f); // 修正只影响其后
        }

        [Test]
        public void Modify_AffectsFollowingStaticProjectile()
        {
            Run(1, 999f, DamageMod(2f), Static(dmg: 25f));

            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(50f, _out[0].Damage, 1e-4f);
        }

        [Test]
        public void BaseDraws_LimitsEmits()
        {
            Run(1, 999f, Emit(), Emit());          // 预算 1 → 只产出第一发
            Assert.AreEqual(1, _out.Count);

            Run(2, 999f, Emit(), Emit());          // 预算 2 → 两发都产出
            Assert.AreEqual(2, _out.Count);
        }

        [Test]
        public void Triple_WithThreeProjectiles_EmitsThree()
        {
            Run(1, 999f, Multi(2), Emit(), Emit(), Emit()); // 预算 1+2=3
            Assert.AreEqual(3, _out.Count);
        }

        [Test]
        public void Triple_DamageMod_AppliesToAllEmits()
        {
            Run(1, 999f, Multi(2), DamageMod(2f), Emit(dmg: 10f), Emit(dmg: 10f), Emit(dmg: 10f));
            Assert.AreEqual(3, _out.Count);
            foreach (var e in _out)
                Assert.AreEqual(20f, e.Damage, 1e-4f); // 多重内修正一次成本、作用于全部
        }

        [Test]
        public void Multicast_CountsStaticProjectileAsProjectileOutput()
        {
            Run(1, 999f, Multi(2), Static(), Emit(), Emit());

            Assert.AreEqual(3, _out.Count);
            Assert.AreEqual(SpellSpawnMode.SkyfallAtPoint, _out[0].SpawnMode);
            Assert.AreEqual(SpellSpawnMode.ForwardProjectile, _out[1].SpawnMode);
            Assert.AreEqual(SpellSpawnMode.ForwardProjectile, _out[2].SpawnMode);
        }

        [Test]
        public void Multicast_BudgetUnfilled_IsDiscarded_NoWrap()
        {
            Run(1, 999f, Multi(2), Emit()); // 预算 3 但只有 1 个投射物 → 产出 1（单遍不回绕，余量作废）
            Assert.AreEqual(1, _out.Count);
        }

        [Test]
        public void Multicast_AfterEmit_ReopensBudget()
        {
            Run(1, 999f, Emit(), Multi(2), Emit()); // 1→产出#1(预算0)→+2(预算2)→产出#2
            Assert.AreEqual(2, _out.Count);
        }

        [Test]
        public void Preview_FireballThenMulticast_ReportsImmediateCostAndRemainingBudget()
        {
            // 基础预算 1：火球消耗后为 0；双重射击即使此时没有预算也会被读取，并把预算加回 2。
            CastPreview preview = CastEvaluator.Preview(
                new[] { Emit(mana: 7f), Multi(2, mana: 3f) },
                1,
                CastModifierState.Default);

            Assert.AreEqual(10f, preview.ImmediateManaCost, 1e-4f);
            Assert.AreEqual(2, preview.RemainingDrawBudget);
        }

        [Test]
        public void EnoughMana_EmitsAll_ReportsSpent()
        {
            var summary = Run(1, 100f, Multi(2), Emit(mana: 6f), Emit(mana: 6f), Emit(mana: 6f));
            Assert.AreEqual(3, _out.Count);
            Assert.IsFalse(summary.Fizzled);
            Assert.AreEqual(18f, summary.ManaSpent, 1e-4f);
        }

        [Test]
        public void ManaCost_IncludesModifyAndMulticast_WhenTheyAreRead()
        {
            var summary = Run(1, 100f, Multi(2, mana: 3f), DamageMod(2f, mana: 4f), Emit(mana: 5f));

            Assert.AreEqual(1, _out.Count);
            Assert.IsFalse(summary.Fizzled);
            Assert.AreEqual(12f, summary.ManaSpent, 1e-4f);
            Assert.AreEqual(20f, _out[0].Damage, 1e-4f);
        }

        [Test]
        public void InsufficientMana_OnModify_FizzlesBeforeFollowingEmit()
        {
            var summary = Run(1, 3f, DamageMod(2f, mana: 4f), Emit(mana: 0f));

            Assert.AreEqual(0, _out.Count);
            Assert.IsTrue(summary.Fizzled);
            Assert.AreEqual(0f, summary.ManaSpent, 1e-4f);
        }

        [Test]
        public void EstimateManaCost_IncludesCurrentLayerOnly_AndDoesNotPrepayTriggerPayload()
        {
            float mana = CastEvaluator.EstimateManaCost(
                new[] { Multi(2, mana: 3f), Emit(mana: 5f, trigger: PayloadTriggerMode.OnImpact), DamageMod(2f, mana: 11f), Emit(mana: 13f) },
                1,
                CastModifierState.Default);

            Assert.AreEqual(8f, mana, 1e-4f);
        }

        [Test]
        public void InsufficientMana_FizzlesMidCast()
        {
            // 可用 10，每发 6：第 1 发后剩 4，第 2 发 6>4 → fizzle
            var summary = Run(1, 10f, Multi(2), Emit(mana: 6f), Emit(mana: 6f), Emit(mana: 6f));
            Assert.AreEqual(1, _out.Count);
            Assert.IsTrue(summary.Fizzled);
            Assert.AreEqual(6f, summary.ManaSpent, 1e-4f);
        }

        [Test]
        public void ZeroCostSpells_NeverFizzle()
        {
            var summary = Run(1, 0f, Emit(mana: 0f));
            Assert.AreEqual(1, _out.Count);
            Assert.IsFalse(summary.Fizzled);
        }

        // 通用修正构造（覆盖 增伤/加速/平铺加伤/散射 四类），用于验证 BakeEmit 端到端写入
        private static SpellDefinition Mod(float dmgMul = 1f, float speedMul = 1f, float dmgAdd = 0f, float spread = 0f,
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
        public void Modifiers_BakeSpeedSpreadAndFlatDamage_IntoEmit()
        {
            // 伤害 = (10 + 5) * 2 = 30；速度 = 20 * 1.5 = 30；散射 = 15
            Run(1, 999f, Mod(dmgMul: 2f, speedMul: 1.5f, dmgAdd: 5f, spread: 15f), Emit(dmg: 10f, speed: 20f));
            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(30f, _out[0].Damage, 1e-4f);
            Assert.AreEqual(30f, _out[0].Speed, 1e-4f);
            Assert.AreEqual(15f, _out[0].SpreadDegrees, 1e-4f);
        }

        [Test]
        public void BounceModifier_BakesBounceCount_IntoEmit()
        {
            Run(1, 999f, Mod(bounce: 2), Emit());
            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(2, _out[0].BounceCount);
        }

        [Test]
        public void BounceModifier_StacksAdditively()
        {
            Run(1, 999f, Mod(bounce: 1), Mod(bounce: 2), Emit());
            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(3, _out[0].BounceCount);
        }

        [Test]
        public void GravityModifier_BakesUseGravity_IntoEmit()
        {
            Run(1, 999f, Mod(useGravity: true), Emit());
            Assert.AreEqual(1, _out.Count);
            Assert.IsTrue(_out[0].UseGravity);
        }

        [Test]
        public void HomingModifier_BakesHomingConfig_IntoEmit()
        {
            Run(1, 999f, Mod(homingRadius: 6f, homingDuration: 0.6f, homingTurnRate: 120f), Emit());
            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(6f, _out[0].HomingRadius, 1e-4f);
            Assert.AreEqual(0.6f, _out[0].HomingDuration, 1e-4f);
            Assert.AreEqual(120f, _out[0].HomingTurnRateDegrees, 1e-4f);
        }

        [Test]
        public void HomingModifier_OnlyAffectsFollowingEmit()
        {
            Run(2, 999f, Emit(), Mod(homingRadius: 6f, homingDuration: 0.6f, homingTurnRate: 120f), Emit());
            Assert.AreEqual(2, _out.Count);
            Assert.AreEqual(0f, _out[0].HomingRadius, 1e-4f);
            Assert.AreEqual(6f, _out[1].HomingRadius, 1e-4f);
            Assert.AreEqual(0.6f, _out[1].HomingDuration, 1e-4f);
            Assert.AreEqual(120f, _out[1].HomingTurnRateDegrees, 1e-4f);
        }

        [Test]
        public void LaterHomingMotionModifier_OverridesEarlierOrbitForEmit()
        {
            Run(3, 999f,
                Mod(orbitRadius: 0.8f, orbitAngularSpeed: 360f, orbitPlaneTilt: 35f),
                Mod(homingRadius: 8f, homingDuration: 0.6f, homingTurnRate: 120f),
                Emit());

            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(ProjectileMotionMode.Homing, _out[0].MotionMode);
        }

        [Test]
        public void LaterOrbitMotionModifier_OverridesEarlierHomingForEmit()
        {
            Run(3, 999f,
                Mod(homingRadius: 8f, homingDuration: 0.6f, homingTurnRate: 120f),
                Mod(orbitRadius: 0.8f, orbitAngularSpeed: 360f, orbitPlaneTilt: 35f),
                Emit());

            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(ProjectileMotionMode.Orbit, _out[0].MotionMode);
        }

        [Test]
        public void OrbitModifier_BakesOrbitConfig_IntoEmit()
        {
            Run(1, 999f, Mod(orbitRadius: 0.8f, orbitAngularSpeed: 360f, orbitPhaseOffset: 45f, orbitPlaneTilt: 35f), Emit());

            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(0.8f, _out[0].OrbitRadius, 1e-4f);
            Assert.AreEqual(360f, _out[0].OrbitAngularSpeedDegrees, 1e-4f);
            Assert.AreEqual(45f, _out[0].OrbitPhaseOffsetDegrees, 1e-4f);
            Assert.AreEqual(35f, _out[0].OrbitPlaneTiltDegrees, 1e-4f);
        }

        [Test]
        public void NullSpellEntry_IsSkipped()
        {
            Run(1, 999f, null, Emit(dmg: 10f));
            Assert.AreEqual(1, _out.Count);
        }

        [Test]
        public void NonTrigger_HasNoPayload()
        {
            Run(1, 999f, Emit());
            Assert.AreEqual(1, _out.Count);
            Assert.IsFalse(_out[0].HasPayload);
        }

        [Test]
        public void OnImpactTrigger_CapturesOneActionAsPayload_AndEndsWhenOuterBudgetIsSpent()
        {
            // Trigger 只捕获后面的一个完整 Action；BaseDraws=1 用完后，本层结束。
            Run(1, 999f, Emit(trigger: PayloadTriggerMode.OnImpact), Emit(), Emit());
            Assert.AreEqual(1, _out.Count);
            Assert.IsTrue(_out[0].HasPayload);
            Assert.AreEqual(PayloadTriggerMode.OnImpact, _out[0].PayloadTrigger);
            Assert.AreEqual(1, _out[0].Payload.Count);
        }

        [Test]
        public void TripleMulticast_DrawsThreeIndependentTriggerActions()
        {
            SpellDefinition trigger1 = Emit(trigger: PayloadTriggerMode.OnImpact);
            SpellDefinition meteor1 = Static(dmg: 11f);
            SpellDefinition trigger2 = Emit(trigger: PayloadTriggerMode.OnImpact);
            SpellDefinition meteor2 = Static(dmg: 22f);
            SpellDefinition trigger3 = Emit(trigger: PayloadTriggerMode.OnImpact);
            SpellDefinition meteor3 = Static(dmg: 33f);

            Run(
                1,
                999f,
                Multi(2),
                trigger1, meteor1,
                trigger2, meteor2,
                trigger3, meteor3);

            Assert.AreEqual(3, _out.Count);
            Assert.AreSame(meteor1, _out[0].Payload[0]);
            Assert.AreSame(meteor2, _out[1].Payload[0]);
            Assert.AreSame(meteor3, _out[2].Payload[0]);
        }

        [Test]
        public void TriggerPayload_ActionIncludesInnerMulticastAndAllOfItsDraws()
        {
            SpellDefinition trigger = Emit(trigger: PayloadTriggerMode.OnImpact);
            SpellDefinition triple = Multi(2);
            SpellDefinition fireball1 = Emit();
            SpellDefinition fireball2 = Emit();
            SpellDefinition fireball3 = Emit();

            Run(
                1,
                999f,
                trigger,
                triple,
                fireball1,
                fireball2,
                fireball3,
                Emit());

            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(4, _out[0].Payload.Count);
            Assert.AreSame(triple, _out[0].Payload[0]);
            Assert.AreSame(fireball3, _out[0].Payload[3]);
        }

        [Test]
        public void NestedTrigger_ActionCaptureRemainsStrictlyShorter()
        {
            SpellDefinition outer = Emit(trigger: PayloadTriggerMode.OnImpact);
            SpellDefinition inner = Emit(trigger: PayloadTriggerMode.OnImpact);
            SpellDefinition meteor = Static();

            Run(1, 999f, outer, inner, meteor);

            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(2, _out[0].Payload.Count);
            Assert.AreSame(inner, _out[0].Payload[0]);
            Assert.AreSame(meteor, _out[0].Payload[1]);
        }

        [Test]
        public void TriggerPayload_CanCaptureStaticProjectile()
        {
            Run(1, 999f, Emit(trigger: PayloadTriggerMode.OnImpact), Static());

            Assert.AreEqual(1, _out.Count);
            Assert.IsTrue(_out[0].HasPayload);
            Assert.AreEqual(1, _out[0].Payload.Count);
            Assert.AreEqual(SpellKind.StaticProjectile, _out[0].Payload[0].Kind);
        }

        [Test]
        public void TriggerPayload_CanCaptureShieldStaticProjectile()
        {
            Run(1, 999f, Emit(trigger: PayloadTriggerMode.OnImpact), Shield(reflectCount: 2));

            Assert.AreEqual(1, _out.Count);
            Assert.IsTrue(_out[0].HasPayload);
            Assert.AreEqual(1, _out[0].Payload.Count);
            Assert.AreEqual(SpellKind.StaticProjectile, _out[0].Payload[0].Kind);
            Assert.AreEqual(SpellSpawnMode.StaticAtPoint, _out[0].Payload[0].SpawnMode);
        }

        [Test]
        public void TriggerPayload_ShieldPreservesIncomingModifierSnapshot()
        {
            Run(1, 999f, Mod(bounce: 2), Emit(trigger: PayloadTriggerMode.OnImpact), Shield(reflectCount: 3));

            Assert.AreEqual(1, _out.Count);
            Assert.IsTrue(_out[0].HasPayload);
            Assert.AreEqual(2, _out[0].PayloadMods.BounceCount);
            Assert.AreEqual(SpellSpawnMode.StaticAtPoint, _out[0].Payload[0].SpawnMode);
        }

        [Test]
        public void OnImpactTrigger_PayloadExcludesTriggerItself()
        {
            var trig = Emit(trigger: PayloadTriggerMode.OnImpact);
            var after = Emit();
            Run(1, 999f, trig, after);
            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(1, _out[0].Payload.Count);
            Assert.AreSame(after, _out[0].Payload[0]); // 载荷是"之后的"，不含触发自身
        }

        [Test]
        public void OnImpactTrigger_AtSequenceEnd_HasNoPayloadAndNoRuntimeTrigger()
        {
            Run(1, 999f, Emit(trigger: PayloadTriggerMode.OnImpact)); // 触发后面没东西
            Assert.AreEqual(1, _out.Count);
            Assert.IsFalse(_out[0].HasPayload); // 空载荷 → 命中时不再产出
            Assert.AreEqual(PayloadTriggerMode.None, _out[0].PayloadTrigger);
            Assert.AreEqual(0f, _out[0].PayloadDelaySeconds, 1e-4f);
        }

        [Test]
        public void OnImpactTrigger_IgnoresPayloadDelaySeconds()
        {
            Run(1, 999f, Emit(trigger: PayloadTriggerMode.OnImpact, delay: 1.5f), Emit());
            Assert.AreEqual(1, _out.Count);
            Assert.IsTrue(_out[0].HasPayload);
            Assert.AreEqual(PayloadTriggerMode.OnImpact, _out[0].PayloadTrigger);
            Assert.AreEqual(0f, _out[0].PayloadDelaySeconds, 1e-4f);
        }

        [Test]
        public void AfterDelayTrigger_CapturesOneActionAndDelay()
        {
            Run(1, 999f, Emit(trigger: PayloadTriggerMode.AfterDelay, delay: 1.5f), Emit(), Emit());
            Assert.AreEqual(1, _out.Count);
            Assert.IsTrue(_out[0].HasPayload);
            Assert.AreEqual(PayloadTriggerMode.AfterDelay, _out[0].PayloadTrigger);
            Assert.AreEqual(1.5f, _out[0].PayloadDelaySeconds, 1e-4f);
            Assert.AreEqual(1, _out[0].Payload.Count);
        }

        [Test]
        public void AfterDelayTrigger_AtSequenceEnd_HasNoPayloadAndNoRuntimeTrigger()
        {
            Run(1, 999f, Emit(trigger: PayloadTriggerMode.AfterDelay, delay: 1.5f));
            Assert.AreEqual(1, _out.Count);
            Assert.IsFalse(_out[0].HasPayload);
            Assert.AreEqual(PayloadTriggerMode.None, _out[0].PayloadTrigger);
            Assert.AreEqual(0f, _out[0].PayloadDelaySeconds, 1e-4f);
        }

        [Test]
        public void PayloadTrigger_PayloadModsSnapshotIsPreserved()
        {
            Run(1, 999f, DamageMod(2f), Emit(trigger: PayloadTriggerMode.AfterDelay), Emit(dmg: 10f));
            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(2f, _out[0].PayloadMods.DamageMul, 1e-4f);
        }

        [Test]
        public void PayloadTrigger_PayloadModsPreserveBounceCount()
        {
            Run(1, 999f, Mod(bounce: 2), Emit(trigger: PayloadTriggerMode.AfterDelay), Emit(dmg: 10f));
            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(2, _out[0].BounceCount);
            Assert.AreEqual(2, _out[0].PayloadMods.BounceCount);
        }

        [Test]
        public void PayloadTrigger_PayloadModsPreserveUseGravity()
        {
            Run(1, 999f, Mod(useGravity: true), Emit(trigger: PayloadTriggerMode.AfterDelay), Emit(dmg: 10f));
            Assert.AreEqual(1, _out.Count);
            Assert.IsTrue(_out[0].UseGravity);
            Assert.IsTrue(_out[0].PayloadMods.UseGravity);
        }

        [Test]
        public void PayloadTrigger_PayloadModsPreserveHomingConfig()
        {
            Run(1, 999f, Mod(homingRadius: 6f, homingDuration: 0.6f, homingTurnRate: 120f),
                Emit(trigger: PayloadTriggerMode.AfterDelay), Emit(dmg: 10f));
            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(6f, _out[0].HomingRadius, 1e-4f);
            Assert.AreEqual(6f, _out[0].PayloadMods.HomingRadius, 1e-4f);
            Assert.AreEqual(0.6f, _out[0].PayloadMods.HomingDuration, 1e-4f);
            Assert.AreEqual(120f, _out[0].PayloadMods.HomingTurnRateDegrees, 1e-4f);
        }

        [Test]
        public void PayloadTrigger_PayloadModsPreserveOrbitConfig()
        {
            Run(1, 999f, Mod(orbitRadius: 0.8f, orbitAngularSpeed: 360f, orbitPhaseOffset: 45f, orbitPlaneTilt: 35f),
                Emit(trigger: PayloadTriggerMode.AfterDelay), Emit(dmg: 10f));

            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(0.8f, _out[0].OrbitRadius, 1e-4f);
            Assert.AreEqual(0.8f, _out[0].PayloadMods.OrbitRadius, 1e-4f);
            Assert.AreEqual(360f, _out[0].PayloadMods.OrbitAngularSpeedDegrees, 1e-4f);
            Assert.AreEqual(45f, _out[0].PayloadMods.OrbitPhaseOffsetDegrees, 1e-4f);
            Assert.AreEqual(35f, _out[0].PayloadMods.OrbitPlaneTiltDegrees, 1e-4f);
            Assert.AreEqual(ProjectileMotionMode.Orbit, _out[0].PayloadMods.MotionMode);
        }

        [Test]
        public void EmitShield_BakesForwardProjectileAndReflectCount()
        {
            Run(1, 999f, ShieldEmit(reflectCount: 5, speed: 12f));

            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(SpellSpawnMode.ForwardProjectile, _out[0].SpawnMode);
            Assert.AreEqual(5, _out[0].ShieldReflectCount);
            Assert.AreEqual(12f, _out[0].Speed, 1e-4f);
        }

        [Test]
        public void EmitShield_BakesMotionModifiers()
        {
            Run(1, 999f,
                Mod(bounce: 2, useGravity: true, homingRadius: 6f, homingDuration: 0.6f, homingTurnRate: 120f),
                ShieldEmit(reflectCount: 3, speed: 10f));

            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(SpellSpawnMode.ForwardProjectile, _out[0].SpawnMode);
            Assert.AreEqual(3, _out[0].ShieldReflectCount);
            Assert.AreEqual(2, _out[0].BounceCount);
            Assert.IsTrue(_out[0].UseGravity);
            Assert.AreEqual(6f, _out[0].HomingRadius, 1e-4f);
            Assert.AreEqual(0.6f, _out[0].HomingDuration, 1e-4f);
            Assert.AreEqual(120f, _out[0].HomingTurnRateDegrees, 1e-4f);
        }

        [Test]
        public void StaticProjectile_Shield_BakesOrbitModifier()
        {
            Run(1, 999f,
                Mod(orbitRadius: 0.8f, orbitAngularSpeed: 360f, orbitPhaseOffset: 45f, orbitPlaneTilt: 35f),
                Shield(reflectCount: 3));

            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(SpellSpawnMode.StaticAtPoint, _out[0].SpawnMode);
            Assert.AreEqual(ProjectileMotionMode.Orbit, _out[0].MotionMode);
            Assert.AreEqual(0.8f, _out[0].OrbitRadius, 1e-4f);
            Assert.AreEqual(360f, _out[0].OrbitAngularSpeedDegrees, 1e-4f);
            Assert.AreEqual(45f, _out[0].OrbitPhaseOffsetDegrees, 1e-4f);
            Assert.AreEqual(35f, _out[0].OrbitPlaneTiltDegrees, 1e-4f);
        }
    }
}
