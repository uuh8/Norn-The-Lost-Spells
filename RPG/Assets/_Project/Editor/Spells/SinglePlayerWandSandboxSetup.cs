using System;
using System.IO;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.Run;
using Game.Skills;
using Game.UI;
using Unity.Cinemachine;
using Unity.Cinemachine.TargetTracking;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.EditorTools
{
    /// <summary>独立入口复制已验证库存测试 Scene，只保存新路径。迁移只改指定测试法杖 SO，不保存用户场景。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerWandSandboxSetup
    {
        private const string Root = "Assets/_Project/Tests/Fixtures/Run/SinglePlayerWands";
        private const string ScenePath = "Assets/_Project/Scenes/Tests/WandSandbox.unity";
        private const string Request = "Temp/SinglePlayer/create-wand-sandbox.request";
        private const string RepairRequest = "Temp/SinglePlayer/repair-wand-hud.request";
        private static double _next;
        static SinglePlayerWandSandboxSetup() { EditorApplication.update += Poll; }
        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _next || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            _next = EditorApplication.timeSinceStartup + .5;
            bool repair = File.Exists(RepairRequest);
            if (!repair && !File.Exists(Request)) return;
            File.Delete(repair ? RepairRequest : Request);
            try { if (repair) RepairHud(); else Create(); } catch (Exception e) { Report("Error", e.ToString()); GameLog.Error(e.ToString(), "WandSandbox"); }
        }
        [MenuItem("Tools/Norn/Single Player/Create Wand Sandbox")]
        private static void Create()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Report("Blocked", "请退出 Play Mode。"); return; }
            for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).isDirty)
            { Report("Blocked", "有未保存场景，没有保存或丢弃。"); return; }
            MigrateLegacyTestWands();
            if (File.Exists(ScenePath)) { Report("Existing", "已有独立场景，未覆盖。"); return; }
            if (!AssetDatabase.IsValidFolder(Root)) AssetDatabase.CreateFolder("Assets/_Project/Tests/Fixtures/Run", "SinglePlayerWands");
            if (AssetDatabase.FindAssets("", new[] { Root }).Length != 0) throw new InvalidOperationException("测试目录已有部分资产，拒绝覆盖。");
            var a = Define("Item_Wand_StarterA", "法杖 A", ItemKind.Wand, MakeWand("A", 100, 10, .8f));
            var b = Define("Item_Wand_StarterB", "法杖 B", ItemKind.Wand, MakeWand("B", 60, 20, .25f));
            var c = Define("Item_Wand_StarterC", "法杖 C", ItemKind.Wand, MakeWand("C", 180, 5, 1.2f));
            var bag = Define("bag", "背包", ItemKind.Backpack);
            var fire = Define("fire", "火球", ItemKind.Spell, spell: Spell("Spell_Emit_Fireball"));
            var ice = Define("ice", "冰球", ItemKind.Spell, spell: Spell("Spell_Emit_Iceball"));
            var trigger = Define("trigger", "触发火球", ItemKind.Spell, spell: Spell("Spell_Emit_TriggerFireball"));
            var catalog = ScriptableObject.CreateInstance<ItemCatalog>(); var data = new SerializedObject(catalog);
            var defs = data.FindProperty("_definitions"); ItemDefinition[] values = { bag, a, b, c, fire, ice, trigger };
            defs.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++) defs.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            data.ApplyModifiedPropertiesWithoutUndo(); AssetDatabase.CreateAsset(catalog, "Assets/_Project/Tests/Fixtures/Run/SinglePlayerWands/Catalog.asset");
            var seeds = ScriptableObject.CreateInstance<SinglePlayerStartingInventory>(); data = new SerializedObject(seeds);
            var items = data.FindProperty("_items"); items.arraySize = 8;
            Seed(items, 0, bag, ItemLocationKind.EquippedBackpack); Seed(items, 1, a, ItemLocationKind.EquippedWand, 0);
            Seed(items, 2, b, ItemLocationKind.EquippedWand, 1); Seed(items, 3, c, ItemLocationKind.EquippedWand, 2);
            Seed(items, 4, fire, ItemLocationKind.WandSlot, 0, 1); Seed(items, 5, ice, ItemLocationKind.WandSlot, 0, 2);
            Seed(items, 6, trigger, ItemLocationKind.WandSlot, 0, 3); Seed(items, 7, fire, ItemLocationKind.WandSlot, 1, 3);
            data.ApplyModifiedPropertiesWithoutUndo(); AssetDatabase.CreateAsset(seeds, "Assets/_Project/Tests/Fixtures/Run/SinglePlayerWands/StartingInventory.asset");
            if (!catalog.TryCreateIndex(out ItemCatalogIndex index, out string error) || !seeds.TryCreate(index, out _, out error)) throw new InvalidOperationException(error);
            var actions = ScriptableObject.CreateInstance<InputActionAsset>(); var map = actions.AddActionMap("Wands");
            for (int i = 1; i <= 3; i++) map.AddAction("Select" + i, InputActionType.Button, "<Keyboard>/" + i);
            AssetDatabase.CreateAsset(actions, "Assets/_Project/Tests/Fixtures/Run/SinglePlayerWands/WandActions.asset");
            Scene original = SceneManager.GetActiveScene();
            Scene scene = EditorSceneManager.OpenScene("Assets/_Project/Scenes/Tests/InventorySandbox.unity", OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                SinglePlayerInventorySession session = Find<SinglePlayerInventorySession>(scene);
                Set(session, "_catalog", catalog); Set(session, "_startingInventory", seeds);
                Set(session, "_sandboxName", "WandSandbox");
                var ui = Find<SinglePlayerInventoryController>(scene); Set(ui, "_openOnStart", false);
                var camera = Find<Camera>(scene); camera.orthographic = false; camera.fieldOfView = 60;
                camera.clearFlags = CameraClearFlags.Skybox; camera.gameObject.AddComponent<AudioListener>(); camera.gameObject.AddComponent<CinemachineBrain>();
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/WizardPlayer.prefab");
                if (prefab == null) throw new InvalidOperationException("已有 WizardPlayer Prefab 缺失。");
                var player = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene); player.transform.position = new Vector3(0, .1f, 0);
                var adapter = player.AddComponent<OwnedWandCastAdapter>(); Set(adapter, "_session", session);
                for (int i = 1; i <= 3; i++)
                { var reference = InputActionReference.Create(actions.FindAction("Select" + i, true));
                    AssetDatabase.CreateAsset(reference, "Assets/_Project/Tests/Fixtures/Run/SinglePlayerWands/Action_Select" + i + ".asset"); Set(adapter, "_select" + i, reference); }
                var pivot = player.transform.Find("CameraRoot");
                if (pivot == null) throw new InvalidOperationException("玩家 CameraRoot 缺失。");
                var virtualCamera = new GameObject("Wand Orbit Camera").AddComponent<CinemachineCamera>(); virtualCamera.Follow = pivot;
                var follow = virtualCamera.gameObject.AddComponent<CinemachineFollow>(); follow.FollowOffset = new Vector3(.7f, .4f, -5);
                follow.TrackerSettings.BindingMode = BindingMode.LockToTarget;
                virtualCamera.gameObject.AddComponent<CinemachineRotateWithFollowTarget>();
                camera.transform.position = new Vector3(.7f, 1.8f, -5); camera.transform.rotation = Quaternion.identity;
                var light = new GameObject("Sun").AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.3f;
                light.transform.rotation = Quaternion.Euler(45, -35, 0);
                var floor = GameObject.CreatePrimitive(PrimitiveType.Cube); floor.name = "Ground";
                floor.transform.position = new Vector3(0, -.5f, 10); floor.transform.localScale = new Vector3(50, 1, 50);
                for (int i = 0; i < 3; i++)
                { var target = GameObject.CreatePrimitive(PrimitiveType.Cube); target.name = "Target " + (i + 1);
                    target.transform.position = new Vector3((i - 1) * 4, 1, 12 + i * 2); target.transform.localScale = new Vector3(2, 2, 1);
                    var health = target.AddComponent<HealthComponent>(); Set(health, "_teamId", 2); Set(health, "_maxHp", 1000f); }
                BuildHud(session);
                // SaveScene 指定新路径；源 InventorySandbox 磁盘内容不写回，当前用户 Scene 不参与保存。
                if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("新 WandSandbox 保存失败。");
            }
            finally { SceneManager.SetActiveScene(original); EditorSceneManager.CloseScene(scene, true); }
            Report("Created", null);
        }
        private static void MigrateLegacyTestWands()
        {
            string[] guids = AssetDatabase.FindAssets("t:ItemDefinition", new[] { "Assets/_Project/Tests/Fixtures/Run" });
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]); var item = AssetDatabase.LoadAssetAtPath<ItemDefinition>(path);
                if (item.Kind != ItemKind.Wand || item.WandDefinition != null) continue;
                var data = new SerializedObject(item); int capacity = data.FindProperty("_wandProgramCapacity").intValue;
                if (capacity < 1) throw new InvalidOperationException("旧法杖容量非法，未迁移：" + path);
                string statsPath = Path.ChangeExtension(path, null) + "_Stats.asset";
                if (File.Exists(statsPath)) throw new InvalidOperationException("迁移目标已有文件，未覆盖：" + statsPath);
                var stats = ScriptableObject.CreateInstance<WandDefinition>(); var parameters = new SerializedObject(stats);
                parameters.FindProperty("_programCapacity").intValue = capacity; parameters.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.CreateAsset(stats, statsPath); data.FindProperty("_wandDefinition").objectReferenceValue = stats;
                data.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(item); AssetDatabase.SaveAssetIfDirty(item);
            }
        }
        private static WandDefinition MakeWand(string name, float mana, float regen, float cooldown)
        { var value = ScriptableObject.CreateInstance<WandDefinition>(); var data = new SerializedObject(value);
            data.FindProperty("_maxMana").floatValue = mana; data.FindProperty("_manaRegenPerSecond").floatValue = regen;
            data.FindProperty("_castCooldown").floatValue = cooldown; data.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.CreateAsset(value, "Assets/_Project/Tests/Fixtures/Run/SinglePlayerWands/Stats_" + name + ".asset"); return value; }
        private static SpellDefinition Spell(string name) => AssetDatabase.LoadAssetAtPath<SpellDefinition>("Assets/_Project/ScriptableObjects/Spell/Definitions/" + name + ".asset");
        private static ItemDefinition Define(string id, string display, ItemKind kind, WandDefinition wand = null, SpellDefinition spell = null)
        { var value = ScriptableObject.CreateInstance<ItemDefinition>(); var data = new SerializedObject(value);
            data.FindProperty("_contentId").stringValue = "wandtest." + id; data.FindProperty("_displayName").stringValue = display;
            data.FindProperty("_kind").intValue = (int)kind; data.FindProperty("_wandDefinition").objectReferenceValue = wand;
            data.FindProperty("_spellDefinition").objectReferenceValue = spell;
            if (kind == ItemKind.Backpack) { data.FindProperty("_backpackWidth").intValue = 4; data.FindProperty("_backpackHeight").intValue = 4; }
            data.ApplyModifiedPropertiesWithoutUndo(); AssetDatabase.CreateAsset(value, "Assets/_Project/Tests/Fixtures/Run/SinglePlayerWands/" + id + ".asset"); return value; }
        private static void Seed(SerializedProperty items, int i, ItemDefinition definition, ItemLocationKind kind, int slot = 0, int parent = -1)
        { var item = items.GetArrayElementAtIndex(i); item.FindPropertyRelative("Definition").objectReferenceValue = definition;
            item.FindPropertyRelative("RemainingCharges").intValue = 0; item.FindPropertyRelative("HasUnlimitedCharges").boolValue = definition.Kind == ItemKind.Spell;
            item.FindPropertyRelative("Location").intValue = (int)kind; item.FindPropertyRelative("SlotIndex").intValue = slot;
            item.FindPropertyRelative("OwnerSeedIndex").intValue = parent; }
        private static T Find<T>(Scene scene) where T : Component
        { foreach (var root in scene.GetRootGameObjects()) { var value = root.GetComponentInChildren<T>(true); if (value != null) return value; } return null; }
        private static void Set(UnityEngine.Object target, string property, UnityEngine.Object value)
        { var data = new SerializedObject(target); data.FindProperty(property).objectReferenceValue = value; data.ApplyModifiedPropertiesWithoutUndo(); }
        private static void Set(UnityEngine.Object target, string property, bool value)
        { var data = new SerializedObject(target); data.FindProperty(property).boolValue = value; data.ApplyModifiedPropertiesWithoutUndo(); }
        private static void Set(UnityEngine.Object target, string property, string value)
        { var data = new SerializedObject(target); data.FindProperty(property).stringValue = value; data.ApplyModifiedPropertiesWithoutUndo(); }
        private static void Set(UnityEngine.Object target, string property, int value)
        { var data = new SerializedObject(target); data.FindProperty(property).intValue = value; data.ApplyModifiedPropertiesWithoutUndo(); }
        private static void Set(UnityEngine.Object target, string property, float value)
        { var data = new SerializedObject(target); data.FindProperty(property).floatValue = value; data.ApplyModifiedPropertiesWithoutUndo(); }
        private static void BuildHud(SinglePlayerInventorySession session)
        {
            var canvas = new GameObject("Wand Resources", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvas.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1440, 900);
            var hud = canvas.AddComponent<WandResourceHud>(); var data = new SerializedObject(hud);
            data.FindProperty("_session").objectReferenceValue = session; var rows = data.FindProperty("_rows"); rows.arraySize = 3;
            for (int i = 0; i < 3; i++)
            {
                RectTransform row = Rect(canvas.transform, "Wand" + (i + 1), new Vector2(24, -24 - i * 84), new Vector2(300, 72));
                var selection = row.gameObject.AddComponent<Image>(); selection.color = new Color(.08f, .16f, .24f, .85f);
                var label = Rect(row, "ManaValue", new Vector2(8, -4), new Vector2(280, 30)).gameObject.AddComponent<Text>(); label.fontSize = 18; label.color = Color.white;
                var mana = Bar(row, "Mana", new Vector2(8, -37), new Color(.15f, .6f, 1));
                var cooldown = Bar(row, "Cooldown", new Vector2(8, -57), new Color(1, .6f, .15f));
                var item = rows.GetArrayElementAtIndex(i); item.FindPropertyRelative("Label").objectReferenceValue = label;
                item.FindPropertyRelative("Mana").objectReferenceValue = mana; item.FindPropertyRelative("Cooldown").objectReferenceValue = cooldown;
                item.FindPropertyRelative("Selection").objectReferenceValue = selection;
            }
            var help = Rect(canvas.transform, "Controls", new Vector2(24, -285), new Vector2(540, 70)).gameObject.AddComponent<Text>();
            help.text = "1/2/3 切换 · 左键施法 · Tab 编程\n蓝色：Mana　橙色：剩余冷却"; help.fontSize = 18; help.color = Color.white;
            var crosshair = Rect(canvas.transform, "Crosshair", Vector2.zero, new Vector2(5, 5));
            crosshair.anchorMin = crosshair.anchorMax = new Vector2(.5f, .5f); crosshair.anchoredPosition = Vector2.zero;
            crosshair.gameObject.AddComponent<Image>().color = Color.white; data.ApplyModifiedPropertiesWithoutUndo();
        }
        private static Image Bar(Transform parent, string name, Vector2 position, Color color)
        { var bar = Rect(parent, name, position, new Vector2(280, 12)).gameObject.AddComponent<Image>(); bar.color = color;
            bar.sprite = FilledSprite();
            bar.type = Image.Type.Filled; bar.fillMethod = Image.FillMethod.Horizontal; bar.fillAmount = 1; return bar; }
        private static Sprite FilledSprite()
        {
            // UGUI Image 在 activeSprite == null 时直接生成普通四边形，Filled 类型也不会裁切。
            var sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            if (sprite == null) throw new InvalidOperationException("UGUI 内置 Sprite 不可用。"); return sprite;
        }
        private static void RepairHud()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || SceneManager.GetActiveScene().path == ScenePath)
                throw new InvalidOperationException("不能保存当前用户 Scene；修复只针对本 Task 生成的独立副本。");
            for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).isDirty)
                throw new InvalidOperationException("有未保存场景，没有自动保存或丢弃。");
            Scene original = SceneManager.GetActiveScene(); Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            try
            {
                var hud = Find<WandResourceHud>(scene); if (hud == null) throw new InvalidOperationException("不是生成的三杖场景。");
                Sprite sprite = FilledSprite();
                foreach (Image image in hud.GetComponentsInChildren<Image>()) if (image.type == Image.Type.Filled) image.sprite = sprite;
                // 只保存此次新建的 WandSandbox；原 InventorySandbox/P7/用户当前 Scene 不保存。
                if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("HUD 修复保存失败。");
            }
            finally { SceneManager.SetActiveScene(original); EditorSceneManager.CloseScene(scene, true); }
            Report("HudRepaired", null);
        }
        private static RectTransform Rect(Transform parent, string name, Vector2 position, Vector2 size)
        { var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>(); rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0, 1); rect.pivot = new Vector2(0, 1); rect.anchoredPosition = position; rect.sizeDelta = size; return rect; }
        private static void Report(string state, string message)
        { Directory.CreateDirectory("Temp/SinglePlayer"); File.WriteAllText("Temp/SinglePlayer/wand-sandbox-report.json", JsonUtility.ToJson(new Result { State = state, Message = message, Scene = ScenePath }, true)); }
        [Serializable] private sealed class Result { public string State, Message, Scene; }
    }
}
