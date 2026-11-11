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
        private const string ContainerUsePath = "Assets/_Project/ScriptableObjects/Items/Containers/Use/UseDefinition.asset";
        private const string PlayerPath = "Assets/_Project/Art/Prefabs/WizardPlayer.prefab";
        private const string CatalogPath = "Assets/_Project/ScriptableObjects/World/Hub/Inventory/Catalog.asset";
        private const string ManifestPath = "Assets/_Project/Resources/Online/GameplayManifest.json";
        public int callbackOrder => 2;

        /// <summary>
        /// 原场景、动画、技能目录都没有变化，只修改联机运行时代码/同步时序时，刷新两端兼容摘要。
        /// 完整Setup还会重新导出地图、采样动画和装配场景；此入口避免每次修C#都重做无关作者内容。
        /// 新增技能、Prefab、动画或副本仍必须执行完整Setup。构建前原Verify仍会重新核对Hash与资源，
        /// 此菜单不能关闭或绕过构建Gate，也不允许在Play/未保存场景中使用。
        /// </summary>
        [MenuItem("Tools/Norn/Online/Refresh Online Runtime Compatibility Hash")]
        public static void RefreshRuntimeCompatibilityHash()
        {
            OnlineSceneSetup.RequireCleanEditor();
            OnlineMovementTools.Verify(); OnlineDungeonTools.Verify();
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>(DefinitionPath);
            if (definition == null || definition.WireHash == 0)
                throw new InvalidOperationException("请先完整Setup Online Combat，不能为未装配内容创建兼容摘要。");
            var data = new SerializedObject(definition);
            // 小段新增的受控输出也要随同一正式Hash刷新；不重导地图Prefab或保存用户其它资源。
            var outputPrefabs = OutputPrefabs(); var outputArray = data.FindProperty("_outputPrefabs");
            outputArray.arraySize = outputPrefabs.Count;
            for (int i = 0; i < outputPrefabs.Count; i++) outputArray.GetArrayElementAtIndex(i).objectReferenceValue = outputPrefabs[i];
            data.ApplyModifiedPropertiesWithoutUndo();
            string hash = ComputeGameplayHash();
            data.Update();
            data.FindProperty("_gameplayConfigHash").stringValue = hash;
            data.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(definition);
            // 仅保存本次Hash目标，避免把导入脏标记或用户其它未保存资源一起写盘。
            AssetDatabase.SaveAssetIfDirty(definition);
            File.WriteAllText(ManifestPath, JsonUtility.ToJson(new Manifest { SchemaVersion = 1, GameplayConfigHash = hash }, true));
            AssetDatabase.ImportAsset(ManifestPath);
            GameLog.Info("运行时兼容Hash已刷新；请配套重建Client/DS并重启本地业务服。Hash=" + hash, "OnlineCombat");
        }

        [MenuItem("Tools/Norn/Online/Setup Online Combat")]
        public static void Setup()
        {
            OnlineSceneSetup.RequireCleanEditor();
            // 此工具同步处理多个大资源，Editor.update期间不会发心跳；保留阶段日志可区分
            // 首次导入/Prefab采样/Hash耗时与真正停滞，不能仅凭外部等待脚本超时就重复装配。
            GameLog.Info("开始导出共同HUD、Inventory与正式副本。", "OnlineCombatSetup");
            // 公共 Resource 是原控件的离线导出结果；所有战斗区域共享一次运行时实例。
            SharedGameplayHudTools.ExportResource();
            SharedInventoryPresentationTools.ExportResource();
            OnlineDungeonTools.Export();
            GameLog.Info("正式地图已导出，开始采样原角色Timeline。", "OnlineCombatSetup");
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
            data.FindProperty("_containerUseDefinition").objectReferenceValue = AssetDatabase.LoadAssetAtPath<ContainerUseDefinition>(ContainerUsePath);
            for (int slot = 0; slot < 3; slot++)
                data.FindProperty("_selectWand" + (slot + 1)).objectReferenceValue = AssetDatabase.LoadMainAssetAtPath(
                    "Assets/_Project/ScriptableObjects/Input/Wands/Action_Select" + (slot + 1) + ".asset");
            var melee = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/BaseEnemy/SwordEnemy_Base.prefab");
            var ranged = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/BaseEnemy/MagicEnemy_Base.prefab");
            data.FindProperty("_rangedEnemyPrefab").objectReferenceValue = ranged;
            data.FindProperty("_bossPrefab").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/Boss/Boss.prefab");
            data.FindProperty("_meleeClipDuration").floatValue = EnemyDuration(melee);
            data.FindProperty("_rangedClipDuration").floatValue = EnemyDuration(ranged);
            BakeAction(data, "ground", prefab, ground, attack.ArrowSpawnTime);
            BakeAction(data, "air", prefab, air, attack.ArrowSpawnTime);
            GameLog.Info("开始收集受控技能产出Prefab与声音。", "OnlineCombatSetup");
            var prefabs = OutputPrefabs(); var array = data.FindProperty("_outputPrefabs"); array.arraySize = prefabs.Count;
            for (int i = 0; i < prefabs.Count; i++) array.GetArrayElementAtIndex(i).objectReferenceValue = prefabs[i];
            var sounds = CastSounds(); var soundArray = data.FindProperty("_soundClips"); soundArray.arraySize = sounds.Count;
            for (int i = 0; i < sounds.Count; i++) soundArray.GetArrayElementAtIndex(i).objectReferenceValue = sounds[i];
            GameLog.Info("受控产出表已收集，开始计算GameplayHash。", "OnlineCombatSetup");
            string hash = ComputeGameplayHash(); data.FindProperty("_gameplayConfigHash").stringValue = hash;
            File.Copy("Temp/OnlineLab/gameplay-hash-latest-inputs.json", "Temp/OnlineLab/gameplay-hash-setup-inputs.json", true);
            data.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(definition); AssetDatabase.SaveAssets();
            File.WriteAllText(ManifestPath, JsonUtility.ToJson(new Manifest { SchemaVersion = 1, GameplayConfigHash = hash }, true));
            AssetDatabase.ImportAsset(ManifestPath);
            // 同一个可见 Prefab 调整也影响 N3 运动配置摘要；两端要一起重建。
            GameLog.Info("GameplayHash已写入，开始装配运动配置与共用入口。", "OnlineCombatSetup");
            OnlineMovementTools.Setup();
            var sceneSetup = UnityEditor.SceneManagement.EditorSceneManager.GetSceneManagerSetup();
            try
            {
                foreach (string scenePath in new[] { RoomLifecycleTools.ScenePath, RoomLifecycleTools.PortalScenePath })
                {
                    var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath, UnityEditor.SceneManagement.OpenSceneMode.Single);
                    if (scenePath == RoomLifecycleTools.PortalScenePath)
                    {
                        SharedGameplayHudTools.Install(scene); SharedGameplayCameraTools.Install(scene);
                        CombatPresentationTools.InstallOnline(scene);
                    }
                    else OnlineCombatHudTools.Install(scene); // 历史 lab 保留技术 HUD，正式入口使用同源导出的原控件。
                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
                }
            }
            finally { UnityEditor.SceneManagement.EditorSceneManager.RestoreSceneManagerSetup(sceneSetup); }
            Verify(); GameLog.Info("战斗 Timeline 与 Prefab 表已从单机资源导出，Hash=" + hash, "OnlineCombat");
        }
        public static void Verify()
        {
            OnlineDungeonTools.Verify();
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>(DefinitionPath);
            if (definition == null || definition.Catalog == null || definition.ContainerUse == null || definition.WireHash == 0 ||
                definition.ReleaseSeconds(false) <= 0 || definition.ReleaseSeconds(true) <= 0)
                throw new InvalidOperationException("Online Combat 基础配置缺失：Definition/Catalog/Hash/Timeline必须全部有效。");
            string actualHash = ComputeGameplayHash();
            if (definition.GameplayConfigHash != actualHash)
                throw new InvalidOperationException("Online Combat GameplayHash过期：stored=" + definition.GameplayConfigHash +
                    ", actual=" + actualHash + "。输入清单已记录Temp/OnlineLab/gameplay-hash-latest-inputs.json；请核对差异，不能跳过校验。");
            foreach (AudioClip sound in CastSounds())
                if (definition.FindSoundKey(sound) == 0) throw new InvalidOperationException("施法 AudioClip 未收录到受控表：" + sound.name);
        }
        public void OnPreprocessBuild(BuildReport report)
        {
            if (AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>(DefinitionPath) == null) return;
            try { Verify(); }
            catch (Exception error)
            {
                // BuildPipeline会把某些普通预检异常记录后继续产物生成，出现Succeeded但Errors>0。
                // 权威配置不一致属于明确拒绝，必须用BuildFailedException停止，不能把该产物交给玩家验收。
                throw new BuildFailedException("Online Combat构建预检拒绝：" + error.Message);
            }
        }

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
            // 关卡可以配置不同Enemy Prefab；从已审核地图的实际输出依赖补齐白名单，
            // 避免未来某个Bow/Elite只在特定副本出现时，本地能出手而Replica找不到受控Key。
            foreach (var prefab in OnlineDungeonTools.OutputPrefabs()) Add(prefab);
            // G使用原受控物理壳；真实瓶模型另按Catalog解析，不能由客户端指定资源路径。
            var containerUse = AssetDatabase.LoadAssetAtPath<ContainerUseDefinition>(ContainerUsePath);
            if (containerUse != null) Add(containerUse.ThrowPrefab);
            // 命中表现/火场也采用同一受控表；只收现有生产 Prefab，运行时不能接受路径。
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/_Project/Art/Prefabs", "Assets/_Project/Prefabs" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.Contains("Tests/", StringComparison.Ordinal)) continue;
                // 导出世界已经由严格白名单证明没有AreaDamage；尤其P8静态Prefab有大量几何节点，
                // 为找一个已被禁止的组件反复整树加载会让Setup/Build耗费数分钟和大量Editor内存。
                // NPC真实产出依赖由上面的OnlineDungeonTools.OutputPrefabs单独收集，不在此漏掉技能。
                if (path.StartsWith(OnlineDungeonTools.WorldFolder + "/", StringComparison.Ordinal)) continue;
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
        [MenuItem("Tools/Norn/Online/Create World Replication Profile")]
        public static void CreateWorldReplicationProfile()
        {
            OnlineSceneSetup.RequireCleanEditor();
            const string path="Assets/_Project/Resources/Online/WorldReplicationProfile.asset";
            var profile=AssetDatabase.LoadAssetAtPath<Game.Multiplayer.OnlineWorldReplicationProfile>(path);
            if(profile==null)
            {
                if(AssetDatabase.LoadMainAssetAtPath(path)!=null)throw new InvalidOperationException("联机复制配置路径已有其他类型资产，保留现场。");
                // 仅创建独立发送配置，使用类内已批准2/10/3初值；不重导World/NPC或改单机活动参数。
                profile=ScriptableObject.CreateInstance<Game.Multiplayer.OnlineWorldReplicationProfile>();
                AssetDatabase.CreateAsset(profile,path);AssetDatabase.SaveAssets();
            }
            if(!profile.Validate(out var error))throw new InvalidOperationException(error);
        }
        private static string ComputeGameplayHash()
        {
            // 对原始 YAML 配置与相关 Prefab 源字节做 Hash，不使用平台相关的导入 Artifact Hash。
            // 美术引用通过 GUID 进入配置；本阶段把角色/动作/技能规则都列入，避免仅身份清单相同就误认为玩法相同。
            var paths = new SortedSet<string>(StringComparer.Ordinal) { PlayerPath, CatalogPath,
                "Assets/_Project/Resources/Online/WorldReplicationProfile.asset", ContainerUsePath };
            // 原输入的绑定也属于两端配置：使用相同脚本但不同数字键资产不能称为相同操作体验。
            foreach (string input in Directory.EnumerateFiles("Assets/_Project/ScriptableObjects/Input/Wands", "*.asset"))
                paths.Add(input.Replace('\\', '/'));
            foreach (string dependency in AssetDatabase.GetDependencies(PlayerPath, true))
                if (dependency.EndsWith(".controller", StringComparison.Ordinal) || dependency.EndsWith(".overrideController", StringComparison.Ordinal)
                    || dependency.EndsWith(".anim", StringComparison.Ordinal) || dependency.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) paths.Add(dependency);
            string[] roots = { "Assets/_Project/ScriptableObjects/Spell", "Assets/_Project/ScriptableObjects/Character", "Assets/_Project/ScriptableObjects/Enemies", "Assets/_Project/Art/Prefabs/BaseEnemy", "Assets/_Project/Art/Prefabs/Boss" };
            foreach (string guid in AssetDatabase.FindAssets("", roots))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!AssetDatabase.IsValidFolder(path) && (path.EndsWith(".asset", StringComparison.Ordinal) || path.EndsWith(".prefab", StringComparison.Ordinal))) paths.Add(path);
            }
            foreach (string path in OnlineDungeonTools.EnumerateHashInputs()) paths.Add(path);
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
            // 固定URP 17.3的TerrainLit声明了这个仅显示用BaseMapShader；Unity在Dedicated预检
            // 才把它加入GetDependencies，造成相同原始规则出现1818/1819两套输入。明确规范化
            // 这一隐式Rendering依赖，而不跳过构建Hash、不改写stored，也不排除碰撞/NavMesh/脚本/Schema。
            // 该路径与本项目锁定URP版本对应；升级引擎/URP或其他平台时须重新审计依赖差异。
            paths.Remove("Packages/com.unity.render-pipelines.universal/Shaders/Terrain/TerrainLitBase.shader");
            using var sha = SHA256.Create();
            var stamps = new List<HashInputStamp>(paths.Count);
            foreach (string path in paths)
            {
                byte[] name = Encoding.UTF8.GetBytes(path + "\n"), bytes = File.ReadAllBytes(path);
                sha.TransformBlock(name, 0, name.Length, null, 0); sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                // 只在Editor离线诊断记录路径/大小/写入时刻，区分内容变化与构建时临时重写/依赖集合变化。
                // 最终兼容性仍由上述真实文件字节SHA256决定，不用时间戳替代Hash验证。
                stamps.Add(new HashInputStamp { Path = path, Length = bytes.LongLength, LastWriteUtcTicks = File.GetLastWriteTimeUtc(path).Ticks });
            }
            // Definition 自身包含输出 Hash，不能把整份 YAML 再 Hash；额外玩法参数显式加入，避免循环。
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>(DefinitionPath);
            byte[] version = Encoding.UTF8.GetBytes("NornCombatV1|30Hz|input-aim-at-press|authority-release|boss-radius="
                + (definition != null ? definition.BossActivationRadius.ToString("R", CultureInfo.InvariantCulture) : "missing"));
            sha.TransformFinalBlock(version, 0, version.Length);
            string result = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            Directory.CreateDirectory("Temp/OnlineLab");
            File.WriteAllText("Temp/OnlineLab/gameplay-hash-latest-inputs.json", JsonUtility.ToJson(new HashInputTrace
            {
                Hash = result, VersionSalt = Encoding.UTF8.GetString(version), IsBuildingPlayer = BuildPipeline.isBuildingPlayer,
                Subtarget = EditorUserBuildSettings.standaloneBuildSubtarget.ToString(), Inputs = stamps.ToArray()
            }, true));
            return result;
        }
        [Serializable] private sealed class HashInputStamp { public string Path; public long Length, LastWriteUtcTicks; }
        [Serializable] private sealed class HashInputTrace { public string Hash, Subtarget, VersionSalt; public bool IsBuildingPlayer; public HashInputStamp[] Inputs; }
        [Serializable] private sealed class Manifest { public uint SchemaVersion; public string GameplayConfigHash; }
    }
}
