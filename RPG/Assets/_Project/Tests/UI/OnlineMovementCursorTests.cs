using System.Reflection;
using System;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Object = UnityEngine.Object;

namespace Game.UI.Tests
{
    public sealed class OnlineMovementCursorTests : InputTestFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void EscapeBelongsToInventoryEvenWhenItsCaptureWasReleasedEarlierInSameFrame(bool inventoryAlreadyClosed)
        {
            ExerciseEscape(true, inventoryAlreadyClosed, false, false);
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        public void EscapeStillTogglesConnectionMenuWhenInventoryDoesNotOwnThePress(bool alreadyOpen, bool expectedOpen)
        {
            ExerciseEscape(false, false, alreadyOpen, expectedOpen);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InventoryOwnerConsumesEscapeEvenWhenConnectionMenuAlsoCapturesInput(bool inventoryAlreadyClosed)
        {
            ExerciseEscape(true, inventoryAlreadyClosed, true, true);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TotalViewOnlyInventoryCaptureStillConsumesEscapeIncludingItsReleaseFrame(bool inventoryAlreadyClosed)
        {
            // 旧的只读 View 合同本身也提供“非菜单捕获”事实；晚订阅者不能因为漏了一次 Owner 事件
            // 就丢掉已有保护。菜单和库存同开仍必须使用明确 Owner，不能靠这个 fallback 猜测。
            ExerciseEscape(true, inventoryAlreadyClosed, false, false, false);
        }

        private static void ExerciseEscape(bool inventoryCaptured, bool releaseBeforeUpdate, bool menuOpen, bool expectedOpen, bool publishVisibility = true)
        {
            var mode = Cursor.lockState; bool visible = Cursor.visible;
            var previousKeyboard = Keyboard.current;
            var keyboard = InputSystem.AddDevice<Keyboard>();
            var root = Object.Instantiate(Resources.Load<GameObject>("Online/DungeonEntryDialog"));
            var dialog = root.GetComponent<DungeonEntryDialogController>();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var onView = typeof(DungeonEntryDialogController).GetMethod("OnGameplay", flags);
            int requests = 0;
            Action<OnlineMovementMenuRequestEvent> countRequest = _ => requests++;
            EventBus<OnlineMovementMenuRequestEvent>.Subscribe(countRequest);
            try
            {
                typeof(DungeonEntryDialogController).GetMethod("Awake", flags).Invoke(dialog, null);
                // 库存与菜单都是合法 Owner，总 InputCaptured=true 不能判断究竟哪层还在使用 Esc。
                // 原 Owner 事实来自共享 Controller 的开闭事件，避免回归继续依赖这个有缺陷的反推。
                typeof(DungeonEntryDialogController).GetMethod("OnDisable", flags).Invoke(dialog, null);
                typeof(DungeonEntryDialogController).GetMethod("OnEnable", flags).Invoke(dialog, null);
                if (inventoryCaptured && publishVisibility)
                    EventBus<InventoryPresentationVisibilityEvent>.Publish(new InventoryPresentationVisibilityEvent { OwnerId = 73011, Open = true });
                onView.Invoke(dialog, new object[] { new OnlineMovementPresentationEvent
                    { Active = true, Connected = true, MenuOpen = menuOpen, InputCaptured = inventoryCaptured || menuOpen } });
                // 官方 InputTestFixture 交换并在 TearDown 恢复输入运行时，让 EditMode 能处理真实的键盘事件，
                // 不受 Editor 当前焦点/Player缓冲影响，也不重置用户的原生设备。这里不 Invoke 菜单按钮；
                // 模拟生产中 Cancel Action 先释放 Tab 捕获，随后 Update 仍读取同一次 Esc 的时序。
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Escape));
                InputSystem.Update();
                Assert.That(keyboard.escapeKey.wasPressedThisFrame, Is.True);
                if (releaseBeforeUpdate)
                {
                    if (publishVisibility)
                        EventBus<InventoryPresentationVisibilityEvent>.Publish(new InventoryPresentationVisibilityEvent { OwnerId = 73011, Open = false });
                    onView.Invoke(dialog, new object[] { new OnlineMovementPresentationEvent
                    { Active = true, Connected = true, MenuOpen = menuOpen, InputCaptured = menuOpen } });
                }
                typeof(DungeonEntryDialogController).GetMethod("Update", flags).Invoke(dialog, null);
                var panel = (GameObject)typeof(DungeonEntryDialogController).GetField("_panel", flags).GetValue(dialog);
                Assert.That(panel.activeSelf, Is.EqualTo(expectedOpen), "同一次 Esc 应优先取消背包交互，不能穿透到另一层菜单。");
                Assert.That(requests, Is.EqualTo(inventoryCaptured ? 0 : 1), "背包消耗的按键不能再发布联机菜单请求。");
            }
            finally
            {
                EventBus<OnlineMovementMenuRequestEvent>.Unsubscribe(countRequest);
                EventBus<InventoryPresentationVisibilityEvent>.Publish(new InventoryPresentationVisibilityEvent { OwnerId = 73011, Open = false });
                typeof(DungeonEntryDialogController).GetMethod("OnDisable", flags).Invoke(dialog, null);
                Object.DestroyImmediate(root);
                InputSystem.RemoveDevice(keyboard);
                if (previousKeyboard != null && previousKeyboard.added) previousKeyboard.MakeCurrent();
                Cursor.lockState = mode; Cursor.visible = visible;
            }
        }

        [Test]
        public void InventoryCaptureUnlocksCursorWithoutOpeningConnectionMenu()
        {
            var mode = Cursor.lockState; bool visible = Cursor.visible;
            var root = Object.Instantiate(Resources.Load<GameObject>("Online/DungeonEntryDialog"));
            var dialog = root.GetComponent<DungeonEntryDialogController>();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                typeof(DungeonEntryDialogController).GetMethod("Awake", flags).Invoke(dialog, null);
                Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
                typeof(DungeonEntryDialogController).GetMethod("OnGameplay", flags).Invoke(dialog,
                    new object[] { new OnlineMovementPresentationEvent { Active = true, Connected = true, InputCaptured = true } });
                Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.None));
                Assert.That(((GameObject)typeof(DungeonEntryDialogController).GetField("_panel", flags).GetValue(dialog)).activeSelf, Is.False);
            }
            finally { Object.DestroyImmediate(root); Cursor.lockState = mode; Cursor.visible = visible; }
        }

        [TestCase(true, true)]
        [TestCase(false, false)]
        public void OnlineMenuOrDisconnectUnlocksCursorForActualButtons(bool menuOpen, bool connected)
        {
            var mode = Cursor.lockState; bool visible = Cursor.visible;
            var root = Object.Instantiate(Resources.Load<GameObject>("Online/DungeonEntryDialog"));
            var dialog = root.GetComponent<DungeonEntryDialogController>();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                typeof(DungeonEntryDialogController).GetMethod("Awake", flags).Invoke(dialog, null);
                Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
                typeof(DungeonEntryDialogController).GetMethod("OnGameplay", flags).Invoke(dialog,
                    new object[] { new OnlineMovementPresentationEvent { Active = true, MenuOpen = menuOpen, Connected = connected } });
                Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.None)); Assert.That(Cursor.visible, Is.True);
            }
            finally { Object.DestroyImmediate(root); Cursor.lockState = mode; Cursor.visible = visible; }
        }
    }
}
