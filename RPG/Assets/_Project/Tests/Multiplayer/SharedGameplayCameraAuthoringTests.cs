using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Multiplayer.Tests
{
    /// <summary>检查原场景相机参数和导出引用，而非把手抄的距离当作相同手感。</summary>
    public sealed class SharedGameplayCameraAuthoringTests
    {
        [Test]
        public void PreviewCopiesOriginalCameraPipelineWithoutGameplayOwnersOrSourceWrites()
        {
            const string sourcePath = "Assets/_Project/Scenes/P7_DemoRun.unity";
            string originalBytes = Convert.ToBase64String(File.ReadAllBytes(sourcePath));
            var exporter = FindType("Game.Project.Editor.SharedGameplayCameraTools");
            Assert.That(exporter, Is.Not.Null, "缺少同源正式 Cinemachine 导出器。");
            GameObject preview = null;
            var source = EditorSceneManager.OpenPreviewScene(sourcePath);
            try
            {
                preview = (GameObject)exporter.GetMethod("BuildPreview").Invoke(null, null);
                Assert.That(preview.activeSelf, Is.False);
                var camera = preview.GetComponentInChildren<Camera>(true);
                var original = Find(source, "Unity.Cinemachine.CinemachineCamera");
                var rig = Find(preview, "Unity.Cinemachine.CinemachineCamera");
                Assert.That(camera, Is.Not.Null); Assert.That(rig, Is.Not.Null);
                Assert.That(preview.GetComponentsInChildren<Camera>(true).Length, Is.EqualTo(1));
                Assert.That(preview.GetComponentsInChildren<AudioListener>(true).Length, Is.EqualTo(1));
                Assert.That(Find(preview, "Unity.Cinemachine.CinemachineBrain"), Is.Not.Null);
                Assert.That(Find(preview, "Game.Rendering.LocalActorCameraBinding"), Is.Not.Null);
                foreach (var component in original.GetComponents<Component>())
                {
                    if (component is Transform) continue;
                    Assert.That(rig.GetComponent(component.GetType()), Is.Not.Null, component.GetType().FullName);
                }
                var left = new SerializedObject(original); var right = new SerializedObject(rig);
                Assert.That(right.FindProperty("Lens.FieldOfView").floatValue, Is.EqualTo(left.FindProperty("Lens.FieldOfView").floatValue));
                Assert.That(right.FindProperty("Target.TrackingTarget").objectReferenceValue, Is.Null);
                var originalFollow = original.GetComponent("CinemachineThirdPersonFollow");
                var follow = rig.GetComponent("CinemachineThirdPersonFollow");
                foreach (string path in new[] { "CameraDistance", "VerticalArmLength", "CameraSide", "AvoidObstacles.CameraRadius" })
                    Assert.That(new SerializedObject(follow).FindProperty(path).floatValue,
                        Is.EqualTo(new SerializedObject(originalFollow).FindProperty(path).floatValue), path);
                foreach (var component in preview.GetComponentsInChildren<Component>(true))
                {
                    Assert.That(component, Is.Not.Null);
                    string ns = component.GetType().Namespace ?? "";
                    Assert.That(ns == "Game.Character" || ns == "Game.Run" || ns == "Game.Combat" || ns == "Game.Multiplayer", Is.False);
                    // 所有 Scene 引用必须在新树内；资产引用则保留源资源。
                    var serialized = new SerializedObject(component); var property = serialized.GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                        var value = property.objectReferenceValue;
                        var linked = value as GameObject ?? (value as Component)?.gameObject;
                        if (linked != null && !EditorUtility.IsPersistent(linked))
                            Assert.That(linked.transform.IsChildOf(preview.transform) || linked == preview, Is.True, property.propertyPath);
                    }
                }
                Assert.That(Convert.ToBase64String(File.ReadAllBytes(sourcePath)), Is.EqualTo(originalBytes));
            }
            finally
            {
                if (preview != null) exporter.GetMethod("ReleasePreview").Invoke(null, new object[] { preview });
                EditorSceneManager.ClosePreviewScene(source);
            }
        }

        [Test]
        public void InstallRejectsUnapprovedSceneBeforeCreatingObjects()
        {
            var exporter = FindType("Game.Project.Editor.SharedGameplayCameraTools"); Assert.That(exporter, Is.Not.Null);
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var error = Assert.Throws<TargetInvocationException>(() => exporter.GetMethod("Install").Invoke(null, new object[] { scene }));
                Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
                Assert.That(scene.rootCount, Is.Zero);
                Assert.That(exporter.GetMethod("Verify", new[] { typeof(Scene) }), Is.Not.Null);
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        private static Component Find(Scene scene, string name)
        { foreach (var root in scene.GetRootGameObjects()) { var found = Find(root, name); if (found != null) return found; } return null; }
        private static Component Find(GameObject root, string name)
        { foreach (var component in root.GetComponentsInChildren<Component>(true)) if (component != null && component.GetType().FullName == name) return component; return null; }
        private static Type FindType(string name)
        { foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) { var found = assembly.GetType(name, false); if (found != null) return found; } return null; }
    }
}
