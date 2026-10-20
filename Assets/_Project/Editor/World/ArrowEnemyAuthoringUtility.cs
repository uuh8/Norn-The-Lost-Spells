using System;
using Game.Character;
using Game.Combat;
using Game.Skills;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// Arrow Enemy 的一次性 Authoring 工具。Unity 的 AssetDatabase/PrefabUtility 只运行于 Editor，
    /// 由这里创建资产可让 Unity 自己维护 GUID，避免手写 .meta 破坏引用关系。
    /// </summary>
    internal static class ArrowEnemyAuthoringUtility
    {
        private const string EnemyRoot = "Assets/_Project/ScriptableObjects/Enemies/Definitions";
        private const string EnemySpellsFolder = "Assets/_Project/ScriptableObjects/Spell/Definitions/Enemy";
        private const string ProgramsFolder = "Assets/_Project/ScriptableObjects/Spell/Programs/Enemy";
        private const string ArrowSpellPath = EnemySpellsFolder + "/Spell_Emit_Arrow.asset";
        private const string GravitySpellPath = EnemySpellsFolder + "/Spell_Modify_Gravity.asset";
        private const string ArrowProgramPath = ProgramsFolder + "/Wand_Enemy_Arrow.asset";
        private const string ArrowAttackPath = "Assets/_Project/ScriptableObjects/Enemies/Attacks/Enemy_Arrow_AttackDefinition.asset";
        private const string ArrowDefinitionPath = "Assets/_Project/ScriptableObjects/Enemies/Definitions/EnemyDefinition_Arrow.asset";
        private const string ArrowPrefabPath = "Assets/_Project/Art/Prefabs/BaseEnemy/ArrowEnemy_Base.prefab";

        private const string ArrowProjectilePath = "Assets/_Project/Art/Prefabs/Arrow.prefab";
        private const string MagicEnemyPrefabPath = "Assets/_Project/Art/Prefabs/BaseEnemy/MagicEnemy_Base.prefab";
        private const string BowControllerPath = "Assets/_Project/Art/Animators/BowHero.controller";

        [MenuItem("Tools/Norn/Arrow Enemy/Create Or Update Authoring")]
        private static void CreateOrUpdateAuthoring()
        {
            EnsureFolder(EnemyRoot);
            EnsureFolder(EnemySpellsFolder);
            EnsureFolder(ProgramsFolder);

            GameObject arrowPrefab = LoadRequired<GameObject>(ArrowProjectilePath);
            RuntimeAnimatorController bowController = LoadRequired<RuntimeAnimatorController>(BowControllerPath);

            SpellDefinition arrowSpell = LoadOrCreate<SpellDefinition>(ArrowSpellPath);
            ConfigureArrowSpell(arrowSpell, arrowPrefab);

            SpellDefinition gravitySpell = LoadOrCreate<SpellDefinition>(GravitySpellPath);
            ConfigureGravitySpell(gravitySpell);

            AttackDefinition attack = LoadOrCreate<AttackDefinition>(ArrowAttackPath);
            ConfigureAttack(attack);

            EnemyDefinition definition = LoadOrCreate<EnemyDefinition>(ArrowDefinitionPath);
            ConfigureDefinition(definition, attack);

            WandLoadout program = LoadOrCreate<WandLoadout>(ArrowProgramPath);
            program.BaseDraws = 1;
            program.Spells = new[] { gravitySpell, arrowSpell };
            EditorUtility.SetDirty(program);

            AssetDatabase.SaveAssets();
            CreateOrUpdatePrefab(definition, program, bowController);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog(
                "Arrow Enemy",
                "Arrow Enemy 的 ScriptableObject 与 Prefab 已创建/更新。请继续按交接清单把 Prefab 放入场景并验证发射点。",
                "确定");
        }

        private static void ConfigureArrowSpell(SpellDefinition spell, GameObject arrowPrefab)
        {
            spell.Kind = SpellKind.Emit;
            spell.DisplayName = "敌人箭矢";
            spell.Description = "普通弓箭敌人使用的物理抛物线 Arrow；重力由前置 Modify 指令开启。";
            spell.Element = SpellElement.None;
            spell.Icon = null;
            spell.ManaCost = 0f;
            spell.ProjectilePrefab = arrowPrefab;
            spell.BaseDamage = 12f;
            spell.BaseSpeed = 16f;
            spell.DamageType = DamageType.Physical;
            spell.CastSfx = null;
            spell.PayloadTrigger = PayloadTriggerMode.None;
            spell.SpawnMode = SpellSpawnMode.ForwardProjectile;
            EditorUtility.SetDirty(spell);
        }

        private static void ConfigureGravitySpell(SpellDefinition spell)
        {
            spell.Kind = SpellKind.Modify;
            spell.DisplayName = "敌人重力修正";
            spell.Description = "让后续 Arrow 受 Rigidbody 重力影响，并由 BallisticToPoint 反解初速度。";
            spell.Element = SpellElement.None;
            spell.Icon = null;
            spell.ManaCost = 0f;
            spell.ModDamageAddFlat = 0f;
            spell.ModDamageMul = 1f;
            spell.ModSpeedMul = 1f;
            spell.ModSpreadAddDegrees = 0f;
            spell.ModBounceAdd = 0;
            spell.ModUseGravity = true;
            spell.ModHomingRadius = 0f;
            spell.ModHomingDuration = 0f;
            spell.ModHomingTurnRateDegrees = 0f;
            spell.ModOrbitRadius = 0f;
            spell.ModOrbitAngularSpeedDegrees = 0f;
            spell.ModOrbitPhaseOffsetDegrees = 0f;
            spell.ModOrbitPlaneTiltDegrees = 0f;
            spell.ExtraDraws = 0;
            EditorUtility.SetDirty(spell);
        }

        private static void ConfigureAttack(AttackDefinition attack)
        {
            // 远程 Enemy 只读取动画名和 ArrowSpawnTime；伤害最终来自 Arrow Spell 的 EmitCommand 快照。
            attack.BaseAmount = 12f;
            attack.Type = DamageType.Physical;
            attack.HalfExtents = new Vector3(0.17f, 0.83f, 0.12f);
            attack.HitActiveStart = 0.149f;
            attack.HitActiveEnd = 0.8f;
            attack.TrailActiveStart = 0.298f;
            attack.TrailActiveEnd = 1f;
            attack.ComboInputStart = 0.45f;
            attack.ComboInputEnd = 1f;
            attack.ArrowSpawnTime = 0.4f;
            attack.AnimationStateName = "Attack01_Bow";
            EditorUtility.SetDirty(attack);
        }

        private static void ConfigureDefinition(EnemyDefinition definition, AttackDefinition attack)
        {
            definition.MoveSpeed = 4f;
            definition.PathRefreshInterval = 0.2f;
            definition.DestinationMoveThreshold = 0.5f;
            definition.DestinationSampleRadius = 2f;
            definition.RetreatStepDistance = 4f;
            definition.RetreatSampleRadius = 1.5f;
            definition.StuckCheckInterval = 0.5f;
            definition.StuckProgressDistance = 0.05f;
            definition.DetectRadius = 16f;
            definition.DetectHeight = 6f;
            definition.LoseRadius = 18f;
            definition.LoseHeight = 8f;
            definition.AttackRange = 14f;
            definition.AttackCooldown = 1.8f;
            definition.Attack = attack;
            definition.HurtDuration = 0.4f;
            definition.HurtStateName = "GetHit_Bow";
            definition.RetreatDistance = 5f;
            definition.RangedRetreatMaxHeight = 1.5f;
            definition.RangedAttackMaxHeight = 6f;
            definition.RangedLineOfSightObstacleMask = 1 << 8;
            definition.CrossFadeDuration = 0.1f;
            EditorUtility.SetDirty(definition);
        }

        private static void CreateOrUpdatePrefab(
            EnemyDefinition definition,
            WandLoadout program,
            RuntimeAnimatorController bowController)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(ArrowPrefabPath) == null)
            {
                LoadRequired<GameObject>(MagicEnemyPrefabPath);
                GameObject contents = PrefabUtility.LoadPrefabContents(MagicEnemyPrefabPath);
                try
                {
                    contents.name = "ArrowEnemy_Base";
                    PrefabUtility.SaveAsPrefabAsset(contents, ArrowPrefabPath);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }

            GameObject root = PrefabUtility.LoadPrefabContents(ArrowPrefabPath);
            try
            {
                root.name = "ArrowEnemy_Base";
                Animator animator = RequireComponent<Animator>(root, "Arrow Enemy 缺少 Animator");
                animator.runtimeAnimatorController = bowController;

                SetActiveByName(root.transform, "Bow1", true);
                SetActiveByName(root.transform, "Wand1_R", false);

                RangedEnemyController controller = RequireComponent<RangedEnemyController>(
                    root,
                    "Arrow Enemy 缺少 RangedEnemyController");
                SerializedObject serializedController = new SerializedObject(controller);
                SetObjectReference(serializedController, "_definition", definition);
                SetObjectReference(serializedController, "_castOrigin", FindDescendant(root.transform, "Weapon_Pivot"));
                SerializedProperty programs = RequireProperty(serializedController, "_spellPrograms");
                programs.arraySize = 1;
                programs.GetArrayElementAtIndex(0).objectReferenceValue = program;
                RequireProperty(serializedController, "_aimHeightOffset").floatValue = 1f;
                RequireProperty(serializedController, "_aimMode").enumValueIndex = (int)SpellAimMode.BallisticToPoint;
                serializedController.ApplyModifiedPropertiesWithoutUndo();

                CharacterCombatFeedback feedback = RequireComponent<CharacterCombatFeedback>(
                    root,
                    "Arrow Enemy 缺少 CharacterCombatFeedback");
                SerializedObject serializedFeedback = new SerializedObject(feedback);
                RequireProperty(serializedFeedback, "_getHitStateName").stringValue = "GetHit_Bow";
                RequireProperty(serializedFeedback, "_dieStateName").stringValue = "Die_Bow";
                serializedFeedback.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(root, ArrowPrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static T LoadOrCreate<T>(string path) where T : ScriptableObject
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null)
                return asset;

            asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        private static T LoadRequired<T>(string path) where T : UnityEngine.Object
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new InvalidOperationException($"缺少必要资产：{path}");
            return asset;
        }

        private static T RequireComponent<T>(GameObject root, string message) where T : Component
        {
            T component = root.GetComponent<T>();
            if (component == null)
                throw new InvalidOperationException(message);
            return component;
        }

        private static SerializedProperty RequireProperty(SerializedObject serializedObject, string propertyName)
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            if (property == null)
                throw new InvalidOperationException($"找不到序列化字段：{propertyName}");
            return property;
        }

        private static void SetObjectReference(
            SerializedObject serializedObject,
            string propertyName,
            UnityEngine.Object value)
        {
            RequireProperty(serializedObject, propertyName).objectReferenceValue = value;
        }

        private static void SetActiveByName(Transform root, string name, bool active)
        {
            Transform target = FindDescendant(root, name);
            if (target == null)
                throw new InvalidOperationException($"Arrow Enemy 缺少子节点：{name}");
            target.gameObject.SetActive(active);
        }

        private static Transform FindDescendant(Transform root, string name)
        {
            if (root.name == name)
                return root;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDescendant(root.GetChild(i), name);
                if (found != null)
                    return found;
            }

            return null;
        }

        private static void EnsureFolder(string targetFolder)
        {
            string[] segments = targetFolder.Split('/');
            string currentFolder = segments[0];
            for (int i = 1; i < segments.Length; i++)
            {
                string nextFolder = currentFolder + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(nextFolder))
                    AssetDatabase.CreateFolder(currentFolder, segments[i]);
                currentFolder = nextFolder;
            }
        }
    }
}
