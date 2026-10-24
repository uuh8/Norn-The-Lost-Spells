using System;
using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Character.Tests
{
    /// <summary>
    /// 验证没有任何 World.Update / Tick 时，瞬时失焦或挂起也能立即撤销动作。
    /// 使用正式 Wizard Prefab 与 Driver，直接调用真实 Unity 生命周期回调；不伪造 Driver 的排队/取消逻辑。
    /// 此测试证明取消边沿的责任，不证明原生系统已收到某次 MouseUp。
    /// </summary>
    public sealed class OnlineCastFocusTests
    {
        private GameObject _root;
        private WizardController _wizard;
        private OnlineCastDriver _driver;

        [TearDown]
        public void Cleanup()
        {
            if (_driver != null) InvokeLifecycle(_driver, "OnDisable");
            if (_root != null) Object.DestroyImmediate(_root);
        }

        private void CreateClient()
        {
            _root = new GameObject("N4 synchronous focus cancellation fixture"); _root.SetActive(false);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/WizardPlayer.prefab");
            Assert.That(prefab, Is.Not.Null);
            var instance = Object.Instantiate(prefab, _root.transform);
            _wizard = instance.GetComponent<WizardController>();
            // 此 fixture 只测试 Driver 生命周期，不接管 Editor 的输入设备；按已有表现测试方式装配。
            _wizard.ConfigureExecutionMode(PlayerExecutionMode.Replica); InvokeLifecycle(_wizard, "Awake");
            InvokeLifecycle(instance.GetComponent<Game.Combat.HealthComponent>(), "Awake");
            _driver = instance.AddComponent<OnlineCastDriver>();
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            Assert.That(definition, Is.Not.Null);
            _driver.InitializeClient(1, 1, definition, true);
            _driver.InstallClientSetup(new string('a', 32), 1, .8f);
        }

        [TestCase("OnApplicationFocus", false, true)]
        [TestCase("OnApplicationPause", true, false)]
        public void NegativeLifecycleEdgeCancelsImmediately_AndPositiveEdgeWaitsForWorldGate(
            string callback, bool blockedValue, bool resumedValue)
        {
            CreateClient();
            _driver.CaptureAttackPress(); _driver.PresentAction(true);
            Assert.That(ReadFlag("_queued"), Is.True);
            Assert.That(_wizard.IsOnlineActionPresentationActive, Is.True);

            // 不调用 World.Update 或 StepPrediction。旧代码只清 Base 的 AttackBuffer，Driver 的独立队列仍然存在。
            SendLifecycle(callback, blockedValue);
            Assert.That(ReadFlag("_queued"), Is.False, "失焦/挂起消息本身必须清除施法队列。");
            Assert.That(_wizard.IsOnlineActionPresentationActive, Is.False, "同步撤销动作，不能等恢复时再取消旧动画。");
            Assert.That(ReadFlag("_waitRelease"), Is.True);

            SendLifecycle(callback, resumedValue);
            Assert.That(ReadFlag("_suppressed"), Is.True, "恢复回调不能绕过仍存在的 Frozen/菜单/连接门。");
            _driver.CaptureAttackPress();
            Assert.That(ReadFlag("_queued"), Is.False, "World 解除门禁前的点击仍应丢弃。");

            _driver.SetBlocked(false); // 模拟真实 World 已核实其余门禁后解除，尚未经过输入 Tick。
            Assert.That(ReadFlag("_waitRelease"), Is.True, "回焦不等于松开旧按键。");
            Assert.That(ReadFlag("_queued"), Is.False);
            _driver.CaptureAttackPress(); // 一个新的 performed 边沿允许显式新按下立即生效。
            Assert.That(ReadFlag("_queued"), Is.True);
        }

        private bool ReadFlag(string field) =>
            (bool)typeof(OnlineCastDriver).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_driver);

        private void SendLifecycle(string method, bool value)
        {
            // inactive fixture 不依赖 Unity 的消息派发资格。旧 Driver 没有该回调时不替它取消队列，
            // 仍调用现有 Base 回调，然后通过实际队列/动作结果检验责任缺口。
            InvokeOptionalLifecycle(_wizard, method, value);
            InvokeOptionalLifecycle(_driver, method, value);
        }

        private static void InvokeOptionalLifecycle(object target, string method, bool value)
        {
            Type type = target.GetType(); MethodInfo lifecycle = null;
            while (type != null && lifecycle == null)
            {
                lifecycle = type.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                type = type.BaseType;
            }
            lifecycle?.Invoke(target, new object[] { value });
        }

        private static void InvokeLifecycle(object target, string method)
        {
            Type type = target.GetType(); MethodInfo lifecycle = null;
            while (type != null && lifecycle == null)
            {
                lifecycle = type.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                type = type.BaseType;
            }
            Assert.That(lifecycle, Is.Not.Null, method);
            lifecycle.Invoke(target, null);
        }
    }
}
