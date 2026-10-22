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
    public sealed class RangedEnemySpellProgramTests
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
        public void CastSelectedProgram_FiltersInvalidProgramsAndIgnoresMana()
        {
            WandLoadout empty = CreateWand();
            WandLoadout valid = CreateWand(CreateEmit(10f));
            RangedEnemyController enemy = CreateEnemy(
                new WandLoadout[] { null, empty, valid },
                out ManaComponent mana);

            LogAssert.Expect(
                LogType.Warning,
                "[Skills] [SpellCaster] EmitCommand.ProjectilePrefab 为空，跳过该法术产出");

            Assert.That(enemy.CastSelectedProgram(), Is.True);
            Assert.That(mana.CurrentMana, Is.Zero,
                "Enemy 使用 IgnoreMana 时不应修改 ManaComponent。" );
        }

        [Test]
        public void CastSelectedProgram_NoValidProgram_ReturnsZero()
        {
            RangedEnemyController enemy = CreateEnemy(
                new WandLoadout[] { null, CreateWand() },
                out _);

            LogAssert.Expect(
                LogType.Warning,
                "[Enemy] 远程敌人没有可执行的法术程序，已跳过本次释放");

            Assert.That(enemy.CastSelectedProgram(), Is.False);
        }

        [Test]
        public void CanAttackTarget_FromDisconnectedHighGround_DoesNotRequireNavMeshPath()
        {
            RangedEnemyController enemy = CreateEnemy(
                new[] { CreateWand(CreateEmit(0f)) },
                out _);
            enemy.Definition.AttackRange = 12f;
            enemy.Definition.RangedAttackMaxHeight = 6f;
            enemy.Definition.RangedLineOfSightObstacleMask = 1 << 8;

            // 测试对象没有 Bake NavMesh；只要高台到目标的射击几何成立，远程攻击资格仍应成立。
            bool canAttack = enemy.CanAttackTarget(new Vector3(5f, -4f, 0f));

            Assert.That(canAttack, Is.True);
        }

        [Test]
        public void CanAttackTarget_WhenFootLineHitsOwnPlatformButEyeLineIsClear_ReturnsTrue()
        {
            RangedEnemyController enemy = CreateEnemy(
                new[] { CreateWand(CreateEmit(0f)) },
                out _);
            enemy.transform.position = new Vector3(0f, 2f, 0f);
            enemy.Definition.AttackRange = 12f;
            enemy.Definition.RangedAttackMaxHeight = 6f;
            enemy.Definition.RangedLineOfSightObstacleMask = 1 << 8;

            GameObject platform = GameObject.CreatePrimitive(PrimitiveType.Cube);
            platform.name = "Ranged Enemy High Platform";
            platform.layer = 8;
            platform.transform.position = new Vector3(0f, 1.5f, 0f);
            platform.transform.localScale = new Vector3(2f, 1f, 2f);
            _createdObjects.Add(platform);
            Physics.SyncTransforms();

            // 敌人脚底到玩家胸口的斜线会立刻穿过脚下平台；眼睛高度到胸口则无遮挡。
            // 这个测试防止未来再次把视觉检测错误地绑定到 Root 或投射物出生点。
            bool canAttack = enemy.CanAttackTarget(new Vector3(5f, 0f, 0f));

            Assert.That(canAttack, Is.True);
        }

        [Test]
        public void CanAttackTarget_WhenGroundLayerWallBlocksShot_ReturnsFalse()
        {
            RangedEnemyController enemy = CreateEnemy(
                new[] { CreateWand(CreateEmit(0f)) },
                out _);
            enemy.Definition.AttackRange = 12f;
            enemy.Definition.RangedAttackMaxHeight = 6f;
            enemy.Definition.RangedLineOfSightObstacleMask = 1 << 8;

            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Ranged LOS Wall";
            wall.layer = 8;
            wall.transform.position = new Vector3(2.5f, 0.5f, 0f);
            wall.transform.localScale = new Vector3(0.5f, 4f, 4f);
            _createdObjects.Add(wall);
            Physics.SyncTransforms();

            bool canAttack = enemy.CanAttackTarget(new Vector3(5f, 0f, 0f));

            Assert.That(canAttack, Is.False);
        }

        [Test]
        public void CanAttackTarget_WhenVerticalDifferenceExceedsLimit_ReturnsFalse()
        {
            RangedEnemyController enemy = CreateEnemy(
                new[] { CreateWand(CreateEmit(0f)) },
                out _);
            enemy.Definition.AttackRange = 12f;
            enemy.Definition.RangedAttackMaxHeight = 4f;
            enemy.Definition.RangedLineOfSightObstacleMask = 1 << 8;

            bool canAttack = enemy.CanAttackTarget(new Vector3(3f, -5f, 0f));

            Assert.That(canAttack, Is.False);
        }

        private RangedEnemyController CreateEnemy(
            WandLoadout[] programs,
            out ManaComponent mana)
        {
            GameObject enemyObject = new GameObject("Ranged Enemy Spell Test");
            enemyObject.SetActive(false);
            _createdObjects.Add(enemyObject);

            mana = enemyObject.AddComponent<ManaComponent>();
            SetPrivateField(mana, "_maxMana", 10f);
            SetPrivateField(mana, "_startFull", false);

            SpellCaster caster = enemyObject.AddComponent<SpellCaster>();
            SetPrivateField(caster, "_mana", mana);

            RangedEnemyController enemy = enemyObject.AddComponent<RangedEnemyController>();
            SetPrivateField(enemy, "_definition", CreateEnemyDefinition());
            SetPrivateField(enemy, "_health", enemyObject.GetComponent<HealthComponent>());
            SetPrivateField(enemy, "_spellCaster", caster);
            SetPrivateField(enemy, "_castOrigin", enemyObject.transform);
            SetPrivateField(enemy, "_spellPrograms", programs);
            // AddComponent 在 EditMode 测试中可能先触发 Awake；重新构建一次，模拟 Prefab 反序列化完成后的初始化结果。
            InvokePrivateMethod(enemy, "BuildValidProgramCache");

            enemyObject.SetActive(true);
            return enemy;
        }

        private EnemyDefinition CreateEnemyDefinition()
        {
            AttackDefinition attack = ScriptableObject.CreateInstance<AttackDefinition>();
            attack.AnimationStateName = "Enemy_SpellCast";
            _createdObjects.Add(attack);

            EnemyDefinition definition = ScriptableObject.CreateInstance<EnemyDefinition>();
            definition.Attack = attack;
            _createdObjects.Add(definition);
            return definition;
        }

        private SpellDefinition CreateEmit(float manaCost)
        {
            SpellDefinition spell = ScriptableObject.CreateInstance<SpellDefinition>();
            spell.Kind = SpellKind.Emit;
            spell.ManaCost = manaCost;
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
            for (System.Type type = target.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null)
                {
                    continue;
                }

                field.SetValue(target, value);
                return;
            }

            Assert.Fail($"找不到测试字段 {fieldName}。");
        }

        private static void InvokePrivateMethod(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"找不到测试方法 {methodName}。");
            method.Invoke(target, null);
        }
    }
}
