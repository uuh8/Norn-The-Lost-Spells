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
            typeof(InventoryDragAutoScroll), typeof(InventoryViewportPan), typeof(InventoryGridLinesGraphic), typeof(InventoryInfoPopup),
            typeof(NearbyPickupListView)
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
        /// 为回归与显式导出建立可销毁的预览。克隆原 Tab Canvas 与原附近提示 Canvas，
        /// 两者内部引用由 Instantiate 映射，跨 Canvas 的 Controller 显式重绑；外层 Session/Held/EventSystem 不进入复制范围。
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
                var nearbyViews = source.GetComponentsInChildren<NearbyPickupListView>(true);
                if (nearbyViews.Length != 1 || nearbyViews[0].GetComponentInParent<Canvas>(true) == null)
                    throw new InvalidOperationException("原工作区必须有唯一的附近提示 Canvas，不能另造一套线上拾取提示。");
                var nearbyCanvas = nearbyViews[0].GetComponentInParent<Canvas>(true);
                var nearbyRoot = Object.Instantiate(nearbyCanvas.gameObject, result.transform, false);
                nearbyRoot.name = nearbyCanvas.name;
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
                ClearExternalReferences(result);
                // 两个 Canvas 分别 Instantiate，原列表的 _inventory 不会自动指向新的 Tab Controller。
                // 只保留 UI 之间的内部引用；Nearby/Actor 由运行时 Scope 取得当前认证身份后注入。
                var nearbyData = new SerializedObject(nearbyRoot.GetComponentInChildren<NearbyPickupListView>(true));
                nearbyData.FindProperty("_inventory").objectReferenceValue = controller;
                nearbyData.ApplyModifiedPropertiesWithoutUndo();
                EnsureInteractionFeedback(controller);
                Validate(result); return result;
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

        /// <summary>
        /// 原作者已删除旧详情/状态区，不能因此把正式拖放的所有失败与等待反馈静默丢弃。
        /// 只在导出克隆中补一条header反馈，不改原工作区，不复用容量/法杖属性，也不增加Runtime查找或输入Owner。
        /// 1920x1080原面板的Logo在x约35..414，Close在x约1499..1738；中间x470..1450/y30..74为空白。
        /// </summary>
        private static void EnsureInteractionFeedback(SinglePlayerInventoryController controller)
        {
            var data = new SerializedObject(controller);
            if (data.FindProperty("_status").objectReferenceValue != null) return;
            var panel = data.FindProperty("_panelRoot").objectReferenceValue as GameObject;
            if (panel == null) throw new InvalidOperationException("原库存缺少面板根，无法接入交互反馈。");
            var node = new GameObject("InventoryInteractionStatus", typeof(RectTransform), typeof(Text), typeof(Shadow));
            node.transform.SetParent(panel.transform, false); node.layer = panel.layer;
            var rect = (RectTransform)node.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, 1); rect.pivot = new Vector2(.5f, .5f);
            rect.anchoredPosition = new Vector2(0, -52); rect.sizeDelta = new Vector2(980, 44);
            var text = node.GetComponent<Text>();
            // 原Controller.Start会把面板内Text统一换成同一中文Font；Editor预览保留可绘制的内置后备。
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); text.fontSize = 18;
            text.color = new Color(.94f, .94f, .91f, 1); text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = false; text.supportRichText = false; text.raycastTarget = false; text.text = string.Empty;
            var shadow = node.GetComponent<Shadow>(); shadow.effectColor = new Color(0, 0, 0, .85f);
            shadow.effectDistance = new Vector2(1, -1);
            data.FindProperty("_status").objectReferenceValue = text; data.ApplyModifiedPropertiesWithoutUndo();
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
            var prompts = root.GetComponentsInChildren<NearbyPickupListView>(true);
            if (prompts.Length != 1 || prompts[0].GetComponentInParent<Canvas>(true) == null)
                throw new InvalidOperationException("共同库存缺少原附近提示 Canvas。");
            var prompt = new SerializedObject(prompts[0]);
            foreach (string field in new[] { "_session", "_nearby", "_hub" })
                if (prompt.FindProperty(field).objectReferenceValue != null)
                    throw new InvalidOperationException("共同拾取提示仍保留单机Owner：" + field);
            if (prompt.FindProperty("_inventory").objectReferenceValue != controllers[0]
                || prompt.FindProperty("_visual").objectReferenceValue == null || prompt.FindProperty("_rows").arraySize == 0)
                throw new InvalidOperationException("共同拾取提示缺少原行布局或背包接线。");
            foreach (string field in AuthorityFields)
                if (controller.FindProperty(field).objectReferenceValue != null)
                    throw new InvalidOperationException("共同库存仍保留单机权限：" + field);
            if (controller.FindProperty("_openOnStart").boolValue)
                throw new InvalidOperationException("共同库存不能在取得线上只读源前自动打开。");
            foreach (string field in new[] { "_panelRoot", "_gridView", "_wandsView", "_backpackWorkspace", "_spellStorage", "_nearbyGrid", "_ghost", "_status" })
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

        private static string SourceHash() => Hash128.Compute("NornSharedInventoryV3|" + AssetDatabase.GetAssetDependencyHash(SourcePrefabPath)).ToString();

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
