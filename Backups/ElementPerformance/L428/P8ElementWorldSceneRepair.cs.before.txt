#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Game.Core;
using Game.ElementField;
using Game.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>
    /// 把早于 GPU PBF/多液体阶段制作的 P8 场景迁移到当前生产版 ElementWorld Contract。
    /// 规则资产跨场景共享；带有粒子 Buffer、Readback 与可见 Mesh 的可变 Runtime 仍由每个场景独立持有，
    /// 因而进入 Boss 关会得到干净的新世界，但不会退回旧版物质规则。
    /// </summary>
    internal static class P8ElementWorldSceneRepair
    {
        private const string P7ScenePath = "Assets/_Project/Scenes/P7_DemoRun.unity";
        private const string P8ScenePath = "Assets/_Project/Scenes/P8_BossField.unity";
        private const string CanonicalProfilePath =
            "Assets/_Project/ScriptableObjects/ElementField/Fluid/ElementWorldProfile_P7_PBF.asset";
        private const string P8ProfilePath =
            "Assets/_Project/ScriptableObjects/P8/ElementField/ElementWorldProfile_P8BossField.asset";
        private const string IntegrationRootName = "GPU_PBF_Fluid";
        private const string FireVisualRootName = "GpuFireParcelVisuals";
        private const string ReactionVisualRootName = "ReactionBurstVisuals";
        private const string TerrainCollisionRootName = "FluidTerrainCollision";
        private const int ProxyGridAxis = 11;
        private const float MinimumTileSize = 8f;
        private const float ArenaMargin = 28f;
        private const float ProxyThickness = 4f;
        private const float ProxyOverlap = 0.5f;
        private const string GateCompletedKey = "Game.P8.ElementWorld.SceneContractGate.V1";
        private const string FocusedTestName =
            "Game.Rendering.Tests.P8ElementWorldSceneContractTests."
            + "P8UsesCanonicalMaterialRulesAndOwnsACompleteGpuRuntimeChain";
        private static TestRunnerApi _testApi;
        private static GateCallbacks _gateCallbacks;

        [InitializeOnLoadMethod]
        private static void ScheduleApprovedMigration()
        {
            // Profile 的旧 Water Mode 是一次性迁移标记。成功保存后条件自然失效，
            // 不需要留下一个会在每次 Domain Reload 改场景的后台工具。
            EditorApplication.delayCall += TryRunApprovedMigration;
            // Scene Contract 由显式菜单或统一 Gate 运行；Domain Reload 自动启动 TestRunner
            // 会与外部全量回归争用场景和 callback，产生与产品代码无关的顺序依赖失败。
        }

        [MenuItem("Tools/Game/Element/Repair P8 ElementWorld Scene")]
        private static void RepairFromMenu()
        {
            Repair();
        }

        [MenuItem("Tools/Game/Element/Validate P8 ElementWorld Scene")]
        private static void ValidateFromMenu()
        {
            EditorPrefs.DeleteKey(GateCompletedKey);
            TryRunFocusedGateOnce();
        }

        private static void TryRunApprovedMigration()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating
                || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.delayCall += TryRunApprovedMigration;
                return;
            }

            ElementWorldProfile profile = AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(P8ProfilePath);
            if (profile == null || !NeedsMigration(profile))
                return;

            try
            {
                Repair();
            }
            catch (Exception exception)
            {
                GameLog.Error($"P8 ElementWorld 自动迁移失败：{exception}", "Editor");
            }
        }

        private static bool NeedsMigration(ElementWorldProfile profile)
        {
            var data = new SerializedObject(profile);
            return data.FindProperty("_waterSimulationMode").intValue != 1
                || data.FindProperty("_materialCatalog").objectReferenceValue == null
                || data.FindProperty("_materialSimulationRouting").objectReferenceValue == null
                || !SceneContainsProductionContract();
        }

        private static bool SceneContainsProductionContract()
        {
            string sceneYaml = File.ReadAllText(P8ScenePath);
            return sceneYaml.Contains($"m_Name: {IntegrationRootName}")
                && sceneYaml.Contains($"m_Name: {FireVisualRootName}")
                && sceneYaml.Contains($"m_Name: {TerrainCollisionRootName}");
        }

        private static void TryRunFocusedGateOnce()
        {
            if (EditorPrefs.GetBool(GateCompletedKey, false)
                || !SceneContainsProductionContract())
            {
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating
                || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.delayCall += TryRunFocusedGateOnce;
                return;
            }

            _testApi = ScriptableObject.CreateInstance<TestRunnerApi>();
            _gateCallbacks = new GateCallbacks();
            _testApi.RegisterCallbacks(_gateCallbacks);
            _testApi.Execute(new ExecutionSettings(new Filter
            {
                testMode = TestMode.EditMode,
                assemblyNames = new[] { "Game.Rendering.Tests" },
                testNames = new[] { FocusedTestName },
            }));
            GameLog.Info("P8 ElementWorld Scene Contract focused test started.", "Editor");
        }

        private static void Repair()
        {
            bool openedP8 = !TryGetLoadedScene(P8ScenePath, out Scene p8Scene);
            if (openedP8)
                p8Scene = EditorSceneManager.OpenScene(P8ScenePath, OpenSceneMode.Additive);

            bool openedP7 = !TryGetLoadedScene(P7ScenePath, out Scene p7Scene);
            if (openedP7)
                p7Scene = EditorSceneManager.OpenScene(P7ScenePath, OpenSceneMode.Additive);

            try
            {
                ElementWorldRuntime p8World = FindInScene<ElementWorldRuntime>(p8Scene)
                    ?? throw new InvalidOperationException("P8_BossField 缺少 ElementWorldRuntime。");
                ElementWorldRuntime p7World = FindInScene<ElementWorldRuntime>(p7Scene)
                    ?? throw new InvalidOperationException("P7_DemoRun 缺少当前生产版 ElementWorldRuntime 模板。");

                ElementWorldProfile canonical =
                    AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(CanonicalProfilePath)
                    ?? throw new InvalidOperationException("缺少当前生产版 ElementWorldProfile。");
                ElementWorldProfile p8Profile =
                    AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(P8ProfilePath)
                    ?? throw new InvalidOperationException("缺少 P8 ElementWorldProfile。");

                Transform interestPoint = GetObject<Transform>(p8World, "_interestPoint")
                    ?? FindGameObject(p8Scene, "WizardPlayer")?.transform
                    ?? throw new InvalidOperationException("P8 ElementWorld 缺少 WizardPlayer Interest Point。");

                CopyCanonicalRules(canonical, p8Profile);
                GameObject integration = CloneProductionChild(
                    p7World.transform,
                    p8World.transform,
                    p8Scene,
                    IntegrationRootName);
                GameObject fireVisuals = CloneProductionChild(
                    p7World.transform,
                    p8World.transform,
                    p8Scene,
                    FireVisualRootName);
                CloneProductionChild(
                    p7World.transform,
                    p8World.transform,
                    p8Scene,
                    ReactionVisualRootName);

                Transform terrainCollisionRoot = BakeTerrainCollisionGrid(
                    p8Scene,
                    p8World.transform,
                    interestPoint);
                RebindRuntimeChain(
                    p8World,
                    p8Profile,
                    interestPoint,
                    terrainCollisionRoot,
                    integration,
                    fireVisuals);
                RepairLegacyRendererReference(p8Scene, p8World);
                RepairStatusPresentation(p8Scene);
                AssertNoCrossSceneReferences(integration, p8Scene);
                AssertNoCrossSceneReferences(fireVisuals, p8Scene);

                EditorUtility.SetDirty(p8Profile);
                AssetDatabase.SaveAssets();
                EditorSceneManager.MarkSceneDirty(p8Scene);
                EditorSceneManager.SaveScene(p8Scene);
                GameLog.Info(
                    "P8 ElementWorld 已迁移：共享生产版规则，场景独立 GPU PBF Runtime，"
                    + "11×11 Terrain Proxy，四种状态各自持有 UI Presentation。",
                    "Editor");
                EditorPrefs.DeleteKey(GateCompletedKey);
                EditorApplication.delayCall += TryRunFocusedGateOnce;
            }
            finally
            {
                // 只关闭本工具临时打开的场景，不改变开发者当前正在编辑的 Scene Setup。
                if (openedP7 && p7Scene.IsValid() && p7Scene.isLoaded)
                    EditorSceneManager.CloseScene(p7Scene, true);
                if (openedP8 && p8Scene.IsValid() && p8Scene.isLoaded)
                    EditorSceneManager.CloseScene(p8Scene, true);
            }
        }

        private static void CopyCanonicalRules(
            ElementWorldProfile canonical,
            ElementWorldProfile p8Profile)
        {
            var source = new SerializedObject(canonical);
            var target = new SerializedObject(p8Profile);
            target.FindProperty("_waterSimulationMode").intValue =
                source.FindProperty("_waterSimulationMode").intValue;

            string[] sharedFields =
            {
                "_materialCatalog",
                "_materialSimulationRouting",
                "_materialStatusProjection",
                "_reactionProfile",
                "_materialReactionBindings",
            };
            for (int i = 0; i < sharedFields.Length; i++)
            {
                string field = sharedFields[i];
                target.FindProperty(field).objectReferenceValue =
                    source.FindProperty(field).objectReferenceValue;
            }

            target.ApplyModifiedPropertiesWithoutUndo();
        }

        private static GameObject CloneProductionChild(
            Transform sourceWorld,
            Transform targetWorld,
            Scene targetScene,
            string childName)
        {
            Transform source = FindDescendant(sourceWorld, childName)
                ?? FindGameObject(sourceWorld.gameObject.scene, childName)?.transform
                ?? throw new InvalidOperationException($"P7 生产版模板缺少 {childName}。");
            GameObject existing = FindGameObject(targetScene, childName);
            if (existing != null)
                Object.DestroyImmediate(existing);

            GameObject clone = Object.Instantiate(source.gameObject);
            clone.name = childName;
            SceneManager.MoveGameObjectToScene(clone, targetScene);
            clone.transform.SetParent(targetWorld, false);
            clone.transform.localPosition = source.localPosition;
            clone.transform.localRotation = source.localRotation;
            clone.transform.localScale = source.localScale;
            return clone;
        }

        private static Transform FindDescendant(Transform root, string objectName)
        {
            Transform[] descendants = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < descendants.Length; i++)
            {
                if (descendants[i].name == objectName)
                    return descendants[i];
            }
            return null;
        }

        private static Transform BakeTerrainCollisionGrid(
            Scene scene,
            Transform worldRoot,
            Transform interestPoint)
        {
            Transform existing = worldRoot.Find(TerrainCollisionRootName);
            if (existing != null)
                Object.DestroyImmediate(existing.gameObject);

            Terrain terrain = FindInScene<Terrain>(scene)
                ?? throw new InvalidOperationException(
                    "P8 使用 TerrainCollider，但场景中没有可采样的 Terrain 组件。");
            TerrainData terrainData = terrain.terrainData
                ?? throw new InvalidOperationException("P8 Terrain 缺少 TerrainData。");

            GameObject root = new GameObject(TerrainCollisionRootName);
            SceneManager.MoveGameObjectToScene(root, scene);
            root.transform.SetParent(worldRoot, false);

            Transform boss = FindGameObject(scene, "Boss")?.transform;
            Vector3 first = interestPoint.position;
            Vector3 second = boss != null ? boss.position : first;
            Vector3 center = (first + second) * 0.5f;
            float requiredSpan = Mathf.Max(
                Mathf.Abs(first.x - second.x) + ArenaMargin * 2f,
                Mathf.Abs(first.z - second.z) + ArenaMargin * 2f);
            float tileSize = Mathf.Max(MinimumTileSize, requiredSpan / ProxyGridAxis);
            tileSize = Mathf.Min(
                tileSize,
                Mathf.Min(terrainData.size.x, terrainData.size.z) / ProxyGridAxis);

            float coverageHalf = tileSize * ProxyGridAxis * 0.5f;
            Vector3 terrainPosition = terrain.transform.position;
            center.x = ClampInsideTerrain(
                center.x,
                terrainPosition.x,
                terrainPosition.x + terrainData.size.x,
                coverageHalf);
            center.z = ClampInsideTerrain(
                center.z,
                terrainPosition.z,
                terrainPosition.z + terrainData.size.z,
                coverageHalf);

            for (int z = 0; z < ProxyGridAxis; z++)
            {
                for (int x = 0; x < ProxyGridAxis; x++)
                {
                    float worldX = center.x + (x - (ProxyGridAxis - 1) * 0.5f) * tileSize;
                    float worldZ = center.z + (z - (ProxyGridAxis - 1) * 0.5f) * tileSize;
                    Vector3 sample = new Vector3(worldX, terrainPosition.y, worldZ);
                    float u = Mathf.Clamp01((worldX - terrainPosition.x) / terrainData.size.x);
                    float v = Mathf.Clamp01((worldZ - terrainPosition.z) / terrainData.size.z);
                    Vector3 normal = terrain.transform.TransformDirection(
                        terrainData.GetInterpolatedNormal(u, v)).normalized;
                    Vector3 surface = new Vector3(
                        worldX,
                        terrain.SampleHeight(sample) + terrainPosition.y,
                        worldZ);

                    GameObject tile = new GameObject($"TerrainProxy_{x:00}_{z:00}");
                    tile.transform.SetParent(root.transform, false);
                    tile.transform.SetPositionAndRotation(
                        surface - normal * (ProxyThickness * 0.5f),
                        Quaternion.FromToRotation(Vector3.up, normal));

                    BoxCollider box = tile.AddComponent<BoxCollider>();
                    box.size = new Vector3(
                        tileSize + ProxyOverlap,
                        ProxyThickness,
                        tileSize + ProxyOverlap);
                    // Collider 必须保持 Enabled/NonTrigger 才能进入 GPU Proxy Collector；
                    // Layer Override 排除所有 CPU Physics Layer，避免重复地面碰撞影响 CharacterController。
                    box.includeLayers = 0;
                    box.excludeLayers = ~0;

                    FluidColliderAuthoring authoring = tile.AddComponent<FluidColliderAuthoring>();
                    var authoringData = new SerializedObject(authoring);
                    authoringData.FindProperty("_collider").objectReferenceValue = box;
                    authoringData.FindProperty("_isDynamic").boolValue = false;
                    authoringData.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            return root.transform;
        }

        private static float ClampInsideTerrain(
            float value,
            float minimum,
            float maximum,
            float requiredHalfExtent)
        {
            if (maximum - minimum <= requiredHalfExtent * 2f)
                return (minimum + maximum) * 0.5f;
            return Mathf.Clamp(value, minimum + requiredHalfExtent, maximum - requiredHalfExtent);
        }

        private static void RebindRuntimeChain(
            ElementWorldRuntime world,
            ElementWorldProfile profile,
            Transform interestPoint,
            Transform collisionRoot,
            GameObject integration,
            GameObject fireVisuals)
        {
            GpuPbfFluidRuntime fluid = integration.GetComponent<GpuPbfFluidRuntime>()
                ?? throw new InvalidOperationException("GPU_PBF_Fluid 缺少 GpuPbfFluidRuntime。");
            FluidGameplayOccupancyBridge bridge =
                integration.GetComponent<FluidGameplayOccupancyBridge>()
                ?? throw new InvalidOperationException("GPU_PBF_Fluid 缺少 Gameplay Occupancy Bridge。");
            ElementWorldExposureSystem exposure = world.GetComponent<ElementWorldExposureSystem>()
                ?? throw new InvalidOperationException("P8 ElementWorldRoot 缺少 Exposure System。");

            var fluidData = new SerializedObject(fluid);
            fluidData.FindProperty("_fluidColliderRoot").objectReferenceValue = collisionRoot;
            fluidData.FindProperty("_simulationBoundsCenter").objectReferenceValue = interestPoint;
            fluidData.ApplyModifiedPropertiesWithoutUndo();

            var bridgeData = new SerializedObject(bridge);
            bridgeData.FindProperty("_fluidSourceComponent").objectReferenceValue = fluid;
            bridgeData.FindProperty("_elementWorldRuntime").objectReferenceValue = world;
            bridgeData.ApplyModifiedPropertiesWithoutUndo();

            var worldData = new SerializedObject(world);
            worldData.FindProperty("_profile").objectReferenceValue = profile;
            worldData.FindProperty("_interestPoint").objectReferenceValue = interestPoint;
            worldData.FindProperty("_gpuPbfFluidRuntime").objectReferenceValue = fluid;
            worldData.FindProperty("_fluidGameplayOccupancy").objectReferenceValue = bridge;
            worldData.ApplyModifiedPropertiesWithoutUndo();

            var exposureData = new SerializedObject(exposure);
            exposureData.FindProperty("_liquidOccupancyComponent").objectReferenceValue = bridge;
            exposureData.ApplyModifiedPropertiesWithoutUndo();

            GpuLiquidSurfaceRenderer[] renderers =
                integration.GetComponents<GpuLiquidSurfaceRenderer>();
            if (renderers.Length != 3)
                throw new InvalidOperationException(
                    $"P7 生产版模板应包含 3 个 Liquid Renderer，实际为 {renderers.Length}。");
            for (int i = 0; i < renderers.Length; i++)
            {
                var rendererData = new SerializedObject(renderers[i]);
                rendererData.FindProperty("_fluidSourceComponent").objectReferenceValue = fluid;
                rendererData.FindProperty("_worldRuntime").objectReferenceValue = world;
                rendererData.ApplyModifiedPropertiesWithoutUndo();
            }

            GpuFireParcelRenderer fireRenderer = fireVisuals.GetComponent<GpuFireParcelRenderer>()
                ?? throw new InvalidOperationException("GpuFireParcelVisuals 缺少 Renderer。");
            var fireData = new SerializedObject(fireRenderer);
            fireData.FindProperty("_worldRuntime").objectReferenceValue = world;
            fireData.FindProperty("_interestCenter").objectReferenceValue = interestPoint;
            fireData.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void RepairLegacyRendererReference(Scene scene, ElementWorldRuntime world)
        {
            ElementWorldWaterRenderer renderer = FindInScene<ElementWorldWaterRenderer>(scene);
            if (renderer == null)
                return;
            var data = new SerializedObject(renderer);
            data.FindProperty("_worldRuntime").objectReferenceValue = world;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void RepairStatusPresentation(Scene scene)
        {
            MonoBehaviour bar = FindBehaviourInScene(scene, "Game.UI.PlayerStatusBar")
                ?? throw new InvalidOperationException("P8_BossField 缺少 PlayerStatusBar。");
            var barData = new SerializedObject(bar);
            string[] viewFields =
            {
                "_burningView",
                "_wetView",
                "_poisonedView",
                "_stickyView",
            };
            var iconIds = new HashSet<int>();
            var textIds = new HashSet<int>();
            for (int i = 0; i < viewFields.Length; i++)
            {
                string viewField = viewFields[i];
                MonoBehaviour view = barData.FindProperty(viewField)?.objectReferenceValue
                    as MonoBehaviour;
                if (view == null)
                    throw new InvalidOperationException($"P8 PlayerStatusBar.{viewField} 未绑定。");

                // 每个 Slot 必须写自己的子 Image/Text。只检查 non-null 无法发现“Sticky 写到 Wet UI”
                // 这类合法但错误的跨 Slot 引用，最终表现会由最后一次状态刷新覆盖前一次刷新。
                Transform iconTransform = FindDescendant(view.transform, "Icon");
                Transform textTransform = FindDescendant(view.transform, "PercentText");
                Image icon = iconTransform != null ? iconTransform.GetComponent<Image>() : null;
                Text percentageText = textTransform != null ? textTransform.GetComponent<Text>() : null;
                if (icon == null || percentageText == null)
                {
                    throw new InvalidOperationException(
                        $"P8 {view.name} 必须包含名为 Icon 和 PercentText 的 UI 子对象。");
                }

                var viewData = new SerializedObject(view);
                viewData.FindProperty("_icon").objectReferenceValue = icon;
                viewData.FindProperty("_percentageText").objectReferenceValue = percentageText;
                viewData.ApplyModifiedPropertiesWithoutUndo();
                iconIds.Add(icon.GetInstanceID());
                textIds.Add(percentageText.GetInstanceID());
            }

            if (iconIds.Count != viewFields.Length || textIds.Count != viewFields.Length)
            {
                throw new InvalidOperationException(
                    "P8 四种 StatusIconView 仍共享 Image 或 Text，无法保证独立刷新。");
            }
        }

        private static void AssertNoCrossSceneReferences(GameObject root, Scene targetScene)
        {
            Component[] components = root.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null)
                    continue;
                var data = new SerializedObject(component);
                SerializedProperty property = data.GetIterator();
                if (!property.NextVisible(true))
                    continue;
                do
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference)
                        continue;
                    Object value = property.objectReferenceValue;
                    GameObject referencedObject = value switch
                    {
                        Component referencedComponent => referencedComponent.gameObject,
                        GameObject referencedGameObject => referencedGameObject,
                        _ => null,
                    };
                    if (referencedObject != null
                        && referencedObject.scene.IsValid()
                        && referencedObject.scene != targetScene)
                    {
                        throw new InvalidOperationException(
                            $"{component.name}.{property.propertyPath} 仍引用其他场景的 {referencedObject.name}。");
                    }
                }
                while (property.NextVisible(false));
            }
        }

        private static T GetObject<T>(Object owner, string propertyName) where T : Object
        {
            var data = new SerializedObject(owner);
            return data.FindProperty(propertyName)?.objectReferenceValue as T;
        }

        private static bool TryGetLoadedScene(string path, out Scene scene)
        {
            scene = SceneManager.GetSceneByPath(path);
            return scene.IsValid() && scene.isLoaded;
        }

        private static T FindInScene<T>(Scene scene) where T : Component
        {
            T[] components = Object.FindObjectsByType<T>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            for (int i = 0; i < components.Length; i++)
            {
                T component = components[i];
                if (component != null && component.gameObject.scene == scene)
                    return component;
            }
            return null;
        }

        private static MonoBehaviour FindBehaviourInScene(Scene scene, string fullTypeName)
        {
            // 迁移工具只修改 SerializedObject 接线，不调用 Game.UI 行为；通过完整类型名定位组件，
            // 可避免 Editor Assembly 为一次性数据修复反向扩大 Runtime 模块依赖。
            MonoBehaviour[] components = Object.FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            for (int i = 0; i < components.Length; i++)
            {
                MonoBehaviour component = components[i];
                if (component != null
                    && component.gameObject.scene == scene
                    && component.GetType().FullName == fullTypeName)
                {
                    return component;
                }
            }
            return null;
        }

        private static GameObject FindGameObject(Scene scene, string objectName)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                Transform[] transforms = roots[i].GetComponentsInChildren<Transform>(true);
                for (int j = 0; j < transforms.Length; j++)
                {
                    if (transforms[j].name == objectName)
                        return transforms[j].gameObject;
                }
            }
            return null;
        }

        private sealed class GateCallbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun)
            {
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                bool passed = result.FailCount == 0;
                if (passed)
                    EditorPrefs.SetBool(GateCompletedKey, true);
                string summary =
                    $"P8 ElementWorld Scene Contract: Passed={result.PassCount}, "
                    + $"Failed={result.FailCount}, Skipped={result.SkipCount}, "
                    + $"Duration={result.Duration:0.###}s";
                if (passed)
                    GameLog.Info(summary, "Editor");
                else
                    GameLog.Error(summary, "Editor");
                if (_testApi != null && _gateCallbacks != null)
                    _testApi.UnregisterCallbacks(_gateCallbacks);
                if (_testApi != null)
                    Object.DestroyImmediate(_testApi);
                _testApi = null;
                _gateCallbacks = null;
            }

            public void TestStarted(ITestAdaptor test)
            {
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.FailCount > 0)
                    GameLog.Error($"P8 Contract Test Failed: {result.Name}\n{result.Message}", "Editor");
            }
        }
    }
}
#endif
