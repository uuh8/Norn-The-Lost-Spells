using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.Multiplayer.Tests
{
    /// <summary>正式 UI 必须有明确的 Editor 导出入口；测试不通过引用 Editor asmdef 改变运行时依赖。</summary>
    public sealed class SharedGameplayHudAuthoringTests
    {
        private const string PlayerSourceScene = "Assets/_Project/Scenes/P7_DemoRun.unity";
        private const string BossSourceScene = "Assets/_Project/Scenes/P8_BossField.unity";

        [TestCase(PlayerSourceScene)]
        [TestCase(BossSourceScene)]
        public void SourceCombatCanvasUsesTheOriginalHeldHudResolutionPolicy(string path)
        {
            WithSourceScene(path, scene =>
            {
                var health = FindUnique(scene, "Game.UI.PlayerHealthBar");
                var canvas = health.GetComponentInParent<Canvas>(true);
                Assert.That(canvas, Is.Not.Null);
                var scaler = canvas.GetComponent("CanvasScaler");
                Assert.That(scaler, Is.Not.Null);
                var data = new SerializedObject(scaler);
                // 原十格栏已经采用1920x1080/Match .5。HP、Boss若仍保持ConstantPixelSize，
                // 2560x1600下十格放大而血条不变；这里约束真正的共同源，不能只修导出副本。
                Assert.That(data.FindProperty("m_UiScaleMode").intValue, Is.EqualTo(1),
                    "共同战斗Canvas必须使用ScaleWithScreenSize，避免与原十格栏缩放分离。");
                AssertVector(data.FindProperty("m_ReferenceResolution").vector2Value, new Vector2(1920f, 1080f));
                Assert.That(data.FindProperty("m_ScreenMatchMode").intValue, Is.Zero);
                Assert.That(data.FindProperty("m_MatchWidthOrHeight").floatValue, Is.EqualTo(.5f));
            });
        }

        [TestCase(PlayerSourceScene)]
        [TestCase(BossSourceScene)]
        public void SourcePlayerHudKeepsIts1080LayoutWhileFollowingTheScreenBottom(string path)
        {
            WithSourceScene(path, scene =>
            {
                var health = FindUnique(scene, "Game.UI.PlayerHealthBar");
                var playerHud = FindAncestor(health.transform, "PlayerHUD") as RectTransform;
                Assert.That(playerHud, Is.Not.Null);
                AssertVector(playerHud.anchorMin, new Vector2(.5f, 0f));
                AssertVector(playerHud.anchorMax, new Vector2(.5f, 0f));
                // 1080基准下旧中心偏移-421.493加半屏540，得到同一可见位置118.507。
                // bottom Anchor把这个值变为距屏幕底部的设计单位，不能再随半屏高度漂260px。
                AssertVector(playerHud.anchoredPosition, new Vector2(-10.710327f, 118.50702f));
                AssertVector(playerHud.sizeDelta, new Vector2(1352.0247f, 237.0139f));
                AssertVector(playerHud.pivot, new Vector2(.5f, .5f));
            });
        }

        [Test]
        public void SourceBossHudFollowsTheTopThroughAViewportStretchParent()
        {
            WithSourceScene(BossSourceScene, scene =>
            {
                var boss = FindUnique(scene, "Game.UI.BossHealthBarController");
                var parent = boss.transform as RectTransform;
                Assert.That(parent, Is.Not.Null);
                // 只改Boss叶树的Anchor还不够：旧Controller父Rect固定1080高，必须一起贴合viewport。
                AssertViewportStretch(parent);
                var bar = parent.Find("BossHUDRoot") as RectTransform;
                Assert.That(bar, Is.Not.Null);
                AssertVector(bar.anchorMin, new Vector2(.5f, 1f));
                AssertVector(bar.anchorMax, new Vector2(.5f, 1f));
                // 顶部预留120设计像素给十格栏；原1080也已有BossName与第十格重叠，不能继续保留。
                AssertVector(bar.anchoredPosition, new Vector2(0f, -235f));
                AssertVector(bar.sizeDelta, new Vector2(1366.1621f, 230.0082f));
            });
        }

        [Test]
        public void SourceStageAndNotificationFollowScreenEdgesThroughAViewportStretchParent()
        {
            WithSourceScene(BossSourceScene, scene =>
            {
                var stage = FindUnique(scene, "Game.UI.DemoRunHudController");
                AssertViewportStretch(stage.transform as RectTransform);
                var data = new SerializedObject(stage);
                var gameplay = ((GameObject)data.FindProperty("_gameplayRoot").objectReferenceValue).transform as RectTransform;
                var notification = ((GameObject)data.FindProperty("_notificationRoot").objectReferenceValue).transform as RectTransform;
                Assert.That(gameplay, Is.Not.Null); Assert.That(notification, Is.Not.Null);
                AssertVector(gameplay.anchorMin, new Vector2(.5f, 1f));
                AssertVector(gameplay.anchorMax, new Vector2(.5f, 1f));
                AssertVector(gameplay.anchoredPosition, new Vector2(0f, -224.76f));
                AssertVector(gameplay.sizeDelta, new Vector2(552.5605f, 209.52f));
                AssertVector(notification.anchorMin, Vector2.one);
                AssertVector(notification.anchorMax, Vector2.one);
                AssertVector(notification.anchoredPosition, new Vector2(-243.56006f, -104.76147f));
                AssertVector(notification.sizeDelta, new Vector2(487.1201f, 209.5228f));
            });
        }

        [Test]
        public void SharedHudExporter_ProvidesExplicitInstallAndVerifyEntryPoints()
        {
            // asmdef 只认识生产模块，透过已加载程序集查找工具，避免测试把 Editor 反向引进 Gameplay。
            var exporter = FindExporter();
            Assert.That(exporter, Is.Not.Null,
                "缺少共享正式 HUD 导出器；旧 OnlineCombatHud 不能代替原 P7/P8 authoring。");
            Assert.That(exporter.GetMethod("Install", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(Scene) }, null), Is.Not.Null);
            Assert.That(exporter.GetMethod("Verify", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(Scene) }, null), Is.Not.Null);
        }

        [Test]
        public void Preview_ReusesFormalHierarchyAndAssetsWithoutSinglePlayerAuthority()
        {
            const string sourcePath = "Assets/_Project/Scenes/P7_DemoRun.unity";
            string sourceBytes = Convert.ToBase64String(File.ReadAllBytes(sourcePath));
            var exporter = FindExporter();
            Assert.That(exporter, Is.Not.Null);
            var build = exporter.GetMethod("BuildPreview", BindingFlags.Public | BindingFlags.Static);
            Assert.That(build, Is.Not.Null, "需要可销毁的 inactive Preview 验证真正导出结果，而不只检查 API 名称。");
            GameObject preview = null;
            var source = SceneManager.GetSceneByPath(sourcePath);
            bool openedHere = !source.IsValid() || !source.isLoaded;
            if (openedHere) source = EditorSceneManager.OpenScene(sourcePath, OpenSceneMode.Additive);
            try
            {
                preview = (GameObject)build.Invoke(null, null);
                Assert.That(preview.activeSelf, Is.False, "在 authoring 清理和只读数据绑定前不能触发 Runtime 生命周期。");
                AssertCount(preview, "Game.UI.PlayerHealthBar", 1);
                AssertCount(preview, "Game.UI.PlayerStatusBar", 1);
                AssertCount(preview, "Game.UI.BossHealthBarController", 1);
                AssertCount(preview, "Game.Rendering.CrosshairUI", 1);
                AssertCount(preview, "Game.UI.HeldItemHud", 1);
                AssertCount(preview, "Game.UI.LocalActorHudBinding", 1);
                // 正式 P7/P8 已把旧蓝条移入十格手持栏，不能为了凑组件数复活已删除的旧 UI。
                AssertCount(preview, "Game.UI.PlayerManaBar", 0);
                foreach (var component in preview.GetComponentsInChildren<Component>(true))
                {
                    Assert.That(component, Is.Not.Null, "共享树不能携带 Missing Script。");
                    string ns = component.GetType().Namespace ?? "";
                    Assert.That(ns == "Game.Run" || ns == "Game.Character" || ns == "Game.Combat" || ns == "Game.Multiplayer",
                        Is.False, "只导出表现，不携带单机 Session、存档、AI 或另一套权威。");
                }
                Component hp = Find(preview, "Game.UI.PlayerHealthBar");
                Component status = Find(preview, "Game.UI.PlayerStatusBar");
                Assert.That(new SerializedObject(hp).FindProperty("_playerHealth").objectReferenceValue, Is.Null);
                Assert.That(new SerializedObject(status).FindProperty("_playerHealth").objectReferenceValue, Is.Null);
                Assert.That(new SerializedObject(Find(preview, "Game.UI.HeldItemHud")).FindProperty("_session").objectReferenceValue, Is.Null);
                Component original = null;
                foreach (var root in source.GetRootGameObjects())
                { original = Find(root, "Game.UI.PlayerHealthBar"); if (original != null) break; }
                Assert.That(original, Is.Not.Null);
                AssertAncestorLayout(original.transform, hp.transform);
                var originalFill = (Component)new SerializedObject(original).FindProperty("_fill").objectReferenceValue;
                var clonedFill = (Component)new SerializedObject(hp).FindProperty("_fill").objectReferenceValue;
                Assert.That(clonedFill, Is.Not.Null);
                Assert.That(new SerializedObject(clonedFill).FindProperty("m_Sprite").objectReferenceValue,
                    Is.EqualTo(new SerializedObject(originalFill).FindProperty("m_Sprite").objectReferenceValue));
                var originalLabel = (Component)new SerializedObject(original).FindProperty("_valueLabel").objectReferenceValue;
                var clonedLabel = (Component)new SerializedObject(hp).FindProperty("_valueLabel").objectReferenceValue;
                Assert.That(new SerializedObject(clonedLabel).FindProperty("m_FontData.m_Font").objectReferenceValue,
                    Is.EqualTo(new SerializedObject(originalLabel).FindProperty("m_FontData.m_Font").objectReferenceValue));
                Assert.That(Convert.ToBase64String(File.ReadAllBytes(sourcePath)), Is.EqualTo(sourceBytes),
                    "导出不能保存或改写正式源 Scene。");
                Assert.That(source.isDirty, Is.False);
            }
            finally
            {
                if (preview != null) exporter.GetMethod("ReleasePreview").Invoke(null, new object[] { preview });
                if (openedHere) EditorSceneManager.CloseScene(source, true);
            }
        }

        [Test]
        public void Install_RejectsUnsavedOrFormalSourceScenesBeforeMutation()
        {
            var exporter = FindExporter(); Assert.That(exporter, Is.Not.Null);
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var error = Assert.Throws<TargetInvocationException>(() =>
                    exporter.GetMethod("Install").Invoke(null, new object[] { scene }));
                Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
                Assert.That(scene.rootCount, Is.Zero, "非法写入目标必须在建立任何对象之前拒绝。");
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        [Test]
        public void SharedHeldHierarchyDoesNotDependOnRemovedSinglePlayerBootstrapActivation()
        {
            var exporter = FindExporter(); Assert.That(exporter, Is.Not.Null);
            var preview = (GameObject)exporter.GetMethod("BuildPreview").Invoke(null, null);
            try
            {
                var held = Find(preview, "Game.UI.HeldItemHud"); Assert.That(held, Is.Not.Null);
                for (var node = held.transform; node != preview.transform; node = node.parent)
                    Assert.That(node.gameObject.activeSelf, Is.True,
                        "纯表现路径不能保留原单机Bootstrap才会激活的inactive祖先：" + node.name);
            }
            finally { exporter.GetMethod("ReleasePreview").Invoke(null, new object[] { preview }); }
        }

        [Test]
        public void FormalSceneContainsNoIndependentOnlineMovementHud()
        {
            const string path = "Assets/_Project/Scenes/Online/OnlineRoom.unity";
            var scene = SceneManager.GetSceneByPath(path);
            bool openedHere = !scene.IsValid() || !scene.isLoaded;
            if (openedHere) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                // 检查实际持久化场景，防止工具只改了导出 Preview，发布 Client 仍携带旧横幅。
                foreach (var root in scene.GetRootGameObjects())
                {
                    Assert.That(root.name, Is.Not.EqualTo("OnlineMovementControls"));
                    AssertCount(root, "Game.UI.OnlineMovementHint", 0);
                }
            }
            finally { if (openedHere) EditorSceneManager.CloseScene(scene, true); }
        }

        private static void AssertAncestorLayout(Transform source, Transform clone)
        {
            while (source != null)
            {
                Assert.That(clone, Is.Not.Null);
                Assert.That(clone.name, Is.EqualTo(source.name));
                // ScreenSpace Canvas 根坐标/尺寸由当前 viewport 驱动；inactive Preview 没有同一 viewport。
                // 真正的作者布局是子 Rect 和 CanvasScaler 参数，不能把运行中的(960,540)当固定美术偏移。
                bool viewportDriven = source.GetComponent<Canvas>() != null && source.GetComponent<Canvas>().renderMode != RenderMode.WorldSpace;
                // 子Rect的localPosition同样是父Rect尺寸、Anchor和anchoredPosition共同求出的派生值。
                // 源Scene Canvas已布局、inactive Preview尚未布局时它们可不同；作者合同应比较下方
                // Anchor/Pivot/sizeDelta/anchoredPosition3D，真实屏幕角点另由viewport PlayMode检查。
                if (!viewportDriven && !(source is RectTransform))
                    Assert.That(clone.localPosition, Is.EqualTo(source.localPosition));
                Assert.That(clone.localRotation, Is.EqualTo(source.localRotation));
                if (!viewportDriven) Assert.That(clone.localScale, Is.EqualTo(source.localScale));
                if (!viewportDriven && source is RectTransform original)
                {
                    var copied = clone as RectTransform; Assert.That(copied, Is.Not.Null);
                    Assert.That(copied.anchorMin, Is.EqualTo(original.anchorMin));
                    Assert.That(copied.anchorMax, Is.EqualTo(original.anchorMax));
                    Assert.That(copied.pivot, Is.EqualTo(original.pivot));
                    Assert.That(copied.sizeDelta, Is.EqualTo(original.sizeDelta));
                    Assert.That(copied.anchoredPosition3D, Is.EqualTo(original.anchoredPosition3D));
                }
                var scaler = source.GetComponent("CanvasScaler");
                if (scaler != null)
                {
                    var copied = clone.GetComponent("CanvasScaler"); Assert.That(copied, Is.Not.Null);
                    var left = new SerializedObject(scaler); var right = new SerializedObject(copied);
                    Assert.That(right.FindProperty("m_UiScaleMode").intValue,
                        Is.EqualTo(left.FindProperty("m_UiScaleMode").intValue));
                    Assert.That(right.FindProperty("m_ScreenMatchMode").intValue,
                        Is.EqualTo(left.FindProperty("m_ScreenMatchMode").intValue));
                    Assert.That(right.FindProperty("m_ScaleFactor").floatValue,
                        Is.EqualTo(left.FindProperty("m_ScaleFactor").floatValue));
                    Assert.That(right.FindProperty("m_ReferenceResolution").vector2Value,
                        Is.EqualTo(left.FindProperty("m_ReferenceResolution").vector2Value));
                    Assert.That(right.FindProperty("m_MatchWidthOrHeight").floatValue,
                        Is.EqualTo(left.FindProperty("m_MatchWidthOrHeight").floatValue));
                }
                source = source.parent; clone = clone.parent;
            }
        }

        private static void WithSourceScene(string path, Action<Scene> inspect)
        {
            var scene = SceneManager.GetSceneByPath(path);
            bool openedHere = !scene.IsValid() || !scene.isLoaded;
            if (openedHere) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                Assert.That(scene.isDirty, Is.False, "源Authoring回归必须读取已保存Scene，不能隐式保存用户修改。");
                inspect(scene);
                Assert.That(scene.isDirty, Is.False, "布局检查只能读，不能修源后把修改当作测试通过。");
            }
            finally { if (openedHere) EditorSceneManager.CloseScene(scene, true); }
        }

        private static Component FindUnique(Scene scene, string type)
        {
            Component found = null;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                    if (component != null && component.GetType().FullName == type)
                    { Assert.That(found, Is.Null, "正式源控件必须唯一：" + type); found = component; }
            Assert.That(found, Is.Not.Null, "正式源缺少控件：" + type);
            return found;
        }

        private static Transform FindAncestor(Transform node, string name)
        {
            for (; node != null; node = node.parent) if (node.name == name) return node;
            Assert.Fail("原控件祖先缺少：" + name); return null;
        }

        private static void AssertViewportStretch(RectTransform rect)
        {
            Assert.That(rect, Is.Not.Null);
            AssertVector(rect.anchorMin, Vector2.zero); AssertVector(rect.anchorMax, Vector2.one);
            AssertVector(rect.offsetMin, Vector2.zero); AssertVector(rect.offsetMax, Vector2.zero);
        }

        private static void AssertVector(Vector2 actual, Vector2 expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(.001f));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(.001f));
        }

        private static void AssertCount(GameObject root, string name, int expected)
        {
            int count = 0;
            foreach (var component in root.GetComponentsInChildren<Component>(true))
                if (component != null && component.GetType().FullName == name) count++;
            Assert.That(count, Is.EqualTo(expected), name);
        }

        private static Component Find(GameObject root, string name)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
                if (component != null && component.GetType().FullName == name) return component;
            return null;
        }

        private static Type FindExporter()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                var type = assemblies[i].GetType("Game.Project.Editor.SharedGameplayHudTools", false);
                if (type != null) return type;
            }
            return null;
        }
    }
}
