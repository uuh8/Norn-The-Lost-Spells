using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.UI.Tests
{
    /// <summary>复现停用期间错过 Focus 回调，并验证迁移不破坏原组件身份与 InputModule 绑定。</summary>
    public sealed class FocusAwareEventSystemTests
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [Test]
        public void ReenabledEventSystemSamplesCurrentFocusInsteadOfStaleDisabledState()
        {
            var root = new GameObject("ExplicitFocusLifecycle"); root.SetActive(false);
            var type = FindType("Game.UI.FocusAwareEventSystem") ?? typeof(EventSystem);
            var system = (EventSystem)root.AddComponent(type);
            try
            {
                // EditMode 显式驱动回调，不打开 Play 或切换用户窗口来制造偶然的 Focus 变化。
                Invoke(system, "OnEnable");
                typeof(EventSystem).GetMethod("OnApplicationFocus", InstanceFlags)
                    .Invoke(system, new object[] { !Application.isFocused });
                Assert.That(system.isFocused, Is.EqualTo(!Application.isFocused));
                Invoke(system, "OnDisable"); Invoke(system, "OnEnable");
                Assert.That(system.isFocused, Is.EqualTo(Application.isFocused),
                    "UGUI EventSystem 原实现只保留 m_HasFocus；Hub 恢复时必须重采，否则首点击可被跳过。");
            }
            finally { Invoke(system, "OnDisable"); Object.DestroyImmediate(root); }
        }

        [Test]
        public void UpgradePreservesSerializedComponentIdentitySettingsAndInputActionBindings()
        {
            var tools = FindType("Game.Project.Editor.EventSystemFocusTools");
            Assert.That(tools, Is.Not.Null, "需要显式 Editor 迁移入口，不能销毁原组件再创建或扫描改写所有 Scene。");
            var upgrade = tools.GetMethod("UpgradeRoot", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(GameObject) }, null);
            Assert.That(upgrade, Is.Not.Null);
            var moduleType = FindType("UnityEngine.InputSystem.UI.InputSystemUIInputModule");
            Assert.That(moduleType, Is.Not.Null);
            string path = "Assets/__FocusMigration_" + Guid.NewGuid().ToString("N") + ".prefab";
            // Preview 不替换 TestRunner 的 Untitled Scene，也不受其中隐藏 Runner 对象和 dirty 位影响。
            var preview = EditorSceneManager.NewPreviewScene();
            GameObject contents = null;
            try
            {
                var root = new GameObject("OriginalEventSystem"); root.SetActive(false);
                SceneManager.MoveGameObjectToScene(root, preview);
                var first = new GameObject("FirstSelected"); first.transform.SetParent(root.transform);
                var system = root.AddComponent<EventSystem>();
                system.firstSelectedGameObject = first; system.sendNavigationEvents = false; system.pixelDragThreshold = 37;
                var module = root.AddComponent(moduleType);
                // 使用真实持久化 InputAction 资产及其引用子资产，避免把临时 Runtime ScriptableObject 塞进 Prefab。
                BindPersistentUiActions(module);
                var asset = PrefabUtility.SaveAsPrefabAsset(root, path); Assert.That(asset, Is.Not.Null);
                Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset.GetComponent<EventSystem>(), out string guid, out long systemId), Is.True);
                Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset.GetComponent(moduleType), out string moduleGuid, out long moduleId), Is.True);
                EditorSceneManager.ClosePreviewScene(preview); preview = default;

                contents = PrefabUtility.LoadPrefabContents(path); root = contents;
                first = root.transform.Find("FirstSelected").gameObject;
                module = root.GetComponent(moduleType);
                var beforeModule = new SerializedObject(module);
                var actions = beforeModule.FindProperty("m_ActionsAsset").objectReferenceValue;
                var point = beforeModule.FindProperty("m_PointAction").objectReferenceValue;
                var click = beforeModule.FindProperty("m_LeftClickAction").objectReferenceValue;
                Assert.That(systemId, Is.Not.Zero); Assert.That(actions, Is.Not.Null);

                upgrade.Invoke(null, new object[] { root });
                // 更换 m_Script 后重新获取组件；旧托管 wrapper 可能已失效，不能继续读旧引用冒充成功。
                var changed = root.GetComponent<EventSystem>();
                Assert.That(changed.GetType().FullName, Is.EqualTo("Game.UI.FocusAwareEventSystem"));
                Assert.That(root.GetComponents<EventSystem>().Length, Is.EqualTo(1));
                Assert.That(changed.firstSelectedGameObject, Is.EqualTo(first));
                Assert.That(changed.sendNavigationEvents, Is.False);
                Assert.That(changed.pixelDragThreshold, Is.EqualTo(37));
                var remaining = root.GetComponent(moduleType);
                var afterModule = new SerializedObject(remaining);
                Assert.That(afterModule.FindProperty("m_ActionsAsset").objectReferenceValue, Is.EqualTo(actions));
                Assert.That(afterModule.FindProperty("m_PointAction").objectReferenceValue, Is.EqualTo(point));
                Assert.That(afterModule.FindProperty("m_LeftClickAction").objectReferenceValue, Is.EqualTo(click));
                Assert.That(PrefabUtility.SaveAsPrefabAsset(root, path), Is.Not.Null);
                PrefabUtility.UnloadPrefabContents(contents); contents = null;
                contents = PrefabUtility.LoadPrefabContents(path); root = contents; changed = root.GetComponent<EventSystem>();
                Assert.That(changed.GetType().FullName, Is.EqualTo("Game.UI.FocusAwareEventSystem"));
                asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset.GetComponent<EventSystem>(), out string afterGuid, out long afterSystemId), Is.True);
                Assert.That(afterGuid, Is.EqualTo(guid)); Assert.That(afterSystemId, Is.EqualTo(systemId),
                    "保存重载 Prefab 后也必须保留真实持久化 FileID，不能仅比较 Preview 的临时 InstanceID。");
                Assert.That(changed.firstSelectedGameObject.name, Is.EqualTo("FirstSelected"));
                Assert.That(changed.sendNavigationEvents, Is.False); Assert.That(changed.pixelDragThreshold, Is.EqualTo(37));
                Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset.GetComponent(moduleType), out string afterModuleGuid, out long afterModuleId), Is.True);
                Assert.That(afterModuleGuid, Is.EqualTo(moduleGuid)); Assert.That(afterModuleId, Is.EqualTo(moduleId));
                var reloadedModule = new SerializedObject(root.GetComponent(moduleType));
                Assert.That(reloadedModule.FindProperty("m_ActionsAsset").objectReferenceValue, Is.EqualTo(actions));
                Assert.That(reloadedModule.FindProperty("m_PointAction").objectReferenceValue, Is.EqualTo(point));
                Assert.That(reloadedModule.FindProperty("m_LeftClickAction").objectReferenceValue, Is.EqualTo(click));
            }
            finally
            {
                if (contents != null) PrefabUtility.UnloadPrefabContents(contents);
                if (preview.IsValid() && preview.isLoaded) EditorSceneManager.ClosePreviewScene(preview);
                AssetDatabase.DeleteAsset(path);
            }
        }

        [Test]
        public void UnknownCustomSystemRejectsWholeRootBeforeChangingEarlierSiblings()
        {
            var tools = FindType("Game.Project.Editor.EventSystemFocusTools"); Assert.That(tools, Is.Not.Null);
            var preview = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("ExplicitMigrationScope"); root.SetActive(false);
            SceneManager.MoveGameObjectToScene(root, preview);
            try
            {
                var first = new GameObject("FirstOriginalSystem"); first.transform.SetParent(root.transform);
                first.AddComponent<EventSystem>();
                var custom = new GameObject("UnknownCustomSystem"); custom.transform.SetParent(root.transform);
                custom.AddComponent<FocusMigrationCustomEventSystem>();
                var error = Assert.Throws<TargetInvocationException>(() =>
                    tools.GetMethod("UpgradeRoot").Invoke(null, new object[] { root }));
                Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
                Assert.That(first.GetComponent<EventSystem>().GetType(), Is.EqualTo(typeof(EventSystem)),
                    "未知自定义类型要在整根预检时拒绝，不能先改第一个 sibling 再失败。");
                Assert.That(custom.GetComponent<FocusMigrationCustomEventSystem>(), Is.Not.Null);
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
        }

        private static void BindPersistentUiActions(Component module)
        {
            const string path = "Assets/_Project/Settings/InputSystem_Actions.inputactions";
            var assets = AssetDatabase.LoadAllAssetsAtPath(path);
            Object actions = null, point = null, click = null;
            for (int i = 0; i < assets.Length; i++)
            {
                string type = assets[i].GetType().FullName;
                if (type == "UnityEngine.InputSystem.InputActionAsset") actions = assets[i];
                if (type != "UnityEngine.InputSystem.InputActionReference") continue;
                var action = assets[i].GetType().GetProperty("action").GetValue(assets[i]);
                string name = action != null ? (string)action.GetType().GetProperty("name").GetValue(action) : "";
                if (name == "Point") point = assets[i];
                if (name == "Click") click = assets[i];
            }
            Assert.That(actions, Is.Not.Null); Assert.That(point, Is.Not.Null); Assert.That(click, Is.Not.Null);
            var data = new SerializedObject(module);
            data.FindProperty("m_ActionsAsset").objectReferenceValue = actions;
            data.FindProperty("m_PointAction").objectReferenceValue = point;
            data.FindProperty("m_LeftClickAction").objectReferenceValue = click;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Invoke(EventSystem target, string name) => target.GetType().GetMethod(name, InstanceFlags).Invoke(target, null);
        private static Type FindType(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            { var type = assembly.GetType(name, false); if (type != null) return type; }
            return null;
        }
    }

    // 只存在测试内存中，不保存该派生类型；验证迁移工具尊重不认识的自定义职责。
    public sealed class FocusMigrationCustomEventSystem : EventSystem { }
}
