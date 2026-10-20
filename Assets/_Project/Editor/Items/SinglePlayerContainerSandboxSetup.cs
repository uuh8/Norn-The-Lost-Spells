using System;
using System.IO;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using Game.Rendering;
using Game.Run;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.EditorTools
{
    /// <summary>保存新独立场景/测试 SO，恢复原 active Scene；已有路径和未保存场景一律不覆盖。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerContainerSandboxSetup
    {
        private const string Root = "Assets/_Project/Tests/Fixtures/Run/SinglePlayerContainers";
        private const string ScenePath = "Assets/_Project/Scenes/Tests/ContainerSandbox.unity";
        private const string Request = "Temp/SinglePlayer/create-container-sandbox.request";
        private const string UpgradeRequest = "Temp/SinglePlayer/upgrade-container-sandbox.request";
        static SinglePlayerContainerSandboxSetup() { EditorApplication.update += Poll; }
        private static void Poll()
        {
            bool upgrade = File.Exists(UpgradeRequest);
            if ((!File.Exists(Request) && !upgrade) || EditorApplication.isCompiling || EditorApplication.isUpdating || SessionState.GetBool("Norn.SinglePlayer.Tests.Running", false)) return;
            File.Delete(upgrade ? UpgradeRequest : Request);
            try { if (upgrade) Upgrade(); else Create(); } catch (Exception exception) { Report("Error", exception.ToString()); GameLog.Error(exception.ToString(), "ContainerSandbox"); }
        }
        [MenuItem("Tools/Norn/Single Player/Create Container Sandbox")]
        private static void Create()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Report("Blocked", "当前 Play Mode。"); return; }
            for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).isDirty)
            { Report("Blocked", "存在未保存场景；没有保存或丢弃。"); return; }
            if (File.Exists(ScenePath)) { Report("Existing", null); return; }
            if (AssetDatabase.IsValidFolder(Root)) throw new InvalidOperationException("部分测试资产已存在，未覆盖。");
            AssetDatabase.CreateFolder("Assets/_Project/Tests/Fixtures/Run", "SinglePlayerContainers");
            var sourceCatalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/Tests/Fixtures/Run/SinglePlayerWands/Catalog.asset");
            var entries = new SerializedObject(sourceCatalog).FindProperty("_definitions");
            var definitions = new ItemDefinition[entries.arraySize + 2];
            for (int i = 0; i < entries.arraySize; i++) definitions[i] = (ItemDefinition)entries.GetArrayElementAtIndex(i).objectReferenceValue;
            var bottle = Define("bottle", "采集瓶", ItemKind.Container);
            var ice = Define("ice", "采集的冰", ItemKind.Material);
            definitions[definitions.Length - 2] = bottle; definitions[definitions.Length - 1] = ice;
            var catalog = ScriptableObject.CreateInstance<ItemCatalog>(); var data = new SerializedObject(catalog);
            var array = data.FindProperty("_definitions"); array.arraySize = definitions.Length;
            for (int i = 0; i < definitions.Length; i++) array.GetArrayElementAtIndex(i).objectReferenceValue = definitions[i];
            data.ApplyModifiedPropertiesWithoutUndo(); AssetDatabase.CreateAsset(catalog, "Assets/_Project/Tests/Fixtures/Run/SinglePlayerContainers/Catalog.asset");
            var seeds = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<SinglePlayerStartingInventory>("Assets/_Project/Tests/Fixtures/Run/SinglePlayerWands/StartingInventory.asset"));
            data = new SerializedObject(seeds); array = data.FindProperty("_items"); int originalCount = array.arraySize; array.arraySize += 2;
            for (int i = originalCount; i < array.arraySize; i++)
            { var seed = array.GetArrayElementAtIndex(i); seed.FindPropertyRelative("Definition").objectReferenceValue = bottle;
                seed.FindPropertyRelative("RemainingCharges").intValue = 0; seed.FindPropertyRelative("HasUnlimitedCharges").boolValue = false;
                seed.FindPropertyRelative("Location").intValue = 0; seed.FindPropertyRelative("SlotIndex").intValue = 0; seed.FindPropertyRelative("OwnerSeedIndex").intValue = -1; }
            data.ApplyModifiedPropertiesWithoutUndo(); AssetDatabase.CreateAsset(seeds, "Assets/_Project/Tests/Fixtures/Run/SinglePlayerContainers/StartingInventory.asset");
            if (!catalog.TryCreateIndex(out var index, out string error) || !seeds.TryCreate(index, out _, out error)) throw new InvalidOperationException(error);
            var profile = Clone<ElementWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/ElementWorldProfile_P7_DemoRun_Grid.asset", "WorldProfile");
            data = new SerializedObject(profile); data.FindProperty("_cellSize").floatValue = .5f; data.FindProperty("_chunkSize").intValue = 4;
            data.FindProperty("_maximumResidentChunks").intValue = 128; data.FindProperty("_activeRadiusXZChunks").intValue = 2;
            data.FindProperty("_activeRadiusYChunks").intValue = 1; data.ApplyModifiedPropertiesWithoutUndo();
            var liquidProfile = Clone<LiquidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset", "LiquidProfile");
            data = new SerializedObject(liquidProfile); data.FindProperty("_resolutionScale").intValue = 1; data.FindProperty("_unitVolumeMultiplier").floatValue = 1;
            data.FindProperty("_maximumChunks").intValue = 16; data.FindProperty("_maximumActiveCells").intValue = 2048;
            data.FindProperty("_maximumStableRegionCells").intValue = 256; data.FindProperty("_maximumAutoCandidateCells").intValue = 256;
            data.ApplyModifiedPropertiesWithoutUndo(); liquidProfile.Validate();
            var solidProfile = ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumNavigationBoxesPerChunk\":8}", solidProfile); AssetDatabase.CreateAsset(solidProfile, "Assets/_Project/Tests/Fixtures/Run/SinglePlayerContainers/SolidProfile.asset");
            var gasProfile = ScriptableObject.CreateInstance<GasWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":256,\"_maximumCellsPerTick\":64}", gasProfile); AssetDatabase.CreateAsset(gasProfile, "Assets/_Project/Tests/Fixtures/Run/SinglePlayerContainers/GasProfile.asset");
            var conversion = ScriptableObject.CreateInstance<MaterialConversionProfile>(); AssetDatabase.CreateAsset(conversion, "Assets/_Project/Tests/Fixtures/Run/SinglePlayerContainers/ConversionProfile.asset");
            Scene original = SceneManager.GetActiveScene(); Scene scene = EditorSceneManager.OpenScene("Assets/_Project/Scenes/Tests/WandSandbox.unity", OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene); var session = Find<SinglePlayerInventorySession>(scene);
                Assign(session, "_catalog", catalog); Assign(session, "_startingInventory", seeds); Assign(session, "_sandboxName", "ContainerSandbox");
                var bootstrap = session.gameObject.AddComponent<ContainerSandboxInventoryBootstrap>(); Assign(bootstrap, "_session", session);
                var inventoryUi = Find<SinglePlayerInventoryController>(scene); Assign(inventoryUi, "_openOnStart", false);
                var wizard = Find<WizardController>(scene); wizard.transform.position = new Vector3(0, .1f, -1.5f);
                var collection = wizard.gameObject.AddComponent<MaterialCollectionController>(); Assign(collection, "_session", session);
                var worldRoot = new GameObject("Container Material World"); worldRoot.transform.position = new Vector3(-8, 0, -8);
                var world = worldRoot.AddComponent<ElementWorldRuntime>(); var liquid = worldRoot.AddComponent<LiquidWorldRuntime>();
                var solid = worldRoot.AddComponent<FixedSolidWorldRuntime>(); var gas = worldRoot.AddComponent<GasWorldRuntime>();
                Assign(world, "_profile", profile); Assign(world, "_gridLiquidRuntime", liquid); Assign(world, "_fixedSolidRuntime", solid);
                Assign(world, "_gasRuntime", gas); Assign(world, "_conversionProfile", conversion); Assign(world, "_interestPoint", wizard.transform);
                Assign(liquid, "_profile", liquidProfile); Assign(solid, "_profile", solidProfile); Assign(gas, "_profile", gasProfile);
                var adapter = worldRoot.AddComponent<MaterialInventoryTransferAdapter>(); Assign(adapter, "_world", world);
                var supply = worldRoot.AddComponent<ContainerSandboxMaterialAuthoring>(); Assign(supply, "_world", world);
                var shader = Shader.Find("Game/Elemental/Liquid Dormant Cell");
                GridLiquidPrototypeSetup.Surface(worldRoot, world, shader, MaterialId.Water, "Water");
                GridLiquidPrototypeSetup.Surface(worldRoot, world, shader, MaterialId.Poison, "Poison");
                var solidRenderer = worldRoot.AddComponent<FixedSolidSurfaceRenderer>(); Assign(solidRenderer, "_world", world);
                Assign(solidRenderer, "_material", AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Art/Elemental/Materials/M_FixedSolidWorld.mat"));
                var gasRenderer = worldRoot.AddComponent<GasFieldVolumeRenderer>(); Assign(gasRenderer, "_world", world);
                Assign(gasRenderer, "_material", AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Art/Elemental/Materials/M_RealGasWorld.mat"));
                BuildHud(session);
                AddContainerUse(scene, world, session, collection);
                // 只保存本生成器的新测试资产，不顺带保存用户其他 dirty SO。
                foreach (string guid in AssetDatabase.FindAssets("", new[] { Root }))
                    AssetDatabase.SaveAssetIfDirty(AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guid)));
                if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("独立容器场景保存失败。");
            }
            finally { SceneManager.SetActiveScene(original); EditorSceneManager.CloseScene(scene, true); }
            Report("Created", null);
        }
        [MenuItem("Tools/Norn/Single Player/Upgrade Container Sandbox Uses")]
        private static void Upgrade()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Report("Blocked", "当前Play Mode，未修改。"); return; }
            for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).isDirty)
            { Report("Blocked", "存在未保存Scene，未保存或丢弃。"); return; }
            if (!File.Exists(ScenePath)) { Create(); return; }
            Scene original = SceneManager.GetActiveScene(); Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                AddContainerUse(scene, Find<ElementWorldRuntime>(scene), Find<SinglePlayerInventorySession>(scene), Find<MaterialCollectionController>(scene));
                foreach (string guid in AssetDatabase.FindAssets("", new[] { Root }))
                    AssetDatabase.SaveAssetIfDirty(AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guid)));
                if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("容器测试Scene升级失败。");
            }
            finally { SceneManager.SetActiveScene(original); EditorSceneManager.CloseScene(scene, true); }
            Report("Upgraded", null);
        }
        private static void AddContainerUse(Scene scene, ElementWorldRuntime world, SinglePlayerInventorySession session, MaterialCollectionController collection)
        {
            var definition = AssetDatabase.LoadAssetAtPath<ContainerUseDefinition>("Assets/_Project/Tests/Fixtures/Run/SinglePlayerContainers/UseDefinition.asset");
            if (definition == null)
            {
                // 新测试资产使用Primitive；不修改用户模型或共享液体材质。
                var material = new Material(Shader.Find("Universal Render Pipeline/Lit")); material.SetColor("_BaseColor", new Color(.05f, .5f, 1));
                AssetDatabase.CreateAsset(material, "Assets/_Project/Tests/Fixtures/Run/SinglePlayerContainers/M_ThrownBottle.asset");
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere); go.name = "ThrownContainer"; go.SetActive(false);
                go.transform.localScale = Vector3.one * .2f; go.GetComponent<Renderer>().sharedMaterial = material;
                go.AddComponent<Rigidbody>(); go.AddComponent<ThrownContainerProjectile>();
                var prefab = PrefabUtility.SaveAsPrefabAsset(go, "Assets/_Project/Tests/Fixtures/Run/SinglePlayerContainers/ThrownContainer.prefab"); UnityEngine.Object.DestroyImmediate(go);
                definition = ScriptableObject.CreateInstance<ContainerUseDefinition>(); Assign(definition, "_throwPrefab", prefab);
                AssetDatabase.CreateAsset(definition, "Assets/_Project/Tests/Fixtures/Run/SinglePlayerContainers/UseDefinition.asset");
            }
            var adapter = world.GetComponent<ContainerDepositAdapter>(); if (adapter == null) adapter = world.gameObject.AddComponent<ContainerDepositAdapter>();
            Assign(adapter, "_world", world);
            var controller = collection.GetComponent<ContainerUseController>(); if (controller == null) controller = collection.gameObject.AddComponent<ContainerUseController>();
            Assign(controller, "_session", session); Assign(controller, "_collection", collection); Assign(controller, "_definition", definition);
            foreach (var root in scene.GetRootGameObjects()) if (root.name == "Container Composition")
            {
                var title = root.transform.Find("Panel/Title").GetComponent<Text>();
                title.rectTransform.sizeDelta = new Vector2(430, 68);
                title.text = "材料容器测试\nE采集 F换瓶 J取冰 I整备/保存\nH按住撒出 G投掷 K放冰（每次100GMU）";
            }
        }
        private static ItemDefinition Define(string id, string display, ItemKind kind)
        {
            var item = ScriptableObject.CreateInstance<ItemDefinition>(); var data = new SerializedObject(item);
            data.FindProperty("_contentId").stringValue = "containertest." + id; data.FindProperty("_displayName").stringValue = display;
            data.FindProperty("_kind").intValue = (int)kind; data.FindProperty("_maxStack").intValue = kind == ItemKind.Container ? 3 : 1;
            data.FindProperty("_containerCapacityGmu").intValue = 150; data.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.CreateAsset(item, "Assets/_Project/Tests/Fixtures/Run/SinglePlayerContainers/" + id + ".asset"); return item;
        }
        private static T Clone<T>(string path, string name) where T : UnityEngine.Object
        { var asset = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<T>(path)); AssetDatabase.CreateAsset(asset, "Assets/_Project/Tests/Fixtures/Run/SinglePlayerContainers/" + name + ".asset"); return asset; }
        private static void Assign(UnityEngine.Object target, string field, UnityEngine.Object value)
        { var data = new SerializedObject(target); data.FindProperty(field).objectReferenceValue = value; data.ApplyModifiedPropertiesWithoutUndo(); }
        private static void Assign(UnityEngine.Object target, string field, string value)
        { var data = new SerializedObject(target); data.FindProperty(field).stringValue = value; data.ApplyModifiedPropertiesWithoutUndo(); }
        private static void Assign(UnityEngine.Object target, string field, bool value)
        { var data = new SerializedObject(target); data.FindProperty(field).boolValue = value; data.ApplyModifiedPropertiesWithoutUndo(); }
        private static T Find<T>(Scene scene) where T : Component
        { foreach (var root in scene.GetRootGameObjects()) { var component = root.GetComponentInChildren<T>(true); if (component != null) return component; } return null; }
        private static void BuildHud(SinglePlayerInventorySession session)
        {
            var root = new GameObject("Container Composition", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay; root.GetComponent<Canvas>().sortingOrder = 40;
            var scaler = root.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1440, 900);
            var panel = Rect(root.transform, "Panel", new Vector2(-16, -16), new Vector2(450, 530)); panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(1, 1);
            panel.gameObject.AddComponent<Image>().color = new Color(.03f, .06f, .09f, .93f);
            var view = panel.gameObject.AddComponent<ContainerCompositionView>(); var data = new SerializedObject(view); data.FindProperty("_session").objectReferenceValue = session;
            var labels = new Text[12]; labels[0] = Label(panel, "Title", new Vector2(16, -10), new Vector2(430, 56), "材料容器测试\nE 按住采集　F 换瓶　G 拾取冰　I 整备/保存");
            string[] names = { "已装 GMU", "容量 GMU", "Water", "Fire", "Poison", "Sticky", "Ice", "DriedSlime", "Steam", "ToxicVapor" };
            var digits = data.FindProperty("_digits"); digits.arraySize = 200;
            for (int row = 0; row < 10; row++)
            {
                labels[row + 1] = Label(panel, names[row], new Vector2(16, -78 - row * 30), new Vector2(130, 26), names[row]);
                for (int column = 0; column < 20; column++) digits.GetArrayElementAtIndex(row * 20 + column).objectReferenceValue =
                    Label(panel, "Digit", new Vector2(180 + column * 12, -78 - row * 30), new Vector2(13, 26), "");
            }
            data.FindProperty("_status").objectReferenceValue = Label(panel, "Status", new Vector2(16, -390), new Vector2(425, 45), "靠近材料并用准星指向它，再按住 E。");
            labels[11] = Label(panel, "Help", new Vector2(16, -442), new Vector2(420, 85), "中央：Water 100 + Poison 50\n右：稳定 Water 100；右上：Steam 30；左：Ice 200\n本场景暂停 Solver 以隔离数量验收；渲染/采集仍工作。");
            var array = data.FindProperty("_labels"); array.arraySize = labels.Length;
            for (int i = 0; i < labels.Length; i++) array.GetArrayElementAtIndex(i).objectReferenceValue = labels[i]; data.ApplyModifiedPropertiesWithoutUndo();
        }
        private static Text Label(Transform parent, string name, Vector2 position, Vector2 size, string content)
        { var text = Rect(parent, name, position, size).gameObject.AddComponent<Text>(); text.fontSize = 16; text.color = Color.white; text.text = content; text.raycastTarget = false; return text; }
        private static RectTransform Rect(Transform parent, string name, Vector2 position, Vector2 size)
        { var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>(); rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1); rect.anchoredPosition = position; rect.sizeDelta = size; return rect; }
        private static void Report(string state, string message)
        { Directory.CreateDirectory("Temp/SinglePlayer"); File.WriteAllText("Temp/SinglePlayer/container-sandbox-report.json",
            JsonUtility.ToJson(new Result { State = state, Message = message, Scene = ScenePath }, true)); }
        [Serializable] private sealed class Result { public string State, Message, Scene; }
    }
}
