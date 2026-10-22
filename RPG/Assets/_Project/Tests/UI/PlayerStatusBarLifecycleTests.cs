using System.Collections;
using System.Reflection;
using Game.Combat;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.UI.Tests
{
    // 覆盖 Unity Awake/OnEnable/Start 的真实帧序，防止首个状态事件被晚初始化覆盖。
    public sealed class PlayerStatusBarLifecycleTests
    {
        [UnityTest]
        public IEnumerator StatusReceivedAfterEnableIsNotClearedByStart()
        {
            GameObject root = new GameObject("StatusBarTestRoot");
            root.SetActive(false);
            GameObject slot = new GameObject("WetSlot");
            GameObject iconObject = new GameObject("Icon");
            GameObject textObject = new GameObject("Percentage");
            try
            {
                slot.transform.SetParent(root.transform);
                iconObject.transform.SetParent(slot.transform);
                textObject.transform.SetParent(slot.transform);

                HealthComponent health = root.AddComponent<HealthComponent>();
                StatusIconView view = slot.AddComponent<StatusIconView>();
                Image icon = iconObject.AddComponent<Image>();
                Text percentage = textObject.AddComponent<Text>();
                PlayerStatusBar bar = root.AddComponent<PlayerStatusBar>();

                SetField(view, "_icon", icon);
                SetField(view, "_percentageText", percentage);
                SetField(bar, "_playerHealth", health);
                SetField(bar, "_wetView", view);

                root.SetActive(true);
                Invoke(bar, "OnStatusChanged", new StatusChangedEvent
                {
                    TargetId = health.Id,
                    Kind = StatusKind.Wet,
                    Intensity = 25f,
                    IsActive = true,
                });

                // 推进一帧让 Unity 执行 Start。旧实现会在这里把 OnEnable 后收到的状态清掉。
                yield return null;

                Assert.That(slot.activeSelf, Is.True);
                Assert.That(percentage.text, Is.EqualTo("25%"));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void SetField(object owner, string name, object value)
        {
            FieldInfo field = owner.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(owner, value);
        }

        private static void Invoke(object owner, string name, params object[] arguments)
        {
            MethodInfo method = owner.GetType().GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method.Invoke(owner, arguments);
        }

    }
}
