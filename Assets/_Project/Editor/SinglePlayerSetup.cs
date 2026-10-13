using System;
using System.IO;
using Game.Core;
using Game.Run;
using Game.Skills;
using Game.UI;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.EditorTools
{
    /// <summary>直接整备入口；只创建/保存新测试 Scene/SO/Prefab，不保存或覆盖当前用户场景。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerSetup
    {
        [MenuItem("Tools/Norn/Single Player/Create Formal Hub (Beffio Map 1)")]
        private static void CreateFormalHub() => SinglePlayerHubSceneSetup.Create();
        private const string Root = "Assets/_Project/ScriptableObjects/Tests/SinglePlayer";
        private const string ScenePath = "Assets/_Project/Scenes/Tests/InventorySandbox.unity";
        private const string PrefabPath = "Assets/_Project/Prefabs/Tests/InventoryItem.prefab";
        private const string Request = "Temp/SinglePlayer/create-inventory-sandbox.request";
        private static double _nextPoll;
        static SinglePlayerSetup() { EditorApplication.update += Poll; }
        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            _nextPoll = EditorApplication.timeSinceStartup + .5d;
            if (!File.Exists(Request)) return;
            File.Delete(Request);
            try { Create(); } catch (Exception e) { Report("Error", e.ToString()); GameLog.Error(e.ToString(), "SinglePlayerSetup"); }
        }
        [MenuItem("Tools/Norn/Single Player/Create Inventory Sandbox")]
        private static void Create()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Report("Blocked", "Play Mode 中不创建 Scene。"); return; }
            for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).isDirty)
            { Report("Blocked", "有未保存场景；没有保存或丢弃。"); return; }
            if (File.Exists(ScenePath)) { ValidateExisting(); Report("Validated", null); return; }
            Folder(Root); Folder("Assets/_Project/Scenes/Tests"); Folder("Assets/_Project/Prefabs/Tests");
            ItemCatalog catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>(Root + "/Catalog.asset");
            SinglePlayerStartingInventory seeds = AssetDatabase.LoadAssetAtPath<SinglePlayerStartingInventory>(Root + "/StartingInventory.asset");
            InputActionAsset actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(Root + "/InventoryActions.asset");
            if (catalog == null)
            {
                if (AssetDatabase.FindAssets("", new[] { Root }).Length > 0) throw new InvalidOperationException("测试目录已有部分资源，拒绝覆盖，请检查创建中断。");
                SpellDefinition spell = AssetDatabase.LoadAssetAtPath<SpellDefinition>("Assets/_Project/ScriptableObjects/Spell/Spell_Emit_WaterField.asset");
                if (spell == null) throw new InvalidOperationException("已有水法术配置缺失。");
                ItemDefinition bag = Define("hub.bag", "背包", ItemKind.Backpack, 1, 1, 1, 4, 4);
                ItemDefinition smallBag = Define("hub.smallbag", "小背包", ItemKind.Backpack, 1, 1, 1, 2, 2);
                ItemDefinition wand = Define("hub.wand", "法杖", ItemKind.Wand, 2, 1, 1, programCapacity: 4);
                ItemDefinition card = Define("hub.water", "水法术", ItemKind.Spell, 1, 1, 2, spell: spell);
                ItemDefinition bar = Define("hub.bar", "长条容器", ItemKind.Container, 2, 1, 1);
                ItemDefinition corner = Define("hub.corner", "L形容器", ItemKind.Container, 2, 2, 1,
                    cells: new[] { Vector2Int.zero, new Vector2Int(0, 1), Vector2Int.one });
                ItemDefinition potion = Define("hub.potion", "小物品", ItemKind.Consumable, 1, 1, 1);
                ItemDefinition bottle = Define("hub.bottle", "空瓶", ItemKind.Container, 1, 1, 3);
                catalog = ScriptableObject.CreateInstance<ItemCatalog>();
                var data = new SerializedObject(catalog); SerializedProperty defs = data.FindProperty("_definitions");
                ItemDefinition[] definitions = { bag, smallBag, wand, card, bar, corner, potion, bottle };
                defs.arraySize = definitions.Length;
                for (int i = 0; i < definitions.Length; i++) defs.GetArrayElementAtIndex(i).objectReferenceValue = definitions[i];
                data.ApplyModifiedPropertiesWithoutUndo(); AssetDatabase.CreateAsset(catalog, Root + "/Catalog.asset");
                seeds = ScriptableObject.CreateInstance<SinglePlayerStartingInventory>();
                data = new SerializedObject(seeds); SerializedProperty items = data.FindProperty("_items"); items.arraySize = 13;
                Seed(items, 0, bag, 0, ItemLocationKind.EquippedBackpack);
                Seed(items, 1, smallBag); Seed(items, 2, wand, 0, ItemLocationKind.EquippedWand, 0);
                Seed(items, 3, wand, 0, ItemLocationKind.EquippedWand, 1); Seed(items, 4, wand);
                Seed(items, 5, card, 3); Seed(items, 6, card, 3); Seed(items, 7, card, 8);
                Seed(items, 8, bar); Seed(items, 9, corner); Seed(items, 10, potion); Seed(items, 11, bottle); Seed(items, 12, bottle);
                data.ApplyModifiedPropertiesWithoutUndo(); AssetDatabase.CreateAsset(seeds, Root + "/StartingInventory.asset");
                actions = ScriptableObject.CreateInstance<InputActionAsset>();
                InputActionMap map = actions.AddActionMap("Inventory");
                map.AddAction("Toggle", InputActionType.Button, "<Keyboard>/tab");
                map.AddAction("Rotate", InputActionType.Button, "<Keyboard>/r");
                map.AddAction("Cancel", InputActionType.Button, "<Keyboard>/escape");
                map.AddAction("Split", InputActionType.Button, "<Keyboard>/leftShift").AddBinding("<Keyboard>/rightShift");
                AssetDatabase.CreateAsset(actions, Root + "/InventoryActions.asset");
            }
            if (catalog == null || seeds == null || actions == null || !catalog.TryCreateIndex(out _, out string error))
                throw new InvalidOperationException("已有测试配置不完整，未覆盖。");
            Scene original = SceneManager.GetActiveScene();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                var systems = new GameObject("SinglePlayerInventory");
                var session = systems.AddComponent<SinglePlayerInventorySession>();
                Set(session, "_catalog", catalog); Set(session, "_startingInventory", seeds);
                var pause = systems.AddComponent<RunPauseCoordinator>();
                var events = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
                events.GetComponent<InputSystemUIInputModule>().AssignDefaultActions();
                var camera = new GameObject("Main Camera", typeof(Camera)); camera.tag = "MainCamera";
                camera.transform.position = new Vector3(5, 10, -2); camera.transform.rotation = Quaternion.Euler(90, 0, 0);
                camera.GetComponent<Camera>().orthographic = true; camera.GetComponent<Camera>().orthographicSize = 5;
                var canvas = new GameObject("InventoryCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                var scaler = canvas.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1440, 900); scaler.matchWidthOrHeight = .5f;
                SinglePlayerInventoryController controller = canvas.AddComponent<SinglePlayerInventoryController>();
                RectTransform panel = Rect(canvas.transform, "InventoryPanel", Vector2.zero, new Vector2(1320, 800));
                panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(.5f, .5f); panel.anchoredPosition = Vector2.zero;
                panel.gameObject.AddComponent<Image>().color = new Color(.065f, .085f, .115f, .98f);
                Label(panel, "Title", "据点整备", new Vector2(22, -12), new Vector2(400, 40), 26);
                Label(panel, "WarehouseTitle", "仓库", new Vector2(22, -70), new Vector2(260, 30));
                RectTransform warehouse = Scroll(panel, "Warehouse", new Vector2(22, -108), new Vector2(286, 565), out RectTransform warehouseContent);
                InventoryDropTarget warehouseTarget = warehouse.gameObject.AddComponent<InventoryDropTarget>();
                Label(panel, "BagTitle", "背包 · 将新背包拖到此处装备", new Vector2(340, -70), new Vector2(470, 30));
                RectTransform bagTarget = Rect(panel, "BackpackEquipTarget", new Vector2(340, -108), new Vector2(470, 44));
                bagTarget.gameObject.AddComponent<Image>().color = new Color(.16f, .22f, .28f, 1);
                Label(bagTarget, "Label", "背包装备位", new Vector2(8, -6), new Vector2(300, 32));
                var backpackTarget = bagTarget.gameObject.AddComponent<InventoryDropTarget>();
                RectTransform gridViewport = Scroll(panel, "BackpackViewport", new Vector2(340, -168), new Vector2(470, 286), out RectTransform gridContent);
                InventoryGridView grid = gridContent.gameObject.AddComponent<InventoryGridView>();
                gridContent.gameObject.AddComponent<Image>().color = Color.clear;
                Label(panel, "EquipmentTitle", "法杖装备位  1 / 2 / 3 · 点击查看程序", new Vector2(340, -470), new Vector2(470, 30));
                RectTransform equipment = Rect(panel, "WandEquipment", new Vector2(340, -510), new Vector2(470, 54));
                Text programTitle = Label(panel, "ProgramTitle", "法杖程序", new Vector2(340, -580), new Vector2(470, 30));
                Scroll(panel, "ProgramViewport", new Vector2(340, -618), new Vector2(470, 114), out RectTransform program);
                WandInventoryView wands = panel.gameObject.AddComponent<WandInventoryView>();
                Set(wands, "_equipmentRoot", equipment); Set(wands, "_programRoot", program); Set(wands, "_programTitle", programTitle);
                Label(panel, "GroundTitle", "地面 · 拖到此处丢弃", new Vector2(856, -70), new Vector2(350, 30));
                RectTransform ground = Scroll(panel, "Ground", new Vector2(856, -108), new Vector2(430, 424), out RectTransform groundContent);
                var groundTarget = ground.gameObject.AddComponent<InventoryDropTarget>();
                Text details = Label(panel, "Details", "", new Vector2(856, -550), new Vector2(430, 110));
                Text status = Label(panel, "Status", "", new Vector2(22, -710), new Vector2(800, 62));
                Button(panel, "NextMember", "下一份", new Vector2(856, -672), controller.SelectNextMember);
                Button(panel, "Save", "保存", new Vector2(998, -732), controller.Save);
                Button(panel, "Close", "关闭 [I]", new Vector2(1140, -732), controller.Close);

                InventoryItemView prefab = AssetDatabase.LoadAssetAtPath<InventoryItemView>(PrefabPath);
                if (prefab == null)
                {
                    InventoryItemView prototype = ItemView(scene, "ItemPrototype");
                    PrefabUtility.SaveAsPrefabAsset(prototype.gameObject, PrefabPath); UnityEngine.Object.DestroyImmediate(prototype.gameObject);
                    prefab = AssetDatabase.LoadAssetAtPath<InventoryItemView>(PrefabPath);
                }
                InventoryItemView ghost = UnityEngine.Object.Instantiate(prefab, canvas.transform); ghost.name = "DragGhost";
                CanvasGroup ghostGroup = ghost.gameObject.AddComponent<CanvasGroup>(); ghostGroup.blocksRaycasts = false; ghostGroup.interactable = false; ghostGroup.alpha = .7f;
                ghost.gameObject.SetActive(false);
                Set(controller, "_session", session); Set(controller, "_pauseCoordinator", pause); Set(controller, "_panelRoot", panel.gameObject);
                Set(controller, "_gridView", grid); Set(controller, "_wandsView", wands); Set(controller, "_warehouseRoot", warehouseContent);
                Set(controller, "_groundRoot", groundContent); Set(controller, "_warehouseTarget", warehouseTarget);
                Set(controller, "_groundTarget", groundTarget); Set(controller, "_backpackTarget", backpackTarget);
                Set(controller, "_itemPrefab", prefab); Set(controller, "_ghost", ghost); Set(controller, "_details", details); Set(controller, "_status", status);
                Set(controller, "_toggleAction", ActionReference(actions, "Toggle")); Set(controller, "_rotateAction", ActionReference(actions, "Rotate"));
                Set(controller, "_cancelAction", ActionReference(actions, "Cancel")); Set(controller, "_splitAction", ActionReference(actions, "Split"));
                if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("新 InventorySandbox 保存失败。");
            }
            finally { if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original); EditorSceneManager.CloseScene(scene, true); }
            Report("Created", null);
        }
        private static InventoryItemView ItemView(Scene scene, string name)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(InventoryItemView));
            root.GetComponent<Image>().color = Color.clear;
            Text label = Label(root.transform, "Label", "", Vector2.zero, new Vector2(260, 50), 14);
            label.rectTransform.anchorMin = Vector2.zero; label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = new Vector2(4, 2); label.rectTransform.offsetMax = new Vector2(-4, -2);
            label.alignment = TextAnchor.MiddleCenter; label.raycastTarget = false;
            InventoryItemView view = root.GetComponent<InventoryItemView>(); Set(view, "_label", label); return view;
        }
        private static InputActionReference ActionReference(InputActionAsset asset, string name)
        {
            string path = Root + "/Action_" + name + ".asset";
            var reference = AssetDatabase.LoadAssetAtPath<InputActionReference>(path);
            if (reference != null) return reference;
            reference = InputActionReference.Create(asset.FindAction(name, true)); AssetDatabase.CreateAsset(reference, path); return reference;
        }
        private static ItemDefinition Define(string id, string display, ItemKind kind, int width, int height, int stack,
            int bagWidth = 1, int bagHeight = 1, int programCapacity = 1, Vector2Int[] cells = null, SpellDefinition spell = null)
        {
            var value = ScriptableObject.CreateInstance<ItemDefinition>(); var data = new SerializedObject(value);
            data.FindProperty("_contentId").stringValue = id; data.FindProperty("_displayName").stringValue = display;
            data.FindProperty("_kind").intValue = (int)kind; data.FindProperty("_shapeWidth").intValue = width;
            data.FindProperty("_shapeHeight").intValue = height; data.FindProperty("_maxStack").intValue = stack;
            data.FindProperty("_backpackWidth").intValue = bagWidth; data.FindProperty("_backpackHeight").intValue = bagHeight;
            data.FindProperty("_wandProgramCapacity").intValue = programCapacity; data.FindProperty("_spellDefinition").objectReferenceValue = spell;
            if (cells == null) { cells = new Vector2Int[width * height]; for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) cells[y * width + x] = new Vector2Int(x, y); }
            var shape = data.FindProperty("_occupiedCells"); shape.arraySize = cells.Length;
            for (int i = 0; i < cells.Length; i++) shape.GetArrayElementAtIndex(i).vector2IntValue = cells[i];
            data.ApplyModifiedPropertiesWithoutUndo(); AssetDatabase.CreateAsset(value, Root + "/" + id + ".asset"); return value;
        }
        private static void ValidateExisting()
        {
            ItemCatalog catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>(Root + "/Catalog.asset");
            SinglePlayerStartingInventory seeds = AssetDatabase.LoadAssetAtPath<SinglePlayerStartingInventory>(Root + "/StartingInventory.asset");
            InputActionAsset actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(Root + "/InventoryActions.asset");
            InventoryItemView prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath)?.GetComponent<InventoryItemView>();
            if (catalog == null || seeds == null || actions == null || prefab == null
                || !catalog.TryCreateIndex(out ItemCatalogIndex index, out _)
                || !seeds.TryCreate(index, out _, out _))
                throw new InvalidOperationException("已有 Sandbox 配置/初始资产/Prefab 无效；未覆盖任何文件。");
            RequireReferences(prefab, "_label");
            string[] names = { "Toggle", "Rotate", "Cancel", "Split" };
            for (int i = 0; i < names.Length; i++)
            {
                InputActionReference reference = AssetDatabase.LoadAssetAtPath<InputActionReference>(Root + "/Action_" + names[i] + ".asset");
                if (reference == null || reference.action != actions.FindAction(names[i], true))
                    throw new InvalidOperationException("已有 InputAction 引用无效：" + names[i]);
            }
            Scene original = SceneManager.GetActiveScene();
            Scene scene = SceneManager.GetSceneByPath(ScenePath); bool opened = !scene.IsValid() || !scene.isLoaded;
            if (opened) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            try
            {
                SinglePlayerInventorySession session = null; SinglePlayerInventoryController ui = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (session == null) session = root.GetComponentInChildren<SinglePlayerInventorySession>(true);
                    if (ui == null) ui = root.GetComponentInChildren<SinglePlayerInventoryController>(true);
                    foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                        if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject) != 0)
                            throw new InvalidOperationException("已有 Sandbox 存在 Missing Script：" + child.name);
                }
                if (session == null || ui == null) throw new InvalidOperationException("已有 Sandbox 缺少会话或界面。");
                RequireReferences(session, "_catalog", "_startingInventory");
                if (!new SerializedObject(session).FindProperty("_sandboxProfile").boolValue)
                    throw new InvalidOperationException("测试入口的 SandboxProfile 已关闭；未自动修改，请恢复隔离配置后测试。");
                RequireReferences(ui, "_session", "_pauseCoordinator", "_panelRoot", "_gridView", "_wandsView",
                    "_warehouseRoot", "_groundRoot", "_warehouseTarget", "_groundTarget", "_backpackTarget",
                    "_itemPrefab", "_ghost", "_details", "_status", "_toggleAction", "_rotateAction", "_cancelAction", "_splitAction");
            }
            finally
            {
                // 校验只读地打开；不调用 SaveScene，不保存或丢弃原工作场景。
                if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
                if (opened) EditorSceneManager.CloseScene(scene, true);
            }
        }
        private static void RequireReferences(UnityEngine.Object target, params string[] fields)
        {
            var data = new SerializedObject(target);
            for (int i = 0; i < fields.Length; i++)
                if (data.FindProperty(fields[i])?.objectReferenceValue == null)
                    throw new InvalidOperationException(target.name + " 缺少引用 " + fields[i] + "；未修改。");
        }
        private static void Seed(SerializedProperty items, int index, ItemDefinition definition, int charges = 0,
            ItemLocationKind kind = ItemLocationKind.Warehouse, int slot = 0)
        {
            var item = items.GetArrayElementAtIndex(index); item.FindPropertyRelative("Definition").objectReferenceValue = definition;
            item.FindPropertyRelative("RemainingCharges").intValue = charges; item.FindPropertyRelative("Location").intValue = (int)kind;
            item.FindPropertyRelative("SlotIndex").intValue = slot;
        }
        private static RectTransform Rect(Transform parent, string name, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false); var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1); rect.anchoredPosition = position; rect.sizeDelta = size; return rect;
        }
        private static Text Label(Transform parent, string name, string text, Vector2 position, Vector2 size, int fontSize = 18)
        {
            RectTransform rect = Rect(parent, name, position, size); Text label = rect.gameObject.AddComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); label.text = text; label.fontSize = fontSize;
            label.color = new Color(.88f, .91f, .94f, 1); label.raycastTarget = false; label.horizontalOverflow = HorizontalWrapMode.Wrap; return label;
        }
        private static RectTransform Scroll(Transform parent, string name, Vector2 position, Vector2 size, out RectTransform content)
        {
            RectTransform outer = Rect(parent, name, position, size); outer.gameObject.AddComponent<Image>().color = new Color(.09f, .12f, .16f, 1);
            ScrollRect scroll = outer.gameObject.AddComponent<ScrollRect>(); scroll.horizontal = true; scroll.vertical = true;
            RectTransform viewport = Rect(outer, "Viewport", Vector2.zero, size); viewport.gameObject.AddComponent<RectMask2D>();
            content = Rect(viewport, "Content", Vector2.zero, size); scroll.viewport = viewport; scroll.content = content;
            scroll.movementType = ScrollRect.MovementType.Clamped; return outer;
        }
        private static void Button(Transform parent, string name, string text, Vector2 position, UnityAction action)
        {
            RectTransform rect = Rect(parent, name, position, new Vector2(130, 42)); rect.gameObject.AddComponent<Image>().color = new Color(.2f, .29f, .36f, 1);
            var button = rect.gameObject.AddComponent<Button>(); Label(rect, "Label", text, new Vector2(8, -4), new Vector2(114, 34)).alignment = TextAnchor.MiddleCenter;
            UnityEventTools.AddPersistentListener(button.onClick, action);
        }
        private static void Set(UnityEngine.Object target, string field, UnityEngine.Object value)
        { var data = new SerializedObject(target); data.FindProperty(field).objectReferenceValue = value; data.ApplyModifiedPropertiesWithoutUndo(); }
        private static void Folder(string path)
        {
            string[] pieces = path.Split('/'); string current = pieces[0];
            for (int i = 1; i < pieces.Length; i++) { string next = current + "/" + pieces[i]; if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, pieces[i]); current = next; }
        }
        private static void Report(string state, string error)
        {
            Directory.CreateDirectory("Temp/SinglePlayer");
            File.WriteAllText("Temp/SinglePlayer/inventory-sandbox-report.json", JsonUtility.ToJson(new SetupReport
            { State = state, Error = error, Utc = DateTime.UtcNow.ToString("O"), Scene = ScenePath, ActiveScene = SceneManager.GetActiveScene().path,
                SceneDirty = SceneManager.GetActiveScene().isDirty }, true));
        }
        [Serializable] private sealed class SetupReport { public string State, Error, Utc, Scene, ActiveScene; public bool SceneDirty; }
    }
}
