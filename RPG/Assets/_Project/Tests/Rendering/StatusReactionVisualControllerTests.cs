using System.Collections;
using Game.Combat;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// 验证 Combat → EventBus → Rendering 的只读表现桥：目标过滤、生命周期映射、
    /// 预实例复用与 Disable 清理。测试不验证具体美术观感，只验证可自动化的架构契约。
    /// </summary>
    public sealed class StatusReactionVisualControllerTests
    {
        private GameObject _root;
        private GameObject _steamTemplate;
        private StatusReactionVisualProfile _profile;
        private StatusReactionVisualController _controller;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return new EnterPlayMode();

            _steamTemplate = new GameObject("SteamTemplate");
            _steamTemplate.SetActive(false);
            _profile = ScriptableObject.CreateInstance<StatusReactionVisualProfile>();
            _profile.ExtinguishSteamPrefab = _steamTemplate;

            // Inactive 根物体允许测试在 Awake 前注入 Profile，模拟 Prefab Inspector 已完成序列化。
            _root = new GameObject("ReactionCarrier");
            _root.SetActive(false);
            _controller = _root.AddComponent<StatusReactionVisualController>();
            _controller.ConfigureForTests(_profile);
            _root.SetActive(true);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Event_FiltersOtherCarrierAndStartsMatchingVisual()
        {
            // 先发送错误 TargetId，证明全局事件不会串到其他角色；再发送正确 Id 启动槽位。
            Publish(_root.GetInstanceID() + 1, ElementReactionPhase.Started, 0.25f, 1f);
            yield return null;
            Assert.IsFalse(_controller.IsExtinguishVisualActive);

            Publish(_root.GetInstanceID(), ElementReactionPhase.Started, 0.75f, 2f);
            yield return null;

            Assert.IsTrue(_controller.IsExtinguishVisualActive);
            Assert.AreEqual(0.75f, _controller.ExtinguishStrength, 1e-4f);
            Assert.AreEqual(2f, _controller.ExtinguishExpectedDuration, 1e-4f);
        }

        [UnityTest]
        public IEnumerator RepeatedStarted_RefreshesSnapshotWithoutInstantiatingAgain()
        {
            // InstanceId 与 childCount 保持不变，证明重复 Started 只复用 Awake 创建的对象。
            Publish(_root.GetInstanceID(), ElementReactionPhase.Started, 0.25f, 1f);
            yield return null;
            int firstInstanceId = _controller.ExtinguishVisualInstanceId;
            int childCount = _root.transform.childCount;

            Publish(_root.GetInstanceID(), ElementReactionPhase.Started, 0.9f, 3f);
            yield return null;

            Assert.AreNotEqual(0, firstInstanceId);
            Assert.AreEqual(firstInstanceId, _controller.ExtinguishVisualInstanceId);
            Assert.AreEqual(childCount, _root.transform.childCount);
            Assert.AreEqual(0.9f, _controller.ExtinguishStrength, 1e-4f);
            Assert.AreEqual(3f, _controller.ExtinguishExpectedDuration, 1e-4f);
        }

        [UnityTest]
        public IEnumerator ResolvedAndCancelled_StopContinuousVisual()
        {
            // 正常完成和外部取消都是终止态，二者都必须停止并清空持续视觉。
            Publish(_root.GetInstanceID(), ElementReactionPhase.Started, 1f, 1f);
            yield return null;
            Publish(_root.GetInstanceID(), ElementReactionPhase.Resolved, 1f, 0f);
            yield return null;
            Assert.IsFalse(_controller.IsExtinguishVisualActive);

            Publish(_root.GetInstanceID(), ElementReactionPhase.Started, 1f, 1f);
            yield return null;
            Publish(_root.GetInstanceID(), ElementReactionPhase.Cancelled, 0f, 0f);
            yield return null;
            Assert.IsFalse(_controller.IsExtinguishVisualActive);
        }

        [UnityTest]
        public IEnumerator DisabledController_UnsubscribesAndClearsVisual()
        {
            // Disable 后既要立即清理旧视觉，也不能再响应后续 EventBus 广播。
            Publish(_root.GetInstanceID(), ElementReactionPhase.Started, 1f, 1f);
            yield return null;
            Assert.IsTrue(_controller.IsExtinguishVisualActive);

            _controller.enabled = false;
            Assert.IsFalse(_controller.IsExtinguishVisualActive);
            Publish(_root.GetInstanceID(), ElementReactionPhase.Started, 0.5f, 2f);
            yield return null;

            Assert.IsFalse(_controller.IsExtinguishVisualActive);
            Assert.AreEqual(0f, _controller.ExtinguishStrength, 1e-4f);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // EventBus 是 static；测试结束必须 Clear，避免 delegate 跨测试残留造成顺序相关失败。
            EventBus<ElementReactionEvent>.Clear();
            if (_root != null)
                Object.Destroy(_root);
            if (_steamTemplate != null)
                Object.Destroy(_steamTemplate);
            if (_profile != null)
                Object.Destroy(_profile);

            yield return null;
            if (Application.isPlaying)
                yield return new ExitPlayMode();
        }

        private static void Publish(
            int targetId,
            ElementReactionPhase phase,
            float strength,
            float duration)
        {
            // 测试统一从真实 EventBus 入口发布，而不是直接调用 private handler，覆盖完整集成路径。
            EventBus<ElementReactionEvent>.Publish(new ElementReactionEvent
            {
                TargetId = targetId,
                Reaction = ElementReactionId.Extinguish,
                Phase = phase,
                WorldPosition = Vector3.one,
                NormalizedStrength = strength,
                ExpectedDuration = duration,
            });
        }
    }
}
