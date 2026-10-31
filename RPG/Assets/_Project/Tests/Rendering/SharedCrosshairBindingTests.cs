using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Rendering.Tests
{
    public sealed class SharedCrosshairBindingTests
    {
        [Test]
        public void LateHudUsesCurrentActorVisibilityAndOldOwnerCannotClearNewBinding()
        {
            var root = new GameObject("SharedCrosshair"); root.SetActive(false);
            var actor = new GameObject("LocalCaster");
            var view = root.AddComponent<CrosshairUI>();
            var crosshair = new GameObject("OriginalCrosshair"); crosshair.transform.SetParent(root.transform);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(CrosshairUI).GetField("_crosshair", flags).SetValue(view, crosshair);
            var bind = typeof(LocalActorPresentation).GetMethod("Bind", new[] { typeof(int), typeof(GameObject), typeof(bool) });
            try
            {
                Assert.That(bind, Is.Not.Null, "共享准心需要可重放的角色显示事实，不能依赖已错过的 Awake 事件。");
                bind.Invoke(null, new object[] { 101, actor, true });
                typeof(CrosshairUI).GetMethod("Awake", flags).Invoke(view, null);
                typeof(CrosshairUI).GetMethod("OnEnable", flags).Invoke(view, null);
                Assert.That(crosshair.activeSelf, Is.True);
                LocalActorPresentation.Clear(202);
                Assert.That(crosshair.activeSelf, Is.True, "旧世界清理不能撤销当前角色绑定。");
                LocalActorPresentation.Clear(101);
                Assert.That(crosshair.activeSelf, Is.False);
                bind.Invoke(null, new object[] { 202, actor, false });
                Assert.That(crosshair.activeSelf, Is.False, "近战形态仍可声明不显示准心，不能因联机一律常亮。");
            }
            finally
            {
                typeof(CrosshairUI).GetMethod("OnDisable", flags).Invoke(view, null);
                LocalActorPresentation.Clear(101); LocalActorPresentation.Clear(202);
                Object.DestroyImmediate(root); Object.DestroyImmediate(actor);
            }
        }
    }
}
