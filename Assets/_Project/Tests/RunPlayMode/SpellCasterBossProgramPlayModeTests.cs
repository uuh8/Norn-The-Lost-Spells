using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Character;
using Game.Combat;
using Game.Skills;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.Run.Tests
{
    /// <summary>
    /// Impact Payload 依赖真实 Projectile 生命周期，因此必须在 PlayMode 验证。
    /// 若放进 EditMode，ProjectileBase.Init 的延迟 Destroy 会被 Unity 正确判定为非法测试环境。
    /// </summary>
    public sealed class SpellCasterBossProgramPlayModeTests
    {
        private readonly List<Object> _createdObjects = new List<Object>();

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                if (_createdObjects[i] != null)
                {
                    Object.Destroy(_createdObjects[i]);
                }
            }

            _createdObjects.Clear();
            yield return null;
        }

        [UnityTest] public IEnumerator ThreeHalfLifeCardsUsePrefabLifetimeAtRuntime(){return CheckHalfLife(-1);}
        [UnityTest] public IEnumerator ThreeHalfLifeCardsUseExplicitLifetimeAtRuntime(){return CheckHalfLife(5);}
        private IEnumerator CheckHalfLife(float lifetime)
        {
            var prefab=CreateProjectilePrefab();
            SetPrivateField(prefab,"_maxLifetime",5f);
            var emit=CreateEmit("Lifetime",0,prefab.gameObject);emit.LifetimeSeconds=lifetime;
            var modifier=CreateEmit("Half",0,null);modifier.Kind=SpellKind.Modify;modifier.ModLifetimeMul=.5f;
            var wand=CreateWand(modifier,modifier,modifier,emit);var caster=CreateZeroManaCaster(wand);
            Assert.That(caster.CastProgram(wand,new Vector3(200,100,200),Vector3.forward,2,100,null,SpellManaPolicy.IgnoreMana),Is.EqualTo(1));
            var projectile=FindRuntimeProjectile(prefab);Assert.That(projectile,Is.Not.Null);_createdObjects.Add(projectile.gameObject);
            // 测试模板刻意inactive以免自身参与场景；克隆需激活才会收到Unity的Update生命周期。
            projectile.gameObject.SetActive(true);
            var remaining=typeof(ProjectileBase).GetField("_lifetimeRemaining",BindingFlags.Instance|BindingFlags.NonPublic);
            Assert.That((float)remaining.GetValue(projectile),Is.EqualTo(.625f).Within(.0001f));
            bool expired=false;projectile.Terminated+=(reason,point,direction)=>expired=reason==ProjectileTerminationReason.LifetimeExpired;
            yield return new WaitForSeconds(.3f);Assert.That(projectile!=null,Is.True);
            yield return new WaitForSeconds(.5f);Assert.That(expired,Is.True);Assert.That(projectile==null,Is.True);
        }

        [UnityTest]
        public IEnumerator CastProgram_IgnoreManaPropagatesIntoImpactPayload()
        {
            BossProgramTestProjectile projectilePrefab =
                CreateProjectilePrefab();
            SpellDefinition trigger = CreateEmit(
                "Trigger",
                10f,
                projectilePrefab.gameObject);
            trigger.PayloadTrigger = PayloadTriggerMode.OnImpact;
            SpellDefinition payload = CreateEmit("Payload", 10f, null);
            WandLoadout wand = CreateWand(trigger, payload);
            SpellCaster caster = CreateZeroManaCaster(wand);

            Assert.That(
                caster.CastProgram(
                    wand,
                    Vector3.zero,
                    Vector3.forward,
                    2,
                    100,
                    null,
                    SpellManaPolicy.IgnoreMana),
                Is.EqualTo(1));

            BossProgramTestProjectile runtimeProjectile =
                FindRuntimeProjectile(projectilePrefab);
            Assert.That(runtimeProjectile, Is.Not.Null);
            _createdObjects.Add(runtimeProjectile.gameObject);

            LogAssert.Expect(
                LogType.Warning,
                "[Skills] [SpellCaster] EmitCommand.ProjectilePrefab 为空，跳过该法术产出");
            InvokeImpact(runtimeProjectile);
            yield return null;
        }

        private SpellCaster CreateZeroManaCaster(WandLoadout wand)
        {
            GameObject casterObject = new GameObject("Boss SpellCaster");
            casterObject.SetActive(false);
            _createdObjects.Add(casterObject);

            ManaComponent mana = casterObject.AddComponent<ManaComponent>();
            SetPrivateField(mana, "_maxMana", 10f);
            SetPrivateField(mana, "_startFull", false);

            SpellCaster caster = casterObject.AddComponent<SpellCaster>();
            SetPrivateField(caster, "_wand", wand);
            SetPrivateField(caster, "_mana", mana);
            casterObject.SetActive(true);
            return caster;
        }

        private BossProgramTestProjectile CreateProjectilePrefab()
        {
            GameObject projectileObject =
                new GameObject("Test Projectile Prefab");
            projectileObject.SetActive(false);
            projectileObject.AddComponent<Rigidbody>();
            projectileObject.AddComponent<SphereCollider>();
            BossProgramTestProjectile projectile =
                projectileObject.AddComponent<BossProgramTestProjectile>();
            _createdObjects.Add(projectileObject);
            return projectile;
        }

        private SpellDefinition CreateEmit(
            string displayName,
            float manaCost,
            GameObject projectilePrefab)
        {
            SpellDefinition spell =
                ScriptableObject.CreateInstance<SpellDefinition>();
            spell.DisplayName = displayName;
            spell.Kind = SpellKind.Emit;
            spell.ManaCost = manaCost;
            spell.ProjectilePrefab = projectilePrefab;
            _createdObjects.Add(spell);
            return spell;
        }

        private WandLoadout CreateWand(params SpellDefinition[] spells)
        {
            WandLoadout wand = ScriptableObject.CreateInstance<WandLoadout>();
            wand.Spells = spells;
            wand.BaseDraws = 1;
            _createdObjects.Add(wand);
            return wand;
        }

        private static BossProgramTestProjectile FindRuntimeProjectile(
            BossProgramTestProjectile prefab)
        {
            BossProgramTestProjectile[] projectiles =
                Object.FindObjectsByType<BossProgramTestProjectile>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
            for (int i = 0; i < projectiles.Length; i++)
            {
                if (projectiles[i] != prefab)
                {
                    return projectiles[i];
                }
            }

            return null;
        }

        private static void InvokeImpact(ProjectileBase projectile)
        {
            FieldInfo field = typeof(ProjectileBase).GetField(
                "Impacted",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            var handler =
                (Action<Vector3, Vector3>)field.GetValue(projectile);
            Assert.That(handler, Is.Not.Null);
            handler.Invoke(Vector3.zero, Vector3.forward);
        }

        private static void SetPrivateField(
            object target,
            string fieldName,
            object value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(
                field,
                Is.Not.Null,
                $"找不到测试字段 {fieldName}。");
            field.SetValue(target, value);
        }
    }

    public sealed class BossProgramTestProjectile : ProjectileBase
    {
    }
}
