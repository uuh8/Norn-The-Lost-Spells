using Game.Materials;
using System;
using System.Collections.Generic;
using Game.Core;
using Game.ElementField;
using Game.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>
    /// Task 13 的幂等 Editor 集成工具。所有 ScriptableObject、Material、Scene 与 .meta
    /// 都由 Unity API 创建；它只追加独立 PBF 根节点，不覆盖 P7 现有 Wizard/Jump/UI 对象。
    /// </summary>
    public static class PbfFluidTask13Setup
    {
        private const string SimulationProfilePath =
            "Assets/_Project/ScriptableObjects/ElementField/Fluid/LiquidSimulationProfile_Water.asset";
        private const string RenderProfilePath =
            "Assets/_Project/ScriptableObjects/ElementField/Fluid/LiquidRenderProfile_Water.asset";
        private const string P7WorldProfilePath =
            "Assets/_Project/ScriptableObjects/ElementField/Fluid/ElementWorldProfile_P7_PBF.asset";
        private const string MaterialCatalogPath =
            "Assets/_Project/ScriptableObjects/Materials/MaterialCatalog_Default.asset";
        private const string MaterialRoutingPath =
            "Assets/_Project/ScriptableObjects/Materials/MaterialSimulationRouting_Default.asset";
        private const string P7WaterValidationWandPath =
            "Assets/_Project/ScriptableObjects/ElementField/Fluid/P7_PBF_WaterValidationWand.asset";
        private const string SourceP7WandPath =
            "Assets/_Project/ScriptableObjects/P7/P7_StartingWandLoadout.asset";
        private const string WaterSpellPath =
            "Assets/_Project/ScriptableObjects/Spell/Spell_Emit_WaterField.asset";
        private const string SourceWorldProfilePath =
            "Assets/_Project/ScriptableObjects/ElementField/ElementWorldProfile_P6B.asset";
        private const string MaterialPath =
            "Assets/_Project/Art/Elemental/Materials/M_ElementWater_PBF.mat";
        private const string SandboxScenePath =
            "Assets/_Project/Scenes/PBF_FluidSandbox.unity";
        private const string P7ScenePath =
            "Assets/_Project/Scenes/P7_DemoRun.unity";
        private const string IntegrationRootName = "GPU_PBF_Fluid";
        private const int MaximumP7ColliderProxies = 128;

        private static readonly string[] ColliderNameTokens =
        {
            "Floor", "Wall", "Ceiling", "Blocker", "Gate", "Sliding",
            "Platform", "Pillar", "Obstacle", "Ground", "Ramp", "Container"
        };

        [MenuItem("Tools/PBF Fluid/Run Task 13 Setup")]
        public static void RunAuthorizedSetupOnce()
        {
            if (EditorApplication.isCompiling
                || EditorApplication.isUpdating
                || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.delayCall += RunAuthorizedSetupOnce;
                return;
            }

            try
            {
                LiquidSimulationProfile simulationProfile = CreateSimulationProfile();
                LiquidRenderProfile renderProfile = CreateRenderProfile();
                MaterialCatalog materialCatalog =
                    AssetDatabase.LoadAssetAtPath<MaterialCatalog>(MaterialCatalogPath)
                    ?? throw new InvalidOperationException($"缺少 Material Catalog：{MaterialCatalogPath}");
                MaterialSimulationRoutingProfile materialRouting = CreateMaterialRoutingProfile();
                ElementWorldProfile worldProfile = CreateP7WorldProfile(materialCatalog, materialRouting);
                Object waterValidationWand = CreateP7WaterValidationWand();
                Material material = CreateWaterMaterial();

                // 新建 Native Asset 后，必须先 Save + 同步 Import，再重新取得带稳定 GUID/local fileID 的实例。
                // 若直接把创建时的临时实例写入 Scene，YAML 可能退化成 {fileID: 0}，Editor 内看似存在而 Player 中为空。
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(SimulationProfilePath, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(RenderProfilePath, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(P7WorldProfilePath, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(MaterialRoutingPath, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(P7WaterValidationWandPath, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(MaterialPath, ImportAssetOptions.ForceSynchronousImport);
                simulationProfile = ReloadPersistentAsset<LiquidSimulationProfile>(SimulationProfilePath);
                renderProfile = ReloadPersistentAsset<LiquidRenderProfile>(RenderProfilePath);
                worldProfile = ReloadPersistentAsset<ElementWorldProfile>(P7WorldProfilePath);
                waterValidationWand = ReloadPersistentAsset<Object>(P7WaterValidationWandPath);
                material = ReloadPersistentAsset<Material>(MaterialPath);

                CreateOrUpdateSandbox(
                    simulationProfile,
                    renderProfile,
                    worldProfile,
                    material);
                IntegrateP7(
                    simulationProfile,
                    renderProfile,
                    worldProfile,
                    waterValidationWand,
                    material);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                ValidateSetup();
                GameLog.Info(
                    "[PBF-TASK13-SETUP] SUCCESS：Profiles、Material、Sandbox 与 P7 增量绑定已由 Unity Editor 创建并保存。",
                    "Editor");
            }
            catch (Exception exception)
            {
                GameLog.Error(
                    $"[PBF-TASK13-SETUP] FAILED：{exception}",
                    "Editor");
            }
        }

        [MenuItem("Tools/PBF Fluid/Validate Task 13 Setup")]
        public static void ValidateSetup()
        {
            var failures = new List<string>();
            RequireAsset<LiquidSimulationProfile>(SimulationProfilePath, failures);
            RequireAsset<LiquidRenderProfile>(RenderProfilePath, failures);
            RequireAsset<ElementWorldProfile>(P7WorldProfilePath, failures);
            RequireAsset<MaterialCatalog>(MaterialCatalogPath, failures);
            RequireAsset<MaterialSimulationRoutingProfile>(MaterialRoutingPath, failures);
            RequireAsset<Object>(P7WaterValidationWandPath, failures);
            RequireAsset<Material>(MaterialPath, failures);
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SandboxScenePath) == null)
                failures.Add($"缺少 Sandbox Scene：{SandboxScenePath}");

            Scene scene = EditorSceneManager.OpenScene(P7ScenePath, OpenSceneMode.Single);
            ElementWorldRuntime world = Object.FindFirstObjectByType<ElementWorldRuntime>(
                FindObjectsInactive.Include);
            ElementWorldExposureSystem exposure = world != null
                ? world.GetComponent<ElementWorldExposureSystem>()
                : null;
            FluidGameplayOccupancyBridge bridge = null;
            GameObject integrationRoot = GameObject.Find(IntegrationRootName);
            MonoBehaviour runSpellSession = FindMonoBehaviour("Game.Run.RunSpellSession");
            if (world == null)
                failures.Add("P7 缺少 ElementWorldRuntime。");
            else
                RequireReference(new SerializedObject(world), "_profile", "P7 ElementWorldRuntime Profile", failures);
            if (exposure == null)
                failures.Add("P7 ElementWorldRoot 缺少 ElementWorldExposureSystem。");
            if (integrationRoot == null)
                failures.Add($"P7 缺少 {IntegrationRootName}。");
            else
            {
                GpuPbfFluidRuntime fluid = integrationRoot.GetComponent<GpuPbfFluidRuntime>();
                GpuLiquidSurfaceRenderer renderer = integrationRoot.GetComponent<GpuLiquidSurfaceRenderer>();
                if (fluid == null)
                    failures.Add("P7 PBF Root 缺少 GpuPbfFluidRuntime。");
                else
                    RequireReference(new SerializedObject(fluid), "_profile", "P7 Fluid Simulation Profile", failures);
                bridge = integrationRoot.GetComponent<FluidGameplayOccupancyBridge>();
                if (bridge == null)
                    failures.Add("P7 PBF Root 缺少 FluidGameplayOccupancyBridge。");
                if (renderer == null)
                    failures.Add("P7 PBF Root 缺少 GpuLiquidSurfaceRenderer。");
                else
                    RequireReference(new SerializedObject(renderer), "_profile", "P7 Liquid Render Profile", failures);
            }
            if (exposure != null && bridge != null)
            {
                RequireReferenceEquals(
                    new SerializedObject(exposure),
                    "_liquidOccupancyComponent",
                    bridge,
                    "P7 Exposure GPU Water Gameplay Occupancy",
                    failures);
            }
            if (runSpellSession == null)
                failures.Add("P7 缺少 RunSpellSession。");
            else
                RequireReference(
                    new SerializedObject(runSpellSession),
                    "_startingWand",
                    "P7 Water Validation Wand",
                    failures);

            if (!scene.IsValid())
                failures.Add("P7 Scene 无法打开。");
            if (failures.Count == 0)
            {
                GameLog.Info("[PBF-TASK13-VALIDATE] SUCCESS：静态资产与 P7 Component Contract 完整。", "Editor");
                return;
            }

            for (int i = 0; i < failures.Count; i++)
                GameLog.Error($"[PBF-TASK13-VALIDATE] {failures[i]}", "Editor");
        }

        private static LiquidSimulationProfile CreateSimulationProfile()
        {
            EnsureFolder("Assets/_Project/ScriptableObjects/ElementField/Fluid");
            LiquidSimulationProfile profile =
                AssetDatabase.LoadAssetAtPath<LiquidSimulationProfile>(SimulationProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<LiquidSimulationProfile>();
                profile.name = "LiquidSimulationProfile_Water";
                AssetDatabase.CreateAsset(profile, SimulationProfilePath);
            }

            var serialized = new SerializedObject(profile);
            SetInt(serialized, "_particleCapacity", 8192);
            SetInt(serialized, "_maxFluidColliders", MaximumP7ColliderProxies);
            SetInt(serialized, "_amountUnitsPerParticle", 8);
            SetFloat(serialized, "_particleRadius", 0.1f);
            SetFloat(serialized, "_smoothingRadius", 0.25f);
            SetFloat(serialized, "_fixedTickRate", 60f);
            SetInt(serialized, "_substeps", 2);
            SetInt(serialized, "_solverIterations", 4);
            SetFloat(serialized, "_cohesionStrength", 12f);
            SetFloat(serialized, "_cohesionRestDistanceRatio", 1f);
            SetFloat(serialized, "_maximumCohesionDeltaSpeed", 0.2f);
            SetFloat(serialized, "_tensileStrength", 0.00005f);
            SetFloat(serialized, "_maximumTensilePositionCorrection", 0.0001f);
            SetFloat(serialized, "_viscosity", 0.08f);
            SetFloat(serialized, "_vorticity", 0.002f);
            SetFloat(serialized, "_maxSpeed", 10f);
            // D00 实测静止 PBF 粒子绝大多数落在 0.05~0.10m/s；0.10 可吸收求解残余抖动，
            // 同时明显低于可见流动速度，配合连续 30 Tick 防止瞬时慢速误休眠。
            SetFloat(serialized, "_sleepThreshold", 0.1f);
            SetFloat(serialized, "_sleepDensityErrorThreshold", 0.02f);
            SetInt(serialized, "_sleepAfterStableTicks", 30);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(profile);
            return profile;
        }

        private static LiquidRenderProfile CreateRenderProfile()
        {
            EnsureFolder("Assets/_Project/ScriptableObjects/ElementField/Fluid");
            LiquidRenderProfile profile =
                AssetDatabase.LoadAssetAtPath<LiquidRenderProfile>(RenderProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<LiquidRenderProfile>();
                profile.name = "LiquidRenderProfile_Water";
                AssetDatabase.CreateAsset(profile, RenderProfilePath);
            }

            var serialized = new SerializedObject(profile);
            SetFloat(serialized, "_targetVoxelSize", 0.10f);
            SetInt(serialized, "_maximumResolutionPerAxis", 96);
            SetFloat(serialized, "_boundsPadding", 0.3f);
            SetFloat(serialized, "_isoLevel", 500f);
            SetInt(serialized, "_maximumTriangleCount", 131072);
            SetBool(serialized, "_useAnisotropy", true);
            SetInt(serialized, "_anisotropyUpdateIntervalFrames", 2);
            SetInt(serialized, "_anisotropyNeighborThreshold", 5);
            SetFloat(serialized, "_minimumAnisotropyScale", 0.65f);
            SetFloat(serialized, "_maximumAnisotropyScale", 1.8f);
            SetFloat(serialized, "_maximumAnisotropyRatio", 2.5f);
            SetBool(serialized, "_useStylizedCrown", true);
            SetFloat(serialized, "_crownHeightRatio", 0.8f);
            SetFloat(serialized, "_crownFalloff", 1.5f);
            SetInt(serialized, "_crownEdgeNeighborCount", 4);
            SetInt(serialized, "_crownInteriorNeighborCount", 12);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(profile);
            return profile;
        }

        private static MaterialSimulationRoutingProfile CreateMaterialRoutingProfile()
        {
            EnsureFolder("Assets/_Project/ScriptableObjects/Materials");
            MaterialSimulationRoutingProfile profile =
                AssetDatabase.LoadAssetAtPath<MaterialSimulationRoutingProfile>(MaterialRoutingPath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<MaterialSimulationRoutingProfile>();
                AssetDatabase.CreateAsset(profile, MaterialRoutingPath);
            }

            var serialized = new SerializedObject(profile);
            SerializedProperty routes = serialized.FindProperty("_routes")
                ?? throw new InvalidOperationException("Material Routing Profile 缺少 _routes。");
            routes.arraySize = 2;
            ConfigureRoute(routes.GetArrayElementAtIndex(0), MaterialId.Fire, MaterialSimulationBackendKind.ElementCell);
            ConfigureRoute(routes.GetArrayElementAtIndex(1), MaterialId.Poison, MaterialSimulationBackendKind.Unsupported);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(profile);
            return profile;
        }

        private static void ConfigureRoute(
            SerializedProperty route,
            MaterialId material,
            MaterialSimulationBackendKind backend)
        {
            route.FindPropertyRelative("_material").intValue = (byte)material;
            route.FindPropertyRelative("_backend").intValue = (byte)backend;
        }

        private static ElementWorldProfile CreateP7WorldProfile(
            MaterialCatalog materialCatalog,
            MaterialSimulationRoutingProfile materialRouting)
        {
            EnsureFolder("Assets/_Project/ScriptableObjects/ElementField/Fluid");
            ElementWorldProfile profile =
                AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(P7WorldProfilePath);
            if (profile == null)
            {
                if (!AssetDatabase.CopyAsset(SourceWorldProfilePath, P7WorldProfilePath))
                    throw new InvalidOperationException("无法复制 P7 ElementWorldProfile。");
                profile = AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(P7WorldProfilePath);
            }

            var serialized = new SerializedObject(profile);
            SetInt(serialized, "_waterSimulationMode", (int)WaterSimulationMode.GpuPbf);
            SetInt(serialized, "_settleAfterUnchangedTicks", 2);
            SetObject(serialized, "_materialCatalog", materialCatalog);
            SetObject(serialized, "_materialSimulationRouting", materialRouting);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(profile);
            return profile;
        }

        private static Object CreateP7WaterValidationWand()
        {
            EnsureFolder("Assets/_Project/ScriptableObjects/ElementField/Fluid");
            Object wand = AssetDatabase.LoadMainAssetAtPath(P7WaterValidationWandPath);
            if (wand == null)
            {
                if (!AssetDatabase.CopyAsset(SourceP7WandPath, P7WaterValidationWandPath))
                    throw new InvalidOperationException("无法复制 P7 PBF Water Validation Wand。");
                wand = AssetDatabase.LoadMainAssetAtPath(P7WaterValidationWandPath);
            }

            Object waterSpell = AssetDatabase.LoadMainAssetAtPath(WaterSpellPath)
                ?? throw new InvalidOperationException($"缺少 Water Spell：{WaterSpellPath}");
            var serialized = new SerializedObject(wand);
            SerializedProperty spells = serialized.FindProperty("Spells")
                ?? throw new InvalidOperationException("P7 PBF Water Validation Wand 缺少 Spells 字段。");
            spells.arraySize = 1;
            spells.GetArrayElementAtIndex(0).objectReferenceValue = waterSpell;
            SerializedProperty baseDraws = serialized.FindProperty("BaseDraws")
                ?? throw new InvalidOperationException("P7 PBF Water Validation Wand 缺少 BaseDraws 字段。");
            baseDraws.intValue = 1;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(wand);
            return wand;
        }

        private static Material CreateWaterMaterial()
        {
            EnsureFolder("Assets/_Project/Art/Elemental/Materials");
            Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material != null)
                return material;

            Shader shader = Shader.Find("Game/Elemental/Liquid Procedural");
            if (shader == null)
                throw new InvalidOperationException("找不到 Game/Elemental/Liquid Procedural Shader。");
            material = new Material(shader)
            {
                name = "M_ElementWater_PBF"
            };
            material.SetColor("_ShallowColor", new Color(0.18f, 0.78f, 0.82f, 1f));
            material.SetColor("_DeepColor", new Color(0.025f, 0.16f, 0.50f, 1f));
            material.SetFloat("_Smoothness", 0.9f);
            material.SetFloat("_WaterAlpha", 1f);
            AssetDatabase.CreateAsset(material, MaterialPath);
            return material;
        }

        private static void CreateOrUpdateSandbox(
            LiquidSimulationProfile simulationProfile,
            LiquidRenderProfile renderProfile,
            ElementWorldProfile worldProfile,
            Material material)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "PBF_FluidSandbox";
            simulationProfile = ReloadPersistentAsset<LiquidSimulationProfile>(SimulationProfilePath);
            renderProfile = ReloadPersistentAsset<LiquidRenderProfile>(RenderProfilePath);
            worldProfile = ReloadPersistentAsset<ElementWorldProfile>(P7WorldProfilePath);
            material = ReloadPersistentAsset<Material>(MaterialPath);

            GameObject systems = new GameObject("__Systems");
            GameObject worldObject = new GameObject("ElementWorldRoot");
            worldObject.transform.SetParent(systems.transform, false);
            ElementWorldRuntime world = worldObject.AddComponent<ElementWorldRuntime>();

            GameObject boundsCenter = new GameObject("SimulationBoundsCenter");
            boundsCenter.transform.SetParent(worldObject.transform, false);
            boundsCenter.transform.position = new Vector3(0f, 3f, 0f);

            GameObject colliderRoot = new GameObject("FluidColliders");
            CreateSandboxCollider(colliderRoot.transform, "Floor", new Vector3(0f, -0.5f, 0f),
                new Vector3(14f, 1f, 14f), Quaternion.identity);
            CreateSandboxCollider(colliderRoot.transform, "Ramp", new Vector3(-3f, 0.9f, 0f),
                new Vector3(5f, 0.5f, 4f), Quaternion.Euler(0f, 0f, -20f));
            CreateSandboxCollider(colliderRoot.transform, "Blocker", new Vector3(2f, 1.5f, 0f),
                new Vector3(1f, 3f, 6f), Quaternion.identity);
            CreateSandboxCollider(colliderRoot.transform, "ContainerFloor", new Vector3(4f, 0.25f, 4f),
                new Vector3(4f, 0.5f, 4f), Quaternion.identity);
            CreateSandboxCollider(colliderRoot.transform, "ContainerWest", new Vector3(2f, 1.5f, 4f),
                new Vector3(0.4f, 3f, 4f), Quaternion.identity);
            CreateSandboxCollider(colliderRoot.transform, "ContainerEast", new Vector3(6f, 1.5f, 4f),
                new Vector3(0.4f, 3f, 4f), Quaternion.identity);
            CreateSandboxCollider(colliderRoot.transform, "ContainerNorth", new Vector3(4f, 1.5f, 6f),
                new Vector3(4f, 3f, 0.4f), Quaternion.identity);

            GpuPbfFluidRuntime fluid = worldObject.AddComponent<GpuPbfFluidRuntime>();
            FluidGameplayOccupancyBridge bridge = worldObject.AddComponent<FluidGameplayOccupancyBridge>();
            GpuLiquidSurfaceRenderer renderer = worldObject.AddComponent<GpuLiquidSurfaceRenderer>();
            ConfigureFluidRuntime(fluid, simulationProfile, colliderRoot.transform, boundsCenter.transform,
                new Vector3(14f, 10f, 14f));
            ConfigureWorld(world, worldProfile, boundsCenter.transform, fluid, bridge);
            ConfigureBridge(bridge, fluid, world);
            ConfigureSurfaceRenderer(renderer, fluid, world, renderProfile, material);

            // 默认 Sandbox 验证稳定流动而不是 Capacity Overflow：4×1000=4000 粒子，
            // 保留一半余量给后续交互。超容量行为由 focused GPU Test 单独验证，避免两类问题互相污染。
            for (int i = 0; i < 4; i++)
            {
                GameObject depositObject = new GameObject($"WaterDeposit_{i:00}");
                depositObject.transform.position = new Vector3(
                    -3f + (i % 2) * 6f,
                    4.5f,
                    -2.5f + (i / 2) * 5f);
                ElementWorldInitialDeposit deposit = depositObject.AddComponent<ElementWorldInitialDeposit>();
                var serializedDeposit = new SerializedObject(deposit);
                SetInt(serializedDeposit, "_materialKind", (int)MaterialId.Water);
                SetInt(serializedDeposit, "_totalAmount", 8000);
                SetFloat(serializedDeposit, "_radius", 1.2f);
                SetBool(serializedDeposit, "_useLinearFalloff", true);
                SetBool(serializedDeposit, "_depositOnStart", true);
                serializedDeposit.ApplyModifiedPropertiesWithoutUndo();
            }

            CreateSandboxCamera();
            CreateSandboxLight();
            EditorSceneManager.SaveScene(scene, SandboxScenePath);
        }

        private static void IntegrateP7(
            LiquidSimulationProfile simulationProfile,
            LiquidRenderProfile renderProfile,
            ElementWorldProfile worldProfile,
            Object waterValidationWand,
            Material material)
        {
            Scene scene = EditorSceneManager.OpenScene(P7ScenePath, OpenSceneMode.Single);
            // OpenScene(Single) 会卸载 Sandbox，并可能让上一场景调用栈持有的 Unity Object wrapper 失效。
            // P7 必须在场景切换后重新取得持久 Asset，不能复用传入的旧 wrapper。
            simulationProfile = ReloadPersistentAsset<LiquidSimulationProfile>(SimulationProfilePath);
            renderProfile = ReloadPersistentAsset<LiquidRenderProfile>(RenderProfilePath);
            worldProfile = ReloadPersistentAsset<ElementWorldProfile>(P7WorldProfilePath);
            waterValidationWand = ReloadPersistentAsset<Object>(P7WaterValidationWandPath);
            material = ReloadPersistentAsset<Material>(MaterialPath);
            ElementWorldRuntime world = Object.FindFirstObjectByType<ElementWorldRuntime>(
                FindObjectsInactive.Include);
            if (world == null)
                throw new InvalidOperationException("P7_DemoRun 缺少 ElementWorldRuntime。");

            Transform worldTransform = world.transform;
            Transform integrationTransform = worldTransform.Find(IntegrationRootName);
            GameObject integrationRoot;
            if (integrationTransform == null)
            {
                integrationRoot = new GameObject(IntegrationRootName);
                integrationRoot.transform.SetParent(worldTransform, false);
            }
            else
            {
                integrationRoot = integrationTransform.gameObject;
            }

            GpuPbfFluidRuntime fluid = GetOrAdd<GpuPbfFluidRuntime>(integrationRoot);
            FluidGameplayOccupancyBridge bridge = GetOrAdd<FluidGameplayOccupancyBridge>(integrationRoot);
            GpuLiquidSurfaceRenderer renderer = GetOrAdd<GpuLiquidSurfaceRenderer>(integrationRoot);
            ElementWorldExposureSystem exposure = world.GetComponent<ElementWorldExposureSystem>();
            if (exposure == null)
                throw new InvalidOperationException("P7 ElementWorldRoot 缺少 ElementWorldExposureSystem。");

            var serializedWorld = new SerializedObject(world);
            SerializedProperty interestProperty = serializedWorld.FindProperty("_interestPoint");
            Transform interestPoint = interestProperty != null
                ? interestProperty.objectReferenceValue as Transform
                : null;
            if (interestPoint == null)
                throw new InvalidOperationException("P7 ElementWorldRuntime 缺少 Interest Point。");

            GameObject levelRoot = GameObject.Find("__Level");
            Transform colliderRoot = levelRoot != null ? levelRoot.transform : worldTransform;
            int authoredColliderCount = AuthorP7FluidColliders(colliderRoot);
            ConfigureFluidRuntime(fluid, simulationProfile, colliderRoot, interestPoint,
                new Vector3(24f, 12f, 24f));
            ConfigureWorld(world, worldProfile, interestPoint, fluid, bridge);
            ConfigureBridge(bridge, fluid, world);
            ConfigureExposure(exposure, bridge);
            ConfigureSurfaceRenderer(renderer, fluid, world, renderProfile, material);
            ConfigureP7WaterValidationWand(waterValidationWand);

            EditorUtility.SetDirty(world);
            EditorUtility.SetDirty(fluid);
            EditorUtility.SetDirty(bridge);
            EditorUtility.SetDirty(exposure);
            EditorUtility.SetDirty(renderer);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            GameLog.Info(
                $"[PBF-TASK13-SETUP] P7 已绑定；FluidColliderAuthoring={authoredColliderCount}。",
                "Editor");
        }

        private static void ConfigureP7WaterValidationWand(Object waterValidationWand)
        {
            MonoBehaviour runSpellSession = FindMonoBehaviour("Game.Run.RunSpellSession")
                ?? throw new InvalidOperationException("P7_DemoRun 缺少 RunSpellSession。");
            var serialized = new SerializedObject(runSpellSession);
            SetObject(serialized, "_startingWand", waterValidationWand);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(runSpellSession);
        }

        private static MonoBehaviour FindMonoBehaviour(string fullTypeName)
        {
            MonoBehaviour[] behaviours = Object.FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour != null && behaviour.GetType().FullName == fullTypeName)
                    return behaviour;
            }

            return null;
        }

        private static int AuthorP7FluidColliders(Transform root)
        {
            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            Array.Sort(colliders, CompareColliderHierarchyPath);
            int authored = 0;
            for (int i = 0; i < colliders.Length && authored < MaximumP7ColliderProxies; i++)
            {
                Collider collider = colliders[i];
                if (collider == null
                    || collider.isTrigger
                    || !IsSupportedCollider(collider)
                    || !HasFluidSurfaceName(collider.gameObject.name))
                {
                    continue;
                }

                FluidColliderAuthoring authoring =
                    collider.GetComponent<FluidColliderAuthoring>()
                    ?? collider.gameObject.AddComponent<FluidColliderAuthoring>();
                var serialized = new SerializedObject(authoring);
                SetObject(serialized, "_collider", collider);
                SetBool(serialized, "_isDynamic", false);
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(authoring);
                authored++;
            }

            if (authored == 0)
                throw new InvalidOperationException("P7 __Level 下没有匹配的 Box/Sphere/Capsule 流体碰撞面。");
            return authored;
        }

        private static int CompareColliderHierarchyPath(Collider left, Collider right)
        {
            return string.CompareOrdinal(GetHierarchyPath(left.transform), GetHierarchyPath(right.transform));
        }

        private static string GetHierarchyPath(Transform transform)
        {
            string path = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = transform.name + "/" + path;
            }
            return path;
        }

        private static bool HasFluidSurfaceName(string objectName)
        {
            for (int i = 0; i < ColliderNameTokens.Length; i++)
            {
                if (objectName.IndexOf(ColliderNameTokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static bool IsSupportedCollider(Collider collider)
        {
            return collider is BoxCollider || collider is SphereCollider || collider is CapsuleCollider;
        }

        private static void ConfigureFluidRuntime(
            GpuPbfFluidRuntime runtime,
            LiquidSimulationProfile profile,
            Transform colliderRoot,
            Transform boundsCenter,
            Vector3 boundsSize)
        {
            var serialized = new SerializedObject(runtime);
            SetObject(serialized, "_profile", profile);
            SetObject(serialized, "_particleLifecycleShader", LoadCompute("PbfParticleLifecycle"));
            SetObject(serialized, "_spatialHashShader", LoadCompute("PbfSpatialHash"));
            SetObject(serialized, "_pbfSolverShader", LoadCompute("PbfSolver"));
            SetObject(serialized, "_collisionShader", LoadCompute("PbfCollision"));
            SetObject(serialized, "_fluidColliderRoot", colliderRoot);
            SetObject(serialized, "_simulationBoundsCenter", boundsCenter);
            SerializedProperty size = serialized.FindProperty("_simulationBoundsSize");
            if (size != null)
                size.vector3Value = boundsSize;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureWorld(
            ElementWorldRuntime world,
            ElementWorldProfile profile,
            Transform interestPoint,
            GpuPbfFluidRuntime fluid,
            FluidGameplayOccupancyBridge bridge)
        {
            var serialized = new SerializedObject(world);
            SetObject(serialized, "_profile", profile);
            SetObject(serialized, "_interestPoint", interestPoint);
            SetObject(serialized, "_gpuPbfFluidRuntime", fluid);
            SetObject(serialized, "_fluidGameplayOccupancy", bridge);
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureBridge(
            FluidGameplayOccupancyBridge bridge,
            GpuPbfFluidRuntime fluid,
            ElementWorldRuntime world)
        {
            var serialized = new SerializedObject(bridge);
            SetObject(serialized, "_fluidSourceComponent", fluid);
            SetObject(serialized, "_elementWorldRuntime", world);
            SetFloat(serialized, "_readbackInterval", 0.25f);
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureExposure(
            ElementWorldExposureSystem exposure,
            FluidGameplayOccupancyBridge bridge)
        {
            var serialized = new SerializedObject(exposure);
            // GPU PBF 的 Water Gameplay Truth 位于子物体 Bridge；这里必须显式接线，
            // 因为 Exposure 位于父物体，运行时同物体 GetComponent 无法跨层级找到它。
            SetObject(serialized, "_liquidOccupancyComponent", bridge);
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureSurfaceRenderer(
            GpuLiquidSurfaceRenderer renderer,
            GpuPbfFluidRuntime fluid,
            ElementWorldRuntime world,
            LiquidRenderProfile profile,
            Material material)
        {
            var serialized = new SerializedObject(renderer);
            SetObject(serialized, "_fluidSourceComponent", fluid);
            SetObject(serialized, "_worldRuntime", world);
            SetObject(serialized, "_profile", profile);
            SetObject(serialized, "_densityCompute", LoadCompute("FluidDensity"));
            SetObject(serialized, "_anisotropyCompute", LoadCompute("FluidAnisotropy"));
            SetObject(serialized, "_marchingCubesCompute", LoadCompute("FluidMarchingCubes"));
            SetObject(serialized, "_material", material);
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static ComputeShader LoadCompute(string fileName)
        {
            string path = $"Assets/_Project/Art/Elemental/Compute/{fileName}.compute";
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            if (shader == null)
                throw new InvalidOperationException($"缺少 ComputeShader：{path}");
            return shader;
        }

        private static void CreateSandboxCollider(
            Transform parent,
            string name,
            Vector3 position,
            Vector3 scale,
            Quaternion rotation)
        {
            GameObject gameObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            gameObject.name = name;
            gameObject.transform.SetParent(parent, false);
            gameObject.transform.SetPositionAndRotation(position, rotation);
            gameObject.transform.localScale = scale;
            BoxCollider collider = gameObject.GetComponent<BoxCollider>();
            FluidColliderAuthoring authoring = gameObject.AddComponent<FluidColliderAuthoring>();
            var serialized = new SerializedObject(authoring);
            SetObject(serialized, "_collider", collider);
            SetBool(serialized, "_isDynamic", false);
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void CreateSandboxCamera()
        {
            GameObject cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            Camera camera = cameraObject.AddComponent<Camera>();
            cameraObject.transform.position = new Vector3(11f, 8f, -13f);
            cameraObject.transform.LookAt(new Vector3(0f, 2f, 0f));
            camera.clearFlags = CameraClearFlags.Skybox;
        }

        private static void CreateSandboxLight()
        {
            GameObject lightObject = new GameObject("Directional Light");
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        private static T GetOrAdd<T>(GameObject gameObject) where T : Component
        {
            return gameObject.GetComponent<T>() ?? gameObject.AddComponent<T>();
        }

        private static void EnsureFolder(string path)
        {
            string[] segments = path.Split('/');
            string current = segments[0];
            for (int i = 1; i < segments.Length; i++)
            {
                string next = current + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, segments[i]);
                current = next;
            }
        }

        private static void RequireAsset<T>(string path, List<string> failures) where T : Object
        {
            if (AssetDatabase.LoadAssetAtPath<T>(path) == null)
                failures.Add($"缺少 {typeof(T).Name}：{path}");
        }

        private static T ReloadPersistentAsset<T>(string path) where T : Object
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null
                || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long localId)
                || string.IsNullOrEmpty(guid)
                || localId == 0)
            {
                throw new InvalidOperationException($"Asset 尚未取得稳定 GUID/local fileID：{path}");
            }

            return asset;
        }

        private static void RequireReference(
            SerializedObject serialized,
            string propertyName,
            string label,
            List<string> failures)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null || property.objectReferenceValue == null)
                failures.Add($"{label} 引用为空。");
        }

        private static void RequireReferenceEquals(
            SerializedObject serialized,
            string propertyName,
            Object expected,
            string label,
            List<string> failures)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null)
            {
                failures.Add($"{label} 字段 {propertyName} 不存在。");
                return;
            }

            if (property.objectReferenceValue != expected)
                failures.Add($"{label} 未指向预期的 {expected.name}。");
        }

        private static void SetObject(SerializedObject serialized, string name, Object value)
        {
            SerializedProperty property = serialized.FindProperty(name)
                ?? throw new InvalidOperationException($"{serialized.targetObject.name} 缺少字段 {name}。");
            property.objectReferenceValue = value;
            if (property.objectReferenceValue == null)
                throw new InvalidOperationException($"{serialized.targetObject.name} 无法保存引用 {name}。");
        }

        private static void SetInt(SerializedObject serialized, string name, int value)
        {
            SerializedProperty property = serialized.FindProperty(name)
                ?? throw new InvalidOperationException($"{serialized.targetObject.name} 缺少字段 {name}。");
            property.intValue = value;
        }

        private static void SetFloat(SerializedObject serialized, string name, float value)
        {
            SerializedProperty property = serialized.FindProperty(name)
                ?? throw new InvalidOperationException($"{serialized.targetObject.name} 缺少字段 {name}。");
            property.floatValue = value;
        }

        private static void SetBool(SerializedObject serialized, string name, bool value)
        {
            SerializedProperty property = serialized.FindProperty(name)
                ?? throw new InvalidOperationException($"{serialized.targetObject.name} 缺少字段 {name}。");
            property.boolValue = value;
        }
    }
}
