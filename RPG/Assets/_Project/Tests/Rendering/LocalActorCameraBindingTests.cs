using System;
using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Rendering.Tests
{
    /// <summary>相机只读取本机表现目标；反射让缺少新契约时得到明确失败，而非编译中断。</summary>
    public sealed class LocalActorCameraBindingTests
    {
        [Test]
        public void CameraRegistryRejectsStaleOwnerAndDestroyedCamera()
        {
            var registry = FindType("Game.Core.LocalActorCameraPresentation");
            Assert.That(registry, Is.Not.Null, "缺少 owner-fenced 原相机注册契约。");
            var actor = new GameObject("LocalActor"); var first = new GameObject("FirstCamera");
            var second = new GameObject("SecondCamera");
            var left = first.AddComponent<Camera>(); var right = second.AddComponent<Camera>();
            try
            {
                registry.GetMethod("Bind").Invoke(null, new object[] { 11, actor, left });
                registry.GetMethod("Bind").Invoke(null, new object[] { 22, actor, right });
                registry.GetMethod("Clear").Invoke(null, new object[] { 11 });
                AssertCamera(registry, actor, right);
                Object.DestroyImmediate(second);
                var args = new object[] { actor, null };
                Assert.That(registry.GetMethod("TryGet").Invoke(null, args), Is.False,
                    "Unity 已销毁对象不能被托管引用继续认作可用相机。");
            }
            finally
            {
                registry.GetMethod("Clear").Invoke(null, new object[] { 22 });
                Object.DestroyImmediate(actor); Object.DestroyImmediate(first);
                if (second != null) Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void LateBindingUsesExplicitPresentationTargetAndClearsOnlyItsOwnRegistration()
        {
            var type = FindType("Game.Rendering.LocalActorCameraBinding");
            Assert.That(type, Is.Not.Null, "缺少原 Cinemachine 的只读绑定器。");
            var registry = FindType("Game.Core.LocalActorCameraPresentation");
            var actor = new GameObject("Actor"); var target = new GameObject("SmoothedCameraTarget");
            var root = new GameObject("OriginalCameraComposition"); root.SetActive(false);
            var cameraRoot = new GameObject("Main Camera"); cameraRoot.transform.SetParent(root.transform);
            var camera = cameraRoot.AddComponent<Camera>();
            cameraRoot.AddComponent(FindType("Unity.Cinemachine.CinemachineBrain"));
            var rigRoot = new GameObject("CinemachineCamera"); rigRoot.transform.SetParent(root.transform);
            var rig = rigRoot.AddComponent(FindType("Unity.Cinemachine.CinemachineCamera"));
            var binding = root.AddComponent(type);
            var data = new SerializedObject(binding);
            data.FindProperty("_camera").objectReferenceValue = camera;
            data.FindProperty("_rig").objectReferenceValue = rig;
            data.ApplyModifiedPropertiesWithoutUndo();
            var bind = typeof(LocalActorPresentation).GetMethod("Bind", new[] { typeof(int), typeof(GameObject), typeof(bool), typeof(Transform) });
            Assert.That(bind, Is.Not.Null, "本机角色契约须明确提供 CameraTarget，不用层级字符串猜测。");
            try
            {
                bind.Invoke(null, new object[] { 31, actor, true, target.transform });
                Invoke(binding, "OnEnable");
                AssertCamera(registry, actor, camera);
                Assert.That(new SerializedObject(rig).FindProperty("Target.TrackingTarget").objectReferenceValue,
                    Is.EqualTo(target.transform), "跟随的是含校正平滑的表现目标，不能误改物理角色根。");
                LocalActorPresentation.Clear(31);
                AssertCamera(registry, actor, null);
                bind.Invoke(null, new object[] { 32, actor, true, target.transform });
                AssertCamera(registry, actor, camera);
                // 后建立的表现相机不能被旧绑定器 OnDisable 清除。
                registry.GetMethod("Bind").Invoke(null, new object[] { 77, actor, camera });
                Invoke(binding, "OnDisable");
                AssertCamera(registry, actor, camera);
                registry.GetMethod("Clear").Invoke(null, new object[] { 77 });
            }
            finally
            {
                Invoke(binding, "OnDisable"); LocalActorPresentation.Clear(31); LocalActorPresentation.Clear(32);
                Object.DestroyImmediate(root); Object.DestroyImmediate(actor); Object.DestroyImmediate(target);
            }
        }

        private static void AssertCamera(Type registry, GameObject actor, Camera expected)
        {
            Assert.That(registry, Is.Not.Null);
            var args = new object[] { actor, null };
            Assert.That(registry.GetMethod("TryGet").Invoke(null, args), Is.EqualTo(expected != null));
            Assert.That(args[1], Is.EqualTo(expected));
        }
        private static void Invoke(Component component, string name) => component.GetType().GetMethod(name,
            BindingFlags.Instance | BindingFlags.NonPublic).Invoke(component, null);
        private static Type FindType(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            { var type = assembly.GetType(name, false); if (type != null) return type; }
            return null;
        }
    }
}
