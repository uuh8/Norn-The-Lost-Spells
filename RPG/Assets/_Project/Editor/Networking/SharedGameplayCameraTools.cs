using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Game.Rendering;
using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.Project.Editor
{
    /// <summary>从正式 P7 导出唯一相机 authoring，避免联机维护另一套距离、FOV 或避障算法。</summary>
    public static class SharedGameplayCameraTools
    {
        public const string SourceScene = "Assets/_Project/Scenes/P7_DemoRun.unity";
        public const string RootPrefix = "SharedGameplayCamera_";
        private static readonly HashSet<Type> Allowed = new HashSet<Type>
        {
            typeof(Transform), typeof(Camera), typeof(AudioListener), typeof(UniversalAdditionalCameraData),
            typeof(CinemachineBrain), typeof(CinemachineCamera), typeof(CinemachineThirdPersonFollow),
            typeof(CinemachineRotateWithFollowTarget), typeof(CinemachineCameraOffset),
            typeof(AimFramingController), typeof(LocalActorCameraBinding)
        };
        private static readonly Regex InstanceIds = new Regex("\"instanceID\":-?[0-9]+", RegexOptions.Compiled);

        [MenuItem("Tools/Norn/Online/Setup Shared Gameplay Camera")]
        public static void Setup()
        {
            OnlineSceneSetup.RequireCleanEditor();
            var original = SceneManager.GetActiveScene();
            var scene = SceneManager.GetSceneByPath(RoomLifecycleTools.PortalScenePath);
            bool opened = !scene.IsValid() || !scene.isLoaded;
            if (opened) scene = EditorSceneManager.OpenScene(RoomLifecycleTools.PortalScenePath, OpenSceneMode.Additive);
            try { Install(scene); Verify(scene); EditorSceneManager.SaveScene(scene); }
            finally
            {
                if (opened) EditorSceneManager.CloseScene(scene, true);
                if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
            }
        }

        [MenuItem("Tools/Norn/Online/Verify Shared Gameplay Camera")]
        public static void VerifyMenu()
        {
            OnlineSceneSetup.RequireCleanEditor();
            var scene = SceneManager.GetSceneByPath(RoomLifecycleTools.PortalScenePath);
            bool opened = !scene.IsValid() || !scene.isLoaded;
            if (opened) scene = EditorSceneManager.OpenScene(RoomLifecycleTools.PortalScenePath, OpenSceneMode.Additive);
            try { Verify(scene); }
            finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
        }

        public static void Install(Scene scene)
        {
            RequireTarget(scene);
            var preview = BuildPreview(); var temporary = preview.scene;
            try
            {
                Validate(preview);
                // 新树验证完成后才替换旧树；失败不会先删除已有表现，也不操作源 Scene。
                foreach (var root in scene.GetRootGameObjects())
                    if (root.name.StartsWith(RootPrefix, StringComparison.Ordinal)) Object.DestroyImmediate(root);
                SceneManager.MoveGameObjectToScene(preview, scene); preview.SetActive(true);
                EditorSceneManager.MarkSceneDirty(scene); preview = null;
            }
            finally
            {
                if (preview != null) Object.DestroyImmediate(preview);
                EditorSceneManager.ClosePreviewScene(temporary);
            }
        }

        public static GameObject BuildPreview()
        {
            var loaded = SceneManager.GetSceneByPath(SourceScene);
            if (loaded.IsValid() && loaded.isLoaded && loaded.isDirty)
                throw new InvalidOperationException("正式 P7 有未保存修改，拒绝导出不确定的相机来源。");
            string hash = SourceHash();
            var source = EditorSceneManager.OpenPreviewScene(SourceScene);
            var output = EditorSceneManager.NewPreviewScene();
            GameObject result = null;
            try
            {
                result = new GameObject(RootPrefix + hash); result.SetActive(false);
                SceneManager.MoveGameObjectToScene(result, output);
                var camera = FindOne<Camera>(source); var rig = FindOne<CinemachineCamera>(source);
                // 两个选择本身只有相机组件；inactive 父节点阻止复制过程触发 Runtime Owner。
                var copiedCamera = Object.Instantiate(camera.gameObject, result.transform, true); copiedCamera.name = camera.name;
                var copiedRig = Object.Instantiate(rig.gameObject, result.transform, true); copiedRig.name = rig.name;
                ClearExternalReferences(result);
                var binding = result.AddComponent<LocalActorCameraBinding>();
                var serialized = new SerializedObject(binding);
                serialized.FindProperty("_camera").objectReferenceValue = copiedCamera.GetComponent<Camera>();
                serialized.FindProperty("_rig").objectReferenceValue = copiedRig.GetComponent<CinemachineCamera>();
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Validate(result);
                if (hash != SourceHash()) throw new InvalidOperationException("导出期间相机来源发生变化，请重试。");
                return result;
            }
            catch
            {
                if (result != null) Object.DestroyImmediate(result);
                EditorSceneManager.ClosePreviewScene(output); throw;
            }
            finally { EditorSceneManager.ClosePreviewScene(source); }
        }

        public static void ReleasePreview(GameObject preview)
        {
            if (preview == null) return;
            var scene = preview.scene;
            if (!EditorSceneManager.IsPreviewScene(scene)) throw new InvalidOperationException("不能通过 ReleasePreview 删除已安装的相机。");
            Object.DestroyImmediate(preview); EditorSceneManager.ClosePreviewScene(scene);
        }

        public static void Verify(Scene scene)
        {
            RequireTarget(scene); GameObject installed = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (!root.name.StartsWith(RootPrefix, StringComparison.Ordinal)) continue;
                if (installed != null) throw new InvalidOperationException("OnlineRoom 存在多份共享相机。");
                installed = root;
            }
            if (installed == null || installed.name != RootPrefix + SourceHash())
                throw new InvalidOperationException("共享相机缺失或来源已变化，请 Setup Shared Gameplay Camera。");
            Validate(installed);
            var preview = BuildPreview();
            try
            {
                if (Signature(installed) != Signature(preview))
                    throw new InvalidOperationException("共享相机参数或资源引用偏离原 P7，请重新导出。");
            }
            finally { ReleasePreview(preview); }
        }

        private static void RequireTarget(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded || scene.path != RoomLifecycleTools.PortalScenePath)
                throw new InvalidOperationException("相机导出只允许写入 OnlineRoom，不覆盖正式单机场景或测试 lab。");
        }
        private static string SourceHash() => Hash128.Compute("NornSharedCameraV1|" + AssetDatabase.GetAssetDependencyHash(SourceScene)).ToString();

        private static T FindOne<T>(Scene scene) where T : Component
        {
            T found = null;
            foreach (var root in scene.GetRootGameObjects()) foreach (var component in root.GetComponentsInChildren<T>(true))
            {
                if (found != null) throw new InvalidOperationException("正式相机来源存在多份 " + typeof(T).Name);
                found = component;
            }
            if (found == null) throw new InvalidOperationException("正式相机来源缺少 " + typeof(T).Name);
            return found;
        }

        private static void ClearExternalReferences(GameObject root)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || !Allowed.Contains(component.GetType()))
                    throw new InvalidOperationException("相机源包含非表现组件，请先核对 authoring。");
                var data = new SerializedObject(component); var property = data.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var value = property.objectReferenceValue;
                    if (value == null || EditorUtility.IsPersistent(value)) continue;
                    var linked = value is Component item ? item.transform : value is GameObject go ? go.transform : null;
                    if (linked == null || !linked.IsChildOf(root.transform)) property.objectReferenceValue = null;
                }
                data.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static void Validate(GameObject root)
        {
            if (root.GetComponentsInChildren<Camera>(true).Length != 1 || root.GetComponentsInChildren<AudioListener>(true).Length != 1 ||
                root.GetComponentsInChildren<CinemachineBrain>(true).Length != 1 || root.GetComponentsInChildren<CinemachineCamera>(true).Length != 1 ||
                root.GetComponentsInChildren<LocalActorCameraBinding>(true).Length != 1)
                throw new InvalidOperationException("共享相机必须是唯一 Camera/Listener/Brain/Rig/Binding。");
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || !Allowed.Contains(component.GetType())) throw new InvalidOperationException("共享相机带有非表现组件或 Missing Script。");
                var data = new SerializedObject(component); var property = data.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var value = property.objectReferenceValue;
                    if (value == null || EditorUtility.IsPersistent(value)) continue;
                    var linked = value is Component item ? item.transform : value is GameObject go ? go.transform : null;
                    if (linked == null || !linked.IsChildOf(root.transform))
                        throw new InvalidOperationException("共享相机残留外部 Scene 引用：" + component.GetType().Name + "." + property.propertyPath);
                }
            }
            var rig = root.GetComponentInChildren<CinemachineCamera>(true);
            if (rig.Target.TrackingTarget != null || rig.Target.LookAtTarget != null)
                throw new InvalidOperationException("相机 authoring 不得预绑定已剥离的单机 Player。");
        }

        private static string Signature(GameObject root)
        {
            var text = new StringBuilder();
            foreach (var node in root.GetComponentsInChildren<Transform>(true))
            {
                text.Append(Path(root.transform, node)).Append('|').Append(node.gameObject.layer).Append('|').Append(node.gameObject.tag);
                if (node != root.transform) text.Append('|').Append(node.gameObject.activeSelf);
                foreach (var component in node.GetComponents<Component>())
                {
                    text.Append('|').Append(component.GetType().FullName).Append('|')
                        .Append(InstanceIds.Replace(EditorJsonUtility.ToJson(component), "\"instanceID\":0"));
                    var data = new SerializedObject(component); var property = data.GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference || property.name == "m_GameObject") continue;
                        var value = property.objectReferenceValue; text.Append('|').Append(property.propertyPath).Append('=');
                        if (value == null) { text.Append("null"); continue; }
                        if (EditorUtility.IsPersistent(value)) { text.Append(GlobalObjectId.GetGlobalObjectIdSlow(value)); continue; }
                        var linked = value is Component item ? item.transform : value is GameObject go ? go.transform : null;
                        text.Append(linked != null ? Path(root.transform, linked) : value.GetType().FullName);
                    }
                }
            }
            return text.ToString();
        }
        private static string Path(Transform root, Transform node) => node == root ? "/" : Path(root, node.parent) + "/" + node.name;
    }
}
