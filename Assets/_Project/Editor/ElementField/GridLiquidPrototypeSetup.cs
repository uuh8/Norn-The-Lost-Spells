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
    /// <summary>
    /// 在独立场景创建 Grid Liquid 对照实验；所有引用由 Editor API 写入，Unity 自己生成 GUID。
    /// 不更动当前 P7 生产配置，也不自动进入 Play 或运行 Benchmark。
    /// </summary>
    [InitializeOnLoad]
    internal static class GridLiquidPrototypeSetup
    {
        private const string Request = "Temp/ElementPerformance/grid-liquid-setup.request";
        private const string Result = "Temp/ElementPerformance/grid-liquid-setup-result.txt";
        private const string ComparisonRequest =
            "Temp/ElementPerformance/grid-liquid-p7-comparison.request";
        private const string ComparisonResult =
            "Temp/ElementPerformance/grid-liquid-p7-comparison-result.txt";
        private const string PartialRequest =
            "Temp/ElementPerformance/grid-liquid-partial-scene.request";
        private const string PartialResult =
            "Temp/ElementPerformance/grid-liquid-partial-scene-result.txt";
        private const string DenseRequest =
            "Temp/ElementPerformance/grid-liquid-dense-scene.request";
        private const string DenseResult =
            "Temp/ElementPerformance/grid-liquid-dense-scene-result.txt";
        private const string Folder = "Assets/_Project/ScriptableObjects/ElementField/Liquids";
        private const string SceneFolder = "Assets/_Project/Scenes/Prototypes";
        private const string ProductionScene = "Assets/_Project/Scenes/P7_DemoRun.unity";
        private const string GridComparisonScene =
            "Assets/_Project/Scenes/Prototypes/P7_DemoRun_GridLiquid.unity";
        private const string ComparisonSuffix = "_P7Comparison.asset";
        private const string BaseProfile =
            "Assets/_Project/ScriptableObjects/ElementField/Fluid/ElementWorldProfile_P7_PBF.asset";
        private const string BaseRouting =
            "Assets/_Project/ScriptableObjects/Materials/MaterialSimulationRouting_Default.asset";
        private const string PrototypeRouting = "Assets/_Project/ScriptableObjects/ElementField/Liquids/MaterialSimulationRouting_Prototype.asset";
        private const string PrototypeWorld = "Assets/_Project/ScriptableObjects/ElementField/Liquids/ElementWorldProfile_GridPrototype.asset";
        private const string PrototypeLiquid = "Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_Prototype.asset";
        private const string DormantShader =
            "Assets/_Project/Art/Elemental/Shaders/ElementLiquidDormant.shader";
        private static double _nextPoll;

        static GridLiquidPrototypeSetup() { EditorApplication.update += Poll; }

        [MenuItem("Tools/Element World/Create Grid Liquid Prototype Scenes")]
        private static void MenuSetup()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                GameLog.Warn("请先退出 Play Mode，再生成 Grid Liquid 原型场景。", "Editor");
                return;
            }
            try { Setup(); }
            catch (Exception exception) { GameLog.Error(exception.ToString(), "Editor"); }
        }

        [MenuItem("Tools/Element World/Refresh P7 Grid Liquid Comparison")]
        private static void MenuComparison()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                GameLog.Warn("请先退出 Play Mode，再更新 P7 Grid 对照场景。", "Editor");
                return;
            }
            try { RefreshP7Comparison(); }
            catch (Exception exception) { GameLog.Error(exception.ToString(), "Editor"); }
        }

        [MenuItem("Tools/Element World/Create Partial Surface Prototype")]
        private static void MenuPartialSurface()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                GameLog.Warn("请先退出 Play Mode，再生成部分水面原型。", "Editor");
                return;
            }
            try { RefreshPartialSurface(); }
            catch (Exception exception) { GameLog.Error(exception.ToString(), "Editor"); }
        }

        private static void RefreshPartialSurface()
        {
            // 单独建场景，不重调 Prototype Profile 或重写已经调整过的其他场景。
            ElementWorldProfile world = AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(PrototypeWorld);
            LiquidWorldProfile liquid = AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>(PrototypeLiquid);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(DormantShader);
            if (world == null || liquid == null || shader == null)
                throw new InvalidOperationException("请先生成 Grid Liquid 原型配置。");
            EnsureFolder(SceneFolder);
            BuildPartialSurfaceScene(world, liquid, shader);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        [MenuItem("Tools/Element World/Create Dense Lake Prototype")]
        private static void MenuDenseLake()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            try { RefreshDenseLake(); }
            catch (Exception exception) { GameLog.Error(exception.ToString(), "Editor"); }
        }

        private static void RefreshDenseLake()
        {
            ElementWorldProfile world = AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(PrototypeWorld);
            LiquidWorldProfile source = AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>(PrototypeLiquid);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(DormantShader);
            if (world == null || source == null || shader == null)
                throw new InvalidOperationException("请先生成 Grid Liquid 原型配置。");
            string profilePath = "Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_DenseLake.asset";
            LiquidWorldProfile liquid = AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>(profilePath);
            if (liquid == null)
            {
                // 深水容量与活动预算独立：只增加稳定 Owner，不增加每 Tick 的工作量。
                liquid = UnityEngine.Object.Instantiate(source);
                liquid.name = "LiquidWorldProfile_DenseLake";
                AssetDatabase.CreateAsset(liquid, profilePath);
                Assign(liquid, "_maximumStableRegions", 128);
                Assign(liquid, "_maximumStableSurfacePatches", 2048);
            }
            BuildDenseLakeScene(world, liquid, shader);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private static void RefreshP7Comparison()
        {
            // 单独更新对照，不重建原型湖、修改原型参数或触发任何 Play／Benchmark。
            EnsureFolder(Folder);
            EnsureFolder(SceneFolder);
            var world = AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(PrototypeWorld);
            var routing = AssetDatabase.LoadAssetAtPath<MaterialSimulationRoutingProfile>(PrototypeRouting);
            var liquid = AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>(PrototypeLiquid);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(DormantShader);
            if (world == null || routing == null || liquid == null || shader == null)
                throw new InvalidOperationException("先生成 Grid Liquid 原型配置，再刷新 P7 对照场景。");
            BuildProductionComparison(ComparisonWorldProfile(world, routing),
                ComparisonLiquidProfile(liquid), shader);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + 1;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating
                || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            if (File.Exists(ComparisonRequest))
            {
                // 对照场景单独落盘；不要借用 Setup 请求去重调全部原型参数。
                File.Delete(ComparisonRequest);
                try { RefreshP7Comparison(); File.WriteAllText(ComparisonResult, "OK"); }
                catch (Exception exception)
                { File.WriteAllText(ComparisonResult, exception.ToString()); }
                return;
            }
            if (File.Exists(PartialRequest))
            {
                File.Delete(PartialRequest);
                try { RefreshPartialSurface(); File.WriteAllText(PartialResult, "OK"); }
                catch (Exception exception)
                { File.WriteAllText(PartialResult, exception.ToString()); }
                return;
            }
            if (File.Exists(DenseRequest))
            {
                File.Delete(DenseRequest);
                try { RefreshDenseLake(); File.WriteAllText(DenseResult, "OK"); }
                catch (Exception exception)
                { File.WriteAllText(DenseResult, exception.ToString()); }
                return;
            }
            if (!File.Exists(Request)) return;
            File.Delete(Request);
            try { Setup(); File.WriteAllText(Result, "OK"); }
            catch (Exception exception) { File.WriteAllText(Result, exception.ToString()); }
        }

        private static void Setup()
        {
            EnsureFolder(Folder);
            EnsureFolder(SceneFolder);
            LiquidBehaviorProfile water = Behavior("Water", MaterialId.Water, 255, 40, true);
            LiquidBehaviorProfile poison = Behavior("Poison", MaterialId.Poison, 128, 28, true);
            LiquidBehaviorProfile sticky = Behavior("Sticky", MaterialId.Sticky, 32, 6, true);
            MaterialSimulationRoutingProfile routing = Routing();
            ElementWorldProfile worldProfile = WorldProfile(routing);
            LiquidWorldProfile liquidProfile = LiquidProfile(water, poison, sticky);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(DormantShader);
            if (shader == null) throw new InvalidOperationException("缺少已存在的 Dormant 液体 Shader。");
            BuildScene(false, worldProfile, liquidProfile, shader);
            BuildScene(true, worldProfile, liquidProfile, shader);
            BuildPartialSurfaceScene(worldProfile, liquidProfile, shader);
            BuildSectorScene(worldProfile, liquidProfile, shader);
            TuneSectorScene();
            TuneExistingReservoir();
            // 对照关卡拥有独立可调的配置副本；原型湖泊调参不应暗中改变 P7 的基线。
            ElementWorldProfile comparisonWorld = ComparisonWorldProfile(worldProfile, routing);
            LiquidWorldProfile comparisonLiquid = ComparisonLiquidProfile(liquidProfile);
            BuildProductionComparison(comparisonWorld, comparisonLiquid, shader);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private static T ComparisonAsset<T>(T source, string path) where T : ScriptableObject
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null) return asset;
            if (source == null) throw new InvalidOperationException("缺少对照配置模板：" + path);
            asset = UnityEngine.Object.Instantiate(source);
            asset.name = Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        private static ElementWorldProfile ComparisonWorldProfile(ElementWorldProfile prototype,
            MaterialSimulationRoutingProfile prototypeRouting)
        {
            var routing = ComparisonAsset(prototypeRouting,
                "Assets/_Project/ScriptableObjects/ElementField/Liquids/MaterialSimulationRouting" + ComparisonSuffix);
            var world = ComparisonAsset(prototype,
                "Assets/_Project/ScriptableObjects/ElementField/Liquids/ElementWorldProfile" + ComparisonSuffix);
            var data = new SerializedObject(world);
            data.FindProperty("_materialSimulationRouting").objectReferenceValue = routing;
            data.FindProperty("_waterSimulationMode").intValue = (int)WaterSimulationMode.GridLiquid;
            data.FindProperty("_enableFluidChunkStreaming").boolValue = false;
            data.ApplyModifiedPropertiesWithoutUndo();
            return world;
        }

        private static LiquidWorldProfile ComparisonLiquidProfile(LiquidWorldProfile prototype)
        {
            var liquid = ComparisonAsset(prototype, "Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile" + ComparisonSuffix);
            var data = new SerializedObject(liquid);
            SerializedProperty materials = data.FindProperty("_materials");
            materials.arraySize = prototype.MaterialCount;
            for (int i = 0; i < prototype.MaterialCount; i++)
            {
                LiquidBehaviorProfile behavior = prototype.GetMaterial(i);
                materials.GetArrayElementAtIndex(i).objectReferenceValue = ComparisonAsset(behavior,
                    "Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidBehavior_" + behavior.Material + ComparisonSuffix);
            }
            data.ApplyModifiedPropertiesWithoutUndo();
            return liquid;
        }

        private static LiquidBehaviorProfile Behavior(string label, MaterialId material,
            int down, int side, bool stable)
        {
            string path = "Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidBehavior_" + label + ".asset";
            var asset = AssetDatabase.LoadAssetAtPath<LiquidBehaviorProfile>(path);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
                AssetDatabase.CreateAsset(asset, path);
            }
            var data = new SerializedObject(asset);
            data.FindProperty("_material").intValue = (int)material;
            data.FindProperty("_maximumDownFlowPerTick").intValue = down;
            data.FindProperty("_maximumSideFlowPerTick").intValue = side;
            data.FindProperty("_allowsStableRegion").boolValue = stable;
            data.ApplyModifiedPropertiesWithoutUndo();
            return asset;
        }

        private static MaterialSimulationRoutingProfile Routing()
        {
            string path = "Assets/_Project/ScriptableObjects/ElementField/Liquids/MaterialSimulationRouting_Prototype.asset";
            var asset = AssetDatabase.LoadAssetAtPath<MaterialSimulationRoutingProfile>(path);
            if (asset == null)
            {
                var source = AssetDatabase.LoadAssetAtPath<MaterialSimulationRoutingProfile>(BaseRouting);
                if (source == null) throw new InvalidOperationException("缺少生产路由模板。");
                asset = UnityEngine.Object.Instantiate(source);
                AssetDatabase.CreateAsset(asset, path);
            }
            var data = new SerializedObject(asset);
            SerializedProperty routes = data.FindProperty("_routes");
            routes.arraySize = 3;
            SetRoute(routes, 0, MaterialId.Fire, MaterialSimulationBackendKind.ElementCell);
            SetRoute(routes, 1, MaterialId.Poison, MaterialSimulationBackendKind.GridLiquid);
            SetRoute(routes, 2, MaterialId.Sticky, MaterialSimulationBackendKind.GridLiquid);
            data.ApplyModifiedPropertiesWithoutUndo();
            return asset;
        }

        private static void SetRoute(SerializedProperty routes, int index,
            MaterialId material, MaterialSimulationBackendKind backend)
        {
            SerializedProperty route = routes.GetArrayElementAtIndex(index);
            route.FindPropertyRelative("_material").intValue = (int)material;
            route.FindPropertyRelative("_backend").intValue = (int)backend;
        }

        private static ElementWorldProfile WorldProfile(MaterialSimulationRoutingProfile routing)
        {
            string path = "Assets/_Project/ScriptableObjects/ElementField/Liquids/ElementWorldProfile_GridPrototype.asset";
            var asset = AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(path);
            if (asset == null)
            {
                var source = AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(BaseProfile);
                if (source == null) throw new InvalidOperationException("缺少生产 Element World 模板。");
                asset = UnityEngine.Object.Instantiate(source);
                AssetDatabase.CreateAsset(asset, path);
            }
            var data = new SerializedObject(asset);
            data.FindProperty("_waterSimulationMode").intValue = (int)WaterSimulationMode.GridLiquid;
            data.FindProperty("_enableFluidChunkStreaming").boolValue = false;
            data.FindProperty("_materialSimulationRouting").objectReferenceValue = routing;
            data.FindProperty("_cellSize").floatValue = .25f;
            data.ApplyModifiedPropertiesWithoutUndo();
            return asset;
        }

        private static LiquidWorldProfile LiquidProfile(LiquidBehaviorProfile water,
            LiquidBehaviorProfile poison, LiquidBehaviorProfile sticky)
        {
            string path = "Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_Prototype.asset";
            var asset = AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>(path);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<LiquidWorldProfile>();
                AssetDatabase.CreateAsset(asset, path);
            }
            var data = new SerializedObject(asset);
            SerializedProperty materials = data.FindProperty("_materials");
            materials.arraySize = 3;
            materials.GetArrayElementAtIndex(0).objectReferenceValue = water;
            materials.GetArrayElementAtIndex(1).objectReferenceValue = poison;
            materials.GetArrayElementAtIndex(2).objectReferenceValue = sticky;
            data.FindProperty("_enableAutoStableDetection").boolValue = true;
            data.FindProperty("_maximumImmediateExpansionCells").intValue = 1024;
            data.FindProperty("_maximumImmediateExpansionSurfaceChunks").intValue = 4;
            data.FindProperty("_maximumStableSurfacePatches").intValue = 1024;
            data.ApplyModifiedPropertiesWithoutUndo();
            return asset;
        }

        private static void BuildScene(bool reservoir, ElementWorldProfile worldProfile,
            LiquidWorldProfile liquidProfile, Shader shader)
        {
            string path = SceneFolder + (reservoir
                ? "/LiquidReservoirPrototype.unity" : "/LiquidWorldPrototype.unity");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) != null) return;
            Scene previous = SceneManager.GetActiveScene();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                Camera camera = Camera();
                Light();
                var root = new GameObject("Grid Liquid World");
                ElementFieldSolidBaker baker = root.AddComponent<ElementFieldSolidBaker>();
                Assign(baker, "_solidLayerMask", 1);
                LiquidWorldRuntime liquids = root.AddComponent<LiquidWorldRuntime>();
                Assign(liquids, "_profile", liquidProfile);
                ElementWorldRuntime world = root.AddComponent<ElementWorldRuntime>();
                Assign(world, "_profile", worldProfile);
                Assign(world, "_gridLiquidRuntime", liquids);
                Assign(world, "_interestPoint", camera.transform);
                Surface(root, world, shader, MaterialId.Water, "Water");
                Surface(root, world, shader, MaterialId.Poison, "Poison");
                Surface(root, world, shader, MaterialId.Sticky, "Sticky");
                if (reservoir) BuildReservoir(liquids);
                else BuildLocal(liquids);
                EditorSceneManager.SaveScene(scene, path);
            }
            finally
            {
                SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void BuildPartialSurfaceScene(ElementWorldProfile worldProfile,
            LiquidWorldProfile liquidProfile, Shader shader)
        {
            string path = "Assets/_Project/Scenes/Prototypes/LiquidPartialSurfacePrototype.unity";
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) != null) return;
            Scene previous = SceneManager.GetActiveScene();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                Camera camera = Camera();
                Light();
                var root = new GameObject("Grid Liquid World");
                ElementFieldSolidBaker baker = root.AddComponent<ElementFieldSolidBaker>();
                Assign(baker, "_solidLayerMask", 1);
                LiquidWorldRuntime liquids = root.AddComponent<LiquidWorldRuntime>();
                Assign(liquids, "_profile", liquidProfile);
                ElementWorldRuntime world = root.AddComponent<ElementWorldRuntime>();
                Assign(world, "_profile", worldProfile);
                Assign(world, "_gridLiquidRuntime", liquids);
                Assign(world, "_interestPoint", camera.transform);
                Surface(root, world, shader, MaterialId.Water, "Water");
                Surface(root, world, shader, MaterialId.Poison, "Poison");
                Surface(root, world, shader, MaterialId.Sticky, "Sticky");
                Cube("Partial Lake Shared Floor", new Vector3(0f, -.25f, 0f),
                    new Vector3(12f, .5f, 6f));
                PartialSurfaceBasin(liquids, MaterialId.Water, -3f);
                PartialSurfaceBasin(liquids, MaterialId.Poison, 0f);
                PartialSurfaceBasin(liquids, MaterialId.Sticky, 3f);
                EditorSceneManager.SaveScene(scene, path);
            }
            finally
            {
                SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void PartialSurfaceBasin(LiquidWorldRuntime liquids,
            MaterialId material, float centerX)
        {
            string label = material + " Partial Lake";
            // 两格高的四壁将六乘六列封到水面；底层满格、顶层 80% 的两个
            // Initial Volume 仍由动态 Store 入账，专门检验自动 Owner 交接。
            Cube(label + " Left Wall", new Vector3(centerX - .875f, .25f, 0f),
                new Vector3(.25f, .5f, 2f));
            Cube(label + " Right Wall", new Vector3(centerX + .875f, .25f, 0f),
                new Vector3(.25f, .5f, 2f));
            Cube(label + " Front Wall", new Vector3(centerX, .25f, -.875f),
                new Vector3(1.5f, .5f, .25f));
            Cube(label + " Back Wall", new Vector3(centerX, .25f, .875f),
                new Vector3(1.5f, .5f, .25f));
            Initial(liquids, material, new Bounds(new Vector3(centerX, .125f, 0f),
                new Vector3(1.5f, .25f, 1.5f)), 1f, label + " Full Base");
            Initial(liquids, material, new Bounds(new Vector3(centerX, .375f, 0f),
                new Vector3(1.5f, .25f, 1.5f)), .8f, label + " Partial Surface");
            Probe(liquids, material, new Vector3(centerX, .8f, 0f));
        }

        private static void BuildDenseLakeScene(ElementWorldProfile worldProfile,
            LiquidWorldProfile liquidProfile, Shader shader)
        {
            string path = "Assets/_Project/Scenes/Prototypes/LiquidDenseLakePrototype.unity";
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) != null) return;
            Scene previous = SceneManager.GetActiveScene();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                Camera camera = Camera();
                camera.transform.position = new Vector3(0f, 27f, -32f);
                camera.transform.LookAt(new Vector3(0f, 1f, 0f));
                camera.fieldOfView = 62f;
                camera.farClipPlane = 100f;
                Light();
                var root = new GameObject("Grid Liquid World");
                ElementFieldSolidBaker baker = root.AddComponent<ElementFieldSolidBaker>();
                Assign(baker, "_solidLayerMask", 1);
                LiquidWorldRuntime liquids = root.AddComponent<LiquidWorldRuntime>();
                Assign(liquids, "_profile", liquidProfile);
                ElementWorldRuntime world = root.AddComponent<ElementWorldRuntime>();
                Assign(world, "_profile", worldProfile);
                Assign(world, "_gridLiquidRuntime", liquids);
                Assign(world, "_interestPoint", camera.transform);
                Surface(root, world, shader, MaterialId.Water, "Water");
                Surface(root, world, shader, MaterialId.Poison, "Poison");
                Surface(root, world, shader, MaterialId.Sticky, "Sticky");
                Cube("Dense Lakes Shared Floor", new Vector3(0f, -.25f, 0f),
                    new Vector3(42f, .5f, 14f));
                DenseLakeBasin(liquids, MaterialId.Water, -14f);
                DenseLakeBasin(liquids, MaterialId.Poison, 0f);
                DenseLakeBasin(liquids, MaterialId.Sticky, 14f);
                EditorSceneManager.SaveScene(scene, path);
            }
            finally
            {
                SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void DenseLakeBasin(LiquidWorldRuntime liquids, MaterialId material, float centerX)
        {
            string label = material + " Dense Lake";
            Cube(label + " Left Wall", new Vector3(centerX - 6.125f, 1f, 0f),
                new Vector3(.25f, 2f, 12.5f));
            Cube(label + " Right Wall", new Vector3(centerX + 6.125f, 1f, 0f),
                new Vector3(.25f, 2f, 12.5f));
            Cube(label + " Front Wall", new Vector3(centerX, 1f, -6.125f),
                new Vector3(12f, 2f, .25f));
            Cube(label + " Back Wall", new Vector3(centerX, 1f, 6.125f),
                new Vector3(12f, 2f, .25f));
            Cube(label + " Shallow Shelf", new Vector3(centerX - 2.5f, .25f, -1.5f),
                new Vector3(3f, .5f, 3f));
            Cube(label + " Island", new Vector3(centerX + 2.5f, 1f, 2.5f),
                new Vector3(2f, 2f, 2f));
            GameObject plug = Cube(material + " Dense Lake Internal Floor - Toggle In Play Mode",
                new Vector3(centerX + .125f, .125f, .125f), Vector3.one * .2f);
            var authoringObject = new GameObject(label + " - 18432 Bounding Cells");
            var region = authoringObject.AddComponent<StableLiquidRegionAuthoring>();
            Assign(region, "_liquids", liquids);
            Assign(region, "_material", (int)material);
            Assign(region, "_worldBounds", new Bounds(new Vector3(centerX, 1f, 0f),
                new Vector3(12f, 2f, 12f)));
            Assign(region, "_initialFill", .65f);
            Assign(region, "_deriveBottomFromSolid", true);
            Assign(region, "_sectorizeByChunk", true);
            var toggle = plug.AddComponent<StableLiquidStaticCellAuthoring>();
            Assign(toggle, "_liquids", liquids);
            Assign(toggle, "_obstacle", plug.GetComponent<BoxCollider>());
            // Probe 在已存在的水面上方，验证深水量很大时仍能接收新增物质。
            Probe(liquids, material, new Vector3(centerX - 4f, 1.25f, -4f));
        }

        private static void BuildSectorScene(ElementWorldProfile worldProfile,
            LiquidWorldProfile liquidProfile, Shader shader)
        {
            string path = "Assets/_Project/Scenes/Prototypes/LiquidSectorPrototype.unity";
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) != null) return;
            Scene previous = SceneManager.GetActiveScene();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                Camera camera = Camera();
                Light();
                var root = new GameObject("Grid Liquid World");
                ElementFieldSolidBaker baker = root.AddComponent<ElementFieldSolidBaker>();
                Assign(baker, "_solidLayerMask", 1);
                LiquidWorldRuntime liquids = root.AddComponent<LiquidWorldRuntime>();
                Assign(liquids, "_profile", liquidProfile);
                ElementWorldRuntime world = root.AddComponent<ElementWorldRuntime>();
                Assign(world, "_profile", worldProfile);
                Assign(world, "_gridLiquidRuntime", liquids);
                Assign(world, "_interestPoint", camera.transform);
                Surface(root, world, shader, MaterialId.Water, "Water");
                Surface(root, world, shader, MaterialId.Poison, "Poison");
                Surface(root, world, shader, MaterialId.Sticky, "Sticky");
                Cube("Sector Lake Floor", new Vector3(0f, -.25f, 0f),
                    new Vector3(7f, .5f, 7f));
                Cube("Sector Lake Left Wall", new Vector3(-3.125f, .5f, 0f),
                    new Vector3(.25f, 1f, 6.25f));
                Cube("Sector Lake Right Wall", new Vector3(3.125f, .5f, 0f),
                    new Vector3(.25f, 1f, 6.25f));
                Cube("Sector Lake Front Wall", new Vector3(0f, .5f, -3.125f),
                    new Vector3(6.25f, 1f, .25f));
                Cube("Sector Lake Back Wall", new Vector3(0f, .5f, 3.125f),
                    new Vector3(6.25f, 1f, .25f));
                GameObject plug = Cube("Sector Lake Internal Floor - Toggle In Play Mode",
                    new Vector3(.125f, .125f, .125f), Vector3.one * .2f);
                var regionObject = new GameObject("Sectorized Stable Lake - 2304 Cells");
                var region = regionObject.AddComponent<StableLiquidRegionAuthoring>();
                Assign(region, "_liquids", liquids);
                Assign(region, "_worldBounds", new Bounds(new Vector3(0f, .5f, 0f),
                    new Vector3(6f, 1f, 6f)));
                Assign(region, "_initialFill", .6f);
                Assign(region, "_deriveBottomFromSolid", true);
                Assign(region, "_sectorizeByChunk", true);
                var toggle = plug.AddComponent<StableLiquidStaticCellAuthoring>();
                Assign(toggle, "_liquids", liquids);
                Assign(toggle, "_obstacle", plug.GetComponent<BoxCollider>());
                Probe(liquids, MaterialId.Water, new Vector3(-2f, 1f, 0f));
                EditorSceneManager.SaveScene(scene, path);
            }
            finally
            {
                SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void TuneSectorScene()
        {
            string path = "Assets/_Project/Scenes/Prototypes/LiquidSectorPrototype.unity";
            Scene previous = SceneManager.GetActiveScene();
            Scene scene = SceneManager.GetSceneByPath(path);
            bool alreadyLoaded = scene.IsValid() && scene.isLoaded;
            if (!alreadyLoaded)
                scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (root.name != "Prototype Camera") continue;
                    Camera camera = root.GetComponent<Camera>();
                    camera.transform.position = new Vector3(0f, 6f, -8f);
                    camera.transform.LookAt(new Vector3(0f, .5f, 0f));
                    camera.fieldOfView = 50f;
                    break;
                }
                EditorSceneManager.SaveScene(scene, path);
            }
            finally
            {
                SceneManager.SetActiveScene(previous);
                if (!alreadyLoaded) EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void BuildProductionComparison(ElementWorldProfile worldProfile,
            LiquidWorldProfile liquidProfile, Shader shader)
        {
            // 单独复制场景用于 P7 法术／状态回归；原生产 Scene、Profile 和 Prefab 都不改写。
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(GridComparisonScene) == null
                && !AssetDatabase.CopyAsset(ProductionScene, GridComparisonScene))
                throw new InvalidOperationException("无法复制 P7 对照场景。");
            Scene previous = SceneManager.GetActiveScene();
            Scene scene = SceneManager.GetSceneByPath(GridComparisonScene);
            bool alreadyLoaded = scene.IsValid() && scene.isLoaded;
            if (!alreadyLoaded)
                scene = EditorSceneManager.OpenScene(GridComparisonScene, OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                ElementWorldRuntime world = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    ElementWorldRuntime candidate = root.GetComponentInChildren<ElementWorldRuntime>(true);
                    if (candidate == null) continue;
                    if (world != null) throw new InvalidOperationException("P7 存在多个元素世界实例。");
                    world = candidate;
                }
                if (world == null) throw new InvalidOperationException("P7 缺少元素世界实例。");
                LiquidWorldRuntime liquids = world.GetComponent<LiquidWorldRuntime>();
                if (liquids == null) liquids = world.gameObject.AddComponent<LiquidWorldRuntime>();
                Assign(liquids, "_profile", liquidProfile);
                Assign(world, "_profile", worldProfile);
                Assign(world, "_gridLiquidRuntime", liquids);
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (GpuPbfFluidRuntime old in root.GetComponentsInChildren<GpuPbfFluidRuntime>(true))
                        old.enabled = false;
                    foreach (FluidChunkStreamingRuntime old in root.GetComponentsInChildren<FluidChunkStreamingRuntime>(true))
                        old.enabled = false;
                    foreach (FluidGameplayOccupancyBridge old in root.GetComponentsInChildren<FluidGameplayOccupancyBridge>(true))
                        old.enabled = false;
                    foreach (GpuLiquidSurfaceRenderer old in root.GetComponentsInChildren<GpuLiquidSurfaceRenderer>(true))
                        old.enabled = false;
                    foreach (DormantLiquidCellRenderer old in root.GetComponentsInChildren<DormantLiquidCellRenderer>(true))
                        old.enabled = false;
                    foreach (ElementWorldWaterRenderer old in root.GetComponentsInChildren<ElementWorldWaterRenderer>(true))
                        old.enabled = false;
                }
                // 每种材料独立补齐动态／稳定表面；已有一部分组件时也不会漏掉其余材料。
                Surface(world.gameObject, world, shader, MaterialId.Water, "Water");
                Surface(world.gameObject, world, shader, MaterialId.Poison, "Poison");
                Surface(world.gameObject, world, shader, MaterialId.Sticky, "Sticky");
                EditorSceneManager.SaveScene(scene, GridComparisonScene);
            }
            finally
            {
                SceneManager.SetActiveScene(previous);
                if (!alreadyLoaded) EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void TuneExistingReservoir()
        {
            string path = "Assets/_Project/Scenes/Prototypes/LiquidReservoirPrototype.unity";
            Scene previous = SceneManager.GetActiveScene();
            Scene scene = SceneManager.GetSceneByPath(path);
            bool alreadyLoaded = scene.IsValid() && scene.isLoaded;
            if (!alreadyLoaded)
                scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                LiquidWorldRuntime liquids = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (liquids == null)
                        liquids = root.GetComponentInChildren<LiquidWorldRuntime>(true);
                    foreach (StableLiquidOutletAuthoring outlet in
                        root.GetComponentsInChildren<StableLiquidOutletAuthoring>(true))
                    {
                        Assign(outlet, "_openingAreaCells",
                            outlet.name == "Depth Lake Outlet - Toggle In Play Mode" ? 2f : 4f);
                        Assign(outlet, "_conductanceGmuPerSecond", 3000f);
                    }
                }
                if (liquids == null) throw new InvalidOperationException("湖盆原型缺少 Grid Liquid。");
                bool hasTopologyProbe = false;
                foreach (GameObject root in scene.GetRootGameObjects())
                    if (root.name == "Small Lake Internal Cell - Toggle In Play Mode")
                    { hasTopologyProbe = true; break; }
                if (!hasTopologyProbe)
                {
                    // 独立 1×0.5×1 m 小湖的上层是干格，能验证“先预检再建墙”。
                    Cube("Small Lake Left Wall", new Vector3(4.375f, .25f, 0f),
                        new Vector3(.25f, .5f, 1.25f));
                    Cube("Small Lake Right Wall", new Vector3(5.625f, .25f, 0f),
                        new Vector3(.25f, .5f, 1.25f));
                    Cube("Small Lake Front Wall", new Vector3(5f, .25f, -.625f),
                        new Vector3(1f, .5f, .25f));
                    Cube("Small Lake Back Wall", new Vector3(5f, .25f, .625f),
                        new Vector3(1f, .5f, .25f));
                    Region(liquids, new Bounds(new Vector3(5f, .25f, 0f),
                        new Vector3(1f, .5f, 1f)), .5f);
                    var probe = new GameObject("Small Lake Internal Cell - Toggle In Play Mode");
                    probe.transform.position = new Vector3(4.625f, .375f, -.375f);
                    BoxCollider obstacle = probe.AddComponent<BoxCollider>();
                    obstacle.size = Vector3.one * .2f;
                    obstacle.enabled = false;
                    var authoring = probe.AddComponent<StableLiquidStaticCellAuthoring>();
                    Assign(authoring, "_liquids", liquids);
                    Assign(authoring, "_obstacle", obstacle);
                }
                bool hasIrregularLake = false;
                foreach (GameObject root in scene.GetRootGameObjects())
                    if (root.name == "Irregular Stable Lake - Solid Footprint")
                    { hasIrregularLake = true; break; }
                if (!hasIrregularLake)
                {
                    // 外墙包住体积，内侧两处 Solid 凹口定义岸线；三材料共用同一 authoring 算法。
                    Cube("Irregular Lake Left Wall", new Vector3(3.375f, .25f, 2.5f),
                        new Vector3(.25f, .5f, 2.25f));
                    Cube("Irregular Lake Right Wall", new Vector3(6.625f, .25f, 2.5f),
                        new Vector3(.25f, .5f, 2.25f));
                    Cube("Irregular Lake Front Wall", new Vector3(5f, .25f, 1.375f),
                        new Vector3(3.25f, .5f, .25f));
                    Cube("Irregular Lake Back Wall", new Vector3(5f, .25f, 3.625f),
                        new Vector3(3.25f, .5f, .25f));
                    Cube("Irregular Lake Shore Notch A", new Vector3(3.875f, .25f, 2.5f),
                        new Vector3(.75f, .5f, 1f));
                    Cube("Irregular Lake Shore Notch B", new Vector3(6.125f, .25f, 1.875f),
                        new Vector3(.75f, .5f, .5f));
                    var lake = new GameObject("Irregular Stable Lake - Solid Footprint");
                    var region = lake.AddComponent<StableLiquidRegionAuthoring>();
                    Assign(region, "_liquids", liquids);
                    Assign(region, "_worldBounds", new Bounds(
                        new Vector3(5f, .25f, 2.5f), new Vector3(3f, .5f, 2f)));
                    Assign(region, "_initialFill", .75f);
                    Assign(region, "_deriveFootprintFromSolid", true);
                }
                bool hasDepthLake = false;
                foreach (GameObject root in scene.GetRootGameObjects())
                    if (root.name == "Variable Depth Stable Lake - Solid Bottom")
                    { hasDepthLake = true; break; }
                if (!hasDepthLake)
                {
                    // 0.25 m 格长：左半部底高 0，右半部底高 0.5 m。
                    // 水量越过浅岸后才扩展表面，适合在 Inspector 改初始 Fill 做跨层对照。
                    Cube("Depth Lake Left Wall A", new Vector3(3.875f, .5f, -3.375f),
                        new Vector3(.25f, 1f, 1.5f));
                    Cube("Depth Lake Left Wall B", new Vector3(3.875f, .5f, -1.625f),
                        new Vector3(.25f, 1f, 1.5f));
                    GameObject depthPlug = Cube("Depth Lake Outlet Plug",
                        new Vector3(3.875f, .5f, -2.5f),
                        new Vector3(.25f, 1f, .5f));
                    Cube("Depth Lake Right Wall", new Vector3(7.125f, .5f, -2.5f),
                        new Vector3(.25f, 1f, 3.25f));
                    Cube("Depth Lake Front Wall", new Vector3(5.5f, .5f, -4.125f),
                        new Vector3(3f, 1f, .25f));
                    Cube("Depth Lake Back Wall", new Vector3(5.5f, .5f, -.875f),
                        new Vector3(3f, 1f, .25f));
                    Cube("Depth Lake Raised Bottom", new Vector3(6.25f, .25f, -2.5f),
                        new Vector3(1.5f, .5f, 3f));
                    var lake = new GameObject("Variable Depth Stable Lake - Solid Bottom");
                    var region = lake.AddComponent<StableLiquidRegionAuthoring>();
                    Assign(region, "_liquids", liquids);
                    Assign(region, "_worldBounds", new Bounds(
                        new Vector3(5.5f, .5f, -2.5f), new Vector3(3f, 1f, 3f)));
                    Assign(region, "_initialFill", .4f);
                    Assign(region, "_deriveBottomFromSolid", true);
                    var outletObject = new GameObject("Depth Lake Outlet - Toggle In Play Mode");
                    var outlet = outletObject.AddComponent<StableLiquidOutletAuthoring>();
                    Assign(outlet, "_source", region);
                    Assign(outlet, "_outletCenter", Marker("Depth Opening Height",
                        new Vector3(3.875f, .375f, -2.5f)));
                    Assign(outlet, "_receivingCell", Marker("Depth Receiving Cell",
                        new Vector3(3.625f, .375f, -2.375f)));
                    Assign(outlet, "_plugCollider", depthPlug.GetComponent<Collider>());
                    Assign(outlet, "_openingAreaCells", 2f);
                    Assign(outlet, "_conductanceGmuPerSecond", 3000f);
                }
                EditorSceneManager.SaveScene(scene, path);
            }
            finally
            {
                SceneManager.SetActiveScene(previous);
                if (!alreadyLoaded) EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static Camera Camera()
        {
            var objectCamera = new GameObject("Prototype Camera");
            objectCamera.tag = "MainCamera";
            Camera camera = objectCamera.AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, 10f, -14f);
            camera.transform.LookAt(new Vector3(0f, 1f, 0f));
            return camera;
        }

        private static void Light()
        {
            var objectLight = new GameObject("Sun");
            Light light = objectLight.AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(45f, -30f, 0f);
        }

        internal static void Surface(GameObject root, ElementWorldRuntime world,
            Shader shader, MaterialId material, string label)
        {
            Material source = LiquidWorldAuthoringTools.GetSurfaceMaterial(material);
            LiquidSurfaceRenderer dynamic = null;
            foreach (LiquidSurfaceRenderer candidate in root.GetComponentsInChildren<LiquidSurfaceRenderer>(true))
            {
                if (new SerializedObject(candidate).FindProperty("_material").intValue != (int)material)
                    continue;
                if (dynamic != null)
                    throw new InvalidOperationException("重复的 Grid 动态表面：" + label);
                dynamic = candidate;
            }
            if (dynamic == null)
            {
                var surfaceObject = new GameObject(label + " Dynamic Surface");
                surfaceObject.transform.SetParent(root.transform);
                dynamic = surfaceObject.AddComponent<LiquidSurfaceRenderer>();
            }
            Assign(dynamic, "_world", world);
            Assign(dynamic, "_material", (int)material);
            Assign(dynamic, "_sourceMaterial", source);
            Assign(dynamic, "_liquidShader", shader);
            // 与 Profile 的同步表面交接页数保持一致，避免旧 Quad 提前撤下。
            Assign(dynamic, "_maximumRebuildsPerFrame", 4);
            dynamic.enabled = true;
            StableLiquidSurfaceRenderer stable = null;
            foreach (StableLiquidSurfaceRenderer candidate in
                root.GetComponentsInChildren<StableLiquidSurfaceRenderer>(true))
            {
                if (new SerializedObject(candidate).FindProperty("_material").intValue != (int)material)
                    continue;
                if (stable != null)
                    throw new InvalidOperationException("重复的 Grid 稳定表面：" + label);
                stable = candidate;
            }
            if (stable == null)
            {
                var stableObject = new GameObject(label + " Stable Surface");
                stableObject.transform.SetParent(root.transform);
                stable = stableObject.AddComponent<StableLiquidSurfaceRenderer>();
            }
            Assign(stable, "_world", world);
            Assign(stable, "_material", (int)material);
            Assign(stable, "_sourceMaterial", source);
            Assign(stable, "_liquidShader", shader);
            stable.enabled = true;
            // 第三种材料接线结束后再建立统一Palette，避免只配置Water的半成品外壳。
            if(material==MaterialId.Sticky)GridLiquidFieldSetup.EnsureWorld(world);
        }

        private static void BuildLocal(LiquidWorldRuntime liquids)
        {
            Cube("Floor", new Vector3(0f, -.25f, 0f), new Vector3(24f, .5f, 24f));
            Cube("Back Wall", new Vector3(0f, 1f, 12.125f), new Vector3(24f, 2f, .25f));
            Cube("Front Wall", new Vector3(0f, 1f, -12.125f), new Vector3(24f, 2f, .25f));
            Cube("Left Wall", new Vector3(-12.125f, 1f, 0f), new Vector3(.25f, 2f, 24f));
            Cube("Right Wall", new Vector3(12.125f, 1f, 0f), new Vector3(.25f, 2f, 24f));
            Initial(liquids, MaterialId.Water, new Bounds(new Vector3(-3f, .125f, 0f),
                new Vector3(6f, .25f, 4f)));
            Initial(liquids, MaterialId.Poison, new Bounds(new Vector3(3f, .125f, 2f),
                new Vector3(3f, .25f, 2f)));
            Initial(liquids, MaterialId.Sticky, new Bounds(new Vector3(3f, .125f, -2f),
                new Vector3(3f, .25f, 2f)));
            Probe(liquids, MaterialId.Water, new Vector3(-3f, 1f, 0f));
            Probe(liquids, MaterialId.Poison, new Vector3(3f, 1f, 2f));
            Probe(liquids, MaterialId.Sticky, new Vector3(3f, 1f, -2f));
        }

        private static void BuildReservoir(LiquidWorldRuntime liquids)
        {
            Cube("Ground", new Vector3(0f, -.25f, 0f), new Vector3(16f, .5f, 10f));
            Cube("Upper Floor", new Vector3(-4f, 2.875f, 0f), new Vector3(4.5f, .25f, 4.5f));
            Cube("Upper Left Wall", new Vector3(-6.125f, 4.25f, 0f),
                new Vector3(.25f, 2.5f, 4.5f));
            Cube("Upper Back Wall", new Vector3(-4f, 4.25f, 2.125f),
                new Vector3(4f, 2.5f, .25f));
            Cube("Upper Front Wall", new Vector3(-4f, 4.25f, -2.125f),
                new Vector3(4f, 2.5f, .25f));
            Cube("Upper Outlet Wall A", new Vector3(-1.875f, 4.25f, -1.25f),
                new Vector3(.25f, 2.5f, 1.5f));
            Cube("Upper Outlet Wall B", new Vector3(-1.875f, 4.25f, 1.25f),
                new Vector3(.25f, 2.5f, 1.5f));
            GameObject plug = Cube("Outlet Plug", new Vector3(-1.875f, 4.25f, 0f),
                new Vector3(.25f, 2.5f, 1f));
            Cube("Lower Left Wall", new Vector3(-2.125f, 1.25f, 0f),
                new Vector3(.25f, 2.5f, 4.5f));
            Cube("Lower Right Wall", new Vector3(2.125f, 1.25f, 0f),
                new Vector3(.25f, 2.5f, 4.5f));
            Cube("Lower Back Wall", new Vector3(0f, 1.25f, 2.125f),
                new Vector3(4f, 2.5f, .25f));
            Cube("Lower Front Wall", new Vector3(0f, 1.25f, -2.125f),
                new Vector3(4f, 2.5f, .25f));
            StableLiquidRegionAuthoring upper = Region(liquids,
                new Bounds(new Vector3(-4f, 4f, 0f), new Vector3(4f, 2f, 4f)), .8f);
            Region(liquids, new Bounds(new Vector3(0f, 1f, 0f),
                new Vector3(4f, 2f, 4f)), .15f);
            var outletObject = new GameObject("Upper Outlet - Toggle In Play Mode");
            StableLiquidOutletAuthoring outlet =
                outletObject.AddComponent<StableLiquidOutletAuthoring>();
            Assign(outlet, "_source", upper);
            Assign(outlet, "_outletCenter", Marker("Opening Height", new Vector3(-1.875f, 3.5f, 0f)));
            Assign(outlet, "_receivingCell", Marker("Receiving Cell", new Vector3(-1.625f, 3.5f, 0f)));
            Assign(outlet, "_plugCollider", plug.GetComponent<Collider>());
            Probe(liquids, MaterialId.Water, new Vector3(0f, 3.5f, 0f));
        }

        private static StableLiquidRegionAuthoring Region(LiquidWorldRuntime liquids,
            Bounds bounds, float fill)
        {
            var regionObject = new GameObject("Stable Lake " + bounds.center);
            var region = regionObject.AddComponent<StableLiquidRegionAuthoring>();
            Assign(region, "_liquids", liquids);
            Assign(region, "_worldBounds", bounds);
            Assign(region, "_initialFill", fill);
            return region;
        }

        private static void Initial(LiquidWorldRuntime liquids, MaterialId material,
            Bounds bounds, float fill = 1f, string label = null)
        {
            var volume = new GameObject(label ?? material + " Initial Puddle");
            var initial = volume.AddComponent<GridLiquidInitialVolume>();
            Assign(initial, "_liquids", liquids);
            Assign(initial, "_material", (int)material);
            Assign(initial, "_worldBounds", bounds);
            Assign(initial, "_fill", fill);
        }

        private static void Probe(LiquidWorldRuntime liquids, MaterialId material, Vector3 position)
        {
            var probe = new GameObject(material + " Deposit Probe");
            probe.transform.position = position;
            var component = probe.AddComponent<LiquidPrototypeProbe>();
            Assign(component, "_liquids", liquids);
            Assign(component, "_material", (int)material);
        }

        private static Transform Marker(string name, Vector3 position)
        {
            var marker = new GameObject(name);
            marker.transform.position = position;
            return marker.transform;
        }

        private static GameObject Cube(string name, Vector3 position, Vector3 scale)
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.position = position;
            cube.transform.localScale = scale;
            return cube;
        }

        private static void Assign(UnityEngine.Object target, string field, UnityEngine.Object value)
        {
            var data = new SerializedObject(target);
            data.FindProperty(field).objectReferenceValue = value;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Assign(UnityEngine.Object target, string field, int value)
        {
            var data = new SerializedObject(target);
            data.FindProperty(field).intValue = value;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Assign(UnityEngine.Object target, string field, Bounds value)
        {
            var data = new SerializedObject(target);
            data.FindProperty(field).boundsValue = value;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Assign(UnityEngine.Object target, string field, float value)
        {
            var data = new SerializedObject(target);
            data.FindProperty(field).floatValue = value;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Assign(UnityEngine.Object target, string field, bool value)
        {
            var data = new SerializedObject(target);
            data.FindProperty(field).boolValue = value;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent)) throw new InvalidOperationException(path);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
