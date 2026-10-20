using System;
using System.Collections.Generic;
using System.IO;
using Game.Combat;
using Game.Character;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using Game.Rendering;
using Game.Skills;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    /// <summary>通过 Editor API 创建独立实验资产；生产 P7/P8 场景保持原样。</summary>
    [InitializeOnLoad]
    internal static class ElementPerformanceSceneSetup
    {
        internal const string ScenePath = "Assets/_Project/Scenes/ElementWorld_Performance.unity";
        private const string ProfileFolder = "Assets/_Project/ScriptableObjects/ElementField/Performance";
        private const string FluidFolder = "Assets/_Project/ScriptableObjects/ElementField/Fluid/";
        private const string DormantShaderPath =
            "Assets/_Project/Art/Elemental/Shaders/ElementLiquidDormant.shader";
        private static double _nextPoll;
        static ElementPerformanceSceneSetup() { EditorApplication.update += Poll; }

        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + 1;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (Ready("Temp/ElementPerformance/setup.request"))
            {
                File.Delete("Temp/ElementPerformance/setup.request");
                try { Setup(); }
                catch (Exception exception) { GameLog.Error($"[ELEMENT-PERF-SETUP] {exception}", "Editor"); }
            }
            if (Ready("Temp/ElementPerformance/build.request"))
            {
                File.Delete("Temp/ElementPerformance/build.request");
                Build();
            }
            if (Ready("Temp/ElementPerformance/wire-streaming.request"))
            {
                File.Delete("Temp/ElementPerformance/wire-streaming.request");
                try { WireFluidChunkStreaming(); }
                catch (Exception exception)
                {
                    File.WriteAllText("Temp/ElementPerformance/wire-streaming-result.txt", exception.ToString());
                    GameLog.Error($"[ELEMENT-STREAMING-WIRING] {exception}", "Editor");
                }
            }
            if (Ready("Temp/ElementPerformance/fix-material-catalog.request"))
            {
                File.Delete("Temp/ElementPerformance/fix-material-catalog.request");
                try { EnsureStickyInDefaultCatalog(); }
                catch (Exception exception)
                {
                    File.WriteAllText("Temp/ElementPerformance/fix-material-catalog-result.txt", exception.ToString());
                    GameLog.Error($"[ELEMENT-MATERIAL-CATALOG] {exception}", "Editor");
                }
            }
            if (Ready("Temp/ElementPerformance/phase-e-profiles.request"))
            {
                File.Delete("Temp/ElementPerformance/phase-e-profiles.request");
                try { CreatePhaseEProfiles(); }
                catch (Exception exception)
                {
                    File.WriteAllText("Temp/ElementPerformance/phase-e-profiles-result.txt", exception.ToString());
                    GameLog.Error($"[ELEMENT-PHASE-E-PROFILES] {exception}", "Editor");
                }
            }
            if (Ready("Temp/ElementPerformance/phase-f-profiles.request"))
            {
                File.Delete("Temp/ElementPerformance/phase-f-profiles.request");
                try { CreatePhaseFProfiles(); }
                catch (Exception exception)
                {
                    File.WriteAllText("Temp/ElementPerformance/phase-f-profiles-result.txt", exception.ToString());
                    GameLog.Error($"[ELEMENT-PHASE-F-PROFILES] {exception}", "Editor");
                }
            }
        }

        private static bool Ready(string path)
        {
            if (!File.Exists(path)) return false;
            string key = "ElementPerformance.Ready." + path;
            string stamp = File.GetLastWriteTimeUtc(path).Ticks.ToString();
            if (SessionState.GetString(key, string.Empty) != stamp)
            {
                SessionState.SetString(key, stamp);
                SessionState.SetFloat(key + ".time", (float)EditorApplication.timeSinceStartup + 5f);
                AssetDatabase.Refresh();
                return false;
            }
            return EditorApplication.timeSinceStartup >= SessionState.GetFloat(key + ".time", 0f);
        }

        [MenuItem("Tools/Element Performance/Create Dedicated Scene And Profiles")]
        private static void Setup()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
                throw new InvalidOperationException("实验场景已经存在，拒绝重建覆盖。");
            if (!AssetDatabase.IsValidFolder(ProfileFolder))
                AssetDatabase.CreateFolder("Assets/_Project/ScriptableObjects/ElementField", "Performance");
            var profiles = CreateProfiles();
            if (!AssetDatabase.CopyAsset("Assets/_Project/Scenes/PBF_FluidSandbox.unity", ScenePath))
                throw new IOException("复制 Sandbox 失败。");
            Scene previous = SceneManager.GetActiveScene();
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                ElementWorldRuntime world = Find<ElementWorldRuntime>(scene);
                GpuPbfFluidRuntime fluid = Find<GpuPbfFluidRuntime>(scene);
                FluidGameplayOccupancyBridge bridge = Find<FluidGameplayOccupancyBridge>(scene);
                Camera camera = Find<Camera>(scene);
                foreach (GameObject root in scene.GetRootGameObjects())
                    foreach (ElementWorldInitialDeposit deposit in root.GetComponentsInChildren<ElementWorldInitialDeposit>(true))
                        UnityEngine.Object.DestroyImmediate(deposit.gameObject);
                var fluidData = new SerializedObject(fluid);
                Transform interest = (Transform)fluidData.FindProperty("_simulationBoundsCenter").objectReferenceValue;
                interest.position = new Vector3(0, 3, 0);
                fluidData.FindProperty("_simulationBoundsSize").vector3Value = new Vector3(24, 12, 24);
                Transform colliderRoot = (Transform)fluidData.FindProperty("_fluidColliderRoot").objectReferenceValue;
                while (colliderRoot.childCount > 0) UnityEngine.Object.DestroyImmediate(colliderRoot.GetChild(0).gameObject);
                GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
                floor.name = "PerformanceFloor";
                floor.transform.SetParent(colliderRoot, false);
                floor.transform.position = new Vector3(50, -0.5f, 0);
                floor.transform.localScale = new Vector3(140, 1, 40);
                var authoring = floor.AddComponent<FluidColliderAuthoring>();
                Set(authoring, "_collider", floor.GetComponent<BoxCollider>());
                fluidData.ApplyModifiedPropertiesWithoutUndo();
                Set(world, "_profile", Load<ElementWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Fluid/ElementWorldProfile_P7_PBF.asset"));
                ElementWorldExposureSystem exposure = world.GetComponent<ElementWorldExposureSystem>();
                if (exposure == null) exposure = world.gameObject.AddComponent<ElementWorldExposureSystem>();
                Set(exposure, "_liquidOccupancyComponent", bridge);
                var exposureData = new SerializedObject(exposure);
                exposureData.FindProperty("_targetLayers").intValue = 1 << 30;
                exposureData.ApplyModifiedPropertiesWithoutUndo();
                // Sandbox 原有 Water/Poison 表面；补齐 Sticky，全部仍使用生产渲染参数。
                var sticky = world.gameObject.AddComponent<GpuLiquidSurfaceRenderer>();
                EditorUtility.CopySerialized(world.GetComponent<GpuLiquidSurfaceRenderer>(), sticky);
                Set(sticky, "_profile", Load<LiquidRenderProfile>("Assets/_Project/ScriptableObjects/ElementField/Fluid/LiquidRenderProfile_Sticky.asset"));
                Set(sticky, "_material", Load<Material>("Assets/_Project/Art/Elemental/Materials/M_ElementSticky_PBF.mat"));
                var stickyData = new SerializedObject(sticky);
                stickyData.FindProperty("_targetMaterial").intValue = (int)MaterialId.Sticky;
                stickyData.ApplyModifiedPropertiesWithoutUndo();
                camera.transform.position = new Vector3(14, 13, -18);
                camera.transform.LookAt(new Vector3(0, 0, 0));
                camera.allowDynamicResolution = false;
                var targets = new GameObject[80];
                StatusController probe = null;
                for (int i = 0; i < targets.Length; i++)
                {
                    GameObject target = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    target.name = "PerformanceTarget_" + i;
                    target.layer = 30;
                    target.transform.position = i == 0 ? new Vector3(8, 0.9f, 0)
                        : new Vector3(-7 + (i % 10) * 1.5f, 0.9f, -6 + (i / 10) * 1.5f);
                    var health = target.AddComponent<HealthComponent>();
                    var healthData = new SerializedObject(health);
                    healthData.FindProperty("_maxHp").floatValue = 10000;
                    healthData.ApplyModifiedPropertiesWithoutUndo();
                    var status = target.AddComponent<StatusController>();
                    Set(status, "_database", Load<StatusDatabase>("Assets/_Project/ScriptableObjects/Combat/Status/StatusDatabase.asset"));
                    Set(status, "_reactionProfile", Load<ElementReactionProfile>("Assets/_Project/ScriptableObjects/Combat/Status/ElementReactionProfile_Default.asset"));
                    targets[i] = target;
                    if (i == 0) probe = status;
                    else target.SetActive(false);
                }
                var lab = new GameObject("ElementPerformanceLab").AddComponent<ElementWorldPerformanceLab>();
                Set(lab, "_world", world); Set(lab, "_fluid", fluid); Set(lab, "_gameplay", bridge);
                Set(lab, "_exposure", exposure); Set(lab, "_interest", interest); Set(lab, "_camera", camera.transform);
                Set(lab, "_probe", probe); Set(lab, "_profile", profiles[0]);
                var labData = new SerializedObject(lab);
                labData.FindProperty("_productionConfiguration").stringValue = ProductionConfiguration(world, fluid);
                var profileArray = labData.FindProperty("_commandLineProfiles");
                profileArray.arraySize = profiles.Count;
                for (int i = 0; i < profiles.Count; i++) profileArray.GetArrayElementAtIndex(i).objectReferenceValue = profiles[i];
                var targetArray = labData.FindProperty("_targets");
                targetArray.arraySize = targets.Length;
                for (int i = 0; i < targets.Length; i++) targetArray.GetArrayElementAtIndex(i).objectReferenceValue = targets[i];
                labData.ApplyModifiedPropertiesWithoutUndo();
                EditorSceneManager.SaveScene(scene);
                AssetDatabase.SaveAssets();
                GameLog.Info($"[ELEMENT-PERF-SETUP] Created scene and {profiles.Count} profiles", "Editor");
            }
            finally
            {
                SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static List<ElementWorldPerformanceProfile> CreateProfiles()
        {
            var water = Load<LiquidMaterialProfile>("Assets/_Project/ScriptableObjects/ElementField/Fluid/LiquidMaterialProfile_Water.asset");
            var poison = Load<LiquidMaterialProfile>("Assets/_Project/ScriptableObjects/ElementField/Fluid/LiquidMaterialProfile_Poison.asset");
            var sticky = Load<LiquidMaterialProfile>("Assets/_Project/ScriptableObjects/ElementField/Fluid/LiquidMaterialProfile_Sticky.asset");
            var profiles = new List<ElementWorldPerformanceProfile>();
            ElementWorldPerformanceProfile Make(string id, int particles)
            {
                var p = ScriptableObject.CreateInstance<ElementWorldPerformanceProfile>();
                p.ScenarioId = id; p.PrefillParticleTarget = particles;
                p.Materials = new[] { water };
                p.DepositPositions = new Vector3[16];
                for (int i = 0; i < 16; i++) p.DepositPositions[i] = new Vector3(-6 + i % 4 * 4, 0, -6 + i / 4 * 4);
                profiles.Add(p);
                return p;
            }
            Make("A00-empty", 0);
            foreach (int amount in new[] { 1024, 4096, 7168, 8192 }) Make("A01-water-" + amount, amount);
            var interest = Make("A02-interest", 4096);
            interest.Movement = ElementPerformanceMovement.InterestOnly;
            interest.Route = new[] { new Vector3(0, 3, 0), new Vector3(6, 3, 0), new Vector3(0, 3, 0) };
            var camera = Make("A03-camera", 4096);
            camera.Movement = ElementPerformanceMovement.CameraOnly;
            camera.Route = new[] { new Vector3(14, 13, -18), new Vector3(-14, 13, -18), new Vector3(14, 13, -18) };
            var overflow = Make("A04-full-pool", 8192); overflow.MeasureWriteCount = 4;
            var travel = Make("A05-travel-return", 0);
            travel.MeasureSeconds = 600; travel.MeasureWriteCount = 599; travel.MeasureWriteInterval = 1;
            travel.Movement = ElementPerformanceMovement.TravelReturn;
            travel.Route = new[] { new Vector3(0, 3, 0), new Vector3(100, 3, 0), new Vector3(0, 3, 0) };
            var mix = Make("A06-mixed", 6144); mix.Materials = new[] { water, poison, sticky };
            Make("A06-water-control", 6144);
            var fire = Make("A07-fire-contact", 4096);
            fire.MeasureMaterial = MaterialId.Fire; fire.MeasureWriteCount = 16; fire.MeasureWriteInterval = 0.5f;
            fire.MeasurePosition = new Vector3(-6, 0, -6);
            foreach (int targets in new[] { 1, 16, 64, 80 }) Make("A08-targets-" + targets, 4096).TargetCount = targets;
            foreach (StatusKind status in new[] { StatusKind.Burning, StatusKind.Poisoned, StatusKind.Sticky })
                foreach (string condition in new[] { "dry", "water", "full" })
                {
                    var p = Make("A09-" + status + "-" + condition, condition == "full" ? 8192 : 0);
                    p.UseProbe = true; p.ProbeStatus = status; p.MeasureSeconds = 12;
                    p.MeasureWriteCount = condition == "dry" ? 0 : 1;
                }
            var target60 = Make("A01-water-4096-60fps", 4096); target60.FrameRateMode = ElementPerformanceFrameRate.Target60;
            Make("A00-capture-off", 0).CaptureEnabled = false;
            Make("A01-water-4096-capture-off", 4096).CaptureEnabled = false;
            foreach (var p in profiles)
            {
                p.ValidateConfiguration();
                AssetDatabase.CreateAsset(p, "Assets/_Project/ScriptableObjects/ElementField/Performance/" + p.ScenarioId + ".asset");
            }
            return profiles;
        }

        [MenuItem("Tools/Element Performance/Build Windows Development")]
        private static void Build()
        {
            bool previousTiming = PlayerSettings.enableFrameTimingStats;
            try
            {
                Scene previous = SceneManager.GetActiveScene();
                Scene experiment = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
                try
                {
                    var lab = Find<ElementWorldPerformanceLab>(experiment);
                    var gameplay = lab.GetComponent<PhaseFWaterGameplayBenchmark>();
                    if (gameplay == null)
                        gameplay = lab.gameObject.AddComponent<PhaseFWaterGameplayBenchmark>();
                    var gameplayData = new SerializedObject(gameplay);
                    gameplayData.FindProperty("_waterWand").objectReferenceValue =
                        AssetDatabase.LoadAssetAtPath<WandLoadout>(
                            "Assets/_Project/ScriptableObjects/Spell/Programs/Validation/Wand_FluidWater.asset");
                    gameplayData.ApplyModifiedPropertiesWithoutUndo();
                    var fluid = Find<GpuPbfFluidRuntime>(experiment);
                    var isolation = lab.GetComponent<ElementPerformanceSurfaceIsolation>();
                    if (isolation == null) isolation = lab.gameObject.AddComponent<ElementPerformanceSurfaceIsolation>();
                    var surfaces = fluid.GetComponents<GpuLiquidSurfaceRenderer>();
                    var isolationData = new SerializedObject(isolation);
                    var surfacesArray = isolationData.FindProperty("_surfaces");
                    surfacesArray.arraySize = surfaces.Length;
                    for (int i = 0; i < surfaces.Length; i++)
                    {
                        surfacesArray.GetArrayElementAtIndex(i).objectReferenceValue = surfaces[i];
                        if (new SerializedObject(surfaces[i]).FindProperty("_targetMaterial").intValue == (int)MaterialId.Water)
                            isolationData.FindProperty("_water").objectReferenceValue = surfaces[i];
                    }
                    isolationData.ApplyModifiedPropertiesWithoutUndo();
                    Transform colliderRoot = (Transform)new SerializedObject(fluid).FindProperty("_fluidColliderRoot").objectReferenceValue;
                    if (colliderRoot.Find("ProbeIsolationWall") == null)
                    {
                        // 将历史填充水与清洗探针隔开，避免旧水流到探针脚下伪装成满池新写入成功。
                        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        wall.name = "ProbeIsolationWall";
                        SceneManager.MoveGameObjectToScene(wall, experiment);
                        wall.transform.SetParent(colliderRoot, false);
                        wall.transform.position = new Vector3(7.2f, 1.5f, 0);
                        wall.transform.localScale = new Vector3(0.2f, 3f, 20f);
                        Set(wall.AddComponent<FluidColliderAuthoring>(), "_collider", wall.GetComponent<BoxCollider>());
                    }
                    var data = new SerializedObject(lab);
                    data.FindProperty("_productionConfiguration").stringValue = ProductionConfiguration(
                        Find<ElementWorldRuntime>(experiment), fluid);
                    data.ApplyModifiedPropertiesWithoutUndo();
                    EditorSceneManager.SaveScene(experiment);
                }
                finally
                {
                    SceneManager.SetActiveScene(previous);
                    EditorSceneManager.CloseScene(experiment, true);
                }
                PlayerSettings.enableFrameTimingStats = true;
                Directory.CreateDirectory("Builds/ElementPerformance");
                BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath }, target = BuildTarget.StandaloneWindows64,
                    locationPathName = "Builds/ElementPerformance/ElementPerformance.exe", options = BuildOptions.Development
                });
                Directory.CreateDirectory("Temp/ElementPerformance");
                File.WriteAllText("Temp/ElementPerformance/build-result.txt", report.summary.result + "\n" + report.summary.totalErrors);
                GameLog.Info($"[ELEMENT-PERF-BUILD] {report.summary.result}; Errors={report.summary.totalErrors}", "Editor");
            }
            catch (Exception exception) { GameLog.Error($"[ELEMENT-PERF-BUILD] {exception}", "Editor"); }
            finally { PlayerSettings.enableFrameTimingStats = previousTiming; }
        }

        private static T Find<T>(Scene scene) where T : Component
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                T result = root.GetComponentInChildren<T>(true);
                if (result != null) return result;
            }
            throw new InvalidOperationException("实验场景缺少 " + typeof(T).Name);
        }

        [MenuItem("Tools/Element Performance/Wire Fluid Chunk Streaming Scenes")]
        private static void WireFluidChunkStreaming()
        {
            ElementWorldPerformanceProfile speedProbe = EnsurePhaseDSpeedProbe();
            ElementWorldPerformanceProfile sustained = EnsurePhaseDProfile(
                "D01-local-dormancy-sustained", false);
            ElementWorldPerformanceProfile reaction = EnsurePhaseDProfile(
                "D02-local-dormancy-fire-wake", true);
            string[] scenePaths =
            {
                "Assets/_Project/Scenes/PBF_FluidSandbox.unity",
                "Assets/_Project/Scenes/P7_DemoRun.unity",
                "Assets/_Project/Scenes/P8_BossField.unity",
                "Assets/_Project/Scenes/ElementWorld_Performance.unity",
            };
            string[] profilePaths =
            {
                "Assets/_Project/ScriptableObjects/ElementField/Fluid/ElementWorldProfile_P7_PBF.asset",
                "Assets/_Project/ScriptableObjects/World/Boss/ElementField/ElementWorldProfile_P8BossField.asset",
            };

            for (int i = 0; i < profilePaths.Length; i++)
            {
                ElementWorldProfile profile = Load<ElementWorldProfile>(profilePaths[i]);
                var data = new SerializedObject(profile);
                data.FindProperty("_fluidWarmPaddingChunks").intValue = 1;
                data.FindProperty("_fluidArchiveGraceSeconds").floatValue = 2f;
                data.FindProperty("_maximumArchivedFluidChunks").intValue = 512;
                data.FindProperty("_maximumArchivedFluidCellRecords").intValue = 65536;
                data.FindProperty("_maximumPendingFluidWrites").intValue = 256;
                data.FindProperty("_gameplaySpawnReserveParticles").intValue = 512;
                data.FindProperty("_enableLocalFluidDormancy").boolValue = true;
                data.FindProperty("_maximumRestoreParticlesPerFrame").intValue = 128;
                data.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(profile);
            }

            for (int i = 0; i < scenePaths.Length; i++)
                WireScene(scenePaths[i], speedProbe, sustained, reaction);

            AssetDatabase.SaveAssets();
            Directory.CreateDirectory("Temp/ElementPerformance");
            File.WriteAllText("Temp/ElementPerformance/wire-streaming-result.txt",
                "Success\n" + string.Join("\n", scenePaths));
            GameLog.Info("[ELEMENT-STREAMING-WIRING] Sandbox/P7/P8/Performance 接线已保存。", "Editor");
        }

        [MenuItem("Tools/Element Performance/Ensure Sticky In Default Catalog")]
        private static void EnsureStickyInDefaultCatalog()
        {
            const string catalogPath = "Assets/_Project/ScriptableObjects/Materials/MaterialCatalog_Default.asset";
            const string stickyPath = "Assets/_Project/ScriptableObjects/Materials/Material_Sticky.asset";
            UnityEngine.Object catalog = AssetDatabase.LoadMainAssetAtPath(catalogPath);
            UnityEngine.Object sticky = AssetDatabase.LoadMainAssetAtPath(stickyPath);
            if (catalog == null || sticky == null)
                throw new InvalidOperationException("Default Material Catalog 或 Sticky MaterialDefinition 缺失。");

            var serialized = new SerializedObject(catalog);
            SerializedProperty definitions = serialized.FindProperty("_definitions");
            if (definitions == null || !definitions.isArray)
                throw new InvalidOperationException("Material Catalog._definitions 序列化字段缺失。");

            for (int i = 0; i < definitions.arraySize; i++)
            {
                if (definitions.GetArrayElementAtIndex(i).objectReferenceValue == sticky)
                {
                    File.WriteAllText("Temp/ElementPerformance/fix-material-catalog-result.txt",
                        "Success=True\nChanged=False\nSticky 已存在");
                    return;
                }
            }

            int index = definitions.arraySize;
            definitions.InsertArrayElementAtIndex(index);
            definitions.GetArrayElementAtIndex(index).objectReferenceValue = sticky;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            File.WriteAllText("Temp/ElementPerformance/fix-material-catalog-result.txt",
                "Success=True\nChanged=True\nSticky 已写入并保存");
            GameLog.Info("[ELEMENT-MATERIAL-CATALOG] Sticky 已写入默认 Catalog 并保存。", "Editor");
        }

        private static ElementWorldPerformanceProfile EnsurePhaseDProfile(
            string scenarioId, bool fireWake)
        {
            string path = "Assets/_Project/ScriptableObjects/ElementField/Performance/" + scenarioId + ".asset";
            ElementWorldPerformanceProfile profile =
                AssetDatabase.LoadAssetAtPath<ElementWorldPerformanceProfile>(path);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<ElementWorldPerformanceProfile>();
                AssetDatabase.CreateAsset(profile, path);
            }

            LiquidMaterialProfile water = Load<LiquidMaterialProfile>(
                "Assets/_Project/ScriptableObjects/ElementField/Fluid/LiquidMaterialProfile_Water.asset");
            LiquidMaterialProfile poison = Load<LiquidMaterialProfile>(
                "Assets/_Project/ScriptableObjects/ElementField/Fluid/LiquidMaterialProfile_Poison.asset");
            LiquidMaterialProfile sticky = Load<LiquidMaterialProfile>(
                "Assets/_Project/ScriptableObjects/ElementField/Fluid/LiquidMaterialProfile_Sticky.asset");
            profile.name = scenarioId;
            profile.ScenarioId = scenarioId;
            profile.WarmupSeconds = 10f;
            profile.SettleSeconds = 15f;
            profile.MeasureSeconds = fireWake ? 15f : 62f;
            profile.DrainTimeoutSeconds = 8f;
            profile.PrefillParticleTarget = 8192;
            profile.ParticlesPerWrite = 256;
            profile.WriteInterval = .1f;
            profile.MeasureWriteCount = fireWake ? 1 : 40;
            // 1.5 秒间隔给上一小批液体进入 Sleeping 的机会；总提交 18432 粒子当量，超过 Pool 两倍。
            profile.MeasureWriteInterval = fireWake ? .1f : 1.5f;
            profile.MeasureWriteDelay = 1f;
            profile.MeasureMaterial = fireWake ? MaterialId.Fire : MaterialId.Water;
            profile.Materials = fireWake
                ? new[] { water }
                : new[] { water, poison, sticky };
            profile.DepositPositions = new Vector3[16];
            for (int i = 0; i < profile.DepositPositions.Length; i++)
                profile.DepositPositions[i] = new Vector3(
                    -6 + i % 4 * 4, 0, -6 + i / 4 * 4);
            profile.MeasurePosition = fireWake ? new Vector3(-6f, 0f, -6f) : new Vector3(8f, 0f, 0f);
            profile.Radius = 1f;
            profile.InitialVelocity = Vector3.zero;
            profile.SurfaceNormal = Vector3.up;
            profile.Movement = ElementPerformanceMovement.None;
            profile.Route = Array.Empty<Vector3>();
            profile.UseProbe = !fireWake;
            profile.ProbeStatus = StatusKind.Burning;
            profile.TargetCount = 1;
            profile.FrameRateMode = ElementPerformanceFrameRate.Target60;
            profile.MaxWritesPerFrame = 4;
            profile.CaptureEnabled = true;
            profile.ValidateConfiguration();
            EditorUtility.SetDirty(profile);
            return profile;
        }

        private static ElementWorldPerformanceProfile EnsurePhaseDSpeedProbe()
        {
            const string scenarioId = "D00-local-dormancy-speed-probe";
            string path = "Assets/_Project/ScriptableObjects/ElementField/Performance/" + scenarioId + ".asset";
            ElementWorldPerformanceProfile profile =
                AssetDatabase.LoadAssetAtPath<ElementWorldPerformanceProfile>(path);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<ElementWorldPerformanceProfile>();
                AssetDatabase.CreateAsset(profile, path);
            }
            LiquidMaterialProfile water = Load<LiquidMaterialProfile>(
                "Assets/_Project/ScriptableObjects/ElementField/Fluid/LiquidMaterialProfile_Water.asset");
            profile.name = scenarioId;
            profile.ScenarioId = scenarioId;
            profile.WarmupSeconds = 3f;
            profile.SettleSeconds = 8f;
            profile.MeasureSeconds = 3f;
            profile.DrainTimeoutSeconds = 5f;
            profile.PrefillParticleTarget = 8192;
            profile.ParticlesPerWrite = 256;
            profile.WriteInterval = .05f;
            profile.MeasureWriteCount = 0;
            profile.Materials = new[] { water };
            profile.DepositPositions = new Vector3[16];
            for (int i = 0; i < profile.DepositPositions.Length; i++)
                profile.DepositPositions[i] = new Vector3(
                    -6 + i % 4 * 4, 0, -6 + i / 4 * 4);
            profile.MeasureMaterial = MaterialId.Water;
            profile.MeasurePosition = Vector3.zero;
            profile.Radius = 1f;
            profile.SurfaceNormal = Vector3.up;
            profile.Movement = ElementPerformanceMovement.None;
            profile.Route = Array.Empty<Vector3>();
            profile.TargetCount = 0;
            profile.FrameRateMode = ElementPerformanceFrameRate.Target60;
            profile.MaxWritesPerFrame = 4;
            profile.CaptureEnabled = true;
            profile.ValidateConfiguration();
            EditorUtility.SetDirty(profile);
            return profile;
        }

        [MenuItem("Tools/Element Performance/Create Phase E Profiles")]
        private static void CreatePhaseEProfiles()
        {
            ElementWorldPerformanceProfile template = AssetDatabase.LoadAssetAtPath<ElementWorldPerformanceProfile>(
                "Assets/_Project/ScriptableObjects/ElementField/Performance/D02-local-dormancy-fire-wake.asset");
            if (template == null) throw new InvalidOperationException("缺少 D02 性能 Profile 模板。");
            string[] ids = { "E00-overlap-sources", "E01-separated-sources", "E02-offscreen-reaction", "E03-camera-only", "E04-sustained-pressure" };
            for (int index = 0; index < ids.Length; index++)
            {
                string path = "Assets/_Project/ScriptableObjects/ElementField/Performance/" + ids[index] + ".asset";
                ElementWorldPerformanceProfile profile = AssetDatabase.LoadAssetAtPath<ElementWorldPerformanceProfile>(path);
                if (profile == null) { profile = UnityEngine.Object.Instantiate(template); AssetDatabase.CreateAsset(profile, path); }
                profile.name = ids[index]; profile.ScenarioId = ids[index];
                profile.EnableWorldActivityExpansion = true;
                profile.ActivitySourcePositions = index == 0
                    ? new[] { Vector3.zero, Vector3.right, Vector3.right * 2 }
                    : new[] { Vector3.zero, Vector3.right * 64 };
                profile.RemoveActivitySourcesAtMeasure = index == 2;
                profile.MeasurePosition = index == 2 ? Vector3.right * 64 : Vector3.zero;
                profile.MeasureMaterial = index == 2 ? MaterialId.Fire : MaterialId.Water;
                profile.UseProbe = false;
                profile.MeasureWriteCount = index == 4 ? 40 : 1;
                profile.MeasureWriteInterval = index == 4 ? 1.5f : .1f;
                profile.MeasureWriteDelay = 1f;
                profile.MeasureSeconds = index == 4 ? 62f : 15f;
                profile.Movement = index == 3 ? ElementPerformanceMovement.CameraOnly : ElementPerformanceMovement.None;
                profile.Route = index == 3 ? new[] { new Vector3(0, 12, -18), new Vector3(64, 12, -18) } : Array.Empty<Vector3>();
                profile.TargetCount = 0;
                profile.ValidateConfiguration();
                EditorUtility.SetDirty(profile);
            }
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            SerializedObject lab = new SerializedObject(Find<ElementWorldPerformanceLab>(scene));
            SerializedProperty profiles = lab.FindProperty("_commandLineProfiles");
            var references = new List<UnityEngine.Object>(profiles.arraySize + ids.Length);
            for (int i = 0; i < profiles.arraySize; i++)
                if (profiles.GetArrayElementAtIndex(i).objectReferenceValue != null)
                    references.Add(profiles.GetArrayElementAtIndex(i).objectReferenceValue);
            for (int i = 0; i < ids.Length; i++)
            {
                UnityEngine.Object candidate = AssetDatabase.LoadAssetAtPath<ElementWorldPerformanceProfile>(
                    "Assets/_Project/ScriptableObjects/ElementField/Performance/" + ids[i] + ".asset");
                if (!references.Contains(candidate)) references.Add(candidate);
            }
            profiles.arraySize = references.Count;
            for (int i = 0; i < references.Count; i++)
                profiles.GetArrayElementAtIndex(i).objectReferenceValue = references[i];
            lab.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
            File.WriteAllText("Temp/ElementPerformance/phase-e-profiles-result.txt", "Succeeded");
        }

        [MenuItem("Tools/Element Performance/Create Fluid Profiles")]
        private static void CreatePhaseFProfiles()
        {
            ElementWorldPerformanceProfile template = AssetDatabase.LoadAssetAtPath<ElementWorldPerformanceProfile>(
                "Assets/_Project/ScriptableObjects/ElementField/Performance/D01-local-dormancy-sustained.asset");
            if (template == null) throw new InvalidOperationException("缺少 D01 性能 Profile 模板。");
            const string id = "F00-audit";
            string path = "Assets/_Project/ScriptableObjects/ElementField/Performance/" + id + ".asset";
            ElementWorldPerformanceProfile profile =
                AssetDatabase.LoadAssetAtPath<ElementWorldPerformanceProfile>(path);
            if (profile == null)
            {
                profile = UnityEngine.Object.Instantiate(template);
                AssetDatabase.CreateAsset(profile, path);
            }
            profile.name = id;
            profile.ScenarioId = id;
            // 守恒基准只使用 Water，隔离 Water+Sticky 合法反应；反应总账要等 GPU
            // 回传实际命中量后再验，不能用命令上限替代执行结果。
            profile.Materials = new[] { Load<LiquidMaterialProfile>(
                "Assets/_Project/ScriptableObjects/ElementField/Fluid/LiquidMaterialProfile_Water.asset") };
            profile.MeasureMaterial = MaterialId.Water;
            profile.UseProbe = false;
            profile.TargetCount = 0;
            profile.RequireFreshMaterialAuditAtDrain = true;
            profile.ValidateConfiguration();
            EditorUtility.SetDirty(profile);

            const string reactionId = "F01-reaction-audit";
            string reactionPath = "Assets/_Project/ScriptableObjects/ElementField/Performance/" + reactionId + ".asset";
            ElementWorldPerformanceProfile reactionProfile =
                AssetDatabase.LoadAssetAtPath<ElementWorldPerformanceProfile>(reactionPath);
            if (reactionProfile == null)
            {
                reactionProfile = UnityEngine.Object.Instantiate(template);
                AssetDatabase.CreateAsset(reactionProfile, reactionPath);
            }
            reactionProfile.name = reactionId;
            reactionProfile.ScenarioId = reactionId;
            // 保留 D01 的 Water/Poison/Sticky 与 Fire 写入；Drain 必须等 GPU 实际反应账本追平。
            reactionProfile.Materials = template.Materials;
            reactionProfile.UseProbe = false;
            reactionProfile.TargetCount = 0;
            reactionProfile.RequireFreshMaterialAuditAtDrain = true;
            reactionProfile.ValidateConfiguration();
            EditorUtility.SetDirty(reactionProfile);

            ElementWorldPerformanceProfile multiSource = CreatePhaseFProfile(template, "F02-multi-source");
            CopyReactionLoad(reactionProfile, multiSource);
            multiSource.EnableWorldActivityExpansion = true;
            // 保留最初失败的重叠负载；移动来源和落点的隔离实验不能代替原场景回归。
            multiSource.ActivitySourcePositions = CreateSourceGrid(12, 4, 16f, Vector3.zero);
            multiSource.ProjectileActivitySourcePositions = CreateSourceGrid(6, 3, 16f, new Vector3(8f, 0f, 8f));
            multiSource.MeasurePosition = new Vector3(8f, 0f, 0f);
            multiSource.Movement = ElementPerformanceMovement.TravelReturn;
            multiSource.Route = new[]
            {
                new Vector3(-48f, 12f, -48f), new Vector3(48f, 12f, -48f),
                new Vector3(48f, 12f, 48f), new Vector3(-48f, 12f, 48f),
                new Vector3(-48f, 12f, -48f)
            };
            multiSource.MoveSpeed = 8f;
            multiSource.DrainTimeoutSeconds = 15f;
            multiSource.RequireFreshMaterialAuditAtDrain = true;
            multiSource.ValidateConfiguration();
            EditorUtility.SetDirty(multiSource);

            ElementWorldPerformanceProfile soak = CreatePhaseFProfile(template, "F03-soak");
            CopyReactionLoad(reactionProfile, soak);
            soak.WarmupSeconds = 10f;
            soak.SettleSeconds = 15f;
            soak.MeasureSeconds = 1800f;
            soak.DrainTimeoutSeconds = 30f;
            soak.MeasureWriteCount = 400;
            soak.MeasureWriteInterval = 1.5f;
            soak.EnableWorldActivityExpansion = true;
            soak.ActivitySourcePositions = CreateSourceGrid(12, 4, 16f, Vector3.zero);
            soak.ProjectileActivitySourcePositions = CreateSourceGrid(6, 3, 16f, new Vector3(8f, 0f, 8f));
            soak.MeasurePosition = new Vector3(8f, 0f, 0f);
            soak.Movement = ElementPerformanceMovement.TravelReturn;
            soak.Route = CreateSoakRoute(120, 64f);
            soak.MoveSpeed = 8f;
            soak.RequireFreshMaterialAuditAtDrain = true;
            soak.ValidateConfiguration();
            EditorUtility.SetDirty(soak);

            ElementWorldPerformanceProfile overBudget = CreatePhaseFProfile(template, "F04-over-budget");
            CopyReactionLoad(reactionProfile, overBudget);
            overBudget.MeasureSeconds = 30f;
            overBudget.DrainTimeoutSeconds = 20f;
            overBudget.MeasureWriteCount = 64;
            overBudget.MeasureWriteInterval = .1f;
            overBudget.EnableWorldActivityExpansion = true;
            overBudget.ActivitySourcePositions = CreateSourceGrid(24, 6, 16f, Vector3.zero);
            overBudget.ProjectileActivitySourcePositions = CreateSourceGrid(12, 4, 16f, new Vector3(8f, 0f, 8f));
            overBudget.Movement = ElementPerformanceMovement.None;
            overBudget.Route = Array.Empty<Vector3>();
            overBudget.RequireFreshMaterialAuditAtDrain = true;
            overBudget.ValidateConfiguration();
            EditorUtility.SetDirty(overBudget);

            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            SerializedObject lab = new SerializedObject(Find<ElementWorldPerformanceLab>(scene));
            AddUniqueReference(lab.FindProperty("_commandLineProfiles"), profile);
            AddUniqueReference(lab.FindProperty("_commandLineProfiles"), reactionProfile);
            AddUniqueReference(lab.FindProperty("_commandLineProfiles"), multiSource);
            AddUniqueReference(lab.FindProperty("_commandLineProfiles"), soak);
            AddUniqueReference(lab.FindProperty("_commandLineProfiles"), overBudget);
            lab.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new IOException("保存 Phase F Profile 引用失败。");
            AssetDatabase.SaveAssets();
            File.WriteAllText("Temp/ElementPerformance/phase-f-profiles-result.txt", "Succeeded");
        }

        private static ElementWorldPerformanceProfile CreatePhaseFProfile(
            ElementWorldPerformanceProfile template, string id)
        {
            string path = "Assets/_Project/ScriptableObjects/ElementField/Performance/" + id + ".asset";
            ElementWorldPerformanceProfile profile =
                AssetDatabase.LoadAssetAtPath<ElementWorldPerformanceProfile>(path);
            if (profile == null)
            {
                profile = UnityEngine.Object.Instantiate(template);
                AssetDatabase.CreateAsset(profile, path);
            }
            profile.name = id;
            profile.ScenarioId = id;
            return profile;
        }

        private static void CopyReactionLoad(
            ElementWorldPerformanceProfile source, ElementWorldPerformanceProfile target)
        {
            target.Materials = source.Materials;
            target.MeasureMaterial = source.MeasureMaterial;
            target.UseProbe = false;
            target.TargetCount = 0;
            target.FrameRateMode = ElementPerformanceFrameRate.Target60;
        }

        private static Vector3[] CreateSourceGrid(int count, int columns, float spacing, Vector3 offset)
        {
            var points = new Vector3[count];
            float centerX = (columns - 1) * spacing * .5f;
            int rows = Mathf.CeilToInt(count / (float)columns);
            float centerZ = (rows - 1) * spacing * .5f;
            for (int i = 0; i < count; i++)
                points[i] = offset + new Vector3((i % columns) * spacing - centerX, 0f,
                    (i / columns) * spacing - centerZ);
            return points;
        }

        private static Vector3[] CreateSoakRoute(int pointCount, float extent)
        {
            var points = new Vector3[pointCount];
            for (int i = 0; i < pointCount; i++)
            {
                int corner = i & 3;
                points[i] = corner == 0 ? new Vector3(-extent, 12f, -extent)
                    : corner == 1 ? new Vector3(extent, 12f, -extent)
                    : corner == 2 ? new Vector3(extent, 12f, extent)
                    : new Vector3(-extent, 12f, extent);
            }
            return points;
        }

        private static void WireScene(
            string path,
            ElementWorldPerformanceProfile speedProbe,
            ElementWorldPerformanceProfile sustained,
            ElementWorldPerformanceProfile reaction)
        {
            Scene scene = SceneManager.GetSceneByPath(path);
            bool openedByTool = !scene.IsValid() || !scene.isLoaded;
            if (openedByTool) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                ElementWorldRuntime world = Find<ElementWorldRuntime>(scene);
                GpuPbfFluidRuntime fluid = Find<GpuPbfFluidRuntime>(scene);
                _ = Find<FluidGameplayOccupancyBridge>(scene);
                ElementWorldExposureSystem exposure = FindOptional<ElementWorldExposureSystem>(scene);
                FluidChunkStreamingRuntime streaming =
                    world.GetComponent<FluidChunkStreamingRuntime>()
                    ?? world.gameObject.AddComponent<FluidChunkStreamingRuntime>();
                Set(world, "_fluidChunkStreamingRuntime", streaming);
                if (exposure != null)
                    Set(exposure, "_liquidOccupancyComponent", streaming);
                ElementWorldPerformanceLab lab = FindOptional<ElementWorldPerformanceLab>(scene);
                if (lab != null)
                {
                    var labData = new SerializedObject(lab);
                    SerializedProperty profiles = labData.FindProperty("_commandLineProfiles");
                    AddUniqueReference(profiles, speedProbe);
                    AddUniqueReference(profiles, sustained);
                    AddUniqueReference(profiles, reaction);
                    labData.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(lab);
                }
                // Surface 与 PBF Buffer 属于同一 Runtime Host；P7/P8 的 World 与 Fluid 并非同一对象。
                // Dormant Renderer 跟随 Surface Host，避免依赖某个测试场景碰巧把两者挂在一起。
                GpuLiquidSurfaceRenderer[] gpuSurfaces = fluid.GetComponents<GpuLiquidSurfaceRenderer>();
                DormantLiquidCellRenderer[] dormant = fluid.GetComponents<DormantLiquidCellRenderer>();
                Shader dormantShader = Load<Shader>(DormantShaderPath);
                for (int surfaceIndex = 0; surfaceIndex < gpuSurfaces.Length; surfaceIndex++)
                {
                    var sourceData = new SerializedObject(gpuSurfaces[surfaceIndex]);
                    int materialId = sourceData.FindProperty("_targetMaterial").intValue;
                    DormantLiquidCellRenderer target = null;
                    for (int dormantIndex = 0; dormantIndex < dormant.Length; dormantIndex++)
                    {
                        var existingData = new SerializedObject(dormant[dormantIndex]);
                        if (existingData.FindProperty("_targetMaterial").intValue == materialId)
                        {
                            target = dormant[dormantIndex];
                            break;
                        }
                    }
                    if (target == null) target = fluid.gameObject.AddComponent<DormantLiquidCellRenderer>();
                    var targetData = new SerializedObject(target);
                    targetData.FindProperty("_worldRuntime").objectReferenceValue = world;
                    targetData.FindProperty("_sourceLiquidMaterial").objectReferenceValue =
                        sourceData.FindProperty("_material").objectReferenceValue;
                    targetData.FindProperty("_dormantShader").objectReferenceValue = dormantShader;
                    targetData.FindProperty("_targetMaterial").intValue = materialId;
                    targetData.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(target);
                }
                EditorUtility.SetDirty(streaming);
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene))
                    throw new IOException("保存 Streaming 接线失败：" + path);
            }
            finally
            {
                // 只关闭本工具打开的场景，保持开发者原 Scene Setup 与当前编辑目标。
                if (openedByTool && scene.IsValid() && scene.isLoaded)
                    EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void AddUniqueReference(SerializedProperty array, UnityEngine.Object value)
        {
            for (int i = 0; i < array.arraySize; i++)
                if (array.GetArrayElementAtIndex(i).objectReferenceValue == value)
                    return;
            int index = array.arraySize;
            array.InsertArrayElementAtIndex(index);
            array.GetArrayElementAtIndex(index).objectReferenceValue = value;
        }
        private static T FindOptional<T>(Scene scene) where T : Component
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                T result = root.GetComponentInChildren<T>(true);
                if (result != null) return result;
            }
            return null;
        }
        private static string ProductionConfiguration(ElementWorldRuntime world, GpuPbfFluidRuntime fluid)
        {
            UnityEngine.Object worldProfile = new SerializedObject(world).FindProperty("_profile").objectReferenceValue;
            UnityEngine.Object fluidProfile = new SerializedObject(fluid).FindProperty("_profile").objectReferenceValue;
            string result = "{\"world\":" + JsonUtility.ToJson(worldProfile)
                + ",\"fluid\":" + JsonUtility.ToJson(fluidProfile) + ",\"materials\":[";
            string[] names = { "Water", "Poison", "Sticky" };
            for (int i = 0; i < names.Length; i++)
            {
                if (i > 0) result += ",";
                result += JsonUtility.ToJson(Load<LiquidMaterialProfile>("Assets/_Project/ScriptableObjects/ElementField/Fluid/LiquidMaterialProfile_" + names[i] + ".asset"));
            }
            return result + "]}";
        }
        private static T Load<T>(string path) where T : UnityEngine.Object => AssetDatabase.LoadAssetAtPath<T>(path)
            ?? throw new InvalidOperationException("缺少实验依赖 " + path);
        private static void Set(UnityEngine.Object target, string property, UnityEngine.Object value)
        {
            var data = new SerializedObject(target);
            SerializedProperty field = data.FindProperty(property) ?? throw new InvalidOperationException(property);
            field.objectReferenceValue = value;
            data.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
