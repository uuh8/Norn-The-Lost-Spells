using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// Stage控件继续复用原Scene的目标、区域、剩余敌人和通知。它们进入共同Host后只换只读来源，
    /// 不能顺带复制单机Run/结果流程，也不能让“隐藏Stage”关闭共同HP、准心或手持栏。
    /// </summary>
    public sealed class SharedGameplayHudTests
    {
        private const string StageSource = "Assets/_Project/Scenes/P8_BossField.unity";

        [TestCase("resource")]
        [TestCase("preview")]
        public void WandResourceBarsGenerateMeshThatMatchesTheirFillAmount(string source)
        {
            // fillAmount 数值变了不代表可见几何变了：Image 没有 Sprite 时，UGUI 会退回完整矩形。
            // 同时检查当前正式资源和再次导出的预览，防止只修旧 Prefab、下次导出又复发。
            if (source == "preview") WithPreview(AssertWandResourceMeshes);
            else
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Resources/Presentation/CombatHud.prefab");
                Assert.That(asset, Is.Not.Null);
                var copy = UnityEngine.Object.Instantiate(asset);
                try { AssertWandResourceMeshes(copy); }
                finally { UnityEngine.Object.DestroyImmediate(copy); }
            }
        }

        private static void AssertWandResourceMeshes(GameObject root)
        {
            var cells = new SerializedObject(FindUnique(root, "Game.UI.HeldItemHud")).FindProperty("_cells");
            var populate = typeof(Image).GetMethod("OnPopulateMesh", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(VertexHelper) }, null);
            Assert.That(populate, Is.Not.Null);
            for (int slot = 0; slot < 3; slot++)
                foreach (string field in new[] { "Mana", "Cooldown" })
                {
                    var image = cells.GetArrayElementAtIndex(slot).FindPropertyRelative(field).objectReferenceValue as Image;
                    Assert.That(image, Is.Not.Null, field + " " + slot);
                    image.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 100f);
                    image.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 8f);
                    image.fillAmount = .25f;
                    using (var vertices = new VertexHelper())
                    {
                        populate.Invoke(image, new object[] { vertices });
                        Assert.That(vertices.currentVertCount, Is.GreaterThan(0));
                        float min = float.PositiveInfinity, max = float.NegativeInfinity;
                        var vertex = UIVertex.simpleVert;
                        for (int i = 0; i < vertices.currentVertCount; i++)
                        {
                            vertices.PopulateUIVertex(ref vertex, i);
                            min = Mathf.Min(min, vertex.position.x); max = Mathf.Max(max, vertex.position.x);
                        }
                        Assert.That(max - min, Is.EqualTo(image.GetPixelAdjustedRect().width * .25f).Within(.1f),
                            field + " " + slot + " 必须真正裁成四分之一，不能只验证 fillAmount 字段。");
                    }
                }
        }

        [Test]
        public void ExportedBossRetainsOriginalTeleportPresenterAndItsLocalHealthBinding()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/World/Online/Npcs/P8_05.prefab");
            Assert.That(prefab, Is.Not.Null, "先导出正式地图，检查实际进入运行时的Boss，不能只检查原Prefab。");
            var presenter = FindUnique(prefab, "Game.UI.BossTeleportVfxController");
            var data = new SerializedObject(presenter);
            var health = data.FindProperty("_bossHealth").objectReferenceValue as Component;
            Assert.That(health, Is.Not.Null); Assert.That(health.transform.IsChildOf(prefab.transform), Is.True);
            foreach (string field in new[] { "_openPrefab", "_idlePrefab", "_closePrefab" })
                Assert.That(data.FindProperty(field).objectReferenceValue, Is.Not.Null, field);
        }

        [Test]
        public void PreviewContainsOneConfiguredStageAdapterWithOnlyStageLeavesUnderItsVisibilityRoots()
        {
            WithPreview(root =>
            {
                var stage = FindUnique(root, "Game.UI.DemoRunHudController");
                Assert.That(((Behaviour)stage).enabled, Is.True, "原P8旧Controller曾禁用，导出后由共同Host接管读源，需要启用只读Adapter。");
                var data = new SerializedObject(stage);
                Assert.That(data.FindProperty("_runController").objectReferenceValue, Is.Null);
                var gameplay = (GameObject)data.FindProperty("_gameplayRoot").objectReferenceValue;
                var notification = (GameObject)data.FindProperty("_notificationRoot").objectReferenceValue;
                Assert.That(gameplay, Is.Not.Null); Assert.That(notification, Is.Not.Null);
                Assert.That(gameplay, Is.Not.EqualTo(root));
                Assert.That(stage.transform.IsChildOf(gameplay.transform), Is.False, "Stage隐藏不能连同接收新进度的Controller一起关闭。");
                foreach (string field in new[] { "_objectiveText", "_encounterText", "_remainingEnemyText" })
                {
                    var leaf = data.FindProperty(field).objectReferenceValue as Component;
                    Assert.That(leaf, Is.Not.Null, field); Assert.That(leaf.transform.IsChildOf(gameplay.transform), Is.True, field);
                }
                foreach (string field in new[] { "_notificationText", "_notificationIcon" })
                {
                    var leaf = data.FindProperty(field).objectReferenceValue as Component;
                    Assert.That(leaf, Is.Not.Null, field); Assert.That(leaf.transform.IsChildOf(notification.transform), Is.True, field);
                }
                foreach (string type in new[] { "Game.UI.PlayerHealthBar", "Game.UI.PlayerStatusBar", "Game.UI.HeldItemHud", "Game.Rendering.CrosshairUI", "Game.UI.BossHealthBarController" })
                {
                    var common = FindUnique(root, type);
                    Assert.That(common.transform.IsChildOf(gameplay.transform), Is.False, "隐藏Stage会误伤基础UI：" + type);
                    Assert.That(common.transform.IsChildOf(notification.transform), Is.False, "通知Root不能拥有基础UI：" + type);
                }
                foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    Assert.That(component, Is.Not.Null);
                    string ns = component.GetType().Namespace ?? string.Empty;
                    Assert.That(ns == "Game.Run" || ns == "Game.Character" || ns == "Game.Combat" || ns == "Game.ElementField", Is.False,
                        "共享Stage不能保留单机权威：" + component.GetType().Name);
                }
            });
        }

        [Test]
        public void StageLeavesPreserveActualSourceLayoutFontColorAndReferencesWithoutChangingSourceBytes()
        {
            byte[] p7 = File.ReadAllBytes("Assets/_Project/Scenes/P7_DemoRun.unity");
            byte[] p8 = File.ReadAllBytes(StageSource);
            var source = SceneManager.GetSceneByPath(StageSource); bool openedHere = !source.IsValid() || !source.isLoaded;
            if (openedHere) source = EditorSceneManager.OpenScene(StageSource, OpenSceneMode.Additive);
            try
            {
                Component original = null;
                foreach (var sceneRoot in source.GetRootGameObjects())
                    foreach (var component in sceneRoot.GetComponentsInChildren<MonoBehaviour>(true))
                        if (component != null && component.GetType().FullName == "Game.UI.DemoRunHudController")
                        { Assert.That(original, Is.Null); original = component; }
                Assert.That(original, Is.Not.Null, "来源必须是实际存在的authoring，不能用硬编码文本代替原控件。");
                WithPreview(root =>
                {
                    var sourceData = new SerializedObject(original); var cloneData = new SerializedObject(FindUnique(root, "Game.UI.DemoRunHudController"));
                    foreach (string field in new[] { "_objectiveText", "_encounterText", "_remainingEnemyText", "_notificationText", "_notificationIcon" })
                    {
                        var a = (Component)sourceData.FindProperty(field).objectReferenceValue;
                        var b = (Component)cloneData.FindProperty(field).objectReferenceValue;
                        Assert.That(b, Is.Not.Null, field);
                        var left = new SerializedObject(a); var right = new SerializedObject(b);
                        Assert.That(right.FindProperty("m_Color").colorValue, Is.EqualTo(left.FindProperty("m_Color").colorValue), field);
                        if (left.FindProperty("m_FontData.m_Font") != null)
                        {
                            Assert.That(right.FindProperty("m_FontData.m_Font").objectReferenceValue, Is.EqualTo(left.FindProperty("m_FontData.m_Font").objectReferenceValue), field);
                            Assert.That(right.FindProperty("m_FontData.m_FontSize").intValue, Is.EqualTo(left.FindProperty("m_FontData.m_FontSize").intValue), field);
                        }
                        AssertLayoutChain(a.transform, b.transform);
                    }
                });
                Assert.That(source.isDirty, Is.False);
                Assert.That(File.ReadAllBytes("Assets/_Project/Scenes/P7_DemoRun.unity"), Is.EqualTo(p7));
                Assert.That(File.ReadAllBytes(StageSource), Is.EqualTo(p8));
            }
            finally { if (openedHere) EditorSceneManager.CloseScene(source, true); }
        }

        private static void AssertLayoutChain(Transform source, Transform clone)
        {
            for (; source != null; source = source.parent, clone = clone.parent)
            {
                Assert.That(clone, Is.Not.Null); Assert.That(clone.name, Is.EqualTo(source.name));
                var original = source as RectTransform; var copied = clone as RectTransform;
                bool viewportDriven = source.GetComponent<Canvas>() != null && source.GetComponent<Canvas>().renderMode != RenderMode.WorldSpace;
                if (original == null || viewportDriven) continue;
                Assert.That(copied, Is.Not.Null); Assert.That(copied.anchorMin, Is.EqualTo(original.anchorMin));
                Assert.That(copied.anchorMax, Is.EqualTo(original.anchorMax)); Assert.That(copied.pivot, Is.EqualTo(original.pivot));
                Assert.That(copied.sizeDelta, Is.EqualTo(original.sizeDelta)); Assert.That(copied.anchoredPosition3D, Is.EqualTo(original.anchoredPosition3D));
            }
        }
        private static Component FindUnique(GameObject root, string fullName)
        {
            Component found = null;
            foreach (var component in root.GetComponentsInChildren<Component>(true))
                if (component != null && component.GetType().FullName == fullName)
                { Assert.That(found, Is.Null, "重复控件：" + fullName); found = component; }
            Assert.That(found, Is.Not.Null, "缺少原版共享控件：" + fullName); return found;
        }
        private static void WithPreview(Action<GameObject> assert)
        {
            Type exporter = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            { exporter = assembly.GetType("Game.Project.Editor.SharedGameplayHudTools"); if (exporter != null) break; }
            Assert.That(exporter, Is.Not.Null);
            var root = (GameObject)exporter.GetMethod("BuildPreview", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            try { Assert.That(root.activeSelf, Is.False); assert(root); }
            finally { exporter.GetMethod("ReleasePreview").Invoke(null, new object[] { root }); }
        }
    }
}
