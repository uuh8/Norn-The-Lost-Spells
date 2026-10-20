using System;
using System.IO;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using Game.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    /// <summary>用户制作入口。Editor 修改使用 Undo，运行时不依赖 Editor 或 Rendering 写接口。</summary>
    [InitializeOnLoad]
    internal static class LiquidWorldAuthoringTools
    {
        private const string Folder = "Assets/_Project/ScriptableObjects/ElementField/Liquids";
        private const string ShaderPath = "Assets/_Project/Art/Elemental/Shaders/ElementLiquidDormant.shader";
        private const string Production = "Assets/_Project/Scenes/P7_DemoRun.unity";
        private const string Request = "Temp/ElementPerformance/liquid-authoring.request";
        private const string Result = "Temp/ElementPerformance/liquid-authoring-result.txt";
        private static double _nextPoll;
        private const string RefreshedKey = "LiquidAuthoring.Refreshed";
        private const string ReadyAtKey = "LiquidAuthoring.ReadyAt";
        static LiquidWorldAuthoringTools() => EditorApplication.update += Poll;

        internal static Material GetSurfaceMaterial(MaterialId material)
        {
            string path = "Assets/_Project/Art/Materials/ElementField/M_GridLiquid_" + material + ".mat";
            Material asset = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (asset != null) return asset; // 后续尊重用户在新材质上的调参。
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            string label = material == MaterialId.Water ? "Water"
                : material == MaterialId.Poison ? "Poison" : "Sticky";
            Material template = AssetDatabase.LoadAssetAtPath<Material>(
                "Assets/_Project/Art/Elemental/Materials/M_Element" + label + "_PBF.mat");
            if (shader == null || template == null)
                throw new InvalidOperationException("缺少已有液体表面 Shader 或材质：" + label);
            // 普通 Mesh 的 Vertex 输入与 GPU Triangle Buffer 不兼容，因此使用共用同一
            // Fragment 的 Dormant Pass；只复制原波纹/PBR/透明参数，不恢复 GPU PBF 模拟。
            asset = new Material(template) { name = "M_GridLiquid_" + material, shader = shader };
            asset.renderQueue = -1;
            asset.enableInstancing = true;
            asset.SetFloat("_DebugForceOpaqueFragment", 0f);
            // PBF 的厚液团参数会让几厘米薄膜整面起泡，放大噪点/方格观感。
            // 共用原 Fragment，仅把 Grid 默认材质调为较宽波纹与较窄接触泡沫。
            asset.SetFloat("_WaveScaleA",3.5f);asset.SetFloat("_WaveScaleB",5.3f);
            asset.SetFloat("_FoamDistance",material==MaterialId.Poison?.04f:.035f);
            asset.SetFloat("_FoamStrength",material==MaterialId.Water?.25f:material==MaterialId.Poison?.12f:.08f);
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        [MenuItem("Tools/Element World/Authoring/Configure Current Scene")]
        private static void ConfigureCurrent()
        {
            GuardEditMode();
            Configure(SceneManager.GetActiveScene());
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            GameLog.Info("已配置三液体 Grid World。请保存场景；地形 Collider 的 Layer 必须包含在 Solid Layer Mask。", "Editor");
        }

        private static void Configure(Scene scene)
        {
            ElementWorldRuntime world = FindWorld(scene);
            if (world == null)
            {
                var go = new GameObject("Element World");
                SceneManager.MoveGameObjectToScene(go, scene);
                Undo.RegisterCreatedObjectUndo(go, "Create Liquid World");
                var baker = go.AddComponent<ElementFieldSolidBaker>();
                Set(baker, "_solidLayerMask", 1);
                world = go.AddComponent<ElementWorldRuntime>();
                var exposure = go.AddComponent<ElementWorldExposureSystem>();
                int targets = LayerMask.GetMask("Player", "Enemy");
                Set(exposure, "_targetLayers", targets != 0 ? targets : ~0);
            }
            var worldData = new SerializedObject(world);
            var oldProfile = worldData.FindProperty("_profile").objectReferenceValue as ElementWorldProfile;
            if (oldProfile == null) oldProfile = AssetDatabase.LoadAssetAtPath<ElementWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/ElementWorldProfile_GridPrototype.asset");
            if (oldProfile == null) throw new InvalidOperationException("缺少元素世界 Profile 模板。");
            LiquidWorldRuntime liquids = world.GetComponent<LiquidWorldRuntime>();
            if (liquids == null) liquids = Undo.AddComponent<LiquidWorldRuntime>(world.gameObject);
            var liquidData = new SerializedObject(liquids);
            var liquidProfile = liquidData.FindProperty("_profile").objectReferenceValue as LiquidWorldProfile;
            if (oldProfile.WaterSimulationMode != WaterSimulationMode.GridLiquid || liquidProfile == null)
            {
                // 每个 Scene 独立参数，保留原 Catalog、反应/状态与火焰配置；不覆盖全局旧 PBF 数据。
                string suffix = "_" + scene.name + "_Grid.asset";
                var profile = Clone(oldProfile, "Assets/_Project/ScriptableObjects/ElementField/Liquids/ElementWorldProfile" + suffix);
                var profileData = new SerializedObject(profile);
                var routeSource = oldProfile.MaterialSimulationRouting;
                var route = Clone(routeSource, "Assets/_Project/ScriptableObjects/ElementField/Liquids/MaterialSimulationRouting" + suffix);
                var routes = new SerializedObject(route);
                var array = routes.FindProperty("_routes");
                for (int i = 0; i < array.arraySize; i++)
                {
                    var row = array.GetArrayElementAtIndex(i);
                    int id = row.FindPropertyRelative("_material").intValue;
                    if (id == (int)MaterialId.Water || id == (int)MaterialId.Poison || id == (int)MaterialId.Sticky)
                        row.FindPropertyRelative("_backend").intValue = (int)MaterialSimulationBackendKind.GridLiquid;
                }
                routes.ApplyModifiedPropertiesWithoutUndo();
                profileData.FindProperty("_materialSimulationRouting").objectReferenceValue = route;
                profileData.FindProperty("_waterSimulationMode").intValue = (int)WaterSimulationMode.GridLiquid;
                profileData.FindProperty("_enableFluidChunkStreaming").boolValue = false;
                profileData.ApplyModifiedPropertiesWithoutUndo();
                var template = AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_DenseLake.asset");
                if (template == null) template = AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_Prototype.asset");
                liquidProfile = Clone(template, "Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile" + suffix);
                Set(liquids, "_profile", liquidProfile);
                Set(world, "_profile", profile);
            }
            if (AssetDatabase.GetAssetPath(liquidProfile).EndsWith("_Grid.asset", StringComparison.Ordinal))
            {
                // 克隆 World 之外也隔离材料行为，调正式关卡粘度不会反向改变对照原型。
                var settings = new SerializedObject(liquidProfile);
                var materials = settings.FindProperty("_materials");
                for (int i = 0; i < liquidProfile.MaterialCount; i++)
                {
                    LiquidBehaviorProfile behavior = liquidProfile.GetMaterial(i);
                    materials.GetArrayElementAtIndex(i).objectReferenceValue = Clone(behavior,
                        "Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidBehavior_" + behavior.Material + "_" + scene.name + "_Grid.asset");
                }
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
            Set(world, "_gridLiquidRuntime", liquids);
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                Disable<GpuPbfFluidRuntime>(root); Disable<FluidChunkStreamingRuntime>(root);
                Disable<FluidGameplayOccupancyBridge>(root); Disable<GpuLiquidSurfaceRenderer>(root);
                Disable<DormantLiquidCellRenderer>(root); Disable<ElementWorldWaterRenderer>(root);
            }
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            GridLiquidPrototypeSetup.Surface(world.gameObject, world, shader, MaterialId.Water, "Water");
            GridLiquidPrototypeSetup.Surface(world.gameObject, world, shader, MaterialId.Poison, "Poison");
            GridLiquidPrototypeSetup.Surface(world.gameObject, world, shader, MaterialId.Sticky, "Sticky");
        }

        [MenuItem("Tools/Element World/Authoring/Create Water Lake")]
        private static void Water() => CreateLake(MaterialId.Water);
        [MenuItem("Tools/Element World/Authoring/Create Poison Lake")]
        private static void Poison() => CreateLake(MaterialId.Poison);
        [MenuItem("Tools/Element World/Authoring/Create Sticky Lake")]
        private static void Sticky() => CreateLake(MaterialId.Sticky);

        private static void CreateLake(MaterialId material)
        {
            GuardEditMode();
            var world = FindWorld(SceneManager.GetActiveScene());
            var liquids = world != null ? world.GetComponent<LiquidWorldRuntime>() : null;
            if (liquids == null) throw new InvalidOperationException("先运行 Configure Current Scene。");
            var go = new GameObject(material + " Lake Bounds");
            Undo.RegisterCreatedObjectUndo(go, "Create Lake Bounds");
            go.layer = 2; // Ignore Raycast；Trigger 又被 Solid Baker 的 Ignore 查询排除。
            var bounds = go.AddComponent<BoxCollider>();
            bounds.isTrigger = true; bounds.size = new Vector3(8, 2, 8); bounds.center = Vector3.up;
            var authoring = go.AddComponent<StableLiquidRegionAuthoring>();
            Set(authoring, "_liquids", liquids); Set(authoring, "_boundsCollider", bounds);
            Set(authoring, "_material", (int)material); Set(authoring, "_deriveBottomFromSolid", true);
            Set(authoring, "_sectorizeByChunk", true);
            Selection.activeGameObject = go;
        }

        [MenuItem("Tools/Element World/Authoring/Create Outlet For Selected Lake")]
        private static void Outlet()
        {
            GuardEditMode();
            var source = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponent<StableLiquidRegionAuthoring>() : null;
            if (source == null) throw new InvalidOperationException("请先选中 Lake Bounds。");
            var go = new GameObject("Lake Outlet - Toggle In Play Mode");
            Undo.RegisterCreatedObjectUndo(go, "Create Lake Outlet");
            Bounds bounds = source.AuthoringBounds;
            go.transform.position = new Vector3(bounds.max.x, bounds.center.y, bounds.center.z);
            var target = new GameObject("Receiving Cell - Move Outside Lake");
            target.transform.SetParent(go.transform); target.transform.localPosition = Vector3.right * .5f;
            var outlet = go.AddComponent<StableLiquidOutletAuthoring>();
            Set(outlet, "_source", source); Set(outlet, "_outletCenter", go.transform);
            Set(outlet, "_receivingCell", target.transform); Selection.activeGameObject = go;
        }

        // 显式自动化命令与菜单共用同一主线程入口；不随 Assembly Reload 自动迁移场景。
        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + .5;
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating
                || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (!SessionState.GetBool(RefreshedKey, false))
            {
                // 外部请求紧跟源码保存时，先完成导入/Reload 再迁移，避免运行上一版配置逻辑。
                SessionState.SetBool(RefreshedKey, true);
                SessionState.SetFloat(ReadyAtKey, (float)EditorApplication.timeSinceStartup + 5f);
                AssetDatabase.Refresh();
                return;
            }
            if (EditorApplication.timeSinceStartup < SessionState.GetFloat(ReadyAtKey, 0)) return;
            SessionState.SetBool(RefreshedKey, false);
            string command = File.ReadAllText(Request).Trim(); File.Delete(Request);
            try
            {
                if (command == "refresh-surfaces") RefreshSurfaces();
                else if (command == "migrate-p7") MigrateProduction();
                else if (command == "audit-p7") GridLiquidP7CutoverAudit.Run();
                else throw new InvalidOperationException("未知 Authoring 命令：" + command);
                AssetDatabase.SaveAssets(); File.WriteAllText(Result, DateTime.UtcNow.ToString("O") + " OK " + command);
            }
            catch (Exception ex) { File.WriteAllText(Result, ex.ToString()); GameLog.Error(ex.ToString(), "Editor"); }
        }

        private static void RefreshSurfaces()
        {
            string[] ids = AssetDatabase.FindAssets("t:Scene", new[] { "Assets/_Project/Scenes/Prototypes" });
            Scene previous = SceneManager.GetActiveScene();
            foreach (string id in ids)
            {
                string path = AssetDatabase.GUIDToAssetPath(id);
                Scene scene = SceneManager.GetSceneByPath(path);
                bool opened = !scene.IsValid() || !scene.isLoaded;
                if (opened) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                try
                {
                    if (scene.isDirty) throw new InvalidOperationException("请先保存场景：" + path);
                    ElementWorldRuntime world = FindWorld(scene);
                    if (world == null || world.GetComponent<LiquidWorldRuntime>() == null) continue;
                    SceneManager.SetActiveScene(scene);
                    Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
                    GridLiquidPrototypeSetup.Surface(world.gameObject, world, shader, MaterialId.Water, "Water");
                    GridLiquidPrototypeSetup.Surface(world.gameObject, world, shader, MaterialId.Poison, "Poison");
                    GridLiquidPrototypeSetup.Surface(world.gameObject, world, shader, MaterialId.Sticky, "Sticky");
                    EditorSceneManager.SaveScene(scene);
                }
                finally
                {
                    if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                    if (opened) EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        [MenuItem("Tools/Element World/Authoring/Migrate P7 To Grid Liquid")]
        private static void MigrateProduction()
        {
            GuardEditMode();
            Scene previous = SceneManager.GetActiveScene(), scene = SceneManager.GetSceneByPath(Production);
            bool opened = !scene.IsValid() || !scene.isLoaded;
            if (opened) scene = EditorSceneManager.OpenScene(Production, OpenSceneMode.Additive);
            try
            {
                if (scene.isDirty) throw new InvalidOperationException("P7 有未保存修改，不能迁移磁盘资产。");
                var world = FindWorld(scene);
                var profile = world != null ? new SerializedObject(world).FindProperty("_profile").objectReferenceValue as ElementWorldProfile : null;
                if (profile == null) throw new InvalidOperationException("P7 缺少 Profile。");
                if (profile.WaterSimulationMode != WaterSimulationMode.GridLiquid)
                {
                    // 备份在项目之外，不增加 Build 场景，不改原 Scene 的 .meta/GUID。
                    Directory.CreateDirectory("Backups/GridLiquidCutover");
                    string backup = "Backups/GridLiquidCutover/P7_DemoRun_" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".unity";
                    File.Copy(Production, backup, false);
                    File.WriteAllText("Backups/GridLiquidCutover/latest.txt", backup);
                }
                SceneManager.SetActiveScene(scene); Configure(scene);
                EditorSceneManager.SaveScene(scene);
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                if (opened) EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void GuardEditMode()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("请在 Edit Mode 且编译完成后操作。");
        }

        [MenuItem("Tools/Element World/Authoring/Restore P7 Before Grid Cutover")]
        private static void RestoreProduction()
        {
            GuardEditMode();
            string index = "Backups/GridLiquidCutover/latest.txt";
            if (!File.Exists(index)) throw new InvalidOperationException("没有本次切换的备份。");
            string backup = Path.GetFullPath(File.ReadAllText(index).Trim());
            string allowed = Path.GetFullPath("Backups/GridLiquidCutover") + Path.DirectorySeparatorChar;
            if (!backup.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !File.Exists(backup))
                throw new InvalidOperationException("回退文件不在预期备份目录。");
            Scene scene = SceneManager.GetSceneByPath(Production);
            if (scene.IsValid() && scene.isDirty) throw new InvalidOperationException("P7 有未保存修改，不能回退。");
            // 回退会替换 Scene 内容，菜单明确显示备份时间；先备份当前已保存版本。
            if (!EditorUtility.DisplayDialog("Restore P7", "恢复切换前 P7：" + Path.GetFileName(backup)
                + "。当前已保存场景也会备份。", "Restore", "Cancel")) return;
            File.Copy(Production, "Backups/GridLiquidCutover/P7_before_restore_" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".unity", false);
            bool loaded = scene.IsValid() && scene.isLoaded;
            if (loaded) EditorSceneManager.CloseScene(scene, true);
            File.Copy(backup, Production, true); // 原 .meta 保留，Build Settings 与 GUID 不变。
            AssetDatabase.ImportAsset(Production, ImportAssetOptions.ForceUpdate);
            if (loaded) EditorSceneManager.OpenScene(Production, OpenSceneMode.Additive);
        }
        private static ElementWorldRuntime FindWorld(Scene scene)
        {
            ElementWorldRuntime found = null;
            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (var candidate in root.GetComponentsInChildren<ElementWorldRuntime>(true))
            { if (found != null) throw new InvalidOperationException("场景应只有一个元素世界。"); found = candidate; }
            return found;
        }
        private static T Clone<T>(T source, string path) where T : UnityEngine.Object
        {
            if (source == null) throw new InvalidOperationException("缺少 Profile 模板：" + path);
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null) return asset;
            asset = UnityEngine.Object.Instantiate(source); asset.name = Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(asset, path); return asset;
        }
        private static void Disable<T>(GameObject root) where T : Behaviour
        { foreach (T old in root.GetComponentsInChildren<T>(true)) { Undo.RecordObject(old, "Disable Legacy Fluid"); old.enabled = false; } }
        internal static void Set(UnityEngine.Object target, string field, UnityEngine.Object value)
        { var data = new SerializedObject(target); data.FindProperty(field).objectReferenceValue = value; data.ApplyModifiedProperties(); }
        internal static void Set(UnityEngine.Object target, string field, int value)
        { var data = new SerializedObject(target); data.FindProperty(field).intValue = value; data.ApplyModifiedProperties(); }
        internal static void Set(UnityEngine.Object target, string field, bool value)
        { var data = new SerializedObject(target); data.FindProperty(field).boolValue = value; data.ApplyModifiedProperties(); }
    }
}
