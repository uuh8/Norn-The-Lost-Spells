using System;
using System.Reflection;
using Game.Character;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.Multiplayer.Tests
{
    public sealed class OnlineCameraTargetTests
    {
        [Test]
        public void SharedCameraDisablesConnectionCameraAndListenerWithoutTouchingOtherScene()
        {
            var scene = EditorSceneManager.NewPreviewScene(); var other = EditorSceneManager.NewPreviewScene();
            var worldRoot = new GameObject("World"); worldRoot.SetActive(false); SceneManager.MoveGameObjectToScene(worldRoot, scene);
            var world = worldRoot.AddComponent<OnlineWorldBootstrap>();
            var actor = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/WizardPlayer.prefab"), worldRoot.transform);
            var original = new GameObject("OriginalCamera"); SceneManager.MoveGameObjectToScene(original, scene); var shared = original.AddComponent<Camera>();
            var staging = new GameObject("ConnectionCamera"); SceneManager.MoveGameObjectToScene(staging, scene);
            var old = staging.AddComponent<Camera>(); var listener = staging.AddComponent<AudioListener>();
            var foreignRoot = new GameObject("OtherSceneCamera"); SceneManager.MoveGameObjectToScene(foreignRoot, other); var foreign = foreignRoot.AddComponent<Camera>();
            try
            {
                typeof(OnlineWorldBootstrap).GetField("<LocalPlayer>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(world, actor.GetComponent<PlayerControllerBase>());
                typeof(OnlineWorldBootstrap).GetMethod("OnCameraBound", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(world, new object[] { new Game.Core.LocalActorCameraBoundEvent { Actor = actor, Camera = shared } });
                Assert.That(old.enabled, Is.False, "Connection Camera 会在 depth=-1 的原镜头之后覆盖世界，必须在绑定时移交输出权。");
                Assert.That(listener.enabled, Is.False, "旧 Listener 也必须移交，不能产生双监听。");
                Assert.That(shared.enabled, Is.True);
                Assert.That(foreign.enabled, Is.True, "只管理当前在线 Scene，不触碰被保留的原 Hub 或其他 Scene。");
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); EditorSceneManager.ClosePreviewScene(other); }
        }

        [Test]
        public void CameraFollowsVisualCorrectionWithoutMovingPhysicsOrOriginalPivot()
        {
            var root = new GameObject("CameraTargetTest"); root.SetActive(false);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/WizardPlayer.prefab");
            var actor = Object.Instantiate(prefab, root.transform);
            var player = actor.GetComponent<PlayerControllerBase>();
            var smoother = actor.AddComponent<MovementPresentationSmoother>(); smoother.Bind(player);
            var target = new GameObject("PresentationTarget"); target.transform.SetParent(root.transform);
            try
            {
                var type = typeof(OnlineWorldBootstrap).Assembly.GetType("Game.Multiplayer.OnlineCameraTarget");
                Assert.That(type, Is.Not.Null, "原 Cinemachine 需要平滑的只读目标，不能移动权威 CC 或丢掉 Prediction 的视觉校正。");
                var component = target.AddComponent(type);
                type.GetMethod("Bind").Invoke(component, new object[] { player });
                actor.transform.position = new Vector3(3, 2, 4);
                var physics = actor.transform.position;
                var pivot = player.CameraRoot.position;
                player.CameraRoot.rotation = Quaternion.Euler(17, 80, 0);
                smoother.PreserveAfterCorrection(physics + Vector3.right);
                type.GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(component, null);
                Assert.That(Vector3.Distance(target.transform.position, pivot + Vector3.right), Is.LessThan(.001f));
                Assert.That(Quaternion.Angle(target.transform.rotation, player.CameraRoot.rotation), Is.LessThan(.001f));
                Assert.That(actor.transform.position, Is.EqualTo(physics));
                Assert.That(player.CameraRoot.position, Is.EqualTo(pivot));
            }
            finally { Object.DestroyImmediate(root); }
        }
    }
}
