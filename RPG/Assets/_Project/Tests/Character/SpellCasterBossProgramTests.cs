using System.Collections.Generic;
using System.Reflection;
using Game.Combat;
using Game.Skills;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.Character.Tests
{
    public sealed class SpellCasterBossProgramTests
    {
        private readonly List<Object> _createdObjects = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                if (_createdObjects[i] != null)
                {
                    Object.DestroyImmediate(_createdObjects[i]);
                }
            }

            _createdObjects.Clear();
        }

        [Test]
        public void CastProgram_IgnoreManaDoesNotChangePlayerCastPolicy()
        {
            SpellDefinition emit = CreateEmit("Expensive Emit", 10f, null);
            WandLoadout wand = CreateWand(emit);
            SpellCaster caster = CreateZeroManaCaster(wand, out ManaComponent mana);

            Assert.That(
                caster.CastWand(
                    Vector3.zero,
                    Vector3.forward,
                    2,
                    100,
                    null),
                Is.Zero,
                "Player 的 CastWand 仍必须因 Mana 不足而失败。");

            LogAssert.Expect(
                LogType.Warning,
                "[Skills] [SpellCaster] EmitCommand.ProjectilePrefab 为空，跳过该法术产出");
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
            Assert.That(mana.CurrentMana, Is.Zero);
        }

        private SpellCaster CreateZeroManaCaster(
            WandLoadout wand,
            out ManaComponent mana)
        {
            GameObject casterObject = new GameObject("Boss SpellCaster");
            casterObject.SetActive(false);
            _createdObjects.Add(casterObject);

            mana = casterObject.AddComponent<ManaComponent>();
            SetPrivateField(mana, "_maxMana", 10f);
            SetPrivateField(mana, "_startFull", false);

            SpellCaster caster = casterObject.AddComponent<SpellCaster>();
            SetPrivateField(caster, "_wand", wand);
            SetPrivateField(caster, "_mana", mana);
            casterObject.SetActive(true);
            return caster;
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

        private static void SetPrivateField(
            object target,
            string fieldName,
            object value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"找不到测试字段 {fieldName}。");
            field.SetValue(target, value);
        }
    }

}
