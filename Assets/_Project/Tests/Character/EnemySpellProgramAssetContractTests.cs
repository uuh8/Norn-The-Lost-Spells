using System.Collections.Generic;
using Game.Combat;
using Game.Skills;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Game.Character.Tests
{
    public sealed class EnemySpellProgramAssetContractTests
    {
        private const string SpellPath =
            "Assets/_Project/ScriptableObjects/Spell/Definitions/Enemy/Spell_Emit_Fireball.asset";
        private const string ProgramPath =
            "Assets/_Project/ScriptableObjects/Spell/Programs/Enemy/Wand_Enemy_Wizard_Fireball.asset";
        private const string PrefabPath =
            "Assets/_Project/Art/Prefabs/BaseEnemy/MagicEnemy_Base.prefab";
        private const string ArrowSpellPath =
            "Assets/_Project/ScriptableObjects/Spell/Definitions/Enemy/Spell_Emit_Arrow.asset";
        private const string GravitySpellPath =
            "Assets/_Project/ScriptableObjects/Spell/Definitions/Enemy/Spell_Modify_Gravity.asset";
        private const string ArrowProgramPath =
            "Assets/_Project/ScriptableObjects/Spell/Programs/Enemy/Wand_Enemy_Arrow.asset";
        private const string ArrowDefinitionPath =
            "Assets/_Project/ScriptableObjects/Enemies/Definitions/EnemyDefinition_Arrow.asset";
        private const string ArrowAttackPath =
            "Assets/_Project/ScriptableObjects/Enemies/Attacks/Enemy_Arrow_AttackDefinition.asset";
        private const string ArrowPrefabPath =
            "Assets/_Project/Art/Prefabs/BaseEnemy/ArrowEnemy_Base.prefab";
        private const string BowControllerPath =
            "Assets/_Project/Art/Animators/Enemy_Bow.controller";

        [Test]
        public void OrdinaryWizardProgram_HasExpectedFireballContract()
        {
            SpellDefinition spell = AssetDatabase.LoadAssetAtPath<SpellDefinition>(SpellPath);
            Assert.That(spell, Is.Not.Null);
            Assert.That(spell.Kind, Is.EqualTo(SpellKind.Emit));
            Assert.That(spell.BaseDamage, Is.EqualTo(10f));
            Assert.That(spell.BaseSpeed, Is.EqualTo(18f));
            Assert.That(spell.DamageType, Is.EqualTo(DamageType.Magical));
            Assert.That(spell.ProjectilePrefab, Is.Not.Null);

            WandLoadout program = AssetDatabase.LoadAssetAtPath<WandLoadout>(ProgramPath);
            Assert.That(program, Is.Not.Null);
            Assert.That(program.BaseDraws, Is.EqualTo(1));
            Assert.That(program.Spells, Has.Length.EqualTo(1));
            Assert.That(program.Spells[0], Is.SameAs(spell));
        }

        [Test]
        public void MagicEnemyPrefab_HasSpellCasterOriginAndOrdinaryProgram()
        {
            WandLoadout program = AssetDatabase.LoadAssetAtPath<WandLoadout>(ProgramPath);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);

            Assert.That(prefab, Is.Not.Null);
            Assert.That(prefab.GetComponent<SpellCaster>(), Is.Not.Null);

            RangedEnemyController controller = prefab.GetComponent<RangedEnemyController>();
            Assert.That(controller, Is.Not.Null);

            SerializedObject serializedController = new SerializedObject(controller);
            Assert.That(
                serializedController.FindProperty("_castOrigin").objectReferenceValue,
                Is.Not.Null);

            SerializedProperty programs = serializedController.FindProperty("_spellPrograms");
            Assert.That(programs.arraySize, Is.EqualTo(1));
            Assert.That(
                programs.GetArrayElementAtIndex(0).objectReferenceValue,
                Is.SameAs(program));

            Assert.That(
                serializedController.FindProperty("_aimMode").enumValueIndex,
                Is.EqualTo((int)SpellAimMode.Direct));
        }

        [Test]
        public void ArrowProgram_HasGravityModifierBeforePhysicalArrow()
        {
            SpellDefinition arrow = AssetDatabase.LoadAssetAtPath<SpellDefinition>(ArrowSpellPath);
            SpellDefinition gravity = AssetDatabase.LoadAssetAtPath<SpellDefinition>(GravitySpellPath);
            WandLoadout program = AssetDatabase.LoadAssetAtPath<WandLoadout>(ArrowProgramPath);

            Assert.That(arrow, Is.Not.Null);
            Assert.That(arrow.Kind, Is.EqualTo(SpellKind.Emit));
            Assert.That(arrow.DamageType, Is.EqualTo(DamageType.Physical));
            Assert.That(arrow.BaseDamage, Is.EqualTo(12f));
            Assert.That(arrow.BaseSpeed, Is.GreaterThan(0f));
            Assert.That(float.IsInfinity(arrow.BaseSpeed), Is.False);
            Assert.That(arrow.ProjectilePrefab, Is.Not.Null);

            Assert.That(gravity, Is.Not.Null);
            Assert.That(gravity.Kind, Is.EqualTo(SpellKind.Modify));
            Assert.That(gravity.ModUseGravity, Is.True);

            Assert.That(program, Is.Not.Null);
            Assert.That(program.BaseDraws, Is.EqualTo(1));
            Assert.That(program.Spells, Is.EqualTo(new[] { gravity, arrow }));

            // 检查生产程序解释后的攻击结果：防止有正确资产但漏应用 Gravity 或速度修正。
            // 20是当前生产箭速，12是伤害；本次不修改平衡参数来迎合旧测试的16。
            var commands = new List<EmitCommand>();
            CastEvaluator.Evaluate(program.Spells, program.BaseDraws, 100f,
                CastModifierState.Default, commands);
            Assert.That(commands, Has.Count.EqualTo(1));
            Assert.That(commands[0].Speed, Is.EqualTo(20f));
            Assert.That(commands[0].Damage, Is.EqualTo(12f));
            Assert.That(commands[0].UseGravity, Is.True);
            Assert.That(commands[0].ProjectilePrefab, Is.SameAs(arrow.ProjectilePrefab));
        }

        [Test]
        public void ArrowEnemyPrefab_UsesBowAnimationAndBallisticArrowProgram()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ArrowPrefabPath);
            EnemyDefinition definition = AssetDatabase.LoadAssetAtPath<EnemyDefinition>(ArrowDefinitionPath);
            AttackDefinition attack = AssetDatabase.LoadAssetAtPath<AttackDefinition>(ArrowAttackPath);
            WandLoadout program = AssetDatabase.LoadAssetAtPath<WandLoadout>(ArrowProgramPath);
            RuntimeAnimatorController bowController =
                AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(BowControllerPath);

            Assert.That(prefab, Is.Not.Null);
            Assert.That(definition, Is.Not.Null);
            Assert.That(attack, Is.Not.Null);
            Assert.That(attack.AnimationStateName, Is.EqualTo("Attack01_Bow"));
            Assert.That(definition.Attack, Is.SameAs(attack));

            RangedEnemyController controller = prefab.GetComponent<RangedEnemyController>();
            Assert.That(controller, Is.Not.Null);
            Assert.That(prefab.GetComponent<SpellCaster>(), Is.Not.Null);

            SerializedObject serializedController = new SerializedObject(controller);
            Assert.That(
                serializedController.FindProperty("_definition").objectReferenceValue,
                Is.SameAs(definition));
            Assert.That(
                serializedController.FindProperty("_castOrigin").objectReferenceValue,
                Is.Not.Null);
            Assert.That(
                serializedController.FindProperty("_aimMode").enumValueIndex,
                Is.EqualTo((int)SpellAimMode.BallisticToPoint));

            SerializedProperty programs = serializedController.FindProperty("_spellPrograms");
            Assert.That(programs.arraySize, Is.EqualTo(1));
            Assert.That(
                programs.GetArrayElementAtIndex(0).objectReferenceValue,
                Is.SameAs(program));

            Animator animator = prefab.GetComponent<Animator>();
            Assert.That(animator, Is.Not.Null);
            Assert.That(animator.runtimeAnimatorController, Is.SameAs(bowController));

            // 敌人独立Controller应真的包含代码CrossFade的入口；只比引用不能发现状态被删。
            var stateNames = new List<string>();
            foreach (ChildAnimatorState state in ((AnimatorController)bowController).layers[0].stateMachine.states)
                stateNames.Add(state.state.name);
            Assert.That(stateNames, Does.Contain("Attack01_Bow"));
            Assert.That(stateNames, Does.Contain("GetHit_Bow"));
            Assert.That(stateNames, Does.Contain("Die_Bow"));

            CharacterCombatFeedback feedback = prefab.GetComponent<CharacterCombatFeedback>();
            Assert.That(feedback, Is.Not.Null);
            SerializedObject serializedFeedback = new SerializedObject(feedback);
            Assert.That(
                serializedFeedback.FindProperty("_getHitStateName").stringValue,
                Is.EqualTo("GetHit_Bow"));
            Assert.That(
                serializedFeedback.FindProperty("_dieStateName").stringValue,
                Is.EqualTo("Die_Bow"));
        }
    }
}
