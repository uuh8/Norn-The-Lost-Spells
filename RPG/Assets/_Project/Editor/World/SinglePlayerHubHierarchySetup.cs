using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Game.Character;
using Game.Core;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>只整理作者层级；不重装系统、不重置布局、不保存用户当前 Scene。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerHubHierarchySetup
    {
        internal const string Review = "Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_HierarchyReview.unity";
        private const string Request = "Temp/SinglePlayer/hub-hierarchy.request";
        private const string Evidence = "TestResults/SinglePlayer/M6-T1-Hierarchy";
        private const string ReviewKey = "Norn.HubAlignment.ReviewPath";
        private const string UndoName = "整理据点 Hierarchy";

        static SinglePlayerHubHierarchySetup() { EditorApplication.update += Poll; }

        private static void Poll()
        {
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            string command = File.ReadAllText(Request).Trim(); File.Delete(Request);
            try
            {
                if (EditorUtility.scriptCompilationFailed) throw new InvalidOperationException("编译失败，不执行旧作者工具。");
                if (command == "prepare") Prepare();
                else if (command == "update-review") Prepare(true);
                else if (command == "apply-current") ApplyCurrent();
                else if (command == "inspect-current") WriteTree(SceneManager.GetActiveScene(), "current-tree.txt");
                else if (command == "clear-review") SessionState.EraseString(ReviewKey);
                else throw new InvalidOperationException("未知层级整理命令：" + command);
            }
            catch (Exception e) { Write("setup-error.txt", e.ToString()); GameLog.Error(e.ToString(), "HubHierarchy"); }
        }

        [MenuItem("Tools/Norn/据点界面/整理当前据点 Hierarchy（Undo，不保存）")]
        private static void ApplyCurrent()
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.path != SinglePlayerHubSceneSetup.Path || EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.scriptCompilationFailed)
                throw new InvalidOperationException("请在非 Play、编译成功的正式据点中整理。");
            // 允许已有未保存布局：本操作保留它们，不重新调用旧装配工具；异常时仅撤销本组。
            string diskHash = HashScene();
            Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName(UndoName);
            try
            {
                string report = ApplyChecked(scene, true);
                if (HashScene() != diskHash) throw new InvalidOperationException("正式 Scene 磁盘被意外修改。");
                Undo.CollapseUndoOperations(group); EditorSceneManager.MarkSceneDirty(scene); SessionState.EraseString(ReviewKey);
                WriteTree(scene, "current-tree.txt");
                Write("current-applied.txt", report + "\n正式 Scene 未保存；磁盘 SHA256 保持 " + diskHash + "\n用户自行保存，Ctrl+Z 整组撤销。\n");
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
        }

        private static void Prepare(bool update = false)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("用户 Scene 未保存，不替用户保存或丢弃。");
            if (SceneManager.GetSceneByPath(Review).isLoaded) throw new InvalidOperationException("用户打开了审阅副本，拒绝覆盖。");
            if (!update && File.Exists(Review)) throw new InvalidOperationException("独立审阅副本已存在，拒绝覆盖。");
            if (!update && !AssetDatabase.CopyAsset(SinglePlayerHubSceneSetup.Path, Review)) throw new IOException("复制据点失败。");
            var original = SceneManager.GetActiveScene(); var scene = EditorSceneManager.OpenScene(Review, OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene); if(!update)WriteTree(scene, "before-tree.txt");
                string report = ApplyChecked(scene, false);
                // 第二次整理必须不生成重复节点；用户后续自行调整时也能重复使用此工具。
                int objects = CountObjects(scene); string tree = Tree(scene);
                report += "\n重复整理：" + ApplyChecked(scene, false);
                if (CountObjects(scene) != objects || Tree(scene) != tree) throw new InvalidOperationException("重复整理改变了已整理层级。");
                if (!EditorSceneManager.SaveScene(scene, Review)) throw new IOException("独立副本保存失败。");
                SessionState.SetString(ReviewKey, Review); WriteTree(scene, "review-tree.txt"); Write("prepared.txt", report);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
                if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
            }
        }

        private static string ApplyChecked(Scene scene, bool undo)
        {
            var before = new Snapshot(scene); Apply(scene, undo);
            string report = before.Verify();
            // 场景搬迁使用 transform.root；玩家分组只能包含玩家，不能把据点系统一起搬走。
            var player = Find<WizardController>(scene);
            if (player.transform.root.name != "__Gameplay" || player.transform.root.childCount != 1 || player.transform.parent != player.transform.root)
                throw new InvalidOperationException("玩家的搬迁根必须是仅含 WizardPlayer 的 __Gameplay。");
            return report;
        }

        internal static void Apply(Scene scene, bool undo)
        {
            Transform systems = Group(scene, "__Systems", undo), gameplay = Group(scene, "__Gameplay", undo);
            Transform level = Group(scene, "__Level", undo), cameras = Group(scene, "__Cameras", undo);
            Transform sites = Group(scene, "__HubInteractions", undo), ui = Group(scene, "__UI", undo);
            // 保留已有根顺序对应的 Canvas 相对次序，不把不同 Canvas 合并或调整 sortingOrder。
            foreach (var root in scene.GetRootGameObjects())
            {
                Transform parent = null;
                if (root.name == "SinglePlayerInventory" || root.name == "EventSystem") parent = systems;
                else if (root.GetComponent<WizardController>() != null) parent = gameplay;
                else if (root.name == "Map_1" || root.name == "Lights" || root.name == "ReflectionProbes") parent = level;
                else if (root.name == "Main Camera" || root.name == "Wand Orbit Camera") parent = cameras;
                else if (root.name == "实验平台 · 真实库存" || root.name == "实验区提示" || root.name.StartsWith("远征点 · ", StringComparison.Ordinal) || root.name.EndsWith(" · E", StringComparison.Ordinal)) parent = sites;
                else if (root.name == "Container Material World") Rename(root, "__ElementWorld", undo);
                else if (root.GetComponent<Canvas>() != null && IsHubCanvas(root)) parent = ui;
                if (parent != null) Parent(root.transform, parent, undo);
            }

            var inventory = Find<SinglePlayerInventoryController>(scene); var workspace = inventory.GetComponent<InventoryWorkspaceView>();
            Rename(inventory.gameObject, "InventoryCanvas_背包法杖与仓库", undo);
            Rename(Get<GameObject>(inventory, "_panelRoot"), "InventoryWorkspaceRoot_背包与仓库", undo);
            Rename(Get<RectTransform>(workspace, "_bag").gameObject, "BackpackRegion_携带背包", undo);
            Rename(Get<RectTransform>(workspace, "_warehouse").gameObject, "WarehouseRegion_仓库", undo);
            Rename(Get<RectTransform>(workspace, "_wands").gameObject, "WandProgrammingRegion_法杖编程", undo);
            Rename(Get<RectTransform>(workspace, "_nearbyRegion").gameObject, "NearbyItemsRegion_附近物品", undo);
            Rename(Get<UnityEngine.UI.Text>(inventory, "_details").transform.parent.gameObject, "ItemDetailsRegion_物品详情", undo);
            var wand = inventory.GetComponentInChildren<WandInventoryView>(true);
            Rename(Get<RectTransform>(wand, "_equipmentRoot").gameObject, "WandEquipmentSlots_三根法杖", undo);

            var shop = Find<ShopController>(scene); Rename(shop.gameObject, "ShopCanvas_据点商店", undo);
            Rename(Get<GameObject>(shop, "_panel"), "ShopRoot_商店界面", undo);
            RenameCanvas(scene, "Wand Resources", "PlayerHUDCanvas_法杖状态", undo);
            RenameCanvas(scene, "Container Composition", "ContainerHUDCanvas_容器状态", undo);
            RenameCanvas(scene, "HubNavigation", "InteractionCanvas_交互提示", undo);
            var legacy=FindNamed(scene,"LegacyInventoryPanel");
            if(legacy!=null)Rename(legacy,"LegacyInventoryPanel_旧绑定载体（保留）",undo);
            // 只排序这些作者分组；不改变 __UI 内的 Canvas 相对顺序。
            var world=FindNamed(scene,"__ElementWorld");
            var order=new[]{systems,gameplay,level,cameras,sites,world!=null?world.transform:null,ui};
            for(int i=0;i<order.Length;i++)if(order[i]!=null && order[i].GetSiblingIndex()!=i)
            {if(undo)Undo.RecordObject(order[i],UndoName);order[i].SetSiblingIndex(i);}
        }

        private static bool IsHubCanvas(GameObject go)
        {
            return go.GetComponent<SinglePlayerInventoryController>() != null || go.GetComponent<ShopController>() != null ||
                go.name == "Wand Resources" || go.name == "Container Composition" || go.name == "HubNavigation" ||
                go.name == "PlayerHUDCanvas_法杖状态" || go.name == "ContainerHUDCanvas_容器状态" || go.name == "InteractionCanvas_交互提示";
        }

        private static void RenameCanvas(Scene scene, string oldName, string newName, bool undo)
        { var go = FindNamed(scene, oldName); if (go != null) Rename(go, newName, undo); }

        // 作者工具和空间诊断按名称找目标时要遍历子层级，不能继续假设所有目标都在 Scene 根。
        internal static IEnumerable<Transform> All(Scene scene)
        { foreach (var root in scene.GetRootGameObjects()) foreach (var item in root.GetComponentsInChildren<Transform>(true)) yield return item; }
        internal static GameObject FindNamed(Scene scene, string name)
        { foreach (var item in All(scene)) if (item.name == name) return item.gameObject; return null; }
        private static T Find<T>(Scene scene) where T : Component
        { foreach (var root in scene.GetRootGameObjects()) { var c = root.GetComponentInChildren<T>(true); if (c != null) return c; } throw new InvalidOperationException(typeof(T).Name + " 缺失。"); }
        private static T Get<T>(Object target, string field) where T : Object
        { return new SerializedObject(target).FindProperty(field).objectReferenceValue as T ?? throw new InvalidOperationException(target.name + "." + field + " 缺失。"); }
        private static Transform Group(Scene scene, string name, bool undo)
        {
            var old = FindNamed(scene, name);
            if (old != null)
            {
                if (old.transform.parent != null || old.transform.position != Vector3.zero || old.transform.rotation != Quaternion.identity || old.transform.localScale != Vector3.one || !old.activeSelf)
                    throw new InvalidOperationException(name + " 必须是激活的单位根 Transform；不自动重置用户修改。");
                return old.transform;
            }
            var go = new GameObject(name); SceneManager.MoveGameObjectToScene(go, scene);
            if (undo) Undo.RegisterCreatedObjectUndo(go, UndoName); return go.transform;
        }
        private static void Parent(Transform target, Transform parent, bool undo)
        { if (target.parent == parent) return; if (undo) Undo.SetTransformParent(target, parent, UndoName); else target.SetParent(parent, true); }
        private static void Rename(GameObject target, string name, bool undo)
        { if (target.name == name) return; if (undo) Undo.RecordObject(target, UndoName); target.name = name; }
        private static int CountObjects(Scene scene) { int count = 0; foreach (var _ in All(scene)) count++; return count; }

        [MenuItem("Tools/Norn/据点界面/选择背包与仓库总面板")]
        private static void SelectInventory() => Select(Get<GameObject>(Find<SinglePlayerInventoryController>(SceneManager.GetActiveScene()), "_panelRoot"));
        [MenuItem("Tools/Norn/据点界面/选择背包区域")]
        private static void SelectBag() => SelectRegion("_bag");
        [MenuItem("Tools/Norn/据点界面/选择仓库区域")]
        private static void SelectWarehouse() => SelectRegion("_warehouse");
        [MenuItem("Tools/Norn/据点界面/选择附近物品区域")]
        private static void SelectNearby() => SelectRegion("_nearbyRegion");
        [MenuItem("Tools/Norn/据点界面/选择法杖编程区域")]
        private static void SelectWands() => SelectRegion("_wands");
        [MenuItem("Tools/Norn/据点界面/选择商店面板")]
        private static void SelectShop() => Select(Get<GameObject>(Find<ShopController>(SceneManager.GetActiveScene()), "_panel"));
        private static void SelectRegion(string field) => Select(Get<RectTransform>(Find<InventoryWorkspaceView>(SceneManager.GetActiveScene()), field).gameObject);
        private static void Select(GameObject go) { Selection.activeGameObject = go; EditorGUIUtility.PingObject(go); }

        private static string Tree(Scene scene)
        {
            var text = new StringBuilder(scene.path + "\n");
            foreach (var root in scene.GetRootGameObjects()) Append(root.transform, text, 0); return text.ToString();
        }
        private static void Append(Transform t, StringBuilder text, int depth)
        {
            text.Append(' ', depth * 2).Append(t.name).Append(t.gameObject.activeSelf ? "" : " [inactive]").AppendLine();
            // 地图/角色模型与数字池不用全部展开，便于审阅真正的作者入口。
            if (depth >= 4 || t.name == "Map_1" || t.GetComponent<WizardController>() != null || t.name == "Panel") return;
            foreach (Transform child in t) Append(child, text, depth + 1);
        }
        private static void WriteTree(Scene scene, string name) => Write(name, Tree(scene));
        private static void Write(string name, string value) { Directory.CreateDirectory(Evidence); File.WriteAllText(Evidence + "/" + name, value); }
        private static string HashScene()
        { using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(SinglePlayerHubSceneSetup.Path))).Replace("-", ""); }

        private sealed class Snapshot
        {
            private readonly List<Pose> _poses = new List<Pose>();
            private readonly List<Reference> _references = new List<Reference>();
            private readonly List<Canvas> _canvases = new List<Canvas>();
            private readonly Scene _scene;
            internal Snapshot(Scene scene)
            {
                _scene = scene;
                foreach (var t in All(scene))
                {
                    _poses.Add(new Pose(t));
                    foreach (var c in t.GetComponents<Component>())
                    {
                        if (c == null) throw new InvalidOperationException(t.name + " 有 Missing Script。");
                        if (c is Canvas canvas) _canvases.Add(canvas);
                        if (c is Transform) continue; // 父子引用正是本轮有意修改的作者结构。
                        var property = new SerializedObject(c).GetIterator();
                        while (property.Next(true)) if (property.propertyType == SerializedPropertyType.ObjectReference)
                            _references.Add(new Reference { Component = c, Path = property.propertyPath, Id = property.objectReferenceInstanceIDValue });
                    }
                }
            }
            internal string Verify()
            {
                foreach (var pose in _poses) pose.Verify();
                foreach (var reference in _references)
                {
                    if (reference.Component == null || new SerializedObject(reference.Component).FindProperty(reference.Path).objectReferenceInstanceIDValue != reference.Id)
                        throw new InvalidOperationException("组件引用发生变化：" + reference.Path);
                }
                var current = new List<Canvas>(); foreach (var t in All(_scene)) if (t.TryGetComponent<Canvas>(out var canvas)) current.Add(canvas);
                if (current.Count != _canvases.Count) throw new InvalidOperationException("Canvas 数量改变。");
                for (int i = 0; i < current.Count; i++) if (current[i] != _canvases[i]) throw new InvalidOperationException("Canvas 相对层级次序改变。");
                return $"保留 {_poses.Count} 个原对象的世界状态/UI四角、{_references.Count} 条原组件引用、{_canvases.Count} 个 Canvas 及相对顺序。";
            }
        }
        private sealed class Reference { internal Component Component; internal string Path; internal int Id; }
        private sealed class Pose
        {
            private readonly Transform _target;
            private readonly Vector3 _position, _scale;
            private readonly Quaternion _rotation;
            private readonly bool _active;
            private readonly Vector3[] _corners;
            internal Pose(Transform target)
            {
                _target = target; _position = target.position; _scale = target.lossyScale; _rotation = target.rotation; _active = target.gameObject.activeInHierarchy;
                if (target is RectTransform rect) { _corners = new Vector3[4]; rect.GetWorldCorners(_corners); }
            }
            internal void Verify()
            {
                if (_target == null || (_target.position - _position).sqrMagnitude > .0001f || (_target.lossyScale - _scale).sqrMagnitude > .0001f || Quaternion.Angle(_target.rotation, _rotation) > .01f || _target.gameObject.activeInHierarchy != _active)
                    throw new InvalidOperationException("世界 Transform 或激活状态改变：" + (_target != null ? _target.name : "对象已删除"));
                if (_corners == null) return;
                var corners = new Vector3[4]; ((RectTransform)_target).GetWorldCorners(corners);
                for (int i = 0; i < 4; i++) if ((corners[i] - _corners[i]).sqrMagnitude > .01f) throw new InvalidOperationException("UI 世界四角改变：" + _target.name);
            }
        }
    }
}
