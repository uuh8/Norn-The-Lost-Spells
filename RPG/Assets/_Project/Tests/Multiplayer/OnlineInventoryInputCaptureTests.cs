using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Multiplayer.Tests
{
    public sealed class OnlineInventoryInputCaptureTests
    {
        [Test]
        public void ClosingOneUiOwnerCannotReleaseOtherOwnerOrPauseTheWorld()
        {
            var root = new GameObject("InputCaptureWorld"); root.SetActive(false);
            var world = root.AddComponent<OnlineWorldBootstrap>();
            float scale = Time.timeScale;
            try
            {
                var method = typeof(OnlineWorldBootstrap).GetMethod("OnInventoryVisibility", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(method, Is.Not.Null, "Tab 捕获必须区别于 Esc 菜单，不能让关闭一个窗口释放其他窗口。");
                Capture(method, world, 11, true); Capture(method, world, 22, true);
                Capture(method, world, 11, false);
                Assert.That(world.LocalInputSuppressed, Is.True);
                world.LocalInputSuppressed = true;
                Capture(method, world, 22, false);
                Assert.That(world.LocalInputSuppressed, Is.True, "Tab 关闭不能释放 Esc 菜单的 Owner。");
                world.LocalInputSuppressed = false;
                Assert.That(world.LocalInputSuppressed, Is.False);
                Assert.That(Time.timeScale, Is.EqualTo(scale));
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static void Capture(MethodInfo method, OnlineWorldBootstrap world, int owner, bool open) =>
            method.Invoke(world, new object[] { new InventoryPresentationVisibilityEvent { OwnerId = owner, Open = open } });

        [Test] public void UiCaptureSynchronouslyBlocksCastDriverBeforeAnotherInputCallback()
        {
            var root = new GameObject("ImmediateUiCapture"); root.SetActive(false);
            var playerRoot = new GameObject("LocalPrediction"); playerRoot.SetActive(false);
            var world = root.AddComponent<OnlineWorldBootstrap>();
            var player = playerRoot.AddComponent<Game.Character.WizardController>();
            var cast = playerRoot.AddComponent<Game.Character.OnlineCastDriver>();
            try
            {
                typeof(OnlineWorldBootstrap).GetProperty("LocalPlayer").SetValue(world, player);
                var field = typeof(Game.Character.OnlineCastDriver).GetField("_suppressed", BindingFlags.Instance | BindingFlags.NonPublic);
                var method = typeof(OnlineWorldBootstrap).GetMethod("OnInventoryVisibility", BindingFlags.Instance | BindingFlags.NonPublic);
                Capture(method, world, 11, true);
                Assert.That(field.GetValue(cast), Is.True, "同一轮Tab→数字键回调不能等Update才阻塞选杖。");
                field.SetValue(cast, false); world.LocalInputSuppressed = true;
                Assert.That(field.GetValue(cast), Is.True, "Esc占用也必须同步关闭动作入口。");
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(playerRoot); }
        }
    }
}
