using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Game.Character;
using Game.Core;
using Game.Run;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Project.Editor
{
    /// <summary>Home的窄导出入口；复用同一Preview/CPU材料/环境管线，但不重导或覆盖P7/P8资源。</summary>
    public static partial class OnlineDungeonTools
    {
        private const string HomeSourcePath = "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity";

        private static void GatherPeacefulHome(Preview preview)
        {
            var players = Find<PlayerControllerBase>(preview.Source);
            if (players.Count != 1 || Find<EnemyControllerBase>(preview.Source).Count != 0
                || Find<WizardBossController>(preview.Source).Count != 0 || Find<DemoEncounterController>(preview.Source).Count != 0
                || Find<BossEncounterController>(preview.Source).Count != 0)
                throw new InvalidOperationException("原Home必须有唯一玩家且没有NPC/战斗遭遇；不能伪造或吞掉作者内容。");
            preview.Player = players[0]; preview.Npcs.Clear(); preview.ShowEncounterHud = false;
            preview.Encounters = Array.Empty<OnlineDungeonEncounterDefinition>(); preview.Areas = Array.Empty<OnlineDungeonAreaBindings>();
            preview.LootTables = Array.Empty<DungeonLootTable>(); preview.CoinPrefab = null;
            preview.FixedSolidSpawns = Array.Empty<OnlineDungeonFixedSolidSpawn>();
            // Home没有掉落或固定库存来源；材料世界本身仍由原CPU管线克隆，不把场景材料当成掉落来源。
            preview.Hash = ComputeSourceHash(preview.Path, preview.LootTables, new SourceFixedSolids(), null);
        }

        private static void AttachPeacefulHomeBindings(Preview preview)
        {
            FindHomeSourceGeometry(preview.Source, out var area, out var research, out var portal, out var trigger);
            var bindings = preview.Root.AddComponent<OnlineHomeWorldBindings>(); var data = new SerializedObject(bindings);
            data.FindProperty("_researchVolume").objectReferenceValue = Clone(preview, research);
            data.FindProperty("_returnPortalCollider").objectReferenceValue = Clone(preview, trigger);
            data.FindProperty("_returnPortalTransform").objectReferenceValue = Clone(preview, portal.transform);
            data.FindProperty("_researchEnabled").boolValue = area.enabled;
            data.FindProperty("_returnPortalEnabled").boolValue = portal.enabled;
            data.ApplyModifiedPropertiesWithoutUndo();
            if (!bindings.TryValidate(out var error)) throw new InvalidOperationException(error);
        }

        private static void FindHomeSourceGeometry(UnityEngine.SceneManagement.Scene source, out HubAreaVolume area,
            out BoxCollider research, out HomePortal portal, out BoxCollider trigger)
        {
            var hubs = Find<HubInteractionController>(source);
            if (hubs.Count != 1) throw new InvalidOperationException("原Home研究入口必须有唯一HubInteractionController。");
            area = new SerializedObject(hubs[0]).FindProperty("_researchArea").objectReferenceValue as HubAreaVolume;
            if (area == null) throw new InvalidOperationException("原Home缺少已接线的研究区域。");
            research = new SerializedObject(area).FindProperty("_volume").objectReferenceValue as BoxCollider;
            if (research == null) research = area.GetComponent<BoxCollider>();
            portal = null;
            foreach (var candidate in Find<HomePortal>(source))
                if (candidate.Returning)
                {
                    if (portal != null) throw new InvalidOperationException("原Home返回门不唯一，拒绝猜测出口。");
                    portal = candidate;
                }
            if (portal == null || research == null) throw new InvalidOperationException("原Home研究Box或返回门缺失。");
            trigger = portal.GetComponent<BoxCollider>();
            if (trigger == null) throw new InvalidOperationException("原Home返回门缺少作者BoxCollider。");
        }

        [MenuItem("Tools/Norn/Online/Export Peaceful Home World")]
        public static void ExportHome()
        {
            OnlineSceneSetup.RequireCleanEditor();
            var protectedFiles = SnapshotHomeProtectedFiles();
            EnsureFolder(WorldFolder); EnsureFolder(DataFolder); EnsureFolder("Assets/_Project/Resources/Online");
            var root = BuildPreview("home"); var preview = Previews[root];
            try
            {
                Vector3[] positions = DerivePlayerSpawns(preview).Positions;
                // 与P7/P8共用CPU配置克隆，只保存HOME_OnlineCPU_*；不调用全Export，也不重新Bake原地图。
                PersistMaterialProfiles(preview);
                foreach (var temporary in preview.MaterialProfiles)
                {
                    var persistent = AssetDatabase.LoadAssetAtPath<ScriptableObject>(DataFolder + "/Materials/" + temporary.name + ".asset");
                    if (persistent == null) throw new InvalidOperationException("Home独立CPU材料配置持久化失败。");
                    AssetDatabase.SaveAssetIfDirty(persistent);
                }
                var world = PrefabUtility.SaveAsPrefabAsset(root, WorldPath("home"));
                if (world == null) throw new InvalidOperationException("Home静态世界保存失败。");
                var definition = AssetDatabase.LoadAssetAtPath<OnlineDungeonDefinition>(DefinitionPath("home"));
                if (definition == null)
                { definition = ScriptableObject.CreateInstance<OnlineDungeonDefinition>(); AssetDatabase.CreateAsset(definition, DefinitionPath("home")); }
                var data = new SerializedObject(definition);
                data.FindProperty("_dungeonId").stringValue = "home";
                data.FindProperty("_sourceScenePath").stringValue = HomeSourcePath;
                data.FindProperty("_sourceHash").stringValue = preview.Hash;
                data.FindProperty("_sourceSceneHash").stringValue = Hash(HomeSourcePath);
                data.FindProperty("_worldPrefab").objectReferenceValue = world;
                data.FindProperty("_showEncounterHud").boolValue = false;
                data.FindProperty("_coinPrefab").objectReferenceValue = null;
                var spawns = data.FindProperty("_playerSpawns"); spawns.arraySize = positions.Length;
                for (int i = 0; i < positions.Length; i++) spawns.GetArrayElementAtIndex(i).vector3Value = positions[i];
                data.ApplyModifiedPropertiesWithoutUndo();
                WriteObjectArray(definition, "_npcs", Array.Empty<OnlineDungeonNpcSpawn>());
                WriteObjectArray(definition, "_encounters", Array.Empty<OnlineDungeonEncounterDefinition>());
                WriteObjectArray(definition, "_lootTables", Array.Empty<DungeonLootTable>());
                WriteObjectArray(definition, "_fixedSolidSpawns", Array.Empty<OnlineDungeonFixedSolidSpawn>());
                EditorUtility.SetDirty(definition); AssetDatabase.SaveAssetIfDirty(definition);
                MergeHomeCatalog(definition);
            }
            finally { ReleasePreview(root); AssertHomeProtectedFiles(protectedFiles); }
            VerifyHome();
            GameLog.Info("已窄导出原Home和平世界、研究/返回门几何与独立CPU材料配置；未重导P7/P8。", "OnlineDungeon");
        }

        private static void MergeHomeCatalog(OnlineDungeonDefinition home)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<OnlineDungeonCatalog>(CatalogPath);
            // 当前任务是在已有P7/P8之上合并Home；目录丢失不能悄悄换成只含Home的新目录。
            if (catalog == null || !catalog.TryGet("p7", out var p7) || !catalog.TryGet("p8", out var p8))
                throw new InvalidOperationException("Home窄导出需要保留原有效P7/P8目录，不能覆盖或重建它们。");
            var candidate = Object.Instantiate(catalog); candidate.name = catalog.name;
            try
            {
                var data = new SerializedObject(candidate); var entries = data.FindProperty("_dungeons");
                var preserved = new List<OnlineDungeonDefinition>(entries.arraySize + 1); bool installed = false;
                for (int i = 0; i < entries.arraySize; i++)
                {
                    var entry = entries.GetArrayElementAtIndex(i).objectReferenceValue as OnlineDungeonDefinition;
                    if (entry == null) throw new InvalidOperationException("现有副本目录有空项，Home窄导出拒绝隐藏原错误。");
                    if (entry.DungeonId == "home")
                    {
                        if (installed) throw new InvalidOperationException("现有副本目录有重复Home，拒绝猜测应覆盖哪一项。");
                        preserved.Add(home); installed = true;
                    }
                    else preserved.Add(entry);
                }
                if (!installed) preserved.Add(home);
                entries.arraySize = preserved.Count;
                for (int i = 0; i < preserved.Count; i++) entries.GetArrayElementAtIndex(i).objectReferenceValue = preserved[i];
                data.ApplyModifiedPropertiesWithoutUndo(); string error = null;
                if (!candidate.TryGet("p7", out var afterP7) || !candidate.TryGet("p8", out var afterP8) || afterP7 != p7 || afterP8 != p8
                    || !candidate.TryValidate(out error))
                    throw new InvalidOperationException("Home合并后目录未保留原P7/P8或未完整验证：" + error);
                // 候选完整有效才交接目录，失败不能把半个Home挂进原P7/P8目录的内存或磁盘状态。
                EditorUtility.CopySerialized(candidate, catalog); EditorUtility.SetDirty(catalog); AssetDatabase.SaveAssetIfDirty(catalog);
            }
            finally { Object.DestroyImmediate(candidate); }
        }

        [MenuItem("Tools/Norn/Online/Verify Peaceful Home World")]
        public static void VerifyHome()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<OnlineDungeonCatalog>(CatalogPath);
            if (catalog == null || !catalog.TryGet("p7", out _) || !catalog.TryGet("p8", out _)
                || !catalog.TryGet("home", out var definition)) throw new InvalidOperationException("Home目录必须与原P7/P8并存且分别有效。");
            if (!definition.IsPeacefulHome || definition.SourceScenePath != HomeSourcePath
                || definition.SourceSceneHash != Hash(HomeSourcePath) || definition.SourceHash != Hash(HomeSourcePath)
                || definition.NpcCount != 0 || definition.EncounterCount != 0 || definition.ShowEncounterHud
                || definition.LootTableCount != 0 || definition.FixedSolidCount != 0 || definition.CoinPrefab != null)
                throw new InvalidOperationException("Home源字节或零遭遇/无掉落合同已变化，请重新窄导出。");
            ValidateWorld(definition.WorldPrefab, 0);
            var bindings = definition.WorldPrefab.GetComponent<OnlineHomeWorldBindings>(); string error = null;
            if (bindings == null || !bindings.TryValidate(out error)) throw new InvalidOperationException("Home原几何接线不完整：" + error);
            var source = EditorSceneManager.OpenPreviewScene(HomeSourcePath);
            try
            {
                FindHomeSourceGeometry(source, out var area, out var research, out var portal, out var trigger);
                VerifyHomeBox(research, bindings.ResearchVolume, definition.WorldPrefab.transform);
                VerifyHomeBox(trigger, bindings.ReturnPortalCollider as BoxCollider, definition.WorldPrefab.transform);
                var data = new SerializedObject(bindings);
                if (data.FindProperty("_researchEnabled").boolValue != area.enabled
                    || data.FindProperty("_returnPortalEnabled").boolValue != portal.enabled)
                    throw new InvalidOperationException("Home作者区域/返回门enabled条件漂移。");
                var players = Find<PlayerControllerBase>(source);
                if (players.Count != 1 || Mathf.Abs(definition.PlayerSpawn(0).x - players[0].transform.position.x) > .001f
                    || Mathf.Abs(definition.PlayerSpawn(0).z - players[0].transform.position.z) > .001f)
                    throw new InvalidOperationException("Home首席出生XZ没有沿用原唯一玩家。");
            }
            finally { EditorSceneManager.ClosePreviewScene(source); }
            if (definition.WorldPrefab.GetComponentsInChildren<Canvas>(true).Length != 0
                || definition.WorldPrefab.GetComponentsInChildren<HomePortal>(true).Length != 0
                || definition.WorldPrefab.GetComponentsInChildren<SinglePlayerInventorySession>(true).Length != 0)
                throw new InvalidOperationException("Home世界仍包含旧Canvas/Portal/资产Owner。");
        }

        private static void VerifyHomeBox(BoxCollider source, BoxCollider exported, Transform exportedRoot)
        {
            if (source == null || exported == null || !source.center.Equals(exported.center) || !source.size.Equals(exported.size)
                || source.isTrigger != exported.isTrigger || source.enabled != exported.enabled
                || Vector3.Distance(source.transform.position, exported.transform.position) > .0001f
                || Quaternion.Angle(source.transform.rotation, exported.transform.rotation) > .01f
                || Vector3.Distance(source.transform.lossyScale, exported.transform.lossyScale) > .0001f
                || HomeAuthoredActive(source.transform, null) != HomeAuthoredActive(exported.transform, exportedRoot))
                throw new InvalidOperationException("Home导出Collider不再等同于原作者几何/激活条件。");
        }
        private static bool HomeAuthoredActive(Transform node, Transform ignoredRoot)
        {
            for (var value = node; value != null && value != ignoredRoot; value = value.parent)
                if (!value.gameObject.activeSelf) return false;
            return true;
        }
        private static Dictionary<string, string> SnapshotHomeProtectedFiles()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string path in new[] { HomeSourcePath, SourcePath("p7"), SourcePath("p8"), WorldPath("p7"), WorldPath("p8"),
                DefinitionPath("p7"), DefinitionPath("p8"), "ProjectSettings/EditorBuildSettings.asset" })
            {
                result.Add(path, HomeFileFingerprint(path));
                if (File.Exists(path + ".meta")) result.Add(path + ".meta", HomeFileFingerprint(path + ".meta"));
            }
            return result;
        }
        private static string HomeFileFingerprint(string path)
        { using var input = File.OpenRead(path); using var sha = SHA256.Create(); return Convert.ToBase64String(sha.ComputeHash(input)); }
        private static void AssertHomeProtectedFiles(Dictionary<string, string> before)
        {
            foreach (var pair in before) if (HomeFileFingerprint(pair.Key) != pair.Value)
                throw new InvalidOperationException("Home窄导出意外改动了受保护的原Scene/BuildList/P7/P8资源：" + pair.Key);
        }
    }
}
