using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.Project.Editor
{
    /// <summary>
    /// 从原练习工作区导出同一份 Tab 携带界面。这里只保留原 Canvas、View、Controller 与 Input 接线，
    /// 不把单机 Session、存档、暂停和关卡生命周期带给线上 Scene；也不制作另一套联机皮肤。
    /// 导出在 inactive Preview Scene 内进行，所有去权威处理都早于运行时 Awake/OnEnable。
    /// </summary>
    public static class SharedInventoryPresentationTools
    {
        public const string SourcePrefabPath = "Assets/_Project/Prefabs/SinglePlayer/PracticeInventoryWorkspace.prefab";
        public const string ResourcePrefabPath = "Assets/_Project/Resources/Presentation/CombatInventory.prefab";
        private const string ProvenancePrefix = "NornCombatInventory:";
        private static readonly Regex InstanceIds = new Regex("\"instanceID\":-?[0-9]+", RegexOptions.Compiled);
        private static readonly string[] AuthorityFields = { "_session", "_hubAccess", "_pauseCoordinator", "_nearby", "_otherWorkspace" };

        // Game.UI 中也有会请求出征/结算的组件，因此不能按命名空间整体放行。
        // 此白名单只承认原库存 View 与其输入分发；写权限仍由 Controller 的显式只读来源门禁限制。
        private static readonly HashSet<Type> ViewTypes = new HashSet<Type>
        {
            typeof(Transform), typeof(RectTransform), typeof(Canvas), typeof(CanvasRenderer),
            // CanvasGroup控制整棵UGUI子树的Raycast资格。原Ghost的blocksRaycasts=false必须保留；
            // 剥掉它会让可见Ghost挡住鼠标下的真实Drop目标，画面相同却完全不能移动物品。
            typeof(CanvasScaler), typeof(CanvasGroup), typeof(GraphicRaycaster), typeof(Image), typeof(RawImage), typeof(Text),
            typeof(Button), typeof(ScrollRect), typeof(Scrollbar), typeof(Mask), typeof(RectMask2D),
            typeof(HorizontalLayoutGroup), typeof(VerticalLayoutGroup), typeof(GridLayoutGroup),
            typeof(ContentSizeFitter), typeof(LayoutElement), typeof(AspectRatioFitter), typeof(Shadow), typeof(Outline),
            typeof(SinglePlayerInventoryController), typeof(BackpackWorkspaceView), typeof(InventoryGridView),
            typeof(WandInventoryView), typeof(WandProgramPanelView), typeof(SpellStorageView), typeof(ToolbeltView),
            typeof(ToolbeltSlotView), typeof(NearbyItemGridView), typeof(InventoryItemView), typeof(InventoryDropTarget),
            typeof(InventoryDragAutoScroll), typeof(InventoryViewportPan), typeof(InventoryGridLinesGraphic), typeof(InventoryInfoPopup)
        };

        [MenuItem("Tools/Norn/Combat/Export Shared Combat Inventory")]
        public static void ExportResource()
        {
            OnlineSceneSetup.RequireCleanEditor();
            if (!AssetDatabase.IsValidFolder("Assets/_Project/Resources/Presentation"))
                AssetDatabase.CreateFolder("Assets/_Project/Resources", "Presentation");
            var preview = BuildPreview();
            try
            {
                // SaveAsPrefabAsset 由 Unity 维护 GUID/.meta；不能手写元数据或回写原工作区 Prefab。
                if (PrefabUtility.SaveAsPrefabAsset(preview, ResourcePrefabPath) == null)
                    throw new InvalidOperationException("共同库存界面资源保存失败。");
            }
            finally { ReleasePreview(preview); }
            AssetDatabase.SaveAssets();
            var importer = AssetImporter.GetAtPath(ResourcePrefabPath);
            importer.userData = ProvenancePrefix + SourceHash(); importer.SaveAndReimport();
            VerifyResource();
        }

        [MenuItem("Tools/Norn/Combat/Verify Shared Combat Inventory")]
        public static void VerifyResource()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(ResourcePrefabPath);
            if (asset == null || asset.activeSelf)
                throw new InvalidOperationException("共同 CombatInventory 缺失或根未保持 inactive，请重新导出。");
            if (AssetImporter.GetAtPath(ResourcePrefabPath).userData != ProvenancePrefix + SourceHash())
                throw new InvalidOperationException("原库存来源已变化，请重新导出 CombatInventory。");
            var expected = BuildPreview(); var actualScene = EditorSceneManager.NewPreviewScene(); GameObject actual = null;
            try
            {
                // 资源根已确认 inactive，作为独立根实例化可避免外部 wrapper 的 m_Father 混入样式摘要。
                actual = Object.Instantiate(asset); SceneManager.MoveGameObjectToScene(actual, actualScene);
                actual.name = expected.name;
                Validate(actual);
                if (Signature(actual) != Signature(expected))
                    throw new InvalidOperationException("CombatInventory 的布局、美术、Input 或内部引用与原导出结果不一致。");
            }
            finally
            {
                if (actual != null) Object.DestroyImmediate(actual);
                if (actualScene.IsValid()) EditorSceneManager.ClosePreviewScene(actualScene);
                ReleasePreview(expected);
            }
        }

        /// <summary>
        /// 为回归与显式导出建立可销毁的预览。只克隆 Controller 所在的原 Canvas，
        /// 其原 Tab 面板和 Ghost 引用由 Instantiate 自动映射到克隆树，外层 Session/Held/EventSystem 不进入复制范围。
        /// </summary>
        public static GameObject BuildPreview()
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePrefabPath);
            if (source == null) throw new InvalidOperationException("原 PracticeInventoryWorkspace Prefab 缺失。");
            var controllers = source.GetComponentsInChildren<SinglePlayerInventoryController>(true);
            if (controllers.Length != 1 || controllers[0].GetComponent<Canvas>() == null)
                throw new InvalidOperationException("原工作区必须有唯一位于 Canvas 的携带 Controller，不能猜测另一套 UI 根。");
            var scene = EditorSceneManager.NewPreviewScene(); GameObject result = null;
            try
            {
                result = new GameObject("CombatInventory"); result.SetActive(false); SceneManager.MoveGameObjectToScene(result, scene);
                var canvas = Object.Instantiate(controllers[0].gameObject, result.transform, false);
                canvas.name = controllers[0].name;
                foreach (var node in result.GetComponentsInChildren<Transform>(true))
                {
                    GameObjectUtility.RemoveMonoBehavioursWithMissingScript(node.gameObject);
                    var components = node.GetComponents<Component>();
                    // 移除引用者后移除依赖；保留 UGUI 自己需要的 RectTransform/CanvasRenderer。
                    for (int i = components.Length - 1; i >= 0; i--)
                        if (components[i] != null && !ViewTypes.Contains(components[i].GetType())) Object.DestroyImmediate(components[i]);
                }
                var controller = canvas.GetComponent<SinglePlayerInventoryController>();
                var data = new SerializedObject(controller);
                foreach (string field in AuthorityFields) data.FindProperty(field).objectReferenceValue = null;
                data.FindProperty("_openOnStart").boolValue = false; data.ApplyModifiedPropertiesWithoutUndo();
                ClearExternalReferences(result); Validate(result); return result;
            }
            catch
            {
                if (result != null) Object.DestroyImmediate(result);
                EditorSceneManager.ClosePreviewScene(scene); throw;
            }
        }

        public static void ReleasePreview(GameObject preview)
        {
            if (preview == null) return;
            var scene = preview.scene; Object.DestroyImmediate(preview);
            if (scene.IsValid() && EditorSceneManager.IsPreviewScene(scene)) EditorSceneManager.ClosePreviewScene(scene);
        }

        private static void ClearExternalReferences(GameObject root)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                var data = new SerializedObject(component); var property = data.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var value = property.objectReferenceValue;
                    if (value == null) continue;
                    // 只克隆 Canvas 时，指向其外层 Workspace 的引用仍可能指着持久化 Prefab Asset，
                    // 不能因为 IsPersistent 就误认为它与字体/Input 等普通资产一样安全。
                    if (EditorUtility.IsPersistent(value))
                    {
                        if ((value is Component || value is GameObject) && AssetDatabase.GetAssetPath(value) == SourcePrefabPath)
                            property.objectReferenceValue = null;
                        continue;
                    }
                    Transform target = value is Component c ? c.transform : (value as GameObject)?.transform;
                    if (target == null || !target.IsChildOf(root.transform)) property.objectReferenceValue = null;
                }
                data.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static void Validate(GameObject root)
        {
            var controllers = root.GetComponentsInChildren<SinglePlayerInventoryController>(true);
            if (root.activeSelf || controllers.Length != 1)
                throw new InvalidOperationException("共同库存必须是 inactive 根和唯一原携带 Controller。");
            var controller = new SerializedObject(controllers[0]);
            foreach (string field in AuthorityFields)
                if (controller.FindProperty(field).objectReferenceValue != null)
                    throw new InvalidOperationException("共同库存仍保留单机权限：" + field);
            if (controller.FindProperty("_openOnStart").boolValue)
                throw new InvalidOperationException("共同库存不能在取得线上只读源前自动打开。");
            foreach (string field in new[] { "_panelRoot", "_gridView", "_wandsView", "_backpackWorkspace", "_spellStorage", "_nearbyGrid", "_ghost" })
                if (controller.FindProperty(field).objectReferenceValue == null)
                    throw new InvalidOperationException("共同库存丢失原只读接线：" + field);
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || !ViewTypes.Contains(component.GetType()))
                    throw new InvalidOperationException("共同库存含白名单外组件或 Missing Script。");
                var data = new SerializedObject(component); var property = data.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var value = property.objectReferenceValue;
                    if (value == null) continue;
                    if (EditorUtility.IsPersistent(value))
                    {
                        if ((value is Component || value is GameObject) && AssetDatabase.GetAssetPath(value) == SourcePrefabPath)
                            throw new InvalidOperationException("共同库存仍指向原工作区 Prefab 的对象：" + property.propertyPath);
                        continue;
                    }
                    Transform target = value is Component c ? c.transform : (value as GameObject)?.transform;
                    if (target == null || !target.IsChildOf(root.transform))
                        throw new InvalidOperationException("共同库存包含树外 Scene 引用：" + component.GetType().Name + "." + property.propertyPath);
                }
            }
        }

        private static string SourceHash() => Hash128.Compute("NornSharedInventoryV1|" + AssetDatabase.GetAssetDependencyHash(SourcePrefabPath)).ToString();

        private static string Signature(GameObject root)
        {
            var text = new StringBuilder();
            foreach (var node in root.GetComponentsInChildren<Transform>(true))
            {
                text.Append(Path(root.transform, node)).Append('|').Append(node.name).Append('|').Append(node.gameObject.layer)
                    .Append('|').Append(node.gameObject.tag).Append('|').Append(node.gameObject.activeSelf);
                foreach (var component in node.GetComponents<Component>())
                {
                    text.Append('|').Append(component.GetType().FullName);
                    // ScreenSpace Canvas 根 Rect 由 Unity 依据 GameView 驱动，不把当前窗口尺寸当作美术差异。
                    if (!(component is RectTransform && node.GetComponent<Canvas>() != null))
                        text.Append(InstanceIds.Replace(EditorJsonUtility.ToJson(component), "\"instanceID\":0"));
                    var data = new SerializedObject(component); var property = data.GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference || property.name == "m_GameObject") continue;
                        var value = property.objectReferenceValue; text.Append('|').Append(property.propertyPath).Append('=');
                        if (value == null) { text.Append("null"); continue; }
                        if (EditorUtility.IsPersistent(value)) text.Append(GlobalObjectId.GetGlobalObjectIdSlow(value));
                        else
                        {
                            Transform target = value is Component c ? c.transform : (value as GameObject)?.transform;
                            text.Append(target != null ? Path(root.transform, target) : "external").Append(':').Append(value.GetType().FullName);
                        }
                    }
                }
                text.AppendLine();
            }
            return text.ToString();
        }
        private static string Path(Transform root, Transform target) => target == root ? "/" :
            target.parent != null ? Path(root, target.parent) + target.GetSiblingIndex() + "/" : "external/";
    }
}
