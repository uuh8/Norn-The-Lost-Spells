using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Game.Rendering;
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
    /// 从唯一正式 authoring 导出共享表现树，不创建另一套联机样式。
    /// 清理发生在 Editor 的 inactive Preview Scene，最终 Client 不会经历“先启动单机再删除”的生命周期。
    /// </summary>
    public static class SharedGameplayHudTools
    {
        public const string PlayerSourceScene = "Assets/_Project/Scenes/P7_DemoRun.unity";
        public const string BossSourceScene = "Assets/_Project/Scenes/P8_BossField.unity";
        // 当前P7已无旧Stage控件；现存唯一authoring位于P8。这里只复用样式，区域目标由各副本Definition供给。
        public const string StageSourceScene = BossSourceScene;
        public const string WorkspaceSourcePrefab = "Assets/_Project/Prefabs/SinglePlayer/PracticeInventoryWorkspace.prefab";
        public const string RootPrefix = "SharedGameplayHud_";
        public const string ResourcePrefabPath = "Assets/_Project/Resources/Presentation/CombatHud.prefab";

        // 这是表现组件白名单，而不是允许所有 UI 命名空间；库存控制器也可能提交资产命令。
        private static readonly HashSet<Type> PresentationTypes = new HashSet<Type>
        {
            typeof(Transform), typeof(RectTransform), typeof(Canvas), typeof(CanvasRenderer),
            typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(Image), typeof(RawImage), typeof(Text),
            typeof(Shadow), typeof(Outline), typeof(Mask), typeof(RectMask2D),
            typeof(HorizontalLayoutGroup), typeof(VerticalLayoutGroup), typeof(GridLayoutGroup),
            typeof(ContentSizeFitter), typeof(LayoutElement), typeof(AspectRatioFitter),
            typeof(PlayerHealthBar), typeof(PlayerManaBar), typeof(PlayerStatusBar), typeof(StatusIconView),
            typeof(BossHealthBarController), typeof(CrosshairUI), typeof(HeldItemHud), typeof(LocalActorHudBinding)
        };
        private static readonly Regex InstanceIdPattern = new Regex("\"instanceID\":-?[0-9]+", RegexOptions.Compiled);

        /// <summary>
        /// 将同一份正式控件导出为所有战斗区域按需加载的资源。Resources 路径由 Host 共用，
        /// 不让每张地图序列化引用一份生成副本，也不让生成资源反向引用源 Scene。
        /// Prefab 根保持 inactive，Host 取得 Context 后才激活，避免在旧角色尚未解绑时提前执行 Start。
        /// </summary>
        [MenuItem("Tools/Norn/Combat/Export Shared Combat HUD")]
        public static void ExportResource()
        {
            OnlineSceneSetup.RequireCleanEditor();
            if (!AssetDatabase.IsValidFolder("Assets/_Project/Resources/Presentation"))
                AssetDatabase.CreateFolder("Assets/_Project/Resources", "Presentation");
            var preview = BuildPreview();
            try
            {
                if (PrefabUtility.SaveAsPrefabAsset(preview, ResourcePrefabPath) == null)
                    throw new InvalidOperationException("共用战斗 HUD 资源保存失败。");
            }
            finally { ReleasePreview(preview); }
            AssetDatabase.SaveAssets();
            // Unity 会把 Prefab 根重命名为文件名 CombatHud，不能再用根名字保存来源 Hash。
            // userData 是 Editor 的导出元数据，不是玩家数据或玩法权威；由 Unity 写 .meta，避免手工造 GUID。
            var importer = AssetImporter.GetAtPath(ResourcePrefabPath);
            importer.userData = "NornCombatHud:" + SourceHash(); importer.SaveAndReimport();
            VerifyResource();
        }

        [MenuItem("Tools/Norn/Combat/Verify Shared Combat HUD")]
        public static void VerifyResource()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(ResourcePrefabPath);
            if (asset == null) throw new InvalidOperationException("共用 CombatHud 资源缺失，请先 Export Shared Combat HUD。");
            if (asset.activeSelf) throw new InvalidOperationException("共用 CombatHud 根必须保持 inactive，由 Context 激活。");
            if (AssetImporter.GetAtPath(ResourcePrefabPath).userData != "NornCombatHud:" + SourceHash())
                throw new InvalidOperationException("共用 CombatHud 来源已更新，请重新导出。");
            var expected = BuildPreview(); var scene = EditorSceneManager.NewPreviewScene();
            GameObject actual = null;
            try
            {
                // 资源已验证 inactive，克隆不会运行表现生命周期；它必须是独立根，
                // 否则用于保护场景引用的检查会正确拒绝指向外部 wrapper 的 Transform.m_Father。
                actual = Object.Instantiate(asset);
                SceneManager.MoveGameObjectToScene(actual, scene);
                // 克隆后内部 Prefab 引用会映射到同一预览树，才能与源控件逐项比较。
                // 直接比较持久化资产与Scene对象的InstanceID没有业务意义。
                actual.name = expected.name; // 只规范化 Unity 由文件名驱动的根名，所有子节点和控件参数仍逐项核对。
                ValidatePresentation(actual);
                if (StyleSignature(actual) != StyleSignature(expected))
                    throw new InvalidOperationException("共用 CombatHud 来源或布局已过期，请重新导出。");
            }
            finally
            {
                if (actual != null) Object.DestroyImmediate(actual);
                EditorSceneManager.ClosePreviewScene(scene); ReleasePreview(expected);
            }
        }

        [MenuItem("Tools/Norn/Online/Setup Shared Gameplay HUD")]
        public static void Setup()
        {
            OnlineSceneSetup.RequireCleanEditor();
            var original = SceneManager.GetActiveScene();
            var target = SceneManager.GetSceneByPath(RoomLifecycleTools.PortalScenePath);
            bool openedHere = !target.IsValid() || !target.isLoaded;
            if (openedHere) target = EditorSceneManager.OpenScene(RoomLifecycleTools.PortalScenePath, OpenSceneMode.Additive);
            try
            {
                Install(target); Verify(target); EditorSceneManager.SaveScene(target);
            }
            finally
            {
                if (openedHere) EditorSceneManager.CloseScene(target, true);
                if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
            }
        }

        [MenuItem("Tools/Norn/Online/Verify Shared Gameplay HUD")]
        public static void VerifyMenu()
        {
            OnlineSceneSetup.RequireCleanEditor();
            var target = SceneManager.GetSceneByPath(RoomLifecycleTools.PortalScenePath);
            bool openedHere = !target.IsValid() || !target.isLoaded;
            if (openedHere) target = EditorSceneManager.OpenScene(RoomLifecycleTools.PortalScenePath, OpenSceneMode.Additive);
            try { Verify(target); }
            finally { if (openedHere) EditorSceneManager.CloseScene(target, true); }
        }

        /// <summary>只写明确的 OnlineRoom；旧技术 HUD 留在历史 lab，正式源 Scene 从不作为写入目标。</summary>
        public static void Install(Scene scene)
        {
            RequireTarget(scene);
            var preview = BuildPreview();
            Scene temporary = preview.scene;
            try
            {
                ValidatePresentation(preview);
                RemoveLegacyMovementHints(scene);
                // 新树完整建立后才替换旧树，配置失败不会先删掉已有可见 UI。
                foreach (var root in scene.GetRootGameObjects())
                    if (!root.name.StartsWith(RootPrefix, StringComparison.Ordinal) &&
                        root.GetComponent<OnlineCombatHudController>() == null &&
                        root.GetComponentInChildren<OnlineCombatHudController>(true) != null)
                        throw new InvalidOperationException("旧测试 HUD 嵌在其他 Root 下，请先核对其所有权，不能连同其他内容删除。");
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name.StartsWith(RootPrefix, StringComparison.Ordinal) ||
                        root.GetComponent<OnlineCombatHudController>() != null)
                        Object.DestroyImmediate(root);
                }
                SceneManager.MoveGameObjectToScene(preview, scene);
                preview.SetActive(true);
                EditorSceneManager.MarkSceneDirty(scene);
                preview = null;
            }
            finally
            {
                if (preview != null) Object.DestroyImmediate(preview);
                EditorSceneManager.ClosePreviewScene(temporary);
            }
        }

        /// <summary>
        /// 返回独立 Preview Scene 中的 inactive 纯表现树。调用者用 ReleasePreview 释放，不能遗留隐藏 Scene。
        /// P7/P8 的现代 MP 在原十格 HeldItemHud 内，不能重新制造已经移除的 PlayerManaBar。
        /// </summary>
        public static GameObject BuildPreview()
        {
            string provenance = SourceHash();
            var previewScene = EditorSceneManager.NewPreviewScene();
            GameObject result = null;
            try
            {
                result = new GameObject(RootPrefix + provenance); result.SetActive(false);
                SceneManager.MoveGameObjectToScene(result, previewScene);
                WithSource(PlayerSourceScene, source =>
                {
                    var hp = FindOne<PlayerHealthBar>(source); var status = FindOne<PlayerStatusBar>(source);
                    CloneSelection(new[] { hp.transform, status.transform }, result.transform);
                });
                WithSource(BossSourceScene, source =>
                    CloneSelection(new[] { FindOne<BossHealthBarController>(source).transform }, result.transform));
                WithSource(StageSourceScene, source => CloneStagePresentation(source, result.transform));
                var workspace = AssetDatabase.LoadAssetAtPath<GameObject>(WorkspaceSourcePrefab);
                if (workspace == null) throw new InvalidOperationException("正式 PracticeInventoryWorkspace Prefab 缺失。");
                var crosshair = workspace.GetComponentInChildren<CrosshairUI>(true);
                var held = workspace.GetComponentInChildren<HeldItemHud>(true);
                if (crosshair == null || held == null || !held.transform.IsChildOf(crosshair.transform))
                    throw new InvalidOperationException("正式手持栏与准心不再共享同一 Canvas，请先核对 authoring。");
                // 整个原 PracticeHeldHUD（含十格、MP、Cooldown、准心）同源复制；其 SP 工作区祖先只保留 Transform。
                var workspaceView = CloneSelection(new[] { crosshair.transform }, result.transform);
                // 原 Prefab 的根默认 inactive，由单机 Bootstrap 初始化完成后激活。
                // 现在该根只保留 Transform，必须由纯表现 composition 接替激活；状态 Icon/Boss 等子节点原显隐不变。
                workspaceView.SetActive(true);
                ClearExternalReferences(result);
                var binding = result.AddComponent<LocalActorHudBinding>();
                var data = new SerializedObject(binding);
                data.FindProperty("_healthBar").objectReferenceValue = result.GetComponentInChildren<PlayerHealthBar>(true);
                data.FindProperty("_manaBar").objectReferenceValue = null;
                data.FindProperty("_statusBar").objectReferenceValue = result.GetComponentInChildren<PlayerStatusBar>(true);
                data.ApplyModifiedPropertiesWithoutUndo();
                ValidatePresentation(result);
                if (provenance != SourceHash()) throw new InvalidOperationException("导出期间正式 UI 来源发生变化，请重新导出。");
                return result;
            }
            catch
            {
                if (result != null) Object.DestroyImmediate(result);
                EditorSceneManager.ClosePreviewScene(previewScene); throw;
            }
        }

        public static void ReleasePreview(GameObject preview)
        {
            if (preview == null) return;
            Scene scene = preview.scene;
            if (!EditorSceneManager.IsPreviewScene(scene))
                throw new InvalidOperationException("ReleasePreview 不能删除已安装的正式 Scene 对象。");
            Object.DestroyImmediate(preview); EditorSceneManager.ClosePreviewScene(scene);
        }

        /// <summary>验证来源摘要和实际样式；仅有同名组件不构成“复用原 UI”的证据。</summary>
        public static void Verify(Scene scene)
        {
            RequireTarget(scene);
            GameObject installed = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.GetComponentInChildren<OnlineCombatHudController>(true) != null ||
                    root.GetComponentInChildren<OnlineMovementHint>(true) != null || root.name == "OnlineMovementControls")
                    throw new InvalidOperationException("OnlineRoom 仍装有独立测试 HUD。");
                if (!root.name.StartsWith(RootPrefix, StringComparison.Ordinal)) continue;
                if (installed != null) throw new InvalidOperationException("OnlineRoom 共享 HUD 重复。");
                installed = root;
            }
            if (installed == null || installed.name != RootPrefix + SourceHash())
                throw new InvalidOperationException("共享 HUD 缺失或正式来源 Hash 已变化，请 Setup Shared Gameplay HUD。");
            ValidatePresentation(installed);
            var expected = BuildPreview();
            try
            {
                if (StyleSignature(installed) != StyleSignature(expected))
                    throw new InvalidOperationException("共享 HUD 的 Rect/CanvasScaler/图形/引用与正式来源不一致，请复导出，禁止手工维护另一套样式。");
            }
            finally { ReleasePreview(expected); }
        }

        /// <summary>
        /// 正式 OnlineRoom 的升级清理。只删除旧工具独占的 Canvas 根；若横幅被挪进其他玩法根，
        /// 拒绝自动连根删除，要求先厘清所有权。两遍检查确保未知布局不会造成部分删除。
        /// </summary>
        public static void RemoveLegacyMovementHints(Scene scene)
        {
            RequireTarget(scene);
            var roots = scene.GetRootGameObjects();
            foreach (var root in roots)
            {
                if (root.GetComponentInChildren<OnlineMovementHint>(true) == null && root.name != "OnlineMovementControls") continue;
                if (root.name != "OnlineMovementControls" || root.GetComponent<Canvas>() == null)
                    throw new InvalidOperationException("旧移动提示不在工具独占的 OnlineMovementControls Canvas 下，拒绝删除未知根。");
                foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                    if (behaviour == null || (!(behaviour is CanvasScaler) && !(behaviour is Text) && !(behaviour is OnlineMovementHint)))
                        throw new InvalidOperationException("旧移动提示 Canvas 混入其他脚本，拒绝连同其玩法内容删除。");
            }
            foreach (var root in roots)
                if (root.name == "OnlineMovementControls") Object.DestroyImmediate(root);
        }

        private static void RequireTarget(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded || scene.path != RoomLifecycleTools.PortalScenePath)
                throw new InvalidOperationException("共享 HUD 只允许写入已加载的 OnlineRoom，不允许覆盖正式单机 Scene 或历史 lab。");
        }

        private static string SourceHash() => Hash128.Compute("NornSharedHudV3|Stage:" + StageSourceScene + "|" +
            AssetDatabase.GetAssetDependencyHash(PlayerSourceScene) + "|" +
            AssetDatabase.GetAssetDependencyHash(BossSourceScene) + "|" +
            AssetDatabase.GetAssetDependencyHash(WorkspaceSourcePrefab)).ToString();

        private static void WithSource(string path, Action<Scene> read)
        {
            var scene = SceneManager.GetSceneByPath(path); bool openedHere = !scene.IsValid() || !scene.isLoaded;
            if (openedHere) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                if (scene.isDirty) throw new InvalidOperationException("正式 UI 来源有未保存修改，拒绝从不确定的 authoring 导出：" + path);
                read(scene);
                if (scene.isDirty) throw new InvalidOperationException("导出意外修改了正式来源：" + path);
            }
            finally { if (openedHere) EditorSceneManager.CloseScene(scene, true); }
        }

        private static T FindOne<T>(Scene source) where T : Component
        {
            T found = null;
            foreach (var root in source.GetRootGameObjects()) foreach (var item in root.GetComponentsInChildren<T>(true))
            {
                if (found != null) throw new InvalidOperationException("正式来源存在多份 " + typeof(T).Name);
                found = item;
            }
            if (found == null) throw new InvalidOperationException("正式来源缺少 " + typeof(T).Name);
            return found;
        }

        private static void CloneStagePresentation(Scene source, Transform parent)
        {
            var original = FindOne<DemoRunHudController>(source);
            var data = new SerializedObject(original);
            // 以真实SerializedObject引用选择叶树，不靠节点名字猜测层级；作者调整目标面板位置时，
            // 祖先Rect/Canvas和布局仍原样保留，兄弟HP、结果面板与单机UIController会被剪掉。
            var selected = new[] { RequireReference<Text>(data, "_objectiveText").transform,
                RequireReference<Text>(data, "_encounterText").transform,
                RequireReference<Text>(data, "_remainingEnemyText").transform,
                RequireReference<GameObject>(data, "_notificationRoot").transform };
            var clone = CloneSelection(selected, parent, original);
            var stage = clone.GetComponentInChildren<DemoRunHudController>(true);
            if (stage == null) throw new InvalidOperationException("StageController不在选定叶树的祖先路径，无法安全保留原装配。");
            Clear(stage, "_runController");
            // P8保留的旧Stage Adapter在源图被禁用。共同Host会在激活前安装显式线上读源，
            // 因此只启用这个纯表现Adapter，不启动已剥离的单机Run/结果流程。
            stage.enabled = true;
        }
        private static T RequireReference<T>(SerializedObject data, string field) where T : Object
        {
            var property = data.FindProperty(field);
            var value = property != null ? property.objectReferenceValue as T : null;
            if (value == null) throw new InvalidOperationException("原Stage authoring缺少有效引用：" + field);
            return value;
        }

        private static GameObject CloneSelection(Transform[] selected, Transform parent, DemoRunHudController stageToRetain = null)
        {
            var sourceRoot = selected[0].root;
            for (int i = 0; i < selected.Length; i++)
                if (selected[i].root != sourceRoot) throw new InvalidOperationException("共享 UI 选择不属于同一 authoring Root。");
            var clone = Object.Instantiate(sourceRoot.gameObject, parent, false); clone.name = sourceRoot.name;
            var retained = stageToRetain != null ? MatchTransform(sourceRoot, clone.transform, stageToRetain.transform).GetComponent<DemoRunHudController>() : null;
            var keep = new HashSet<Transform>();
            for (int i = 0; i < selected.Length; i++)
            {
                var target = MatchTransform(sourceRoot, clone.transform, selected[i]);
                foreach (var descendant in target.GetComponentsInChildren<Transform>(true)) keep.Add(descendant);
                for (var ancestor = target; ancestor != null && ancestor != parent; ancestor = ancestor.parent) keep.Add(ancestor);
            }
            Prune(clone.transform, keep);
            foreach (var node in clone.GetComponentsInChildren<Transform>(true))
            {
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(node.gameObject);
                var components = node.GetComponents<Component>();
                // 逆序移除引用者，避免 RequireComponent 持有者尚在时先删除其依赖。
                for (int i = components.Length - 1; i >= 0; i--)
                    if (components[i] != null && components[i] != retained && !PresentationTypes.Contains(components[i].GetType())) Object.DestroyImmediate(components[i]);
            }
            return clone;
        }

        private static Transform MatchTransform(Transform sourceRoot, Transform cloneRoot, Transform source)
        {
            if (source == sourceRoot) return cloneRoot;
            var parent = MatchTransform(sourceRoot, cloneRoot, source.parent);
            return parent.GetChild(source.GetSiblingIndex());
        }

        private static void Prune(Transform node, HashSet<Transform> keep)
        {
            for (int i = node.childCount - 1; i >= 0; i--)
            {
                var child = node.GetChild(i);
                if (!keep.Contains(child)) Object.DestroyImmediate(child.gameObject);
                else Prune(child, keep);
            }
        }

        private static void ClearExternalReferences(GameObject root)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                var data = new SerializedObject(component); var property = data.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    Object value = property.objectReferenceValue;
                    if (value == null || EditorUtility.IsPersistent(value)) continue;
                    Transform referenced = value is Component c ? c.transform : value is GameObject go ? go.transform : null;
                    if (referenced == null || !referenced.IsChildOf(root.transform)) property.objectReferenceValue = null;
                }
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            // 明确清除会话与 UI 命令 Owner；即使今后移到 Persistent 根，也不能给线上表现回退单机数据源。
            foreach (var hp in root.GetComponentsInChildren<PlayerHealthBar>(true)) Clear(hp, "_playerHealth");
            foreach (var mana in root.GetComponentsInChildren<PlayerManaBar>(true)) Clear(mana, "_playerMana");
            foreach (var status in root.GetComponentsInChildren<PlayerStatusBar>(true)) Clear(status, "_playerHealth");
            foreach (var held in root.GetComponentsInChildren<HeldItemHud>(true))
                foreach (string field in new[] { "_session", "_backpack", "_warehouse", "_shop" }) Clear(held, field);
            foreach (var stage in root.GetComponentsInChildren<DemoRunHudController>(true)) Clear(stage, "_runController");
        }

        private static void Clear(Object target, string field)
        {
            var data = new SerializedObject(target); var property = data.FindProperty(field);
            if (property == null) throw new InvalidOperationException("共享 UI 绑定字段已变化：" + target.GetType().Name + "." + field);
            property.objectReferenceValue = null; data.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ValidatePresentation(GameObject root)
        {
            RequireCount<PlayerHealthBar>(root, 1); RequireCount<PlayerStatusBar>(root, 1);
            RequireCount<PlayerManaBar>(root, 0); RequireCount<BossHealthBarController>(root, 1);
            RequireCount<CrosshairUI>(root, 1); RequireCount<HeldItemHud>(root, 1); RequireCount<LocalActorHudBinding>(root, 1);
            RequireCount<DemoRunHudController>(root, 1);
            var stage = root.GetComponentInChildren<DemoRunHudController>(true);
            ValidateStagePresentation(root, stage);
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                // Stage只允许那一个已经验证过叶树边界的Adapter；它不进入全局Type白名单，
                // 避免别的HP/Boss选择在复制共同祖先时顺带保留第二个未配置StageController。
                if (component == null || component != stage && !PresentationTypes.Contains(component.GetType()))
                    throw new InvalidOperationException("共享 HUD 带有白名单外组件或 Missing Script。");
                var data = new SerializedObject(component); var property = data.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    Object value = property.objectReferenceValue;
                    if (value == null || EditorUtility.IsPersistent(value)) continue;
                    Transform referenced = value is Component c ? c.transform : value is GameObject go ? go.transform : null;
                    if (referenced == null || !referenced.IsChildOf(root.transform))
                        throw new InvalidOperationException("共享 HUD 引用了来源 Scene 的外部对象：" + component.GetType().Name + "." + property.propertyPath);
                }
            }
            RequireNull(root.GetComponentInChildren<PlayerHealthBar>(true), "_playerHealth");
            RequireNull(root.GetComponentInChildren<PlayerStatusBar>(true), "_playerHealth");
            var held = root.GetComponentInChildren<HeldItemHud>(true);
            foreach (string field in new[] { "_session", "_backpack", "_warehouse", "_shop" }) RequireNull(held, field);
            var binding = new SerializedObject(root.GetComponent<LocalActorHudBinding>());
            if (binding.FindProperty("_healthBar").objectReferenceValue != root.GetComponentInChildren<PlayerHealthBar>(true) ||
                binding.FindProperty("_statusBar").objectReferenceValue != root.GetComponentInChildren<PlayerStatusBar>(true) ||
                binding.FindProperty("_manaBar").objectReferenceValue != null)
                throw new InvalidOperationException("共享原 HUD 的 LocalActor 只读绑定不完整。");
        }

        private static void ValidateStagePresentation(GameObject root, DemoRunHudController stage)
        {
            if (!stage.enabled) throw new InvalidOperationException("共同Stage Adapter未启用。");
            RequireNull(stage, "_runController");
            var data = new SerializedObject(stage);
            var gameplay = RequireReference<GameObject>(data, "_gameplayRoot");
            var notification = RequireReference<GameObject>(data, "_notificationRoot");
            if (gameplay == root || notification == root || stage.transform.IsChildOf(gameplay.transform) ||
                stage.transform.IsChildOf(notification.transform))
                throw new InvalidOperationException("Stage显隐Root不能包含共同根或StageController自身。");
            foreach (string field in new[] { "_objectiveText", "_encounterText", "_remainingEnemyText" })
                if (!RequireReference<Text>(data, field).transform.IsChildOf(gameplay.transform))
                    throw new InvalidOperationException("Stage目标叶树不属于原GameplayHUD：" + field);
            if (!RequireReference<Text>(data, "_notificationText").transform.IsChildOf(notification.transform) ||
                !RequireReference<Image>(data, "_notificationIcon").transform.IsChildOf(notification.transform))
                throw new InvalidOperationException("Stage通知叶树不属于原NotificationRoot。");
            foreach (var candidate in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                bool common = candidate is PlayerHealthBar || candidate is PlayerManaBar || candidate is PlayerStatusBar ||
                    candidate is BossHealthBarController || candidate is HeldItemHud || candidate is CrosshairUI || candidate is LocalActorHudBinding;
                if (common && (candidate.transform.IsChildOf(gameplay.transform) || candidate.transform.IsChildOf(notification.transform)))
                    throw new InvalidOperationException("Stage显隐Root误包含基础战斗UI：" + candidate.GetType().Name);
            }
        }

        private static void RequireNull(Object target, string field)
        {
            if (new SerializedObject(target).FindProperty(field).objectReferenceValue != null)
                throw new InvalidOperationException("线上共享 HUD 不得保留单机来源：" + target.GetType().Name + "." + field);
        }
        private static void RequireCount<T>(GameObject root, int count) where T : Component
        {
            if (root.GetComponentsInChildren<T>(true).Length != count)
                throw new InvalidOperationException("共享 HUD 的 " + typeof(T).Name + " 数量不符正式 authoring。");
        }

        private static string StyleSignature(GameObject root)
        {
            var text = new StringBuilder();
            foreach (var node in root.GetComponentsInChildren<Transform>(true))
            {
                text.Append(Path(root.transform, node)).Append('|').Append(node.name).Append('|')
                    .Append(node.gameObject.layer).Append('|').Append(node.gameObject.tag).Append('|');
                // wrapper 的 active 是安装行为；其余子节点的原始 active 位仍必须逐个保持。
                if (node != root.transform) text.Append(node.gameObject.activeSelf);
                foreach (var component in node.GetComponents<Component>())
                {
                    text.Append(component.GetType().FullName).Append('|');
                    // Unity 对 ScreenSpace Canvas 的根 Rect 有驱动权；它随 GameView 尺寸变化，不是美术被改。
                    if (!(component is RectTransform && node.GetComponent<Canvas>() != null))
                        text.Append(InstanceIdPattern.Replace(EditorJsonUtility.ToJson(component), "\"instanceID\":0"));
                    var data = new SerializedObject(component); var property = data.GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference || property.name == "m_GameObject") continue;
                        var value = property.objectReferenceValue; text.Append('|').Append(property.propertyPath).Append('=');
                        if (value == null) { text.Append("null"); continue; }
                        if (EditorUtility.IsPersistent(value)) text.Append(GlobalObjectId.GetGlobalObjectIdSlow(value));
                        else
                        {
                            Transform reference = value is Component c ? c.transform : (value as GameObject)?.transform;
                            text.Append(reference != null ? Path(root.transform, reference) : "external").Append(':').Append(value.GetType().FullName);
                        }
                    }
                }
                text.AppendLine();
            }
            return text.ToString();
        }

        private static string Path(Transform root, Transform target)
        {
            if (target == root) return "/";
            return target.parent != null ? Path(root, target.parent) + target.GetSiblingIndex() + "/" : "external/";
        }
    }
}
