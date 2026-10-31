using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Game.Character.Tests
{
    public sealed class PortalInteractInputFocusTests
    {
        [Test]
        public void ReenableSamplesCurrentFocusInsteadOfRetainingInactiveHubValue()
        {
            var root = new GameObject("Restored Hub portal input"); root.SetActive(false);
            root.AddComponent<CharacterController>(); var input = root.AddComponent<PortalInteractInput>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(PortalInteractInput);
            try
            {
                type.GetMethod("Awake", flags).Invoke(input, null);
                // 停用的 Hub 可能错过 OnApplicationFocus；用当前状态的反值模拟这份陈旧缓存。
                type.GetField("_focused", flags).SetValue(input, !Application.isFocused);
                type.GetField("_waitRelease", flags).SetValue(input, false);
                type.GetMethod("OnEnable", flags).Invoke(input, null);
                Assert.That((bool)type.GetField("_focused", flags).GetValue(input), Is.EqualTo(Application.isFocused));
                Assert.That((bool)type.GetField("_waitRelease", flags).GetValue(input), Is.True,
                    "恢复焦点不等于允许继承旧按键；仍需等待F释放后才能重新交互。");
            }
            finally
            {
                type.GetMethod("OnDisable", flags).Invoke(input, null);
                type.GetMethod("OnDestroy", flags).Invoke(input, null);
                Object.DestroyImmediate(root);
            }
        }
    }
}
