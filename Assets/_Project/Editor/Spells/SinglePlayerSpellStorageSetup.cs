using System;
using System.IO;
using System.Text;
using Game.Core;
using Game.Run;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.EditorTools
{
    /// <summary>只为用户新增的Spells区域和第七工具格接线；独立Review先验证，正式Scene单Undo且不保存。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerSpellStorageSetup
    {
        public const string Review = "Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_SpellStorageReview.unity";
        private const string Request = "Temp/SinglePlayer/spell-storage-setup.request";
        private const string Evidence = "TestResults/SinglePlayer/M6-T1-SpellStorage";
        private static bool _undo;
        static SinglePlayerSpellStorageSetup() { EditorApplication.update += Poll; }
        private static void Poll()
        {
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            string command = File.ReadAllText(Request).Trim(); File.Delete(Request);
            try
            {
                if (EditorUtility.scriptCompilationFailed) throw new InvalidOperationException("编译失败，拒绝用旧Assembly接线。");
                if (command == "prepare") Prepare();
                else if (command == "apply-current") ApplyCurrent();
                else throw new InvalidOperationException("未知法术专区命令。");
            }
            catch (Exception e) { Write("setup-error-" + DateTime.UtcNow.Ticks + ".txt", e.ToString()); GameLog.Error(e.ToString(), "SpellStorageSetup"); }
        }
        private static void Prepare()
        {
            var original = SceneManager.GetActiveScene();
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).path == Review) throw new InvalidOperationException("Review已打开，不替用户保存。");
            var scene = EditorSceneManager.OpenScene(Review, OpenSceneMode.Additive);
            try
            {
                // 新对象默认进入active Scene；必须在创建控件之前设置，避免污染用户当前Scene。
                SceneManager.SetActiveScene(scene); _undo = false;
                string report = Apply(scene);
                if (!EditorSceneManager.SaveScene(scene, Review)) throw new IOException("Review保存失败。");
                Write("prepared.txt", report);
                SessionState.SetString("Norn.HubAlignment.ReviewPath", Review);
            }
            finally { EditorSceneManager.CloseScene(scene, true); SceneManager.SetActiveScene(original); }
        }
        [MenuItem("Tools/Norn/据点界面/接线12法术位与七道具位（Undo，不保存）")]
        private static void ApplyCurrent()
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.path != SinglePlayerHubSceneSetup.Path || EditorApplication.isPlaying) throw new InvalidOperationException("只允许正式据点Edit Mode接线。");
            Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("接线法术专区和七道具位"); _undo = true;
            try { var report = Apply(scene); Undo.CollapseUndoOperations(group); EditorSceneManager.MarkSceneDirty(scene); Write("current-applied.txt", report + "\n正式Scene未保存，请用户自行保存。"); }
            catch { Undo.RevertAllDownToGroup(group); throw; }
            finally { _undo = false; }
        }
        private static string Apply(Scene scene)
        {
            SinglePlayerInventoryController backpack = null, warehouse = null;
            foreach (var root in scene.GetRootGameObjects()) foreach (var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))
                if (Get<BackpackWorkspaceView>(ui, "_backpackWorkspace") != null) backpack = ui; else warehouse = ui;
            if (backpack == null || warehouse == null) throw new InvalidOperationException("缺少独立背包/仓库，不能猜测接线。");
            var panel = Get<GameObject>(backpack, "_panelRoot").transform;
            var spells = FindNamed(panel, "Spells") as RectTransform;
            if (spells == null) throw new InvalidOperationException("缺少用户黄色Spells区域；不重新生成外框。");
            var right = panel.Find("RightPart");
            string rightBefore = Snapshot(right), warehouseBefore = Snapshot(Get<GameObject>(warehouse, "_panelRoot").transform);
            Vector2 anchorsMin=spells.anchorMin, anchorsMax=spells.anchorMax, position=spells.anchoredPosition, size=spells.sizeDelta, pivot=spells.pivot;
            var viewport = spells.Find("SpellStorageRuntime_Viewport") as RectTransform;
            if (viewport == null)
            {
                viewport = NewRect(spells, "SpellStorageRuntime_Viewport"); Stretch(viewport, 2);
                Add<RectMask2D>(viewport.gameObject);
            }
            var content = viewport.Find("Content") as RectTransform;
            if (content == null)
            {
                content = NewRect(viewport, "Content"); content.anchorMin = content.anchorMax = content.pivot = new Vector2(0, 1);
                content.anchoredPosition = Vector2.zero; content.sizeDelta = new Vector2(720, 64);
            }
            var scroll = Add<ScrollRect>(viewport.gameObject); Record(scroll);
            scroll.viewport = viewport; scroll.content = content; scroll.horizontal = true; scroll.vertical = false;
            scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 24;
            var view = Add<SpellStorageView>(spells.gameObject); Record(view); view.Configure(content); Set(backpack, "_spellStorage", view);
            var tools = Get<ToolbeltView>(Get<BackpackWorkspaceView>(backpack, "_backpackWorkspace"), "_tools");
            if (tools == null) throw new InvalidOperationException("未找到现有道具栏。");
            var slots = new ToolbeltSlotView[HeldItemSlots.ToolCount];
            for (int i = 0; i < slots.Length; i++)
            {
                string name = "Slot" + HeldItemSlots.KeyLabel(i + 3);
                var node = tools.transform.Find(name);
                if (node == null) throw new InvalidOperationException("缺少用户道具格：" + name);
                slots[i] = Add<ToolbeltSlotView>(node.gameObject); Record(slots[i]);
                var icon = node.Find("Icon")?.GetComponent<Image>(); var label = node.Find("Caption")?.GetComponent<Text>();
                if (icon == null || label == null) throw new InvalidOperationException("道具格缺少Icon/Caption：" + name);
                slots[i].Configure(i, icon, label); Record(label); label.text = HeldItemSlots.KeyLabel(i + 3) + "\n拖入道具";
            }
            Record(tools); tools.Configure(slots);
            HeldItemHud hud = null;
            foreach (var root in scene.GetRootGameObjects()) { hud = root.GetComponentInChildren<HeldItemHud>(true); if (hud != null) break; }
            if (hud == null) throw new InvalidOperationException("缺少手持HUD。");
            var data = new SerializedObject(hud); var existing = data.FindProperty("_cells");
            if (existing.arraySize == 9)
            {
                var last = existing.GetArrayElementAtIndex(8);
                var oldRoot = ((Image)last.FindPropertyRelative("Selection").objectReferenceValue).transform;
                var copy = UnityEngine.Object.Instantiate(oldRoot.gameObject, oldRoot.parent, false); copy.name = "Slot0";
                if (_undo) Undo.RegisterCreatedObjectUndo(copy, "增加0键HUD格");
                var cells = new HeldItemHud.Cell[HeldItemSlots.TotalCount];
                for (int i = 0; i < 9; i++)
                {
                    var cell = existing.GetArrayElementAtIndex(i);
                    cells[i] = new HeldItemHud.Cell { Icon = Ref<Image>(cell, "Icon"), Selection = Ref<Image>(cell, "Selection"),
                        Mana = Ref<Image>(cell, "Mana"), Cooldown = Ref<Image>(cell, "Cooldown"), Title = Ref<Text>(cell, "Title"), Value = Ref<Text>(cell, "Value") };
                }
                cells[9] = new HeldItemHud.Cell { Selection = copy.GetComponent<Image>(), Icon = copy.transform.Find("Icon").GetComponent<Image>(),
                    Mana = copy.transform.Find("Mana").GetComponent<Image>(), Cooldown = copy.transform.Find("Cooldown").GetComponent<Image>(),
                    Title = copy.transform.Find("Title").GetComponent<Text>(), Value = copy.transform.Find("Value").GetComponent<Text>() };
                Record(hud); hud.Configure(backpack.Session, cells); Record(hud.gameObject); hud.gameObject.name = "HeldItemRow_1至0";
            }
            else if (existing.arraySize != HeldItemSlots.TotalCount) throw new InvalidOperationException("HUD格数异常。");
            // 仅校验黄色区域原有几何字段；新增children会改变RectTransform序列化的child列表。
            if (rightBefore != Snapshot(right) || warehouseBefore != Snapshot(Get<GameObject>(warehouse, "_panelRoot").transform))
                throw new InvalidOperationException("修改越界：右侧或仓库发生变化。");
            if (spells.anchorMin!=anchorsMin || spells.anchorMax!=anchorsMax || spells.anchoredPosition!=position || spells.sizeDelta!=size || spells.pivot!=pivot)
                throw new InvalidOperationException("修改越界：黄色区域几何发生变化。");
            return "用户Spells黄色区域已接12位真实储存；七道具格4–0与十格HUD已接线。外框几何未重排，右侧与仓库组件审计一致。";
        }
        private static T Ref<T>(SerializedProperty cell, string field) where T : UnityEngine.Object => (T)cell.FindPropertyRelative(field).objectReferenceValue;
        private static Transform FindNamed(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++) { var found = FindNamed(root.GetChild(i), name); if (found != null) return found; }
            return null;
        }
        private static string Snapshot(Transform root)
        {
            if (root == null) throw new InvalidOperationException("审计区域缺失。");
            var result = new StringBuilder(); foreach (var component in root.GetComponentsInChildren<Component>(true))
                if (component != null) result.Append(component.GetInstanceID()).Append(EditorJsonUtility.ToJson(component));
            return result.ToString();
        }
        private static T Get<T>(UnityEngine.Object target, string field) where T : UnityEngine.Object =>
            new SerializedObject(target).FindProperty(field).objectReferenceValue as T;
        private static void Set(UnityEngine.Object target, string field, UnityEngine.Object value)
        { Record(target); var data = new SerializedObject(target); data.FindProperty(field).objectReferenceValue = value; data.ApplyModifiedPropertiesWithoutUndo(); }
        private static void Record(UnityEngine.Object target) { if (_undo) Undo.RecordObject(target, "法术专区接线"); }
        private static T Add<T>(GameObject root) where T : Component => root.GetComponent<T>() ?? (_undo ? Undo.AddComponent<T>(root) : root.AddComponent<T>());
        private static RectTransform NewRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform)); if (_undo) Undo.RegisterCreatedObjectUndo(go, "法术专区控件");
            go.transform.SetParent(parent, false); return (RectTransform)go.transform;
        }
        private static void Stretch(RectTransform rect, float margin)
        { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = Vector2.one * margin; rect.offsetMax = -Vector2.one * margin; }
        private static void Write(string name, string result)
        { Directory.CreateDirectory(Evidence); File.WriteAllText(Evidence + "/" + name, DateTime.UtcNow.ToString("O") + "\n" + result); }
    }
}
