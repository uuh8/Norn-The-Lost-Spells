using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Game.Character;
using Game.Core;
using Game.Run;
using Game.Skills;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Game.Project.Editor
{
    /// <summary>从已验证单机资源导出 Timeline / 生成挂点 / Prefab 白名单，不手填另一套近似技能参数。</summary>
    public sealed class OnlineCombatTools : IPreprocessBuildWithReport
    {
        public const string DefinitionPath = "Assets/_Project/Resources/Online/CombatDefinition.asset";
        private const string PlayerPath = "Assets/_Project/Art/Prefabs/WizardPlayer.prefab";
        private const string CatalogPath = "Assets/_Project/ScriptableObjects/World/Hub/Inventory/Catalog.asset";
        private const string ManifestPath = "Assets/_Project/Resources/Online/GameplayManifest.json";
        public int callbackOrder => 2;

        [MenuItem("Tools/Norn/Online/Setup Online Combat")]
        public static void Setup()
        {
            OnlineSceneSetup.RequireCleanEditor();
            OnlineCombatHudTools.InstallBossPresentation(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/Boss/Boss.prefab"));
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>(DefinitionPath);
            if (definition == null) { definition = ScriptableObject.CreateInstance<OnlineCombatDefinition>(); AssetDatabase.CreateAsset(definition, DefinitionPath); }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPath);
            var wizard = prefab.GetComponent<WizardController>();
            var animator = prefab.GetComponentInChildren<Animator>(true);
            var controller = animator.runtimeAnimatorController as AnimatorController;
            if (wizard == null || wizard.Combo == null || wizard.Combo.SegmentCount == 0 || controller == null)
                throw new InvalidOperationException("Wizard 原始 Combo / AnimatorController 缺失。");
            var attack = wizard.Combo.Segments[0];
            var ground = Find(controller.layers[0].stateMachine, attack.AnimationStateName);
            var air = Find(controller.layers[0].stateMachine, "JumpAttack_MagicWand");
            if (ground == null || air == null) throw new InvalidOperationException("正式 Wizard 攻击 State 不存在。");
            var data = new SerializedObject(definition);
            data.FindProperty("_catalog").objectReferenceValue = AssetDatabase.LoadAssetAtPath<ItemCatalog>(CatalogPath);
            var melee = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/BaseEnemy/SwordEnemy_Base.prefab");
            var ranged = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/BaseEnemy/MagicEnemy_Base.prefab");
            data.FindProperty("_rangedEnemyPrefab").objectReferenceValue = ranged;
            data.FindProperty("_bossPrefab").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/Boss/Boss.prefab");
            data.FindProperty("_meleeClipDuration").floatValue = EnemyDuration(melee);
            data.FindProperty("_rangedClipDuration").floatValue = EnemyDuration(ranged);
            BakeAction(data, "ground", prefab, ground, attack.ArrowSpawnTime);
            BakeAction(data, "air", prefab, air, attack.ArrowSpawnTime);
            var prefabs = OutputPrefabs(); var array = data.FindProperty("_outputPrefabs"); array.arraySize = prefabs.Count;
            for (int i = 0; i < prefabs.Count; i++) array.GetArrayElementAtIndex(i).objectReferenceValue = prefabs[i];
            var sounds = CastSounds(); var soundArray = data.FindProperty("_soundClips"); soundArray.arraySize = sounds.Count;
            for (int i = 0; i < sounds.Count; i++) soundArray.GetArrayElementAtIndex(i).objectReferenceValue = sounds[i];
            string hash = ComputeGameplayHash(); data.FindProperty("_gameplayConfigHash").stringValue = hash;
            data.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(definition); AssetDatabase.SaveAssets();
            File.WriteAllText(ManifestPath, JsonUtility.ToJson(new Manifest { SchemaVersion = 1, GameplayConfigHash = hash }, true));
            AssetDatabase.ImportAsset(ManifestPath);
            // 同一个可见 Prefab 调整也影响 N3 运动配置摘要；两端要一起重建。
            OnlineMovementTools.Setup();
            var sceneSetup = UnityEditor.SceneManagement.EditorSceneManager.GetSceneManagerSetup();
            try
            {
                foreach (string scenePath in new[] { RoomLifecycleTools.ScenePath, RoomLifecycleTools.PortalScenePath })
                {
                    var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath, UnityEditor.SceneManagement.OpenSceneMode.Single);
                    OnlineCombatHudTools.Install(scene);
                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
                }
            }
            finally { UnityEditor.SceneManagement.EditorSceneManager.RestoreSceneManagerSetup(sceneSetup); }
            Verify(); GameLog.Info("战斗 Timeline 与 Prefab 表已从单机资源导出，Hash=" + hash, "OnlineCombat");
        }
        public static void Verify()
        {
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>(DefinitionPath);
            if (definition == null || definition.Catalog == null || definition.WireHash == 0
                || definition.GameplayConfigHash != ComputeGameplayHash()
                || definition.ReleaseSeconds(false) <= 0 || definition.ReleaseSeconds(true) <= 0)
                throw new InvalidOperationException("Online Combat 配置缺失或过期，请 Setup Online Combat 再构建两端。");
            foreach (AudioClip sound in CastSounds())
                if (definition.FindSoundKey(sound) == 0) throw new InvalidOperationException("施法 AudioClip 未收录到受控表：" + sound.name);
        }
        public void OnPreprocessBuild(BuildReport report)
        { if (AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>(DefinitionPath) != null) Verify(); }

        private static AnimatorState Find(AnimatorStateMachine machine, string name)
        {
            foreach (var item in machine.states) if (item.state.name == name) return item.state;
            foreach (var child in machine.stateMachines) { var found = Find(child.stateMachine, name); if (found != null) return found; }
            return null;
        }
        private static float EnemyDuration(GameObject prefab)
        {
            var enemy = prefab.GetComponent<EnemyControllerBase>(); var animator = prefab.GetComponentInChildren<Animator>(true);
            var controller = animator.runtimeAnimatorController as AnimatorController;
            var state = enemy != null && enemy.Definition != null && enemy.Definition.Attack != null && controller != null
                ? Find(controller.layers[0].stateMachine, enemy.Definition.Attack.AnimationStateName) : null;
            if (state == null || !(state.motion is AnimationClip clip) || state.speed <= 0 || state.speedParameterActive || !Mathf.Approximately(animator.speed, 1))
                throw new InvalidOperationException("敌人攻击 Clip/固定速度配置无效：" + prefab.name);
            return clip.length / state.speed;
        }
        private static void BakeAction(SerializedObject output, string prefix, GameObject prefab, AnimatorState state, float normalizedRelease)
        {
            if (!(state.motion is AnimationClip clip) || state.speed <= 0 || state.speedParameterActive)
                throw new InvalidOperationException("权威 Timeline 需要可确定长度与速度的攻击 Clip：" + state.name);
            // 单机出手还受 .1s CrossFade 门禁；取 max 而非额外相加，因为目标 Clip 在混合中已经前进。
            float release = Mathf.Max(.1f, clip.length / state.speed * normalizedRelease);
            output.FindProperty("_" + prefix + "ReleaseSeconds").floatValue = release;
            output.FindProperty("_" + prefix + "AnimationHash").intValue = Animator.StringToHash(state.name);
            var root = PrefabUtility.LoadPrefabContents(PlayerPath);
            try
            {
                var wizard = root.GetComponent<WizardController>(); var animator = root.GetComponentInChildren<Animator>(true);
                animator.applyRootMotion = false;
                // 编辑器离线采样实际攻击姿态，把法杖尖的局部挂点导出；DS Wizard 不需要依赖 Animator 才能出手。
                clip.SampleAnimation(animator.gameObject, Mathf.Min(clip.length, release * state.speed));
                if (wizard.FireballSpawnPoint == null) throw new InvalidOperationException("Wizard 施法挂点缺失。");
                output.FindProperty("_" + prefix + "SpawnOffset").vector3Value = root.transform.InverseTransformPoint(wizard.FireballSpawnPoint.position);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
        private static List<GameObject> OutputPrefabs()
        {
            var paths = new SortedDictionary<string, GameObject>(StringComparer.Ordinal);
            foreach (string guid in AssetDatabase.FindAssets("t:SpellDefinition", new[] { "Assets/_Project/ScriptableObjects/Spell/Definitions" }))
            {
                var spell = AssetDatabase.LoadAssetAtPath<SpellDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                Add(spell.ProjectilePrefab); Add(spell.LandingSitePrefab);
            }
            // 命中表现/火场也采用同一受控表；只收现有生产 Prefab，运行时不能接受路径。
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/_Project/Art/Prefabs", "Assets/_Project/Prefabs" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.Contains("Tests/", StringComparison.Ordinal)) continue;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab.GetComponentInChildren<Game.Combat.AreaDamage>(true) != null) Add(prefab);
            }
            // Fireball_Explosion 没有 AreaDamage，仍是客户端必须重放的真实命中表现。
            // 沿生产产出 Prefab 的序列化依赖收集嵌套 Prefab，可覆盖以后增加的子爆炸，避免第二份人工白名单遗漏。
            // 白名单在 Editor 固化并参与玩法 Hash；网络消息只发送 Key，不允许客户端指定路径。
            foreach (string path in new List<string>(paths.Keys))
                foreach (string dependency in AssetDatabase.GetDependencies(path, true))
                    if (dependency.EndsWith(".prefab", StringComparison.Ordinal)
                        && dependency.StartsWith("Assets/_Project/", StringComparison.Ordinal)
                        && !dependency.Contains("/Tests/", StringComparison.Ordinal))
                        Add(AssetDatabase.LoadAssetAtPath<GameObject>(dependency));
            return new List<GameObject>(paths.Values);
            void Add(GameObject prefab) { if (prefab != null) paths[AssetDatabase.GetAssetPath(prefab)] = prefab; }
        }
        private static List<AudioClip> CastSounds()
        {
            // 沿用现有单机 SpellDefinition 的声音引用，不维护火球/Boss 名称分支或第二份手工音频列表。
            var paths = new SortedDictionary<string, AudioClip>(StringComparer.Ordinal);
            foreach (string guid in AssetDatabase.FindAssets("t:SpellDefinition", new[] { "Assets/_Project/ScriptableObjects/Spell" }))
            {
                var spell = AssetDatabase.LoadAssetAtPath<SpellDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (spell.CastSfx != null) paths[AssetDatabase.GetAssetPath(spell.CastSfx)] = spell.CastSfx;
            }
            return new List<AudioClip>(paths.Values);
        }
        private static string ComputeGameplayHash()
        {
            // 对原始 YAML 配置与相关 Prefab 源字节做 Hash，不使用平台相关的导入 Artifact Hash。
            // 美术引用通过 GUID 进入配置；本阶段把角色/动作/技能规则都列入，避免仅身份清单相同就误认为玩法相同。
            var paths = new SortedSet<string>(StringComparer.Ordinal) { PlayerPath, CatalogPath };
            foreach (string dependency in AssetDatabase.GetDependencies(PlayerPath, true))
                if (dependency.EndsWith(".controller", StringComparison.Ordinal) || dependency.EndsWith(".overrideController", StringComparison.Ordinal)
                    || dependency.EndsWith(".anim", StringComparison.Ordinal) || dependency.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) paths.Add(dependency);
            string[] roots = { "Assets/_Project/ScriptableObjects/Spell", "Assets/_Project/ScriptableObjects/Character", "Assets/_Project/ScriptableObjects/Enemies", "Assets/_Project/Art/Prefabs/BaseEnemy", "Assets/_Project/Art/Prefabs/Boss" };
            foreach (string guid in AssetDatabase.FindAssets("", roots))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!AssetDatabase.IsValidFolder(path) && (path.EndsWith(".asset", StringComparison.Ordinal) || path.EndsWith(".prefab", StringComparison.Ordinal))) paths.Add(path);
            }
            foreach (var prefab in OutputPrefabs()) paths.Add(AssetDatabase.GetAssetPath(prefab));
            foreach (AudioClip sound in CastSounds())
            {
                string path = AssetDatabase.GetAssetPath(sound); paths.Add(path);
                if (File.Exists(path + ".meta")) paths.Add(path + ".meta");
            }
            // 敌人/Boss 的骨骼同样参与权威命中；仅 Hash Prefab GUID 会漏掉 Clip 与 Import Settings 修改。
            foreach (string path in new List<string>(paths))
                if (path.EndsWith(".prefab", StringComparison.Ordinal))
                    foreach (string dependency in AssetDatabase.GetDependencies(path, true))
                        if (dependency.EndsWith(".controller", StringComparison.Ordinal) || dependency.EndsWith(".overrideController", StringComparison.Ordinal)
                            || dependency.EndsWith(".anim", StringComparison.Ordinal) || dependency.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                        { paths.Add(dependency); if (File.Exists(dependency + ".meta")) paths.Add(dependency + ".meta"); }
            // Gameplay 代码版本也属于可兼容性的输入；同样配置但不同裁决实现不能进入同一副本。
            foreach (string module in new[] { "Core", "Combat", "Skills", "Run", "Character", "Multiplayer", "Networking" })
                foreach (string file in Directory.EnumerateFiles("Assets/_Project/Scripts/" + module, "*.cs", SearchOption.AllDirectories)) paths.Add(file.Replace('\\', '/'));
            foreach (string schema in Directory.EnumerateFiles("../Shared/Protocol/Protos", "*.proto")) paths.Add(schema.Replace('\\', '/'));
            using var sha = SHA256.Create();
            foreach (string path in paths)
            {
                byte[] name = Encoding.UTF8.GetBytes(path + "\n"), bytes = File.ReadAllBytes(path);
                sha.TransformBlock(name, 0, name.Length, null, 0); sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            }
            // Definition 自身包含输出 Hash，不能把整份 YAML 再 Hash；额外玩法参数显式加入，避免循环。
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>(DefinitionPath);
            byte[] version = Encoding.UTF8.GetBytes("NornCombatV1|30Hz|input-aim-at-press|authority-release|boss-radius="
                + (definition != null ? definition.BossActivationRadius.ToString("R", CultureInfo.InvariantCulture) : "missing"));
            sha.TransformFinalBlock(version, 0, version.Length);
            return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
        }
        [Serializable] private sealed class Manifest { public uint SchemaVersion; public string GameplayConfigHash; }
    }
}
