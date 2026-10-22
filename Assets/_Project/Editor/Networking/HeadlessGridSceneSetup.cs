using System;
using Game.Core;
using Game.ElementField;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Project.Editor
{
    /// <summary>显式装配独立 CPU 世界实验；正式场景和原 KCP 实验场景不参与改写。</summary>
    public static class HeadlessGridSceneSetup
    {
        public const string ScenePath = "Assets/_Project/Scenes/Tests/HeadlessGridLab.unity";
        private const string Fixtures = "Assets/_Project/Tests/Fixtures/Networking/HeadlessGrid";
        private const string Templates = "Assets/_Project/ScriptableObjects/ElementField/";

        [MenuItem("Tools/Norn/Online/Create Headless Grid Lab")]
        public static void CreateLab()
        {
            OnlineSceneSetup.RequireCleanEditor();
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
            { GameLog.Info("Headless Grid 场景已存在，没有覆盖。", "OnlineLab"); return; }
            EnsureFolder(Fixtures);
            EnsureFolder("Assets/_Project/Scenes/Tests");
            ElementWorldProfile worldProfile = Clone<ElementWorldProfile>(
                Templates + "Liquids/ElementWorldProfile_P7_DemoRun_Grid.asset", Fixtures + "/World.asset", out bool newWorld);
            if (newWorld)
            {
                var data = new SerializedObject(worldProfile);
                data.FindProperty("_waterSimulationMode").intValue = (int)WaterSimulationMode.GridLiquid;
                data.FindProperty("_tickRate").floatValue = 10;
                data.FindProperty("_maximumResidentChunks").intValue = 64;
                data.FindProperty("_activeRadiusXZChunks").intValue = 1;
                data.FindProperty("_activeRadiusYChunks").intValue = 1;
                data.FindProperty("_enableWorldActivityExpansion").boolValue = false;
                data.FindProperty("_enableFluidChunkStreaming").boolValue = false;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            LiquidWorldProfile liquidProfile = Clone<LiquidWorldProfile>(
                Templates + "Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset", Fixtures + "/Liquids.asset", out bool newLiquid);
            if (newLiquid)
            {
                var data = new SerializedObject(liquidProfile);
                data.FindProperty("_maximumChunks").intValue = 64;
                data.FindProperty("_maximumActiveCells").intValue = 4096;
                data.FindProperty("_maximumPendingDeposits").intValue = 32;
                data.FindProperty("_maximumCellsPerTick").intValue = 512;
                data.FindProperty("_maximumStableRegions").intValue = 8;
                data.FindProperty("_maximumStableOutlets").intValue = 8;
                data.FindProperty("_maximumStableRegionCells").intValue = 4096;
                data.FindProperty("_maximumStableSurfacePatches").intValue = 128;
                data.FindProperty("_maximumAutoCandidateCells").intValue = 512;
                data.FindProperty("_autoValidationCellsPerTick").intValue = 256;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            HeadlessGridProbeProfile probeProfile = AssetDatabase.LoadAssetAtPath<HeadlessGridProbeProfile>(Fixtures + "/Probe.asset");
            if (probeProfile == null)
            {
                if (AssetDatabase.LoadMainAssetAtPath(Fixtures + "/Probe.asset") != null)
                    throw new InvalidOperationException("Probe 路径已有其他资产，拒绝覆盖。");
                probeProfile = ScriptableObject.CreateInstance<HeadlessGridProbeProfile>();
                AssetDatabase.CreateAsset(probeProfile, Fixtures + "/Probe.asset");
            }
            var solidProfile = Require<FixedSolidWorldProfile>(Templates + "Phases/FixedSolidWorld_Default.asset");
            var gasProfile = Require<GasWorldProfile>(Templates + "Phases/GasWorld_Default.asset");
            Scene previous = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(previous.path)) throw new InvalidOperationException("请先处理当前未命名 Scene，再创建实验场景。");
            Scene lab = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(lab);
                var interest = new GameObject("World Interest");
                interest.transform.position = Vector3.up;
                var floor = new GameObject("Static Collider Floor");
                floor.transform.position = new Vector3(0, -.5f, 0);
                floor.AddComponent<BoxCollider>().size = new Vector3(16, 1, 16);
                var root = new GameObject("CPU Material World");
                // Player 中探针验证角色/配置之后才激活；不让 Awake 抢在实验入口之前初始化。
                root.SetActive(false);
                var baker = root.AddComponent<ElementFieldSolidBaker>();
                var bakerData = new SerializedObject(baker);
                bakerData.FindProperty("_solidLayerMask").intValue = 1;
                bakerData.ApplyModifiedPropertiesWithoutUndo();
                var liquids = root.AddComponent<LiquidWorldRuntime>();
                var solids = root.AddComponent<FixedSolidWorldRuntime>();
                var gas = root.AddComponent<GasWorldRuntime>();
                Set(liquids, "_profile", liquidProfile);
                Set(solids, "_profile", solidProfile);
                Set(gas, "_profile", gasProfile);
                var world = root.AddComponent<ElementWorldRuntime>();
                Set(world, "_profile", worldProfile);
                Set(world, "_gridLiquidRuntime", liquids);
                Set(world, "_fixedSolidRuntime", solids);
                Set(world, "_gasRuntime", gas);
                Set(world, "_interestPoint", interest.transform);
                var probe = new GameObject("Headless Grid Probe").AddComponent<HeadlessGridProbe>();
                Set(probe, "_world", world);
                Set(probe, "_liquids", liquids);
                Set(probe, "_profile", probeProfile);
                if (!EditorSceneManager.SaveScene(lab, ScenePath)) throw new InvalidOperationException("Headless Grid 场景保存失败。");
                AssetDatabase.SaveAssets();
                GameLog.Info("Headless Grid 实验已创建：无 Camera/Renderer/GPU Runtime。", "OnlineLab");
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(lab, true);
            }
        }

        private static T Require<T>(string path) where T : UnityEngine.Object
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null) throw new InvalidOperationException("缺少实验模板：" + path);
            return asset;
        }

        private static T Clone<T>(string source, string destination, out bool created) where T : ScriptableObject
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(destination);
            created = existing == null;
            if (existing != null) return existing;
            if (AssetDatabase.LoadMainAssetAtPath(destination) != null) throw new InvalidOperationException("Fixture 路径已有其他资产。");
            T asset = UnityEngine.Object.Instantiate(Require<T>(source));
            asset.name = System.IO.Path.GetFileNameWithoutExtension(destination);
            AssetDatabase.CreateAsset(asset, destination);
            return asset;
        }

        private static void Set(UnityEngine.Object target, string name, UnityEngine.Object value)
        {
            var data = new SerializedObject(target);
            data.FindProperty(name).objectReferenceValue = value;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int split = path.LastIndexOf('/');
            EnsureFolder(path.Substring(0, split));
            AssetDatabase.CreateFolder(path.Substring(0, split), path.Substring(split + 1));
        }
    }
}
