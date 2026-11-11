using System;
using System.Collections.Generic;
using System.IO;
using Game.Character;
using Game.Core;
using Game.Run;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Project.Editor
{
    /// <summary>
    /// 原成功出口的离线接线与窄刷新。P7固定门来自Scene；P8只有原动态门Prefab，不能为通过检查
    /// 编造一扇固定门。所有子树在inactive世界内完成剥离，旧单机Session/Stage Owner从不进入在线World。
    /// </summary>
    public static partial class OnlineDungeonTools
    {
        private static void AttachOriginalReturnPortals(Preview preview)
        {
            if (preview.Id == "home") return;
            if (preview.Id == "p7")
            {
                var portals = Find<StageExitPortal>(preview.Source);
                if (portals.Count != 1) throw new InvalidOperationException("原P7必须有唯一固定成功返回门，不能猜测出口。");
                var original = portals[0]; var source = new SerializedObject(original);
                GameObject root;
                if (preview.Clones.TryGetValue(original.transform.GetInstanceID(), out var mapped)) root = mapped.gameObject;
                else
                {
                    root = Object.Instantiate(original.gameObject, preview.Root.transform, false); root.name = original.name;
                    root.transform.SetPositionAndRotation(original.transform.position, original.transform.rotation);
                    root.transform.localScale = original.transform.lossyScale;
                    root.SetActive(HomeAuthoredActive(original.transform, null));
                }
                WriteReturnBinding(root, false, source.FindProperty("_encounterIndex").intValue,
                    source.FindProperty("_playerLayers").intValue);
            }
            else if (preview.Id == "p8")
            {
                if (Find<StageExitPortal>(preview.Source).Count != 0)
                    throw new InvalidOperationException("原P8已新增固定门，不能继续套用旧动态门来源。");
                var prefab = ReadP8ReturnPrefab(preview.Source);
                var root = Object.Instantiate(prefab, preview.Root.transform, false); root.name = "P8ReturnPortalTemplate";
                root.SetActive(false);
                // 原动态门按实际本机Actor父链准入，没有作者LayerMask；认证Owner仍由DS在交互处再次核验。
                WriteReturnBinding(root, true, 0, ~0);
            }
        }

        private static GameObject ReadP8ReturnPrefab(UnityEngine.SceneManagement.Scene source)
        {
            var bootstraps = Find<PracticeInventorySceneBootstrap>(source);
            if (bootstraps.Count != 1) throw new InvalidOperationException("原P8需要唯一练习入口的返回门Prefab引用。");
            var prefab = new SerializedObject(bootstraps[0]).FindProperty("_returnPortalPrefab").objectReferenceValue as GameObject;
            if (prefab == null || !EditorUtility.IsPersistent(prefab) || prefab.GetComponent<HubPracticePortal>() == null
                || prefab.GetComponent<SphereCollider>() == null)
                throw new InvalidOperationException("原P8动态返回门必须是已有HubPracticePortal/Sphere资产，不能造替代几何。");
            return prefab;
        }

        private static void WriteReturnBinding(GameObject root, bool dynamicPlacement, int encounter, int layers)
        {
            var binding = root.GetComponent<OnlineDungeonReturnBindings>();
            if (binding == null) binding = root.AddComponent<OnlineDungeonReturnBindings>();
            var presentations = new List<Behaviour>();
            foreach (var value in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (value is PortalVisualPlacement || value is PortalVfxController) presentations.Add(value);
            var data = new SerializedObject(binding);
            data.FindProperty("_portalCollider").objectReferenceValue = root.GetComponent<Collider>();
            data.FindProperty("_portalTransform").objectReferenceValue = root.transform;
            data.FindProperty("_encounterIndex").intValue = encounter; data.FindProperty("_playerLayers").intValue = layers;
            data.FindProperty("_dynamicPlacement").boolValue = dynamicPlacement;
            data.FindProperty("_inactiveTemplateRoot").objectReferenceValue = dynamicPlacement ? root : null;
            var refs = data.FindProperty("_presentations"); var enabled = data.FindProperty("_authoredPresentationEnabled");
            refs.arraySize = enabled.arraySize = presentations.Count;
            for (int i = 0; i < presentations.Count; i++)
            {
                refs.GetArrayElementAtIndex(i).objectReferenceValue = presentations[i];
                enabled.GetArrayElementAtIndex(i).boolValue = presentations[i].enabled;
            }
            data.ApplyModifiedPropertiesWithoutUndo();
            if (!binding.TryValidate(out var error)) throw new InvalidOperationException(error);
            // 固定门不再有旧Stage Owner替它控制Open事件；generic表现必须等可信clear后由组合根开放。
            binding.SetPresentationAvailable(false);
            RemoveUnauthorizedBehaviours(root, false);
        }

        private static bool IsControlledReturnPresentation(MonoBehaviour value)
            => (value is PortalVisualPlacement || value is PortalVfxController)
                && value.GetComponentInParent<OnlineDungeonReturnBindings>(true) != null;

        /// <summary>将原返回Prefab及其嵌套表现Prefab纳入来源摘要；不因P8零掉落就漏掉真实门几何变化。</summary>
        private static SortedSet<string> ReturnHashInputs(string scenePath)
        {
            var paths = new SortedSet<string>(StringComparer.Ordinal);
            if (scenePath == HomeSourcePath) return paths;
            var scene = EditorSceneManager.OpenPreviewScene(scenePath);
            try
            {
                if (scenePath == SourcePath("p8")) AddReturnPrefabHashInputs(ReadP8ReturnPrefab(scene), paths);
                else if (scenePath == SourcePath("p7"))
                {
                    var portals = Find<StageExitPortal>(scene);
                    if (portals.Count != 1) throw new InvalidOperationException("P7返回门来源不唯一。");
                    foreach (var presentation in portals[0].GetComponentsInChildren<PortalVfxController>(true))
                    {
                        var source = PrefabUtility.GetCorrespondingObjectFromSource(presentation);
                        if (source != null) AddReturnPrefabHashInputs(source.gameObject, paths);
                    }
                    // 旧门Awake也可能按原资源入口补表现。该引用纳入摘要，但不在导出中新增另一套视觉。
                    var common = Resources.Load<GameObject>("SinglePlayer/UnifiedPortalVisual");
                    if (common != null) AddReturnPrefabHashInputs(common, paths);
                }
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
            return paths;
        }
        private static void AddReturnPrefabHashInputs(GameObject prefab, SortedSet<string> paths)
        {
            AddLootSource(prefab, paths);
            foreach (var path in AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(prefab), true))
            {
                if (!path.EndsWith(".prefab", StringComparison.Ordinal) || !path.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                AddLootSource(AssetDatabase.LoadAssetAtPath<GameObject>(path), paths);
            }
        }

        /// <summary>
        /// 在既有Prefab contents上只替换返回子树；不重新克隆原地图、NPC、NavMesh或CPU材料配置。
        /// 候选由ReleasePreview释放。测试可检查此内存结果，而不保存任何正式文件。
        /// </summary>
        public static GameObject BuildReturnRefreshPreview(string id)
        {
            if (id != "p7" && id != "p8") throw new ArgumentException("返回门窄刷新仅接已有P7/P8。", nameof(id));
            var definition = AssetDatabase.LoadAssetAtPath<OnlineDungeonDefinition>(DefinitionPath(id));
            string path = SourcePath(id);
            if (definition == null || definition.WorldPrefab == null || definition.DungeonId != id || definition.SourceScenePath != path
                || definition.SourceSceneHash != Hash(path))
                throw new InvalidOperationException("原Scene基线已变化或正式World缺失；返回门窄刷新不能掩盖几何/NPC更新：" + id);
            var preview = new Preview { Id = id, Path = path };
            try
            {
                preview.Source = EditorSceneManager.OpenPreviewScene(path);
                preview.Root = PrefabUtility.LoadPrefabContents(WorldPath(id)); preview.LoadedPrefabContents = true;
                preview.World = preview.Root.scene;
                if (preview.Root.activeSelf) throw new InvalidOperationException("已有正式World必须保持inactive。");
                foreach (var binding in preview.Root.GetComponentsInChildren<OnlineDungeonReturnBindings>(true))
                {
                    if (binding.gameObject == preview.Root) throw new InvalidOperationException("返回门不能覆盖整个世界根。");
                    Object.DestroyImmediate(binding.gameObject);
                }
                AttachOriginalReturnPortals(preview); ClearOutsideReferences(preview.Root);
                ValidateWorld(preview.Root, definition.EncounterCount); ValidateReturnBinding(preview.Root, id);
                var loot = ReadSourceLoot(preview.Source, id);
                preview.Hash = ComputeSourceHash(path, loot.Tables, ReadFixedSolidSource(preview.Source, id), loot.CoinPrefab);
                Previews.Add(preview.Root, preview); return preview.Root;
            }
            catch { Close(preview); throw; }
        }

        [MenuItem("Tools/Norn/Online/Refresh Original Return Portals Only")]
        public static void RefreshReturnBindings()
        {
            OnlineSceneSetup.RequireCleanEditor();
            var protectedFiles = SnapshotReturnProtectedFiles(); var pending = new List<Preview>();
            try
            {
                // 所有候选先完整验证，再保存两个owned资源；第二张来源错误时不先覆盖第一张正式World。
                foreach (string id in Ids)
                { var root = BuildReturnRefreshPreview(id); pending.Add(Previews[root]); }
                foreach (var preview in pending)
                {
                    var saved = PrefabUtility.SaveAsPrefabAsset(preview.Root, WorldPath(preview.Id));
                    if (saved == null) throw new InvalidOperationException("返回门窄刷新保存World失败：" + preview.Id);
                    var definition = AssetDatabase.LoadAssetAtPath<OnlineDungeonDefinition>(DefinitionPath(preview.Id));
                    var data = new SerializedObject(definition); data.FindProperty("_sourceHash").stringValue = preview.Hash;
                    data.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(definition); AssetDatabase.SaveAssetIfDirty(definition);
                }
            }
            finally
            {
                foreach (var preview in pending) if (preview.Root != null) ReleasePreview(preview.Root);
                foreach (var pair in protectedFiles) if (HomeFileFingerprint(pair.Key) != pair.Value)
                    throw new InvalidOperationException("返回门窄刷新改动了受保护的原Scene/NPC/材料/Catalog/BuildList：" + pair.Key);
            }
            VerifyReturnBindings();
            GameLog.Info("已窄接原P7固定成功门与原P8动态模板；保留原Scene、NPC、NavMesh与材料资源。", "OnlineDungeon");
        }
        [MenuItem("Tools/Norn/Online/Verify Original Return Portals")]
        public static void VerifyReturnBindings()
        {
            foreach (string id in Ids)
            {
                var definition = AssetDatabase.LoadAssetAtPath<OnlineDungeonDefinition>(DefinitionPath(id));
                if (definition == null || definition.WorldPrefab == null || definition.SourceSceneHash != Hash(SourcePath(id))
                    || definition.SourceHash != ComputeSourceHash(id)) throw new InvalidOperationException("返回门来源摘要已漂移：" + id);
                ValidateReturnBinding(definition.WorldPrefab, id);
            }
        }
        private static void ValidateReturnBinding(GameObject world, string id)
        {
            var values = world.GetComponentsInChildren<OnlineDungeonReturnBindings>(true); string error = null;
            if (values.Length != 1 || !values[0].TryValidate(out error) || values[0].DynamicPlacement != (id == "p8")
                || values[0].DynamicPlacement && values[0].InactiveTemplateRoot.activeSelf)
                throw new InvalidOperationException("原成功返回门接线无效：" + id + " / " + error);
        }
        private static Dictionary<string, string> SnapshotReturnProtectedFiles()
        {
            var paths = new List<string> { SourcePath("p7"), SourcePath("p8"), HomeSourcePath, CatalogPath, "ProjectSettings/EditorBuildSettings.asset" };
            foreach (var directory in new[] { WorldFolder + "/Npcs", DataFolder + "/Materials" })
                if (Directory.Exists(directory)) paths.AddRange(Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly));
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                if (!File.Exists(path)) continue;
                result[path] = HomeFileFingerprint(path);
                if (File.Exists(path + ".meta")) result[path + ".meta"] = HomeFileFingerprint(path + ".meta");
            }
            return result;
        }
    }
}
