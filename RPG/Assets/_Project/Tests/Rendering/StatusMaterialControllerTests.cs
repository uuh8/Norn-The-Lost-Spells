using System.Collections;
using System.Collections.Generic;
using Game.Combat;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// 这里使用真实 EventBus、StatusController、Renderer 与 MaterialPropertyBlock，
    /// 目的是验证完整的“状态事件 -> 表现桥 -> Renderer 参数”链路，而不是验证 Mock。
    /// </summary>
    public sealed class StatusMaterialControllerTests
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int BurningId = Shader.PropertyToID("_BurningIntensity");
        private static readonly int WetId = Shader.PropertyToID("_WetIntensity");
        private readonly List<GameObject> _createdObjects = new List<GameObject>();

        [UnityTest]
        public IEnumerator StatusEvent_FiltersTargetAndNormalizesIntensity()
        {
            yield return new EnterPlayMode();
            RestoreNormalGameTime();

            StatusMaterialController controller = CreateCharacter("Target", out Renderer targetRenderer);
            int targetId = controller.gameObject.GetInstanceID();

            PublishStatus(targetId + 1, StatusKind.Burning, 100f);
            yield return null;
            Assert.AreEqual(0f, controller.BurningVisualIntensity, 0.0001f,
                "其他角色的事件不应改变当前角色的状态材质参数。");

            PublishStatus(targetId, StatusKind.Burning, 150f);
            AdvanceUntilSettled(controller);

            var block = new MaterialPropertyBlock();
            targetRenderer.GetPropertyBlock(block);
            Assert.AreEqual(1f, controller.BurningVisualIntensity, 0.0001f,
                "Gameplay 的 0~100 强度应归一化并 Clamp 到 Shader 使用的 0~1。");
            Assert.AreEqual(1f, block.GetFloat(BurningId), 0.0001f);
        }

        [UnityTest]
        public IEnumerator WetEvent_WritesHalfIntensityAndPreservesExistingBaseColor()
        {
            yield return new EnterPlayMode();
            RestoreNormalGameTime();

            StatusMaterialController controller = CreateCharacter("Wet", out Renderer targetRenderer);
            var block = new MaterialPropertyBlock();
            Color existingFlashColor = Color.magenta;
            block.SetColor(BaseColorId, existingFlashColor);
            targetRenderer.SetPropertyBlock(block);

            PublishStatus(controller.gameObject.GetInstanceID(), StatusKind.Wet, 50f);
            AdvanceUntilSettled(controller);

            targetRenderer.GetPropertyBlock(block);
            Assert.AreEqual(0.5f, controller.WetVisualIntensity, 0.0001f);
            Assert.AreEqual(0.5f, block.GetFloat(WetId), 0.0001f);
            Assert.AreEqual(existingFlashColor, block.GetColor(BaseColorId),
                "状态桥必须先 GetPropertyBlock，不能清掉受击闪红等系统写入的字段。");
        }

        [UnityTest]
        public IEnumerator SharedMaterial_StillKeepsPerRendererStatusValues()
        {
            yield return new EnterPlayMode();
            RestoreNormalGameTime();

            StatusMaterialController first = CreateCharacter("First", out Renderer firstRenderer);
            StatusMaterialController second = CreateCharacter("Second", out Renderer secondRenderer);
            secondRenderer.sharedMaterial = firstRenderer.sharedMaterial;

            PublishStatus(first.gameObject.GetInstanceID(), StatusKind.Burning, 100f);
            PublishStatus(second.gameObject.GetInstanceID(), StatusKind.Burning, 25f);
            AdvanceUntilSettled(first, second);

            var firstBlock = new MaterialPropertyBlock();
            var secondBlock = new MaterialPropertyBlock();
            firstRenderer.GetPropertyBlock(firstBlock);
            secondRenderer.GetPropertyBlock(secondBlock);

            Assert.AreSame(firstRenderer.sharedMaterial, secondRenderer.sharedMaterial,
                "两名角色仍应引用同一份 Shared Material，而不是生成 Material Instance。");
            Assert.AreEqual(1f, firstBlock.GetFloat(BurningId), 0.0001f);
            Assert.AreEqual(0.25f, secondBlock.GetFloat(BurningId), 0.0001f,
                "MaterialPropertyBlock 应允许共享材质的不同 Renderer 使用不同参数。");
        }

        [UnityTest]
        public IEnumerator DisabledController_UnsubscribesAndClearsVisualState()
        {
            yield return new EnterPlayMode();
            RestoreNormalGameTime();

            StatusMaterialController controller = CreateCharacter("Disabled", out Renderer targetRenderer);
            int targetId = controller.gameObject.GetInstanceID();
            PublishStatus(targetId, StatusKind.Burning, 100f);
            AdvanceUntilSettled(controller);
            Assert.AreEqual(1f, controller.BurningVisualIntensity, 0.0001f);

            controller.enabled = false;
            PublishStatus(targetId, StatusKind.Burning, 50f);
            yield return null;

            var block = new MaterialPropertyBlock();
            targetRenderer.GetPropertyBlock(block);
            Assert.AreEqual(0f, controller.BurningVisualIntensity, 0.0001f);
            Assert.AreEqual(0f, block.GetFloat(BurningId), 0.0001f,
                "禁用组件时清零视觉参数，避免对象池复用后残留旧状态。");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            for (int i = 0; i < _createdObjects.Count; i++)
            {
                if (_createdObjects[i] != null)
                    Object.Destroy(_createdObjects[i]);
            }

            _createdObjects.Clear();
            EventBus<StatusChangedEvent>.Clear();
            RestoreNormalGameTime();

            if (Application.isPlaying)
            {
                yield return null;
                yield return new ExitPlayMode();
            }
        }

        private StatusMaterialController CreateCharacter(string objectName, out Renderer targetRenderer)
        {
            var root = new GameObject(objectName);
            _createdObjects.Add(root);
            root.AddComponent<StatusController>();

            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = "Visual";
            visual.transform.SetParent(root.transform, false);
            targetRenderer = visual.GetComponent<Renderer>();

            // AddComponent 会立即调用 Awake/OnEnable，因此必须先建立 Renderer 子物体。
            return root.AddComponent<StatusMaterialController>();
        }

        private static void RestoreNormalGameTime()
        {
            // EditMode 测试进入的是当前打开的场景，而本项目的 Wand Editor 会用
            // Time.timeScale = 0 暂停游戏。若测试继承了这个全局状态，生产组件中的
            // Time.deltaTime 就始终为 0，平滑动画自然永远不会前进。
            // 测试必须显式建立“游戏正常运行”的前置条件，不能要求生产代码绕过暂停规则。
            Time.timeScale = 1f;
        }

        private static void AdvanceUntilSettled(params StatusMaterialController[] controllers)
        {
            const int maxFrames = 180;

            // EditMode Test Runner 的 EnterPlayMode 只保证 Application.isPlaying，Run All 时不保证
            // Editor PlayerLoop 会真的推进 MonoBehaviour.Update。这里显式发送 Unity 的 Update 消息，
            // 仍然测试生产组件原本的 Update 路径，同时消除测试对 Editor 窗口调度时序的依赖。
            for (int frame = 0; frame < maxFrames; frame++)
            {
                bool allSettled = true;
                for (int i = 0; i < controllers.Length; i++)
                {
                    StatusMaterialController controller = controllers[i];
                    if (controller.IsTransitioning)
                        controller.SendMessage("Update", SendMessageOptions.RequireReceiver);

                    allSettled &= !controller.IsTransitioning;
                }

                if (allSettled)
                    return;
            }

            StatusMaterialController first = controllers.Length > 0 ? controllers[0] : null;
            Assert.Fail(
                "状态材质强度在显式推进 180 次 Update 后仍未收敛。\n"
                + $"Application.isPlaying={Application.isPlaying}, Time.timeScale={Time.timeScale}, "
                + $"Time.deltaTime={Time.deltaTime}, FrameCount={Time.frameCount}\n"
                + (first == null
                    ? "Controller=<null>"
                    : $"Controller.activeInHierarchy={first.gameObject.activeInHierarchy}, enabled={first.enabled}, "
                      + $"IsTransitioning={first.IsTransitioning}, Burning={first.BurningVisualIntensity}, "
                      + $"Wet={first.WetVisualIntensity}, Poisoned={first.PoisonedVisualIntensity}, Sticky={first.StickyVisualIntensity}"));
        }

        private static void PublishStatus(int targetId, StatusKind kind, float intensity)
        {
            EventBus<StatusChangedEvent>.Publish(new StatusChangedEvent
            {
                TargetId = targetId,
                Kind = kind,
                Intensity = intensity,
                IsActive = intensity > 0f
            });
        }
    }
}
