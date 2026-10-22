using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Game.Core;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.EditorTools
{
    /// <summary>先列出真实引用，再增量接线。不会以文件名或Editor隐藏状态判断一个对象是否可删。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerInventoryAuditSetup
    {
        private const string Formal = "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        internal const string Review = "Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_InventoryAuditReview.unity";
        private const string Request = "Temp/SinglePlayer/inventory-audit.request";
        private const string Evidence = "TestResults/SinglePlayer/M6-T8-InventoryAudit";
        private const string UndoName = "手持栏分型接线与三界面遗留清理";
        private static bool _undo;

        static SinglePlayerInventoryAuditSetup() => EditorApplication.update += Poll;
        private static void Poll()
        {
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            string command = File.ReadAllText(Request).Trim(); File.Delete(Request);
            try
            {
                if (EditorUtility.scriptCompilationFailed) throw new InvalidOperationException("编译失败，拒绝使用旧Assembly。");
                if (command == "inspect") Audit(SceneManager.GetActiveScene(), "inspection.txt");
                else if (command == "references") References();
                else if (command == "prepare") Prepare(false);
                else if (command == "prepare-fixed") Prepare(true);
                else if (command == "apply-current") ApplyCurrent();
                else throw new ArgumentException(command);
            }
            catch (Exception error) { Write("setup-error.txt", error.ToString()); GameLog.Error(error.ToString(), "InventoryAudit"); }
        }
        private static void RequireClean()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("请保存要保留的Scene；不代为保存或丢弃。");
        }
        private static void Prepare(bool fix)
        {
            RequireClean(); if (SceneManager.GetSceneByPath(Review).isLoaded) throw new InvalidOperationException("Review已打开，拒绝覆盖。");
            var original = SceneManager.GetActiveScene(); string hash = Hash();
            if (!File.Exists(Review)) { if (!AssetDatabase.CopyAsset(Formal, Review)) throw new IOException("复制Review失败。"); }
            else { File.Copy(Formal, Review, true); AssetDatabase.ImportAsset(Review, ImportAssetOptions.ForceUpdate); }
            var scene = EditorSceneManager.OpenScene(Review, OpenSceneMode.Additive);
            try
            {
                // 新对象只属于Review；正式Scene不能承担临时UI创建或Canvas重排。
                SceneManager.SetActiveScene(scene); _undo = false; if (fix) Apply(scene);
                EditorSceneManager.SaveScene(scene, Review); Audit(scene, "review-layout.txt");
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true); SceneManager.SetActiveScene(original);
                Write("review-isolation.txt", "FormalHashUnchanged=" + (hash == Hash()) + "\nFormalDirty=" + original.isDirty);
            }
        }
        private static void Audit(Scene scene, string file)
        {
            var report = new StringBuilder(); report.AppendLine(DateTime.UtcNow.ToString("O"));
            report.AppendLine("Scene=" + scene.path + " Dirty=" + scene.isDirty + " SceneCount=" + SceneManager.sceneCount);
            foreach (var root in scene.GetRootGameObjects()) foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component == null) { report.AppendLine("MissingScript under " + root.name); continue; }
                if (!(component is HeldItemHud || component is SinglePlayerInventoryController || component is ShopController || component is WalletView || component is WorldPickupPromptView || component is InventoryWorkspaceView || component is WandInventoryView)) continue;
                report.AppendLine(component.GetType().Name + " " + Path(component.transform) + " Enabled=" + component.enabled);
                var iterator = new SerializedObject(component).GetIterator();
                while (iterator.Next(true))
                    if (iterator.propertyType == SerializedPropertyType.ObjectReference)
                        report.AppendLine("  " + iterator.propertyPath + "=" + (iterator.objectReferenceValue != null ? PathOf(iterator.objectReferenceValue) : "NULL"));
                if (component is HeldItemHud)
                    for (int i = 0; i < component.transform.childCount; i++)
                    {
                        var slot = component.transform.GetChild(i); report.Append("  Children " + slot.name + ":");
                        foreach (Transform child in slot) report.Append(" " + child.name); report.AppendLine();
                        foreach (var label in slot.GetComponentsInChildren<Text>(true))
                            report.AppendLine("    Text " + Path(label.transform) + " Active=" + label.gameObject.activeSelf + " Enabled=" + label.enabled
                                + " Color=" + label.color + " Font=" + label.fontSize + " Rect=" + label.rectTransform.rect + " Position=" + label.rectTransform.anchoredPosition);
                    }
            }
            Write(file, report.ToString());
        }
        private static string PathOf(UnityEngine.Object value) => value is Component c ? Path(c.transform) : value is GameObject go ? Path(go.transform) : value.name;
        private static string Path(Transform value) => value.parent != null ? Path(value.parent) + "/" + value.name : value.name;
        private static void References()
        {
            var report = new StringBuilder();
            // GetDependencies解析Unity自己的二进制Scene/Prefab引用；文本rg不适用于这些资产。
            var scripts = new[] { "ShopBackpackView", "ShopInventorySelection", "InventoryWorkspaceView", "WorldPickupPromptView", "WandEditorController" };
            foreach (string name in scripts)
            {
                string target = null;
                foreach (string guid in AssetDatabase.FindAssets(name + " t:MonoScript", new[] { "Assets/_Project" }))
                { string path = AssetDatabase.GUIDToAssetPath(guid); if (System.IO.Path.GetFileNameWithoutExtension(path) == name) { target = path; break; } }
                report.AppendLine(name + "=" + target); if (target == null) continue;
                foreach (string guid in AssetDatabase.FindAssets("t:Scene t:Prefab", new[] { "Assets/_Project" }))
                {
                    string asset = AssetDatabase.GUIDToAssetPath(guid);
                    foreach (string dependency in AssetDatabase.GetDependencies(asset, false))
                        if (dependency == target) { report.AppendLine("  Consumer=" + asset); break; }
                }
            }
            Write("references.txt", report.ToString());
        }
        private static void Apply(Scene scene)
        {
            bool fixedPrompt = false;
            foreach (var root in scene.GetRootGameObjects()) if (root.GetComponentInChildren<NearbyPickupListView>(true) != null) fixedPrompt = true;
            foreach (var root in scene.GetRootGameObjects()) foreach (var hud in root.GetComponentsInChildren<HeldItemHud>(true))
            {
                Record(hud); var data = new SerializedObject(hud); var cells = data.FindProperty("_cells");
                if (cells.arraySize != HeldItemSlots.TotalCount || hud.transform.childCount != HeldItemSlots.TotalCount)
                    throw new InvalidOperationException("保留作者十格排列；实际格数不是10，请先检查层级。");
                for (int i = 0; i < cells.arraySize; i++)
                {
                    var slot = hud.transform.GetChild(i); var cell = cells.GetArrayElementAtIndex(i);
                    Ref(cell, "Icon", Find<Image>(slot, "Icon"));
                    Ref(cell, "Selection", slot.GetComponent<Image>());
                    Ref(cell, "Title", Find<Text>(slot, "Title")); Ref(cell, "Value", Find<Text>(slot, "Value"));
                    Ref(cell, "Mana", i < HeldItemSlots.WandCount ? Find<Image>(slot, "Mana") : null);
                    Ref(cell, "Cooldown", i < HeldItemSlots.WandCount ? Find<Image>(slot, "Cooldown") : null);
                    // 高亮仍用作者原引用，不将Frame换成一块会覆盖图案的纯色Image。
                }
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var cleanup = new StringBuilder();
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var view in root.GetComponentsInChildren<ContainerCompositionView>(true))
                    foreach (var label in view.GetComponentsInChildren<Text>(true))
                        if (label.text == "当前容器 · F 切换") { Record(label); label.text = "当前容器"; cleanup.AppendLine("Removed stale F instruction " + Path(label.transform)); }
                foreach (var controller in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))
                {
                    var data = new SerializedObject(controller);
                    if (data.FindProperty("_backpackWorkspace").objectReferenceValue == null) continue;
                    var legacy = data.FindProperty("_workspace").objectReferenceValue as InventoryWorkspaceView;
                    if (legacy == null) continue;
                    Record(controller); data.FindProperty("_workspace").objectReferenceValue = null; data.ApplyModifiedPropertiesWithoutUndo();
                    if (!ExternallyReferenced(scene, legacy)) { cleanup.AppendLine("Removed obsolete component " + Path(legacy.transform)); Remove(legacy); }
                }
                foreach (var legacy in root.GetComponentsInChildren<InventoryWorkspaceView>(true))
                    if (!legacy.enabled && !ExternallyReferenced(scene, legacy))
                    { cleanup.AppendLine("Removed disabled layout component " + Path(legacy.transform)); Remove(legacy); }
                foreach (var legacy in root.GetComponentsInChildren<WorldPickupPromptView>(true))
                {
                    if (legacy.enabled || !fixedPrompt) continue;
                    if (ExternallyReferenced(scene, legacy.gameObject)) continue;
                    cleanup.AppendLine("Removed disabled world prompt " + Path(legacy.transform)); Remove(legacy.gameObject);
                }
            }
            Write(_undo ? "current-cleanup.txt" : "review-cleanup.txt", cleanup.ToString());
        }
        private static bool ExternallyReferenced(Scene scene, UnityEngine.Object target)
        {
            var targetRoot = target as GameObject;
            foreach (var root in scene.GetRootGameObjects()) foreach (var source in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (source == null || source == target || targetRoot != null && source.transform.IsChildOf(targetRoot.transform)) continue;
                var iterator = new SerializedObject(source).GetIterator();
                while (iterator.Next(true))
                {
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var reference = iterator.objectReferenceValue;
                    if (reference == target) return true;
                    if (targetRoot != null && reference is Component c && c.transform.IsChildOf(targetRoot.transform)) return true;
                    if (targetRoot != null && reference is GameObject go && go.transform.IsChildOf(targetRoot.transform)) return true;
                }
            }
            return false;
        }
        private static void Remove(UnityEngine.Object target)
        { if (_undo) Undo.DestroyObjectImmediate(target); else UnityEngine.Object.DestroyImmediate(target); }
        private static T Find<T>(Transform slot, string name) where T : Component
        { var child = slot.Find(name); return child != null ? child.GetComponent<T>() : null; }
        private static void Ref(SerializedProperty cell, string name, UnityEngine.Object value) => cell.FindPropertyRelative(name).objectReferenceValue = value;
        private static void Record(UnityEngine.Object value) { if (_undo) Undo.RecordObject(value, UndoName); }
        [MenuItem("Tools/Norn/库存/接入精修手持栏（Undo，不保存）")]
        private static void ApplyCurrent()
        {
            var scene = SceneManager.GetActiveScene(); if (scene.path != Formal) throw new InvalidOperationException("只增量处理正式据点。");
            string hash = Hash(); Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName(UndoName);
            try
            {
                _undo = true; Apply(scene); Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(group);
                if (hash != Hash()) throw new InvalidOperationException("正式Scene磁盘意外变化。");
                EditorSceneManager.MarkSceneDirty(scene); Audit(scene, "current-applied.txt");
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
            finally { _undo = false; }
        }
        private static string Hash()
        { using var sha = SHA256.Create(); return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(Formal))); }
        private static void Write(string name, string value) { Directory.CreateDirectory(Evidence); File.WriteAllText(Evidence + "/" + name, value); }
    }
}
