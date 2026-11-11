using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.ElementField;
using Game.Run;
using Game.Skills;
using Game.UI;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.Project.Editor
{
    /// <summary>
    /// 正式地图的离线导出边界：只读原Scene，把静态世界、原NPC实例覆盖值与空间接线分开保存。
    /// 所有克隆先放进inactive父根，再剥离单机Owner；永远不在运行时加载整张单机图后才删除Session。
    /// </summary>
    public static partial class OnlineDungeonTools
    {
        public const string CatalogPath = "Assets/_Project/Resources/Online/DungeonCatalog.asset";
        public const string WorldFolder = "Assets/_Project/Prefabs/World/Online";
        public const string DataFolder = "Assets/_Project/ScriptableObjects/World/Online";
        private static readonly Dictionary<GameObject, Preview> Previews = new Dictionary<GameObject, Preview>();
        private static readonly string[] Ids = { "p7", "p8" };

        public sealed class SourceInfo
        {
            public string SourceHash, SourceScenePath;
            public int NpcCount, EncounterCount, GateCount, EntryVolumeCount;
            public Vector3 SourcePlayerPosition;
            public bool MissingLightingDataAsset;
            public bool ShowEncounterHud;
            public int LootTableCount;
            public int FixedSolidCount;
            public int[] NpcLootStages, NpcLootEnemies;
        }
        /// <summary>逐NPC的只读导出审计；只记录资源路径/受控key，不赋予客户端选程序或生成Gameplay的权限。</summary>
        public sealed class NpcProgramMappingInfo
        {
            public ulong ActorId;
            public string SourcePrefabPath;
            public string[] ProgramPaths, OutputPrefabPaths;
            public uint[] OutputKeys;
            public bool MatchesExportedPrograms;
        }
        public sealed class PlayerSpawnInfo
        {
            public Vector3 SourcePosition;
            public Vector3[] Positions, GroundNormals;
            public float MinimumSeparation;
            public int CandidateChecks, MaxCandidateChecks;
            public bool UsedSourceCollidersOnly;
        }
        private sealed class SourceNpc
        {
            internal GameObject Root;
            internal Vector3 Position, Scale;
            internal Quaternion Rotation;
            internal int Encounter;
            internal int LootStage = -1, LootEnemy = -1;
            internal float Duration;
        }
        private sealed class Preview
        {
            internal string Id, Path, Hash;
            internal Scene Source, World;
            internal GameObject Root;
            internal PlayerControllerBase Player;
            internal readonly List<SourceNpc> Npcs = new List<SourceNpc>();
            internal OnlineDungeonEncounterDefinition[] Encounters;
            internal OnlineDungeonAreaBindings[] Areas;
            internal readonly Dictionary<int, Transform> Clones = new Dictionary<int, Transform>();
            internal SourceInfo Info;
            internal bool ShowEncounterHud = true;
            internal DungeonLootTable[] LootTables = Array.Empty<DungeonLootTable>();
            internal GameObject CoinPrefab;
            internal OnlineDungeonFixedSolidSpawn[] FixedSolidSpawns = Array.Empty<OnlineDungeonFixedSolidSpawn>();
            internal readonly List<ScriptableObject> MaterialProfiles = new List<ScriptableObject>();
            internal bool LoadedPrefabContents;
        }

        private sealed class SourceLoot
        {
            internal DungeonLootTable[] Tables = Array.Empty<DungeonLootTable>();
            internal GameObject CoinPrefab;
            internal readonly List<HealthComponent> Members = new List<HealthComponent>();
            internal readonly List<int> Stages = new List<int>(), Enemies = new List<int>();
        }
        private sealed class LootRefresh
        {
            internal OnlineDungeonDefinition Definition;
            internal DungeonLootTable[] Tables;
            internal GameObject CoinPrefab;
            internal int[] Stages, Enemies;
            internal string SceneHash, SourceHash;
            internal OnlineDungeonFixedSolidSpawn[] FixedSolidSpawns;
        }

        private sealed class SourceFixedSolids
        {
            internal OnlineDungeonFixedSolidSpawn[] Spawns = Array.Empty<OnlineDungeonFixedSolidSpawn>();
            internal readonly SortedSet<string> HashInputs = new SortedSet<string>(StringComparer.Ordinal);
        }

        [MenuItem("Tools/Norn/Online/Export Formal Dungeon Worlds")]
        public static void Export()
        {
            OnlineSceneSetup.RequireCleanEditor();
            EnsureFolder(WorldFolder + "/Npcs"); EnsureFolder(DataFolder); EnsureFolder("Assets/_Project/Resources/Online");
            var definitions = new OnlineDungeonDefinition[Ids.Length];
            for (int d = 0; d < Ids.Length; d++)
            {
                var root = BuildPreview(Ids[d]); var preview = Previews[root];
                try
                {
                    // NPC保留原Scene实例值，不能只取PrefabUtility.GetCorrespondingObject而丢掉血量/程序/变换覆盖。
                    var npcs = new OnlineDungeonNpcSpawn[preview.Npcs.Count];
                    for (int i = 0; i < npcs.Length; i++)
                    {
                        var original = preview.Npcs[i];
                        var holder = new GameObject("InactiveNpcExport"); holder.SetActive(false); SceneManager.MoveGameObjectToScene(holder, preview.World);
                        try
                        {
                            var npc = Object.Instantiate(original.Root, holder.transform, false);
                            npc.name = original.Root.name; npc.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                            npc.transform.localScale = original.Scale; npc.SetActive(true);
                            StripNpc(npc); ClearOutsideReferences(npc);
                            string path = WorldFolder + "/Npcs/" + Ids[d].ToUpperInvariant() + "_" + (i + 5).ToString("D2") + ".prefab";
                            var saved = PrefabUtility.SaveAsPrefabAsset(npc, path);
                            if (saved == null) throw new InvalidOperationException("NPC导出失败：" + original.Root.name);
                            npcs[i] = new OnlineDungeonNpcSpawn { ActorId = (ulong)i + 5, Prefab = saved,
                                Position = original.Position, Rotation = original.Rotation, AttackClipDuration = original.Duration,
                                EncounterIndex = original.Encounter, InitiallyVisible = Ids[d] == "p8",
                                LootStageIndex = original.LootStage, LootEnemyIndex = original.LootEnemy };
                        }
                        finally { Object.DestroyImmediate(holder); }
                    }
                    Vector3[] spawns = DerivePlayerSpawns(preview).Positions;
                    PersistMaterialProfiles(preview);
                    var world = PrefabUtility.SaveAsPrefabAsset(root, WorldPath(Ids[d]));
                    if (world == null) throw new InvalidOperationException("静态地图保存失败。");
                    var definition = AssetDatabase.LoadAssetAtPath<OnlineDungeonDefinition>(DefinitionPath(Ids[d]));
                    if (definition == null) { definition = ScriptableObject.CreateInstance<OnlineDungeonDefinition>(); AssetDatabase.CreateAsset(definition, DefinitionPath(Ids[d])); }
                    var data = new SerializedObject(definition);
                    data.FindProperty("_dungeonId").stringValue = Ids[d]; data.FindProperty("_sourceScenePath").stringValue = preview.Path;
                    data.FindProperty("_sourceHash").stringValue = preview.Hash; data.FindProperty("_worldPrefab").objectReferenceValue = world;
                    data.FindProperty("_sourceSceneHash").stringValue = Hash(preview.Path);
                    data.FindProperty("_showEncounterHud").boolValue = preview.ShowEncounterHud;
                    data.FindProperty("_coinPrefab").objectReferenceValue = preview.CoinPrefab;
                    var positions = data.FindProperty("_playerSpawns"); positions.arraySize = spawns.Length;
                    for (int i = 0; i < spawns.Length; i++) positions.GetArrayElementAtIndex(i).vector3Value = spawns[i];
                    data.ApplyModifiedPropertiesWithoutUndo();
                    // 公共数组合同仅在Editor通过反射写入序列化字段，保留Unity对象引用；Runtime不会执行反射。
                    WriteObjectArray(definition, "_npcs", npcs); WriteObjectArray(definition, "_encounters", preview.Encounters);
                    WriteObjectArray(definition, "_lootTables", preview.LootTables);
                    WriteObjectArray(definition, "_fixedSolidSpawns", preview.FixedSolidSpawns);
                    EditorUtility.SetDirty(definition); definitions[d] = definition;
                }
                finally { ReleasePreview(root); }
            }
            var catalog = AssetDatabase.LoadAssetAtPath<OnlineDungeonCatalog>(CatalogPath);
            if (catalog == null) { catalog = ScriptableObject.CreateInstance<OnlineDungeonCatalog>(); AssetDatabase.CreateAsset(catalog, CatalogPath); }
            var catalogData = new SerializedObject(catalog); var entries = catalogData.FindProperty("_dungeons");
            // 原菜单只重导P7/P8。已独立导出的Home或后续地图仍保留原引用，不能被两张战斗图的刷新删掉。
            var merged = new List<OnlineDungeonDefinition>(definitions);
            for (int i = 0; i < entries.arraySize; i++)
            {
                var existing = entries.GetArrayElementAtIndex(i).objectReferenceValue as OnlineDungeonDefinition;
                if (existing == null) continue;
                bool replaced = false;
                foreach (var updated in definitions) if (updated.DungeonId == existing.DungeonId) { replaced = true; break; }
                if (!replaced) merged.Add(existing);
            }
            entries.arraySize = merged.Count;
            for (int i = 0; i < merged.Count; i++) entries.GetArrayElementAtIndex(i).objectReferenceValue = merged[i];
            catalogData.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(catalog); AssetDatabase.SaveAssets(); Verify();
            GameLog.Info("已从原P7/P8导出地图、NPC、入口及独立CPU材料配置；网络权威、恢复、掉落和交易仍需各自实际验收。", "OnlineDungeon");
        }

        [MenuItem("Tools/Norn/Online/Verify Formal Dungeon Worlds")]
        public static void Verify()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<OnlineDungeonCatalog>(CatalogPath); string error = null;
            if (catalog == null || !catalog.TryValidate(out error)) throw new InvalidOperationException("正式副本目录无效：" + error);
            foreach (string id in Ids)
            {
                if (!catalog.TryGet(id, out var definition)) throw new InvalidOperationException("正式地图缺失：" + id);
                // 源场景的文本皮肤与显隐策略是两份信息。只核对 SourceHash 无法发现新增字段
                // 仍停留在默认值的旧导出资产；只读 Preview 防止把 P8 再默认为普通遭遇 HUD。
                var source = EditorSceneManager.OpenPreviewScene(SourcePath(id));
                try
                {
                    var loot = ReadSourceLoot(source, id);
                    var solids = ReadFixedSolidSource(source, id);
                    if (definition.SourceSceneHash != Hash(SourcePath(id)) || definition.SourceHash != ComputeSourceHash(SourcePath(id), loot.Tables, solids, loot.CoinPrefab))
                        throw new InvalidOperationException("正式地图Scene或原掉落依赖已变化，请走对应官方刷新/Export：" + id);
                    ValidateLootMapping(definition, loot, true);
                    if (!SameFixedSolidSpawns(definition, solids.Spawns))
                        throw new InvalidOperationException("正式固体来源与原Scene世界姿态/数量不一致，请窄刷新：" + id);
                    if (definition.ShowEncounterHud != ReadSourceHudPolicy(source))
                        throw new InvalidOperationException("正式地图HUD显隐策略与原场景不一致，请重新Export：" + id);
                }
                finally { EditorSceneManager.ClosePreviewScene(source); }
                ValidateWorld(definition.WorldPrefab, definition.EncounterCount);
                if (definition.NpcCount != (id == "p7" ? 12 : 1)) throw new InvalidOperationException("正式NPC数量与源地图不一致。");
                for (int i = 0; i < definition.NpcCount; i++)
                {
                    var npc = definition.GetNpc(i);
                    foreach (var component in npc.Prefab.GetComponentsInChildren<MonoBehaviour>(true))
                        if (component == null || !AllowedNpc(component)) throw new InvalidOperationException("NPC包含未授权脚本：" + component);
                }
            }
        }

        /// <summary>为测试/审查创建独立Preview。源Scene和克隆都不加入作者的正式场景列表，调用者必须Release。</summary>
        public static GameObject BuildPreview(string dungeonId)
        {
            string path = SourcePath(dungeonId); var preview = new Preview { Id = dungeonId, Path = path, Hash = Hash(path) };
            try
            {
                preview.Source = EditorSceneManager.OpenPreviewScene(path); preview.World = EditorSceneManager.NewPreviewScene();
                preview.Root = new GameObject(dungeonId.ToUpperInvariant() + "World"); preview.Root.SetActive(false);
                SceneManager.MoveGameObjectToScene(preview.Root, preview.World);
                foreach (var original in preview.Source.GetRootGameObjects())
                {
                    var clone = Object.Instantiate(original, preview.Root.transform, false); clone.name = original.name;
                    MapTransforms(original.transform, clone.transform, preview.Clones);
                }
                RemapSceneReferences(preview);
                Gather(preview);
                PrepareMaterialWorld(preview);
                AttachOriginalReturnPortals(preview);
                RemoveActorsAndOwnership(preview);
                var bindings = preview.Root.AddComponent<OnlineDungeonWorldBindings>(); WriteObjectArray(bindings, "_areas", preview.Areas);
                if (preview.Id == "home") AttachPeacefulHomeBindings(preview);
                var environment = preview.Root.AddComponent<OnlineDungeonEnvironment>(); CaptureEnvironment(preview, environment);
                ClearOutsideReferences(preview.Root); ValidateWorld(preview.Root, preview.Areas.Length);
                preview.Info = new SourceInfo { SourceHash = preview.Hash, SourceScenePath = preview.Path, NpcCount = preview.Npcs.Count,
                    EncounterCount = preview.Areas.Length, SourcePlayerPosition = preview.Player.transform.position,
                    ShowEncounterHud = preview.ShowEncounterHud,
                    GateCount = preview.Root.GetComponentsInChildren<ArenaGate>(true).Length,
                    MissingLightingDataAsset = environment.SourceLightingDataMissing };
                preview.Info.LootTableCount = preview.LootTables.Length;
                preview.Info.FixedSolidCount = preview.FixedSolidSpawns.Length;
                preview.Info.NpcLootStages = new int[preview.Npcs.Count]; preview.Info.NpcLootEnemies = new int[preview.Npcs.Count];
                for (int i = 0; i < preview.Npcs.Count; i++)
                { preview.Info.NpcLootStages[i] = preview.Npcs[i].LootStage; preview.Info.NpcLootEnemies[i] = preview.Npcs[i].LootEnemy; }
                foreach (var area in preview.Areas) preview.Info.EntryVolumeCount += area.EntryVolumes.Length;
                Previews.Add(preview.Root, preview); return preview.Root;
            }
            catch { Close(preview); throw; }
        }
        public static void ReleasePreview(GameObject root)
        {
            if (ReferenceEquals(root, null) || !Previews.TryGetValue(root, out var preview)) return;
            Previews.Remove(root); Close(preview);
        }
        public static SourceInfo InspectSource(string id)
        { var root = BuildPreview(id); try { return Previews[root].Info; } finally { ReleasePreview(root); } }

        /// <summary>
        /// 按原遭遇成员顺序核对原Scene→导出NPC→程序→受控PrefabKey。不实例化敌人、不运行AI、不保存资源。
        /// 毒/粘/火是不同原程序；该报告只能证实静态映射，真实运行时选中哪一套仍须看DS出生事实。
        /// </summary>
        public static NpcProgramMappingInfo[] InspectNpcProgramMappings(string id)
        {
            var definition = AssetDatabase.LoadAssetAtPath<OnlineDungeonDefinition>(DefinitionPath(id));
            var combat = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            if (definition == null || combat == null) throw new InvalidOperationException("逐NPC审计缺正式Definition或受控输出表。");
            var scene = EditorSceneManager.OpenPreviewScene(SourcePath(id));
            try
            {
                var originals = new List<GameObject>();
                if (id == "p7")
                {
                    var source = ReadSourceLoot(scene, id);
                    foreach (var member in source.Members) originals.Add(member.gameObject);
                }
                else
                {
                    var owners = Find<BossEncounterController>(scene);
                    if (owners.Count != 1) throw new InvalidOperationException("P8逐NPC审计需要唯一原Boss Encounter。");
                    var health = new SerializedObject(owners[0]).FindProperty("_bossHealth").objectReferenceValue as HealthComponent;
                    if (health == null) throw new InvalidOperationException("原Boss Encounter没有可信Boss Health。");
                    originals.Add(health.gameObject);
                }
                if (originals.Count != definition.NpcCount) throw new InvalidOperationException("原NPC与导出NPC数量不一致。");
                var result = new NpcProgramMappingInfo[originals.Count];
                for (int i = 0; i < result.Length; i++)
                {
                    var original = originals[i]; var exported = definition.GetNpc(i).Prefab;
                    var programs = ReadNpcPrograms(original); var copied = ReadNpcPrograms(exported);
                    bool same = SameNpcDefinition(original, exported) && programs.Length == copied.Length;
                    for (int p = 0; p < programs.Length && same; p++) same = programs[p] == copied[p];
                    var paths = new string[programs.Length];
                    var outputs = new SortedDictionary<string, GameObject>(StringComparer.Ordinal);
                    for (int p = 0; p < programs.Length; p++)
                    {
                        paths[p] = AssetDatabase.GetAssetPath(programs[p]);
                        if (string.IsNullOrEmpty(paths[p]) || programs[p].Spells == null)
                            throw new InvalidOperationException("原NPC程序不是持久化资源或缺少指令序列。");
                        foreach (var spell in programs[p].Spells)
                        {
                            if (spell == null) throw new InvalidOperationException("原NPC程序含空法术。");
                            AddProgramOutput(outputs, spell.ProjectilePrefab); AddProgramOutput(outputs, spell.LandingSitePrefab);
                        }
                        // 继续检查命中/爆炸等Prefab依赖，避免只核对根投射物却漏掉其后继Gameplay。
                        foreach (string dependency in AssetDatabase.GetDependencies(paths[p], true))
                        {
                            if (!dependency.EndsWith(".prefab", StringComparison.Ordinal)) continue;
                            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(dependency);
                            if (prefab != null && (prefab.GetComponentInChildren<ProjectileBase>(true) != null
                                || prefab.GetComponentInChildren<AreaDamage>(true) != null)) AddProgramOutput(outputs, prefab);
                        }
                    }
                    var prefabPaths = new string[outputs.Count]; var keys = new uint[outputs.Count]; int next = 0;
                    foreach (var pair in outputs)
                    {
                        prefabPaths[next] = pair.Key; uint key = combat.FindOutputKey(pair.Value);
                        keys[next++] = combat.OutputPrefab(key) == pair.Value ? key : 0;
                    }
                    result[i] = new NpcProgramMappingInfo { ActorId = definition.GetNpc(i).ActorId,
                        SourcePrefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(original),
                        ProgramPaths = paths, OutputPrefabPaths = prefabPaths, OutputKeys = keys, MatchesExportedPrograms = same };
                }
                return result;
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }
        private static void AddProgramOutput(SortedDictionary<string, GameObject> outputs, GameObject prefab)
        {
            if (prefab == null) return;
            string path = AssetDatabase.GetAssetPath(prefab);
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("原程序输出不是持久化Prefab。");
            outputs[path] = prefab;
        }
        private static WandLoadout[] ReadNpcPrograms(GameObject root)
        {
            if (root == null) throw new InvalidOperationException("原NPC或导出NPC引用缺失。");
            var ranged = root.GetComponent<RangedEnemyController>();
            if (ranged != null)
            {
                var values = new SerializedObject(ranged).FindProperty("_spellPrograms"); var result = new WandLoadout[values.arraySize];
                for (int i = 0; i < result.Length; i++) result[i] = values.GetArrayElementAtIndex(i).objectReferenceValue as WandLoadout;
                foreach (var program in result) if (program == null) throw new InvalidOperationException("原远程NPC含空程序。");
                return result;
            }
            var boss = root.GetComponent<WizardBossController>();
            if (boss == null) return Array.Empty<WandLoadout>(); // 近战没有远程程序，不能替它填默认火球。
            if (boss.Definition == null || boss.Definition.Programs == null) throw new InvalidOperationException("原Boss缺程序配置。");
            var programs = new WandLoadout[boss.Definition.Programs.Length];
            for (int i = 0; i < programs.Length; i++)
            {
                var selection = boss.Definition.Programs[i];
                if (selection == null || selection.Wand == null) throw new InvalidOperationException("原Boss含空程序选择。");
                programs[i] = selection.Wand;
            }
            return programs;
        }
        private static bool SameNpcDefinition(GameObject original, GameObject exported)
        {
            if (exported == null) return false;
            var sourceEnemy = original.GetComponent<EnemyControllerBase>(); var copiedEnemy = exported.GetComponent<EnemyControllerBase>();
            if (sourceEnemy != null) return copiedEnemy != null && sourceEnemy.GetType() == copiedEnemy.GetType() && sourceEnemy.Definition == copiedEnemy.Definition;
            var sourceBoss = original.GetComponent<WizardBossController>(); var copiedBoss = exported.GetComponent<WizardBossController>();
            return sourceBoss != null && copiedBoss != null && sourceBoss.Definition == copiedBoss.Definition;
        }
        /// <summary>与Export共用真实出生验证，供Editor测试检查有界搜索和临时Scene清理，不保存任何资产。</summary>
        public static PlayerSpawnInfo InspectPlayerSpawns(GameObject preview)
        {
            if (preview == null || !Previews.TryGetValue(preview, out var source)) throw new ArgumentException("只接受本工具创建的Preview。", nameof(preview));
            return DerivePlayerSpawns(source);
        }
        private static void Close(Preview preview)
        {
            if (preview.Root != null)
            {
                if (preview.LoadedPrefabContents) PrefabUtility.UnloadPrefabContents(preview.Root);
                else Object.DestroyImmediate(preview.Root);
            }
            foreach (var profile in preview.MaterialProfiles) if (profile != null) Object.DestroyImmediate(profile);
            if (!preview.LoadedPrefabContents && preview.World.IsValid()) EditorSceneManager.ClosePreviewScene(preview.World);
            if (preview.Source.IsValid()) EditorSceneManager.ClosePreviewScene(preview.Source);
        }

        private static void Gather(Preview preview)
        {
            if (preview.Id == "home") { GatherPeacefulHome(preview); return; }
            // 原 P8 的 Stage Adapter 已禁用，Boss HUD 再负责其专属栏；离线读这个实际选择，
            // 不在 Client 按 DungeonId 特判，也不借进度/血量猜皮肤。没有旧 Adapter 的区域
            // 使用公共遭遇 HUD（例如当前 P7）；多个来源无法确定 authoring，显式拒绝导出。
            preview.ShowEncounterHud = ReadSourceHudPolicy(preview.Source);
            if (preview.Id == "p7")
            {
                var runs = Find<DemoRunController>(preview.Source);
                if (runs.Count != 1) throw new InvalidOperationException("P7必须有唯一原DemoRunController。");
                var run = runs[0]; var runData = new SerializedObject(run); var members = runData.FindProperty("_encounters");
                preview.Player = ((HealthComponent)runData.FindProperty("_playerHealth").objectReferenceValue).GetComponent<PlayerControllerBase>();
                preview.Encounters = new OnlineDungeonEncounterDefinition[members.arraySize]; preview.Areas = new OnlineDungeonAreaBindings[members.arraySize];
                var allEntries = Find<EncounterEntryTrigger>(preview.Source);
                for (int i = 0; i < members.arraySize; i++)
                {
                    var encounter = (DemoEncounterController)members.GetArrayElementAtIndex(i).objectReferenceValue;
                    var data = new SerializedObject(encounter); var enemies = data.FindProperty("_enemyMembers");
                    for (int e = 0; e < enemies.arraySize; e++) AddNpc(preview, ((HealthComponent)enemies.GetArrayElementAtIndex(e).objectReferenceValue).gameObject, i);
                    var authoring = run.Definition.GetEncounter(i);
                    preview.Encounters[i] = new OnlineDungeonEncounterDefinition { DisplayName = authoring.DisplayName, ObjectiveText = authoring.ObjectiveText };
                    var volumes = new List<Collider>();
                    foreach (var entry in allEntries)
                        if (new SerializedObject(entry).FindProperty("_encounter").objectReferenceValue == encounter)
                            foreach (var collider in entry.GetComponents<Collider>()) volumes.Add(Clone(preview, collider));
                    var gates = data.FindProperty("_gates"); var copied = new ArenaGate[gates.arraySize];
                    for (int g = 0; g < copied.Length; g++) copied[g] = Clone(preview, gates.GetArrayElementAtIndex(g).objectReferenceValue as ArenaGate);
                    preview.Areas[i] = new OnlineDungeonAreaBindings { EntryVolumes = volumes.ToArray(), Gates = copied };
                }
            }
            else
            {
                var owners = Find<BossEncounterController>(preview.Source); var entries = Find<BossEntryTrigger>(preview.Source);
                if (owners.Count != 1 || entries.Count != 1) throw new InvalidOperationException("P8需要唯一Boss Encounter与安全圈入口。");
                var data = new SerializedObject(owners[0]);
                preview.Player = ((HealthComponent)data.FindProperty("_playerHealth").objectReferenceValue).GetComponent<PlayerControllerBase>();
                var boss = (HealthComponent)data.FindProperty("_bossHealth").objectReferenceValue; AddNpc(preview, boss.gameObject, 0);
                var entryData = new SerializedObject(entries[0]); var safe = entryData.FindProperty("_safeZoneVisualRoot").objectReferenceValue as Transform;
                var colliders = entries[0].GetComponents<Collider>(); var copied = new Collider[colliders.Length];
                for (int i = 0; i < copied.Length; i++) copied[i] = Clone(preview, colliders[i]);
                preview.Areas = new[] { new OnlineDungeonAreaBindings { EntryVolumes = copied, Gates = Array.Empty<ArenaGate>(),
                    SafeZoneVisualRoot = safe != null ? preview.Clones[safe.GetInstanceID()].gameObject : null } };
                preview.Encounters = new[] { new OnlineDungeonEncounterDefinition { DisplayName = "Boss区域", ObjectiveText = "击败Boss", ExitToStart = true } };
            }
            if (preview.Player == null || preview.Npcs.Count != (preview.Id == "p7" ? 12 : 1)) throw new InvalidOperationException("源图玩家/NPC引用不完整。");
            // 在移除单机Owner前取原配置；不把剥离后的空Prefab误判成原图没有掉落，也不挂回单机会话。
            var loot = ReadSourceLoot(preview.Source, preview.Id); preview.LootTables = loot.Tables; preview.CoinPrefab = loot.CoinPrefab;
            var solids = ReadFixedSolidSource(preview.Source, preview.Id); preview.FixedSolidSpawns = solids.Spawns;
            for (int i = 0; i < loot.Members.Count; i++)
            {
                if (i >= preview.Npcs.Count || preview.Npcs[i].Root != loot.Members[i].gameObject)
                    throw new InvalidOperationException("原掉落成员次序与正式NPC导出次序不一致。");
                preview.Npcs[i].LootStage = loot.Stages[i]; preview.Npcs[i].LootEnemy = loot.Enemies[i];
            }
            preview.Hash = ComputeSourceHash(preview.Path, loot.Tables, solids, loot.CoinPrefab);
        }

        /// <summary>只读原Scene的固定固体值；在单机Owner剥离前捕获实际Layout实例的世界Transform。</summary>
        public static OnlineDungeonFixedSolidSpawn[] InspectFixedSolidSpawns(string dungeonId)
        {
            var scene = EditorSceneManager.OpenPreviewScene(SourcePath(dungeonId));
            try { return ReadFixedSolidSource(scene, dungeonId).Spawns; }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        private static SourceFixedSolids ReadFixedSolidSource(Scene scene, string dungeonId)
        {
            var result = new SourceFixedSolids(); WorldItemSpawnPoint original = null;
            foreach (var point in Find<WorldItemSpawnPoint>(scene))
            {
                // food和其它原作者点仍不在本次线上范围；一旦原固体来源变更则显式拒绝，不能悄悄多发或改量。
                if (point.SpawnId != "p7.explore.0" && (point.Definition == null || point.Definition.Kind != ItemKind.Material)) continue;
                if (dungeonId != "p7" || point.SpawnId != "p7.explore.0" || original != null
                    || !point.enabled || !point.gameObject.activeInHierarchy || point.Definition == null
                    || point.Definition.Kind != ItemKind.Material || point.Definition.ContentId != "containertest.ice")
                    throw new InvalidOperationException("固定固体仅允许原P7有效且唯一的p7.explore.0冰来源。");
                original = point;
            }
            if (dungeonId == "p8") return result;
            if (original == null) throw new InvalidOperationException("原P7固定冰来源缺失，拒绝把缺失默认成新空地图。");
            var pointData = new SerializedObject(original); var definition = original.Definition;
            var itemData = new SerializedObject(definition);
            int quantity = pointData.FindProperty("_initialMaterialQuantity").intValue;
            int key = itemData.FindProperty("_materialKey").intValue;
            if (quantity != 256 || key != 5 || itemData.FindProperty("_materialItemCapacityGmu").intValue < quantity)
                throw new InvalidOperationException("原固定冰必须保留显式Key5/256GMU，不能从模板容量补满或自动接受新来源量。");
            var combat = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            if (combat == null || combat.Catalog == null || !combat.Catalog.TryCreateIndex(out var catalog, out _)
                || !catalog.TryGet(definition.ContentId, out var trusted) || trusted != definition)
                throw new InvalidOperationException("原固定冰定义必须与受信Catalog为同一资源。");
            result.Spawns = new[] { new OnlineDungeonFixedSolidSpawn { SpawnId = original.SpawnId, ContentId = definition.ContentId,
                MaterialKey = (byte)key, Quantity = (ulong)quantity, Position = original.transform.position, Rotation = original.transform.rotation } };
            var proof = ScriptableObject.CreateInstance<OnlineDungeonDefinition>();
            try
            {
                var data = new SerializedObject(proof); data.FindProperty("_dungeonId").stringValue = dungeonId; data.ApplyModifiedPropertiesWithoutUndo();
                WriteObjectArray(proof, "_fixedSolidSpawns", result.Spawns);
                if (!proof.TryValidateFixedSolidSpawns(out var error)) throw new InvalidOperationException(error);
            }
            finally { Object.DestroyImmediate(proof); }

            // Scene只保存Prefab引用GUID；把原Layout、SpawnPoint规则和冰/模型源及meta也纳入依赖，避免字节改变却沿旧Hash。
            var prefabSource = PrefabUtility.GetCorrespondingObjectFromSource(original);
            if (prefabSource == null || AssetDatabase.GetAssetPath(prefabSource) != "Assets/_Project/Prefabs/SinglePlayer/P7Dungeon/FixedFourStageLayout.prefab")
                throw new InvalidOperationException("原固定冰不再来自已批准的FixedFourStageLayout，拒绝窄刷新掩盖来源变更。");
            AddLootSource(prefabSource, result.HashInputs); AddLootSource(MonoScript.FromMonoBehaviour(original), result.HashInputs);
            AddLootSource(definition, result.HashInputs);
            if (definition.PickupPrefab != null) AddLootSource(definition.PickupPrefab, result.HashInputs);
            if (definition.WorldModel != null) AddLootSource(definition.WorldModel, result.HashInputs);
            return result;
        }

        private static bool SameFixedSolidSpawns(OnlineDungeonDefinition definition, OnlineDungeonFixedSolidSpawn[] source)
        {
            if (definition.FixedSolidCount != source.Length) return false;
            for (int i = 0; i < source.Length; i++)
            {
                var actual = definition.GetFixedSolid(i); var expected = source[i];
                if (actual == null || actual.SpawnId != expected.SpawnId || actual.ContentId != expected.ContentId
                    || actual.MaterialKey != expected.MaterialKey || actual.Quantity != expected.Quantity
                    || !actual.Position.Equals(expected.Position) || !actual.Rotation.Equals(expected.Rotation)) return false;
            }
            return true;
        }

        private static SourceLoot ReadSourceLoot(Scene scene, string id)
        {
            var result = new SourceLoot(); var contents = Find<DungeonRunContent>(scene);
            if (id == "p8")
            {
                if (contents.Count != 0) throw new InvalidOperationException("原P8新增了掉落Owner，不能自动套用P7规则。");
                return result;
            }
            var runs = Find<DemoRunController>(scene);
            if (contents.Count != 1 || runs.Count != 1 || contents[0].StageCount != 4)
                throw new InvalidOperationException("P7需要唯一原DungeonRunContent及四阶段映射。");
            var content = contents[0]; var source = new SerializedObject(content);
            var coin = source.FindProperty("_coinPrefab").objectReferenceValue as WorldCurrencyPickup;
            if (coin == null || !PrefabUtility.IsPartOfPrefabAsset(coin))
                throw new InvalidOperationException("原P7缺少可复用的CoinPrefab资产，不能以程序替代品更改Trigger或视觉。");
            result.CoinPrefab = coin.gameObject;
            var tables = source.FindProperty("_tables"); var encounters = new SerializedObject(runs[0]).FindProperty("_encounters");
            if (tables.arraySize != 4 || encounters.arraySize != 4) throw new InvalidOperationException("原P7掉落表/遭遇不是四阶段。");
            result.Tables = new DungeonLootTable[4];
            for (int stage = 0; stage < 4; stage++)
            {
                var encounter = content.GetEncounter(stage);
                if (encounter == null || encounter != encounters.GetArrayElementAtIndex(stage).objectReferenceValue)
                    throw new InvalidOperationException("掉落与原遭遇次序不一致，不能重新排序敌人。");
                result.Tables[stage] = content.GetTable(stage);
                for (int enemy = 0; enemy < encounter.EnemyCount; enemy++)
                {
                    var member = encounter.GetEnemy(enemy); if (member == null) throw new InvalidOperationException("原掉落敌人引用缺失。");
                    result.Members.Add(member); result.Stages.Add(stage); result.Enemies.Add(enemy);
                }
            }
            // 用同一纯配置验证器证明最多12敌、完整原表最多48个Item key。只创建内存SO，不改原Scene或表。
            var proof = ScriptableObject.CreateInstance<OnlineDungeonDefinition>();
            try
            {
                var data = new SerializedObject(proof); data.FindProperty("_dungeonId").stringValue = id; data.ApplyModifiedPropertiesWithoutUndo();
                var npcs = new OnlineDungeonNpcSpawn[result.Members.Count]; var areas = new OnlineDungeonEncounterDefinition[result.Tables.Length];
                for (int i = 0; i < areas.Length; i++) areas[i] = new OnlineDungeonEncounterDefinition { DisplayName = "source" };
                for (int i = 0; i < npcs.Length; i++) npcs[i] = new OnlineDungeonNpcSpawn { ActorId = (ulong)i + 5,
                    EncounterIndex = result.Stages[i], LootStageIndex = result.Stages[i], LootEnemyIndex = result.Enemies[i] };
                WriteObjectArray(proof, "_npcs", npcs); WriteObjectArray(proof, "_encounters", areas); WriteObjectArray(proof, "_lootTables", result.Tables);
                if (!proof.TryValidateItemDrops(out var error)) throw new InvalidOperationException(error);
            }
            finally { Object.DestroyImmediate(proof); }
            return result;
        }

        private static void ValidateLootMapping(OnlineDungeonDefinition definition, SourceLoot source, bool configured)
        {
            if (source.Tables.Length != 0 && definition.NpcCount != source.Members.Count)
                throw new InvalidOperationException("原掉落敌人数与已导出NPC不一致，需要正式Export。");
            if (configured)
            {
                if (definition.CoinPrefab != source.CoinPrefab) throw new InvalidOperationException("正式CoinPrefab与原Scene引用不一致，请窄刷新掉落配置。");
                if (definition.LootTableCount != source.Tables.Length) throw new InvalidOperationException("正式Definition缺少原掉落表，请刷新掉落配置。");
                for (int i = 0; i < source.Tables.Length; i++)
                    if (definition.GetLootTable(i) != source.Tables[i]) throw new InvalidOperationException("正式掉落表引用与原Scene不一致。");
            }
            for (int i = 0; i < definition.NpcCount; i++)
            {
                var npc = definition.GetNpc(i); int stage = source.Tables.Length == 0 ? -1 : source.Stages[i];
                int enemy = source.Tables.Length == 0 ? -1 : source.Enemies[i];
                if (source.Tables.Length != 0 && (npc.EncounterIndex != stage
                    || !npc.Position.Equals(source.Members[i].transform.position) || !npc.Rotation.Equals(source.Members[i].transform.rotation)))
                    throw new InvalidOperationException("原NPC布局与现有导出不一致，窄刷新不能改写静态地图来源。");
                if (configured && (npc.LootStageIndex != stage || npc.LootEnemyIndex != enemy))
                    throw new InvalidOperationException("正式NPC原stage/enemy索引不一致，请刷新掉落配置。");
            }
        }

        /// <summary>
        /// 官方窄刷新：只读原Scene，先验证全部候选，再只保存受影响Definition。不会克隆/重导WorldPrefab、
        /// 不改Build Scenes、不SaveAllAssets，也不能用刷新Hash掩盖原Scene几何变化。
        /// </summary>
        public static void RefreshLootConfiguration()
        {
            OnlineSceneSetup.RequireCleanEditor();
            // 与正式Authority使用同一可信目录与来源界；Ground满时由事务拒绝/pending，不扩大传输边界。
            var combat = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            if (combat == null || combat.Catalog == null || !combat.Catalog.TryCreateIndex(out var catalog, out var catalogError))
                throw new InvalidOperationException("正式Item来源缺少可信物品目录。");
            long itemBound = DungeonLootPlanner.CalculateItemLootBound(catalog);
            GameLog.Info("原P7完整Item来源key上界=" + itemBound + "，原Ground总条目上限=" + OnlineGroundInventorySnapshot.MaximumItems, "OnlineDungeon");
            var pending = new List<LootRefresh>();
            foreach (string id in Ids)
            {
                var definition = AssetDatabase.LoadAssetAtPath<OnlineDungeonDefinition>(DefinitionPath(id));
                if (definition == null || definition.DungeonId != id || definition.SourceScenePath != SourcePath(id))
                    throw new InvalidOperationException("缺少已正式导出的Definition，不能靠掉落刷新建立地图：" + id);
                string sceneHash = Hash(SourcePath(id));
                // 首次迁移接受旧纯Scene Hash；之后只接受独立Scene基线。源Scene变化必须由原正式Export处理。
                string baseline = string.IsNullOrEmpty(definition.SourceSceneHash) ? definition.SourceHash : definition.SourceSceneHash;
                if (baseline != sceneHash) throw new InvalidOperationException("原Scene已变化；窄掉落刷新拒绝更新基线，请正式Export：" + id);
                var scene = EditorSceneManager.OpenPreviewScene(SourcePath(id));
                try
                {
                    var loot = ReadSourceLoot(scene, id); ValidateLootMapping(definition, loot, false);
                    var solids = ReadFixedSolidSource(scene, id);
                    if (definition.ShowEncounterHud != ReadSourceHudPolicy(scene)) throw new InvalidOperationException("原HUD策略与正式导出不一致。");
                    var candidate = Object.Instantiate(definition);
                    try
                    {
                        var npcs = new OnlineDungeonNpcSpawn[definition.NpcCount]; var stages = new int[npcs.Length]; var enemies = new int[npcs.Length];
                        for (int i = 0; i < npcs.Length; i++)
                        {
                            var old = definition.GetNpc(i); stages[i] = loot.Tables.Length == 0 ? -1 : loot.Stages[i]; enemies[i] = loot.Tables.Length == 0 ? -1 : loot.Enemies[i];
                            npcs[i] = new OnlineDungeonNpcSpawn { ActorId = old.ActorId, Prefab = old.Prefab, Position = old.Position,
                                Rotation = old.Rotation, AttackClipDuration = old.AttackClipDuration, EncounterIndex = old.EncounterIndex,
                                InitiallyVisible = old.InitiallyVisible, LootStageIndex = stages[i], LootEnemyIndex = enemies[i] };
                        }
                        WriteObjectArray(candidate, "_npcs", npcs); WriteObjectArray(candidate, "_lootTables", loot.Tables);
                        WriteObjectArray(candidate, "_fixedSolidSpawns", solids.Spawns);
                        var coinData = new SerializedObject(candidate);
                        coinData.FindProperty("_coinPrefab").objectReferenceValue = loot.CoinPrefab; coinData.ApplyModifiedPropertiesWithoutUndo();
                        if (!candidate.TryValidate(out var error)) throw new InvalidOperationException("掉落刷新候选非法：" + error);
                        ValidateWorld(candidate.WorldPrefab, candidate.EncounterCount);
                        pending.Add(new LootRefresh { Definition = definition, Tables = loot.Tables, CoinPrefab = loot.CoinPrefab, Stages = stages, Enemies = enemies,
                            FixedSolidSpawns = solids.Spawns, SceneHash = sceneHash, SourceHash = ComputeSourceHash(SourcePath(id), loot.Tables, solids, loot.CoinPrefab) });
                    }
                    finally { Object.DestroyImmediate(candidate); }
                }
                finally { EditorSceneManager.ClosePreviewScene(scene); }
            }
            foreach (var next in pending)
            {
                var data = new SerializedObject(next.Definition); var tables = data.FindProperty("_lootTables");
                data.FindProperty("_coinPrefab").objectReferenceValue = next.CoinPrefab;
                tables.arraySize = next.Tables.Length;
                for (int i = 0; i < next.Tables.Length; i++) tables.GetArrayElementAtIndex(i).objectReferenceValue = next.Tables[i];
                var npcs = data.FindProperty("_npcs");
                for (int i = 0; i < npcs.arraySize; i++)
                { var npc = npcs.GetArrayElementAtIndex(i); npc.FindPropertyRelative("LootStageIndex").intValue = next.Stages[i]; npc.FindPropertyRelative("LootEnemyIndex").intValue = next.Enemies[i]; }
                data.FindProperty("_sourceSceneHash").stringValue = next.SceneHash; data.FindProperty("_sourceHash").stringValue = next.SourceHash;
                bool changed = data.ApplyModifiedPropertiesWithoutUndo();
                if (!SameFixedSolidSpawns(next.Definition, next.FixedSolidSpawns))
                { WriteObjectArray(next.Definition, "_fixedSolidSpawns", next.FixedSolidSpawns); changed = true; }
                if (changed) { EditorUtility.SetDirty(next.Definition); AssetDatabase.SaveAssetIfDirty(next.Definition); }
            }
            Verify();
        }

        /// <summary>规则依赖只遍历原SO引用图；视觉仅直接PickupPrefab/WorldModel源与meta，不递归无关材质纹理或Library产物。</summary>
        public static IEnumerable<string> EnumerateLootHashInputs(string dungeonId)
        {
            var scene = EditorSceneManager.OpenPreviewScene(SourcePath(dungeonId));
            try
            {
                var loot = ReadSourceLoot(scene, dungeonId);
                var paths = LootHashInputs(loot.Tables);
                if (loot.CoinPrefab != null) AddLootSource(loot.CoinPrefab, paths);
                paths.UnionWith(ReadFixedSolidSource(scene, dungeonId).HashInputs); return paths;
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }
        public static string ComputeSourceHash(string dungeonId)
        {
            if (dungeonId == "home") return Hash(HomeSourcePath);
            var scene = EditorSceneManager.OpenPreviewScene(SourcePath(dungeonId));
            try
            {
                var loot = ReadSourceLoot(scene, dungeonId);
                return ComputeSourceHash(SourcePath(dungeonId), loot.Tables, ReadFixedSolidSource(scene, dungeonId), loot.CoinPrefab);
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }
        private static string ComputeSourceHash(string scenePath, DungeonLootTable[] tables, SourceFixedSolids solids, GameObject coinPrefab)
        {
            var returns = ReturnHashInputs(scenePath);
            if (tables.Length == 0 && solids.Spawns.Length == 0 && returns.Count == 0) return Hash(scenePath);
            var paths = LootHashInputs(tables); paths.UnionWith(solids.HashInputs); paths.UnionWith(returns); paths.Add(scenePath);
            if (coinPrefab != null) AddLootSource(coinPrefab, paths);
            var body = new StringBuilder("NornDungeonSource4\n");
            foreach (string path in paths) body.Append(path).Append('\n').Append(Hash(path)).Append('\n');
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(body.ToString()))).Replace("-", "").ToLowerInvariant();
        }
        private static SortedSet<string> LootHashInputs(DungeonLootTable[] tables)
        {
            var paths = new SortedSet<string>(StringComparer.Ordinal); var queue = new Queue<ScriptableObject>(); var seen = new HashSet<int>();
            foreach (var table in tables) queue.Enqueue(table);
            while (queue.Count != 0)
            {
                var rule = queue.Dequeue(); if (rule == null || !seen.Add(rule.GetInstanceID())) continue;
                AddLootSource(rule, paths);
                if (rule is ItemDefinition item) { if (item.PickupPrefab != null) AddLootSource(item.PickupPrefab, paths); if (item.WorldModel != null) AddLootSource(item.WorldModel, paths); }
                // 只追SO直接引用；进入Item/Wand/Spell规则和WandRolls，不沿模型去追材质、贴图和导入缓存。
                var data = new SerializedObject(rule); var property = data.GetIterator();
                while (property.Next(true))
                    if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue is ScriptableObject child) queue.Enqueue(child);
            }
            return paths;
        }
        private static void AddLootSource(Object value, SortedSet<string> paths)
        {
            string path = AssetDatabase.GetAssetPath(value);
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal) || !File.Exists(path))
                throw new InvalidOperationException("原掉落依赖必须是可定位的Assets源文件：" + value.name);
            if (EditorUtility.IsDirty(value)) throw new InvalidOperationException("原掉落源资产尚未保存，拒绝给内存配置签旧字节Hash：" + path);
            paths.Add(path); if (File.Exists(path + ".meta")) paths.Add(path + ".meta");
        }

        private static bool ReadSourceHudPolicy(Scene scene)
        {
            var stages = Find<DemoRunHudController>(scene);
            if (stages.Count > 1) throw new InvalidOperationException("原场景存在多个遭遇HUD Adapter，无法确定共同显示策略。");
            return stages.Count == 0 || stages[0].enabled;
        }
        private static void AddNpc(Preview preview, GameObject root, int encounter)
        {
            var enemy = root.GetComponent<EnemyControllerBase>(); var boss = root.GetComponent<WizardBossController>();
            if ((enemy != null) == (boss != null)) throw new InvalidOperationException("原NPC必须有唯一Controller。");
            preview.Npcs.Add(new SourceNpc { Root = root, Position = root.transform.position, Rotation = root.transform.rotation,
                Scale = root.transform.lossyScale, Encounter = encounter, Duration = enemy != null ? AttackDuration(enemy)
                    : Mathf.Max(.05f, boss.Definition.CastTelegraphDuration + boss.Definition.CastRecoveryDuration) });
        }
        private static float AttackDuration(EnemyControllerBase enemy)
        {
            var animator = enemy.GetComponentInChildren<Animator>(true);
            if (enemy.Definition == null || enemy.Definition.Attack == null || animator == null) throw new InvalidOperationException("原NPC缺少攻击/Animator配置。");
            var overrides = animator.runtimeAnimatorController as AnimatorOverrideController;
            var controller = (overrides != null ? overrides.runtimeAnimatorController : animator.runtimeAnimatorController) as AnimatorController;
            var state = controller != null ? FindState(controller.layers[0].stateMachine, enemy.Definition.Attack.AnimationStateName) : null;
            if (state == null || !(state.motion is AnimationClip clip) || state.speed <= 0 || state.speedParameterActive || animator.speed <= 0)
                throw new InvalidOperationException("NPC攻击必须可离线确定Clip时长：" + enemy.name);
            if (overrides != null && overrides[clip] != null) clip = overrides[clip];
            return clip.length / state.speed / animator.speed;
        }
        private static AnimatorState FindState(AnimatorStateMachine machine, string name)
        {
            foreach (var state in machine.states) if (state.state.name == name) return state.state;
            foreach (var child in machine.stateMachines) { var result = FindState(child.stateMachine, name); if (result != null) return result; }
            return null;
        }

        private static void RemoveActorsAndOwnership(Preview preview)
        {
            var remove = new HashSet<GameObject>();
            foreach (var player in Find<PlayerControllerBase>(preview.Source)) remove.Add(preview.Clones[player.transform.GetInstanceID()].gameObject);
            foreach (var npc in preview.Npcs) remove.Add(preview.Clones[npc.Root.transform.GetInstanceID()].gameObject);
            foreach (var canvas in preview.Root.GetComponentsInChildren<Canvas>(true)) remove.Add(canvas.gameObject);
            foreach (var behaviour in preview.Root.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour != null && IsOwnedObject(behaviour.GetType().Name)) remove.Add(behaviour.gameObject);
            foreach (var node in remove) if (node != null) Object.DestroyImmediate(node);
            RemoveUnauthorizedBehaviours(preview.Root, false);
            // Cinemachine等脚本可能RequireComponent(Camera)，先剥脚本再删原单机Camera/Listener，
            // 否则Unity会拒绝删除，联机加载后可能留下第二套镜头或AudioListener。
            foreach (var camera in preview.Root.GetComponentsInChildren<Camera>(true)) Object.DestroyImmediate(camera);
            foreach (var listener in preview.Root.GetComponentsInChildren<AudioListener>(true)) Object.DestroyImmediate(listener);
            foreach (var sound in preview.Root.GetComponentsInChildren<AudioSource>(true)) Object.DestroyImmediate(sound);
            foreach (var gate in preview.Root.GetComponentsInChildren<ArenaGate>(true)) gate.SetClosed(false);
        }
        private static bool IsOwnedObject(string name) => name == "WorldItemSpawnPoint" || name == "WorldItemPickup" ||
            name == "WorldCurrencyPickup" || name == "SpellRewardPickup" || name == "DungeonFinalRewardPickup";
        private static bool AllowedWorld(MonoBehaviour value) => value is ArenaGate || value is OnlineDungeonWorldBindings || value is OnlineHomeWorldBindings ||
            value is OnlineDungeonReturnBindings || IsControlledReturnPresentation(value) || value is OnlineDungeonEnvironment || value is NavMeshSurface || value is NavMeshModifier || value is NavMeshModifierVolume ||
            value is Volume || value is UniversalAdditionalLightData || value is OnlineMaterialWorldBinding ||
            value is ElementWorldRuntime || value is ElementFieldSolidBaker || value is ElementWorldExposureSystem ||
            value is LiquidWorldRuntime || value is FixedSolidWorldRuntime || value is GasWorldRuntime ||
            value is MaterialEnvironmentAuthoring || value is ContainerDepositAdapter || value is MaterialInventoryTransferAdapter ||
            IsMaterialRenderer(value.GetType().FullName) || value.GetType().FullName == "Game.Rendering.ReactionBurstVisualController";

        private static bool IsMaterialRenderer(string name) => name == "Game.Rendering.GridLiquidFieldSurfaceRenderer"
            || name == "Game.Rendering.LiquidSurfaceRenderer" || name == "Game.Rendering.StableLiquidSurfaceRenderer"
            || name == "Game.Rendering.FixedSolidSurfaceRenderer" || name == "Game.Rendering.GasFieldVolumeRenderer"
            || name == "Game.Rendering.ElementWorldWaterRenderer" || name == "Game.Rendering.ElementWorldFireRenderer"
            || name == "Game.Rendering.GpuFireParcelRenderer";

        /// <summary>
        /// Preview只创建内存Profile，绝不修改源Scene/原SO或保存新Asset。沿用原材料Root、位置、三相分辨率、
        /// Catalog和Renderer资产，仅把在线Gameplay后端固定为CPU；非Water的独立GPU Route也必须覆盖。
        /// 原场景中Disabled GPU组件仍可能Awake，因此它们不进入导出白名单，而不是只改enabled。
        /// </summary>
        private static void PrepareMaterialWorld(Preview preview)
        {
            var worlds = preview.Root.GetComponentsInChildren<ElementWorldRuntime>(true);
            if (worlds.Length != 1) throw new InvalidOperationException("正式地图必须有唯一材料Root。");
            var world = worlds[0]; var data = new SerializedObject(world);
            var source = data.FindProperty("_profile").objectReferenceValue as ElementWorldProfile;
            if (source == null) throw new InvalidOperationException("原材料世界缺少Profile。");
            var profile = CloneMaterialProfile(preview, source); var config = new SerializedObject(profile);
            var sourceRoute = config.FindProperty("_materialSimulationRouting").objectReferenceValue as MaterialSimulationRoutingProfile;
            if (sourceRoute == null) throw new InvalidOperationException("原材料世界缺少完整Routing配置。");
            var route = CloneMaterialProfile(preview, sourceRoute); var routeData = new SerializedObject(route);
            var routes = routeData.FindProperty("_routes");
            for (int i = 0; i < routes.arraySize; i++)
            {
                var backend = routes.GetArrayElementAtIndex(i).FindPropertyRelative("_backend");
                if (backend.intValue == (int)MaterialSimulationBackendKind.GpuPbfLiquid)
                    backend.intValue = (int)MaterialSimulationBackendKind.GridLiquid;
            }
            routeData.ApplyModifiedPropertiesWithoutUndo();
            config.FindProperty("_materialSimulationRouting").objectReferenceValue = route;
            config.FindProperty("_waterSimulationMode").intValue = (int)WaterSimulationMode.GridLiquid;
            config.FindProperty("_enableFluidChunkStreaming").boolValue = false;
            config.ApplyModifiedPropertiesWithoutUndo();
            data.FindProperty("_profile").objectReferenceValue = profile;
            data.FindProperty("_executionMode").intValue = (int)WorldExecutionMode.Replica;
            data.FindProperty("_interestPoint").objectReferenceValue = null;
            data.FindProperty("_gpuPbfFluidRuntime").objectReferenceValue = null;
            data.FindProperty("_fluidGameplayOccupancy").objectReferenceValue = null;
            data.FindProperty("_fluidChunkStreamingRuntime").objectReferenceValue = null;
            data.ApplyModifiedPropertiesWithoutUndo();
            CloneOwnerProfile(preview, world.GetComponent<LiquidWorldRuntime>());
            CloneOwnerProfile(preview, world.GetComponent<FixedSolidWorldRuntime>());
            CloneOwnerProfile(preview, world.GetComponent<GasWorldRuntime>());
            foreach (var exposure in preview.Root.GetComponentsInChildren<ElementWorldExposureSystem>(true)) exposure.enabled = false;
            var binding = preview.Root.AddComponent<OnlineMaterialWorldBinding>();
            var bindingData = new SerializedObject(binding); bindingData.FindProperty("_world").objectReferenceValue = world;
            bindingData.ApplyModifiedPropertiesWithoutUndo();
        }
        private static ScriptableObject CloneMaterialProfile(Preview preview, ScriptableObject source)
        {
            var clone = Object.Instantiate(source); clone.name = preview.Id.ToUpperInvariant() + "_OnlineCPU_" + source.GetType().Name;
            preview.MaterialProfiles.Add(clone); return clone;
        }
        private static void CloneOwnerProfile(Preview preview, MonoBehaviour owner)
        {
            if (owner == null) throw new InvalidOperationException("正式材料世界缺少三相CPU Owner。");
            var data = new SerializedObject(owner); var field = data.FindProperty("_profile");
            if (!(field.objectReferenceValue is ScriptableObject source)) throw new InvalidOperationException("三相Owner缺少Profile。");
            field.objectReferenceValue = CloneMaterialProfile(preview, source); data.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// 仅Export动作持久化独立在线配置。既有Asset用CopySerialized保持GUID；先建立全部对象映射，再替换
        /// Profile间引用及场景组件引用，避免Prefab保存成指向Preview临时对象的空引用。关闭Preview只销毁内存副本。
        /// </summary>
        private static void PersistMaterialProfiles(Preview preview)
        {
            string folder = DataFolder + "/Materials"; EnsureFolder(folder);
            var copies = new Dictionary<Object, Object>();
            foreach (var temporary in preview.MaterialProfiles)
            {
                string path = folder + "/" + temporary.name + ".asset";
                var persistent = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                if (persistent == null) { persistent = ScriptableObject.CreateInstance(temporary.GetType()); AssetDatabase.CreateAsset(persistent, path); }
                else if (persistent.GetType() != temporary.GetType()) throw new InvalidOperationException("在线材料配置路径类型冲突：" + path);
                persistent.name = temporary.name; copies.Add(temporary, persistent);
            }
            foreach (var pair in copies)
            {
                EditorUtility.CopySerialized(pair.Key, pair.Value);
                RemapMaterialReferences(pair.Value, copies); EditorUtility.SetDirty(pair.Value);
            }
            foreach (var component in preview.Root.GetComponentsInChildren<MonoBehaviour>(true))
                if (component != null) RemapMaterialReferences(component, copies);
        }
        private static void RemapMaterialReferences(Object owner, Dictionary<Object, Object> copies)
        {
            var data = new SerializedObject(owner); var property = data.GetIterator();
            while (property.Next(true))
                if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue != null
                    && copies.TryGetValue(property.objectReferenceValue, out var replacement)) property.objectReferenceValue = replacement;
            data.ApplyModifiedPropertiesWithoutUndo();
        }
        private static bool AllowedNpc(MonoBehaviour value)
        {
            string name = value.GetType().FullName;
            return value is EnemyControllerBase || value is WizardBossController || value is SpellCaster || value is HealthComponent ||
                value is StatusController || value is MeleeHitDetector || value is CharacterCombatFeedback || value is EnemyHealthBar ||
                // Teleport Presenter只订阅已发生事实，不裁决位移；必须随原Boss保留，否则正式P8会丢传送门细节。
                // 该组件自身在Dedicated Awake禁用，DS不生成六套Portal。仍不允许整类UI或Run Owner进入白名单。
                value is BossTeleportVfxController ||
                name == "Game.Rendering.StatusMaterialController" || value.GetType().Namespace == "UnityEngine.UI";
        }
        private static void StripNpc(GameObject root)
        {
            RemoveUnauthorizedBehaviours(root, true);
            foreach (var listener in root.GetComponentsInChildren<AudioListener>(true)) Object.DestroyImmediate(listener);
        }
        /// <summary>
        /// RequireComponent 不只是添加组件时的便利：Editor 也禁止删除仍被其它组件要求的目标。
        /// GetComponentsInChildren 的数组顺序不代表依赖顺序；简单正序/反序都会在部分正式地图遗留Owner。
        /// 因此仅在同物体没有依赖者时拆除组件，循环得到“先依赖者、后被依赖者”的顺序。
        /// 这是离线Editor工具，允许为检查依赖分配临时数组；它不进入客户端/Server的逐帧热路径。
        /// </summary>
        private static void RemoveUnauthorizedBehaviours(GameObject root, bool npc)
        {
            var pending = new List<MonoBehaviour>();
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour != null && !(npc ? AllowedNpc(behaviour) : AllowedWorld(behaviour))) pending.Add(behaviour);
            while (pending.Count != 0)
            {
                bool removed = false;
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var candidate = pending[i];
                    if (candidate == null) { pending.RemoveAt(i); removed = true; continue; }
                    if (HasRequiredComponentDependent(candidate)) continue;
                    Object.DestroyImmediate(candidate);
                    pending.RemoveAt(i); removed = true;
                }
                // 如果保留的纯表现组件反向要求一个被禁止的Owner，必须失败并改装配边界；
                // 不能为了让导出变绿偷偷保留客户端Gameplay/材料模拟权威。
                if (!removed) throw new InvalidOperationException("不能按RequireComponent依赖剥离Owner：" + pending[0]);
            }
        }
        private static bool HasRequiredComponentDependent(MonoBehaviour candidate)
        {
            var target = candidate.GetType();
            foreach (var dependent in candidate.GetComponents<Component>())
            {
                if (dependent == null || dependent == candidate) continue;
                foreach (RequireComponent requirement in dependent.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                    if (RequiresType(requirement.m_Type0, target) || RequiresType(requirement.m_Type1, target) ||
                        RequiresType(requirement.m_Type2, target)) return true;
            }
            return false;
        }
        private static bool RequiresType(Type required, Type candidate) => required != null && required.IsAssignableFrom(candidate);
        private static void ValidateWorld(GameObject root, int encounters)
        {
            if (root == null || root.activeSelf) throw new InvalidOperationException("世界Prefab必须保持inactive。");
            if (root.GetComponentsInChildren<OnlineMaterialWorldBinding>(true).Length != 1
                || root.GetComponentsInChildren<ElementWorldRuntime>(true).Length != 1)
                throw new InvalidOperationException("正式世界缺少唯一材料Binding/Runtime，请重新Export。");
            foreach (var node in root.GetComponentsInChildren<Transform>(true))
            {
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(node.gameObject) != 0) throw new InvalidOperationException("地图含Missing Script。");
                foreach (var behaviour in node.GetComponents<MonoBehaviour>())
                    if (behaviour == null || !AllowedWorld(behaviour)) throw new InvalidOperationException("地图含未授权Owner：" + behaviour);
            }
            var bindings = root.GetComponent<OnlineDungeonWorldBindings>(); string error = null;
            if (bindings == null || !bindings.TryValidate(encounters, out error)) throw new InvalidOperationException("地图入口接线无效：" + error);
            var surfaces = root.GetComponentsInChildren<NavMeshSurface>(true);
            var home = root.GetComponent<OnlineHomeWorldBindings>();
            if (encounters == 0 && home != null)
            {
                // 原Home没有NPC和NavMeshSurface。保留真实原图，不能为通过战斗地图检查伪造NavMesh。
                if (surfaces.Length != 0 || !home.TryValidate(out error))
                    throw new InvalidOperationException("和平Home几何或原无NavMesh布局不一致：" + error);
                return;
            }
            if (surfaces.Length != 1 || surfaces[0].navMeshData == null) throw new InvalidOperationException("必须保留原唯一NavMeshData，不能重新Bake另一张图。");
        }

        private static PlayerSpawnInfo DerivePlayerSpawns(Preview preview)
        {
            var cc = preview.Player.GetComponent<CharacterController>();
            if (cc == null) throw new InvalidOperationException("原玩家缺少CharacterController，无法验证真实出生体积。");
            var scale = preview.Player.transform.lossyScale;
            float horizontal = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            float radius = cc.radius * horizontal, height = Mathf.Max(cc.height * Mathf.Abs(scale.y), radius * 2f);
            float skin = Mathf.Max(0f, cc.skinWidth * horizontal);
            // Registry以直立Quaternion.identity实例化同源玩家，胶囊中心按该姿态计算；NPC导航仍保留原NavMesh。
            Vector3 center = Vector3.Scale(cc.center, scale);
            if (!(radius > .01f) || !float.IsFinite(height) || Vector3.Dot(preview.Player.transform.up, Vector3.up) < .999f)
                throw new InvalidOperationException("只支持有效直立玩家胶囊，不能用错误尺寸猜测出生点。");
            float spacing = Mathf.Max(1.2f, radius * 2f + .3f);
            var report = new PlayerSpawnInfo { SourcePosition = preview.Player.transform.position,
                Positions = new Vector3[4], GroundNormals = new Vector3[4], MinimumSeparation = spacing * .85f,
                MaxCandidateChecks = 33, UsedSourceCollidersOnly = true };
            var active = SceneManager.GetActiveScene(); Scene queryScene = default;
            GameObject probeRoot = null;
            var surfaces = preview.Root.GetComponentsInChildren<NavMeshSurface>(true); var enabled = new bool[surfaces.Length];
            for (int i = 0; i < surfaces.Length; i++) { enabled[i] = surfaces[i].enabled; surfaces[i].enabled = false; }
            try
            {
                // SceneManager.CreateScene(LocalPhysicsMode)只供PlayMode；NewScene又会受用户/测试器
                // 的untitled Scene限制。因此沿用Editor专用Preview，不要求保存、关闭任何现有Scene。
                // 不假定Preview拥有独立PhysicsScene：仅遍历本图Collider做几何Query，净空使用
                // 两形状指定Pose的ComputePenetration，不Simulate、不做全局Raycast/Overlap。
                queryScene = EditorSceneManager.NewPreviewScene();
                SceneManager.MoveGameObjectToScene(preview.Root, queryScene); preview.Root.SetActive(true); Physics.SyncTransforms();
                int mask = 0;
                for (int layer = 0; layer < 32; layer++) if (!Physics.GetIgnoreLayerCollision(cc.gameObject.layer, layer)) mask |= 1 << layer;
                var sourceColliders = preview.Root.GetComponentsInChildren<Collider>(false);
                probeRoot = new GameObject("OfflinePlayerCapsuleQuery"); probeRoot.SetActive(false);
                SceneManager.MoveGameObjectToScene(probeRoot, queryScene);
                var probe = probeRoot.AddComponent<CapsuleCollider>(); probe.direction = 1;
                // CapsuleCollider轴向为Y，center=0；每次通过ComputePenetration的Pose传入实际世界中心。
                // 内缩与CC的Skin Width一致，地面接触不会误判穿透；外部Collider的尺度仍取其真实Transform。
                float clearanceRadius = Mathf.Max(.01f, radius - Mathf.Min(skin, radius * .5f));
                probe.radius = clearanceRadius; probe.height = height - 2f * (radius - clearanceRadius);
                int count = 0;
                float minNormal = Mathf.Max(.6f, Mathf.Cos(Mathf.Clamp(cc.slopeLimit, 0f, 89f) * Mathf.Deg2Rad));
                for (int attempt = 0; attempt < report.MaxCandidateChecks && count < 4; attempt++)
                {
                    report.CandidateChecks++;
                    var desired = report.SourcePosition;
                    if (attempt != 0)
                    {
                        int ring = (attempt - 1) / 8 + 1, direction = (attempt - 1) % 8;
                        float angle = direction * Mathf.PI * .25f;
                        desired += new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * spacing * ring;
                    }
                    if (!TryPlacePlayer(sourceColliders, probe, desired, center, radius, height, minNormal, mask, out var position, out var normal))
                    {
                        // 首席保留作者选择的XZ；只按原地面修正落脚Y。原位无地面/净空应明确失败，不能悄悄挪到别处。
                        if (attempt == 0) throw new InvalidOperationException("原玩家出生XZ缺少可站地面或胶囊净空：" + preview.Id + " " + desired);
                        continue;
                    }
                    bool separate = true;
                    for (int i = 0; i < count; i++) if (Vector3.Distance(report.Positions[i], position) < report.MinimumSeparation) separate = false;
                    if (!separate) continue;
                    report.Positions[count] = position; report.GroundNormals[count] = normal; count++;
                }
                if (count != 4) throw new InvalidOperationException("原出生区域在33个候选内无法容纳四席地面/胶囊，请调整原authoring；不回退原点。");
                return report;
            }
            finally
            {
                if (probeRoot != null) Object.DestroyImmediate(probeRoot);
                preview.Root.SetActive(false);
                if (preview.Root.scene != preview.World) SceneManager.MoveGameObjectToScene(preview.Root, preview.World);
                for (int i = 0; i < surfaces.Length; i++) if (surfaces[i] != null) surfaces[i].enabled = enabled[i];
                if (queryScene.IsValid()) EditorSceneManager.ClosePreviewScene(queryScene);
                if (active.IsValid() && active.isLoaded) SceneManager.SetActiveScene(active);
            }
        }
        private static bool TryPlacePlayer(Collider[] colliders, CapsuleCollider probe, Vector3 desired, Vector3 center, float radius, float height,
            float minNormal, int mask, out Vector3 position, out Vector3 normal)
        {
            position = normal = default;
            float feetOffset = center.y - height * .5f;
            var ray = desired + new Vector3(center.x, feetOffset + .5f, center.z);
            if (!RaycastSource(colliders, ray, 2.5f, mask, out var hit) || hit.normal.y < minNormal) return false;
            // 斜面上的球底不能只按中心Ray高度放置：r/n.y-r给出球与局部平面相切所需的额外抬升。
            position = desired; position.y = hit.point.y - feetOffset + radius * (1f / hit.normal.y - 1f); normal = hit.normal;
            // 中心一条Ray可能落在悬崖边缘。再检查胶囊足迹内四个支撑点，避免半个身体悬空的候选。
            float footprint = radius * .6f;
            for (int i = 0; i < 4; i++)
            {
                var offset = i == 0 ? Vector3.right : i == 1 ? Vector3.left : i == 2 ? Vector3.forward : Vector3.back;
                var supportRay = hit.point + offset * footprint + Vector3.up * .5f;
                if (!RaycastSource(colliders, supportRay, 1f, mask, out var support) ||
                    support.normal.y < minNormal || Mathf.Abs(support.point.y - hit.point.y) > footprint + .1f) return false;
            }
            Vector3 worldCenter = position + center;
            for (int i = 0; i < colliders.Length; i++)
            {
                var collider = colliders[i]; if (!CanQuery(collider, mask)) continue;
                // ComputePenetration不依赖物理空间结构更新，也不需要让探测胶囊参与真实模拟；
                // 一方是Capsule，另一方可为本图Mesh/Box等实际形状。这里仅在Editor离线导出执行。
                if (Physics.ComputePenetration(probe, worldCenter, Quaternion.identity, collider, collider.transform.position,
                    collider.transform.rotation, out _, out float distance) && distance > .0001f) return false;
            }
            return true;
        }

        private static bool CanQuery(Collider collider, int mask) => collider != null && collider.enabled &&
            collider.gameObject.activeInHierarchy && !collider.isTrigger && (mask & 1 << collider.gameObject.layer) != 0;

        private static bool RaycastSource(Collider[] colliders, Vector3 origin, float distance, int mask, out RaycastHit nearest)
        {
            nearest = default; bool found = false; var ray = new Ray(origin, Vector3.down);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (!CanQuery(colliders[i], mask) || !colliders[i].Raycast(ray, out var hit, distance)) continue;
                if (found && hit.distance >= nearest.distance) continue;
                nearest = hit; found = true;
            }
            return found;
        }

        /// <summary>受GameplayHash保护的精确来源及派生资产；Definition不反向保存GameplayHash，所以不会循环Hash。</summary>
        public static IEnumerable<string> EnumerateHashInputs()
        {
            var paths = new SortedSet<string>(StringComparer.Ordinal) { SourcePath("p7"), SourcePath("p8"),
                "Assets/_Project/Editor/Networking/OnlineDungeonTools.cs", CatalogPath };
            foreach (string folder in new[] { WorldFolder, DataFolder })
                if (Directory.Exists(folder)) foreach (string path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    if (!path.EndsWith(".meta", StringComparison.Ordinal)) paths.Add(path.Replace('\\', '/'));
            // NavMesh、原NPC程序/Clip等引用也必须加入，不能仅Hash一个不变GUID而漏掉实际资产内容修改。
            foreach (string root in new[] { SourcePath("p7"), SourcePath("p8") })
                foreach (string path in AssetDatabase.GetDependencies(root, true))
                    if (File.Exists(path) && !path.EndsWith(".cs", StringComparison.Ordinal)) paths.Add(path);
            // 与SourceHash共用新增原规则/直接模型源及其meta，不能只保护Scene里的GUID而漏掉被引用配置字节。
            foreach (string id in Ids) foreach (string path in EnumerateLootHashInputs(id)) paths.Add(path);
            return paths;
        }
        public static IEnumerable<GameObject> OutputPrefabs()
        {
            var outputs = new SortedDictionary<string, GameObject>(StringComparer.Ordinal);
            if (Directory.Exists(WorldFolder + "/Npcs"))
                foreach (string npc in Directory.EnumerateFiles(WorldFolder + "/Npcs", "*.prefab"))
                    foreach (string path in AssetDatabase.GetDependencies(npc.Replace('\\', '/'), true))
                    {
                        // 网络产出是已挂Projectile/AreaDamage的Prefab；先筛路径再加载，
                        // 不为检查类型把NPC全部FBX、纹理、音频等依赖逐一反序列化。
                        if (!path.EndsWith(".prefab", StringComparison.Ordinal)) continue;
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        if (prefab != null && (prefab.GetComponentInChildren<ProjectileBase>(true) != null || prefab.GetComponentInChildren<AreaDamage>(true) != null)) outputs[path] = prefab;
                    }
            return outputs.Values;
        }

        private static void CaptureEnvironment(Preview preview, OnlineDungeonEnvironment environment)
        {
            // Prefab没有Scene RenderSettings；按受控原Scene YAML读取稳定字段，不临时覆盖作者Active Scene来取全局值。
            string text = File.ReadAllText(preview.Path);
            Match block = Regex.Match(text, @"(?m)^RenderSettings:\s*\n(?<body>[\s\S]*?)(?=^--- !u!)");
            if (!block.Success) throw new InvalidOperationException("源Scene缺少RenderSettings。");
            string body = block.Groups["body"].Value;
            var data = new SerializedObject(environment);
            Set(data, "_fog", Number(body, "m_Fog") != 0); Set(data, "_fogMode", (int)Number(body, "m_FogMode"));
            Set(data, "_fogDensity", Number(body, "m_FogDensity")); Set(data, "_fogStart", Number(body, "m_LinearFogStart")); Set(data, "_fogEnd", Number(body, "m_LinearFogEnd"));
            data.FindProperty("_fogColor").colorValue = ReadColor(body, "m_FogColor");
            data.FindProperty("_ambientSky").colorValue = ReadColor(body, "m_AmbientSkyColor");
            data.FindProperty("_ambientEquator").colorValue = ReadColor(body, "m_AmbientEquatorColor");
            data.FindProperty("_ambientGround").colorValue = ReadColor(body, "m_AmbientGroundColor");
            data.FindProperty("_subtractiveShadow").colorValue = ReadColor(body, "m_SubtractiveShadowColor");
            Set(data, "_ambientMode", (int)Number(body, "m_AmbientMode")); Set(data, "_ambientIntensity", Number(body, "m_AmbientIntensity"));
            Set(data, "_reflectionMode", (int)Number(body, "m_DefaultReflectionMode")); Set(data, "_reflectionResolution", (int)Number(body, "m_DefaultReflectionResolution"));
            Set(data, "_reflectionIntensity", Number(body, "m_ReflectionIntensity")); Set(data, "_reflectionBounces", (int)Number(body, "m_ReflectionBounces"));
            Set(data, "_haloStrength", Number(body, "m_HaloStrength")); Set(data, "_flareStrength", Number(body, "m_FlareStrength")); Set(data, "_flareFadeSpeed", Number(body, "m_FlareFadeSpeed"));
            data.FindProperty("_skybox").objectReferenceValue = AssetReference<Material>(body, "m_SkyboxMaterial");
            data.FindProperty("_customReflection").objectReferenceValue = AssetReference<Cubemap>(body, "m_CustomReflection");
            data.ApplyModifiedPropertiesWithoutUndo();
            CaptureBakedLighting(preview, environment, text);
        }

        private static void CaptureBakedLighting(Preview preview, OnlineDungeonEnvironment environment, string sourceText)
        {
            var settings = new SerializedObject(environment);
            var lighting = Regex.Match(sourceText, @"(?m)^  m_LightingDataAsset: \{[^}]*guid: ([a-f0-9]{32})[^}]*\}");
            string lightingPath = lighting.Success ? AssetDatabase.GUIDToAssetPath(lighting.Groups[1].Value) : string.Empty;
            bool customLighting = lighting.Success && !lighting.Groups[1].Value.StartsWith("0000000000000000", StringComparison.Ordinal);
            settings.FindProperty("_sourceLightingDataPath").stringValue = lightingPath;
            settings.FindProperty("_sourceLightingDataMissing").boolValue = customLighting && string.IsNullOrEmpty(lightingPath);
            // 此时source仍处在只读Preview。只采集它实际使用且能解析的光照贴图，不把其他已加载Scene的整份全局数组带走。
            var global = LightmapSettings.lightmaps; var maps = new List<OnlineDungeonEnvironment.BakedMap>();
            var renderers = new List<OnlineDungeonEnvironment.BakedRenderer>(); var indices = new Dictionary<int, int>();
            foreach (var original in Find<Renderer>(preview.Source))
            {
                int index = original.lightmapIndex;
                if (index < 0 || index >= global.Length || global[index] == null || global[index].lightmapColor == null ||
                    !preview.Clones.TryGetValue(original.transform.GetInstanceID(), out var node) || node == null || !node.IsChildOf(preview.Root.transform)) continue;
                var clone = CorrespondingComponent(node, original) as Renderer; if (clone == null) continue;
                if (!indices.TryGetValue(index, out int local))
                {
                    local = maps.Count; indices.Add(index, local); var source = global[index];
                    maps.Add(new OnlineDungeonEnvironment.BakedMap { Color = source.lightmapColor, Direction = source.lightmapDir, ShadowMask = source.shadowMask });
                }
                renderers.Add(new OnlineDungeonEnvironment.BakedRenderer { Target = clone, Index = local, ScaleOffset = original.lightmapScaleOffset });
            }
            settings.ApplyModifiedPropertiesWithoutUndo();
            WriteObjectArray(environment, "_lightmaps", maps.ToArray()); WriteObjectArray(environment, "_lightmappedRenderers", renderers.ToArray());
            var sun = Regex.Match(sourceText, @"(?m)^  m_Sun: \{fileID: ([0-9]+)\}");
            if (sun.Success && ulong.TryParse(sun.Groups[1].Value, out ulong sunId) && sunId != 0)
            {
                foreach (var original in Find<Light>(preview.Source))
                    if (GlobalObjectId.GetGlobalObjectIdSlow(original).targetObjectId == sunId)
                    {
                        settings = new SerializedObject(environment); settings.FindProperty("_sun").objectReferenceValue = Clone(preview, original);
                        settings.ApplyModifiedPropertiesWithoutUndo(); break;
                    }
            }
        }

        private static float Number(string text, string key)
        {
            var match = Regex.Match(text, @"(?m)^  " + key + @": ([-+0-9.eE]+)\s*$");
            if (!match.Success || !float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value))
                throw new InvalidOperationException("源RenderSettings字段无效：" + key);
            return value;
        }
        private static Color ReadColor(string text, string key)
        {
            var match = Regex.Match(text, @"(?m)^  " + key + @": \{r: ([^,]+), g: ([^,]+), b: ([^,]+), a: ([^}]+)\}");
            if (!match.Success) throw new InvalidOperationException("源颜色字段缺失：" + key);
            return new Color(float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), float.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                float.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture), float.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture));
        }
        private static T AssetReference<T>(string text, string key) where T : Object
        {
            var match = Regex.Match(text, @"(?m)^  " + key + @": \{[^}]*guid: ([a-f0-9]{32})[^}]*\}");
            if (!match.Success) return null;
            string path = AssetDatabase.GUIDToAssetPath(match.Groups[1].Value);
            var value = AssetDatabase.LoadAssetAtPath<T>(path);
            if (value == null) throw new InvalidOperationException("源环境资源丢失：" + key);
            return value;
        }
        private static void Set(SerializedObject data, string name, float value) => data.FindProperty(name).floatValue = value;
        private static void Set(SerializedObject data, string name, int value) => data.FindProperty(name).intValue = value;
        private static void Set(SerializedObject data, string name, bool value) => data.FindProperty(name).boolValue = value;
        private static void WriteObjectArray<T>(Object target, string field, T[] values)
        {
            // 仅Editor离散写入共同数组合同；不把反射放入Runtime/Tick。Unity对象引用保持原值，避免Json中instanceID跨对象丢失。
            var property = target.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (property == null) throw new InvalidOperationException("导出合同缺字段：" + field);
            property.SetValue(target, values); EditorUtility.SetDirty(target);
        }
        private static List<T> Find<T>(Scene scene) where T : Component
        { var result = new List<T>(); foreach (var root in scene.GetRootGameObjects()) result.AddRange(root.GetComponentsInChildren<T>(true)); return result; }
        private static T Clone<T>(Preview preview, T value) where T : Component
        {
            if (value == null || !preview.Clones.TryGetValue(value.transform.GetInstanceID(), out var node)) throw new InvalidOperationException("源引用没有对应克隆。");
            return (T)CorrespondingComponent(node, value);
        }
        private static Component CorrespondingComponent(Transform clone, Component original)
        {
            if (original is Transform) return clone;
            // 同一物体可以有多个同类型Collider/Renderer。只按Type取第一个会把入口、门板或光照引用
            // 映射到错误实例；以源组件在同类型数组中的序号匹配克隆，保留作者的精确接线。
            var originals = original.GetComponents(original.GetType());
            var copies = clone.GetComponents(original.GetType());
            for (int i = 0; i < originals.Length; i++)
                if (originals[i] == original && i < copies.Length) return copies[i];
            throw new InvalidOperationException("克隆缺少对应组件：" + original);
        }
        private static void MapTransforms(Transform original, Transform clone, Dictionary<int, Transform> mapping)
        { mapping.Add(original.GetInstanceID(), clone); for (int i = 0; i < original.childCount; i++) MapTransforms(original.GetChild(i), clone.GetChild(i), mapping); }
        private static void RemapSceneReferences(Preview preview)
        {
            // Instantiate只自动映射同一克隆树内的引用；原Scene跨Root的Gate/Volume等引用必须再映射一遍。
            // 否则对克隆调用SetClosed会碰到源Scene对象，或SavePrefab后残留非法Scene引用。
            foreach (var component in preview.Root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform) continue;
                var data = new SerializedObject(component); var property = data.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var value = property.objectReferenceValue;
                    if (value == null || EditorUtility.IsPersistent(value)) continue;
                    Transform source = value is Component c ? c.transform : (value as GameObject)?.transform;
                    if (source == null || !preview.Clones.TryGetValue(source.GetInstanceID(), out var target)) continue;
                    property.objectReferenceValue = value is GameObject ? target.gameObject : CorrespondingComponent(target, (Component)value);
                }
                data.ApplyModifiedPropertiesWithoutUndo();
            }
        }
        private static void ClearOutsideReferences(GameObject root)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform) continue;
                var data = new SerializedObject(component); var property = data.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var value = property.objectReferenceValue;
                    if (value == null || EditorUtility.IsPersistent(value)) continue;
                    Transform target = value is Component c ? c.transform : (value as GameObject)?.transform;
                    if (target != null && target != root.transform && !target.IsChildOf(root.transform)) property.objectReferenceValue = null;
                }
                data.ApplyModifiedPropertiesWithoutUndo();
            }
        }
        private static string SourcePath(string id) => id == "p7" ? "Assets/_Project/Scenes/P7_DemoRun.unity" : id == "p8" ? "Assets/_Project/Scenes/P8_BossField.unity" : id == "home" ? HomeSourcePath : throw new ArgumentException("副本ID未注册。", nameof(id));
        private static string WorldPath(string id) => WorldFolder + "/" + id.ToUpperInvariant() + "World.prefab";
        private static string DefinitionPath(string id) => DataFolder + "/" + id.ToUpperInvariant() + ".asset";
        private static string Hash(string path)
        { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant(); }
        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int slash = path.LastIndexOf('/'); string parent = path.Substring(0, slash); EnsureFolder(parent); AssetDatabase.CreateFolder(parent, path.Substring(slash + 1));
        }
    }
}
