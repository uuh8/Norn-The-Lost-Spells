using System;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace Game.Project.Editor
{
    /// <summary>显式迁移已知生产资源；只替换 EventSystem 的脚本身份，保留组件 FileID 与原 InputModule 接线。</summary>
    public static class EventSystemFocusTools
    {
        private const string ScriptPath = "Assets/_Project/Scripts/UI/FocusAwareEventSystem.cs";
        private const string WorkspacePrefab = "Assets/_Project/Prefabs/SinglePlayer/PracticeInventoryWorkspace.prefab";
        // 明确列出本次修复涉及的生产入口；不 FindAssets 扫描历史 Review、实验场景或第三方内容。
        private static readonly string[] ProductionScenes =
        {
            "Assets/_Project/Scenes/P7_MainMenu.unity",
            "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity",
            "Assets/_Project/Scenes/Online/OnlineRoom.unity",
            "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity",
            "Assets/_Project/Scenes/P7_DemoRun.unity",
            "Assets/_Project/Scenes/P8_BossField.unity"
        };

        [MenuItem("Tools/Norn/UI/Upgrade Production EventSystem Focus")]
        public static void Upgrade()
        {
            OnlineSceneSetup.RequireCleanEditor(); RequireScript();
            var original = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                // 先只读检查全部固定资源，再写任何一份。否则在后面的 Scene 发现自定义派生时，
                // 前面的 Scene 已保存，或当前已加载 Scene 已变 Dirty，形成难以解释的部分迁移。
                PreflightProductionAssets();
                for (int i = 0; i < ProductionScenes.Length; i++)
                {
                    string path = ProductionScenes[i];
                    var scene = SceneManager.GetSceneByPath(path);
                    bool opened = !scene.IsValid() || !scene.isLoaded;
                    if (opened) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                    try
                    {
                        PreflightScene(scene);
                        int changes = 0;
                        foreach (var root in scene.GetRootGameObjects()) changes += UpgradeRoot(root);
                        RequireFocusAware(scene);
                        if (changes != 0 && !EditorSceneManager.SaveScene(scene))
                            throw new InvalidOperationException("EventSystem 迁移后保存失败：" + path);
                    }
                    finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
                }
                var workspace = PrefabUtility.LoadPrefabContents(WorkspacePrefab);
                try
                {
                    if (PreflightRoot(workspace) == 0) throw new InvalidOperationException("工作区源 Prefab 缺少 EventSystem。");
                    int changes = UpgradeRoot(workspace); RequireFocusAware(workspace);
                    if (changes != 0 && PrefabUtility.SaveAsPrefabAsset(workspace, WorkspacePrefab) == null)
                        throw new InvalidOperationException("工作区 EventSystem 迁移后保存失败。");
                }
                finally { PrefabUtility.UnloadPrefabContents(workspace); }
                AssetDatabase.SaveAssets();
            }
            finally { EditorSceneManager.RestoreSceneManagerSetup(original); }
        }

        /// <summary>调用者明确持有根对象；未知派生先整根拒绝，不在已修改普通兄弟组件后才发现冲突。</summary>
        public static int UpgradeRoot(GameObject root)
        {
            PreflightRoot(root);
            var script = RequireScript(); int changes = 0;
            var systems = root.GetComponentsInChildren<EventSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                var previous = systems[i];
                if (previous == null || previous.GetType() != typeof(EventSystem)) continue;
                var owner = previous.gameObject;
                var identity = GlobalObjectId.GetGlobalObjectIdSlow(previous);
                var first = previous.firstSelectedGameObject;
                bool navigation = previous.sendNavigationEvents; int threshold = previous.pixelDragThreshold;
                bool enabled = previous.enabled;
                var modules = owner.GetComponents<BaseInputModule>();
                var moduleStates = new string[modules.Length];
                for (int m = 0; m < modules.Length; m++) moduleStates[m] = EditorJsonUtility.ToJson(modules[m]);

                // MonoBehaviour 的序列化槽位仍是原组件；删除再 Add 会改变 FileID 并破坏外部引用。
                var data = new SerializedObject(previous);
                var property = data.FindProperty("m_Script");
                if (property == null) throw new InvalidOperationException("目标不是可迁移的 MonoBehaviour。");
                property.objectReferenceValue = script; data.ApplyModifiedPropertiesWithoutUndo();

                // 更换脚本可能重建托管 wrapper，不能继续沿用 previous 引用检查结果。
                var current = owner.GetComponent<FocusAwareEventSystem>();
                if (current == null) throw new InvalidOperationException("Unity 未完成 EventSystem 脚本类型迁移，请停止保存并检查来源。");
                var after = GlobalObjectId.GetGlobalObjectIdSlow(current);
                if (identity.targetObjectId != 0 && (after.targetObjectId != identity.targetObjectId || !after.assetGUID.Equals(identity.assetGUID)))
                    throw new InvalidOperationException("EventSystem 组件身份改变，拒绝保存迁移。");
                if (current.firstSelectedGameObject != first || current.sendNavigationEvents != navigation ||
                    current.pixelDragThreshold != threshold || current.enabled != enabled)
                    throw new InvalidOperationException("EventSystem 原序列化设置未保留，拒绝保存迁移。");
                var remaining = owner.GetComponents<BaseInputModule>();
                if (remaining.Length != modules.Length) throw new InvalidOperationException("InputModule 数量意外改变。");
                for (int m = 0; m < modules.Length; m++)
                    if (remaining[m] != modules[m] || EditorJsonUtility.ToJson(remaining[m]) != moduleStates[m])
                        throw new InvalidOperationException("InputModule 或 Actions 接线意外改变，拒绝保存迁移。");
                EditorUtility.SetDirty(current); changes++;
            }
            return changes;
        }

        private static void PreflightProductionAssets()
        {
            var original = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                for (int i = 0; i < ProductionScenes.Length; i++)
                {
                    var scene = SceneManager.GetSceneByPath(ProductionScenes[i]);
                    bool opened = !scene.IsValid() || !scene.isLoaded;
                    if (opened) scene = EditorSceneManager.OpenScene(ProductionScenes[i], OpenSceneMode.Additive);
                    try { PreflightScene(scene); }
                    finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
                }
                var workspace = PrefabUtility.LoadPrefabContents(WorkspacePrefab);
                try
                {
                    if (PreflightRoot(workspace) == 0) throw new InvalidOperationException("工作区源 Prefab 缺少 EventSystem。");
                }
                finally { PrefabUtility.UnloadPrefabContents(workspace); }
                // 这里不能 SaveScene / SaveAsPrefabAsset / SaveAssets：预检只决定能否开始迁移。
            }
            finally { EditorSceneManager.RestoreSceneManagerSetup(original); }
        }

        private static void PreflightScene(Scene scene)
        {
            if (scene.isDirty) throw new InvalidOperationException("来源 Scene 有未保存修改，拒绝迁移：" + scene.path);
            int count = 0;
            foreach (var root in scene.GetRootGameObjects()) count += PreflightRoot(root);
            if (count == 0) throw new InvalidOperationException("已知生产 Scene 缺少 EventSystem：" + scene.path);
        }

        private static int PreflightRoot(GameObject root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            var systems = root.GetComponentsInChildren<EventSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                var system = systems[i];
                if (system == null || (system.GetType() != typeof(EventSystem) && system.GetType() != typeof(FocusAwareEventSystem)))
                    throw new InvalidOperationException("发现自定义 EventSystem，预检拒绝迁移且保留其作者实现：" + root.name);
            }
            return systems.Length;
        }

        private static MonoScript RequireScript()
        {
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(ScriptPath);
            if (script == null || script.GetClass() != typeof(FocusAwareEventSystem))
                throw new InvalidOperationException("FocusAwareEventSystem 尚未成功编译，不能迁移 Scene 脚本引用。");
            return script;
        }

        private static void RequireFocusAware(Scene scene)
        {
            int count = 0;
            foreach (var root in scene.GetRootGameObjects()) count += RequireFocusAware(root, false);
            if (count == 0) throw new InvalidOperationException("已知生产 Scene 缺少 EventSystem：" + scene.path);
        }
        private static void RequireFocusAware(GameObject root) => RequireFocusAware(root, true);
        private static int RequireFocusAware(GameObject root, bool requireOne)
        {
            var systems = root.GetComponentsInChildren<EventSystem>(true);
            for (int i = 0; i < systems.Length; i++)
                if (systems[i] == null || systems[i].GetType() != typeof(FocusAwareEventSystem))
                    throw new InvalidOperationException("生产资源仍有未经确认的 EventSystem 类型：" + root.name);
            if (requireOne && systems.Length == 0) throw new InvalidOperationException("工作区源 Prefab 缺少 EventSystem。");
            return systems.Length;
        }
    }
}
