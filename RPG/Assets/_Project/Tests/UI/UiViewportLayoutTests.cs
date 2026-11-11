using System;
using System.Collections.Generic;
using System.Reflection;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.UI.Tests
{
    /// <summary>
    /// 读取正式资源的原 Rect 树，在隔离 Preview 内驱动五种设计视口并测量实际角点。
    /// 这里只调用纯布局入口，不启动账号、库存、输入或 Scene Gameplay；不保存任何资源。
    /// 几何测试不等于 Native 截图，尤其不证明字体栅格化与透明图片的逐像素覆盖。
    /// </summary>
    public sealed partial class UiViewportLayoutTests
    {
        private static readonly Vector2[] Viewports =
        {
            new Vector2(1920, 1080), new Vector2(2560, 1440), new Vector2(1920, 1200),
            new Vector2(3440, 1440), new Vector2(1280, 720)
        };
        private readonly List<Scene> _scenes = new List<Scene>();
        private readonly List<GameObject> _roots = new List<GameObject>();
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [TearDown] public void Cleanup()
        {
            foreach (var root in _roots) if (root != null) Object.DestroyImmediate(root);
            _roots.Clear();
            foreach (var scene in _scenes) if (scene.IsValid() && scene.isLoaded) EditorSceneManager.ClosePreviewScene(scene);
            _scenes.Clear();
        }

        [Test] public void OriginalMainMenuBackgroundCoversFiveViewports()
        {
            var scene = Open("Assets/_Project/Scenes/P7_MainMenu.unity");
            var menu = Find<DemoMainMenuController>(scene)[0];
            Configure(menu);
            var panel = (GameObject)new SerializedObject(menu).FindProperty("_mainPanel").objectReferenceValue;
            var image = panel.GetComponent<Image>();
            Assert.That(image, Is.Not.Null); Assert.That(image.sprite, Is.Not.Null);
            Assert.That(image.color.a, Is.EqualTo(1f));
            var fit = panel.GetComponent<AspectRatioFitter>();
            Assert.That(fit, Is.Not.Null); Assert.That(fit.aspectMode, Is.EqualTo(AspectRatioFitter.AspectMode.EnvelopeParent));
            Assert.That(fit.aspectRatio, Is.EqualTo(image.sprite.rect.width / image.sprite.rect.height).Within(.0001f));
            foreach (var viewport in Viewports)
            {
                var bounds = Measure((RectTransform)panel.transform, viewport);
                Assert.That(bounds.xMin, Is.LessThanOrEqualTo(.5f), viewport.ToString());
                Assert.That(bounds.yMin, Is.LessThanOrEqualTo(.5f), viewport.ToString());
                Assert.That(bounds.xMax, Is.GreaterThanOrEqualTo(viewport.x - .5f), viewport.ToString());
                Assert.That(bounds.yMax, Is.GreaterThanOrEqualTo(viewport.y - .5f), viewport.ToString());
            }
        }

        [Test] public void OriginalAccountCardRemainsCenteredAndInsideFiveViewports()
        {
            var menu = Clone("Assets/_Project/Prefabs/UI/Online/AccountMenu.prefab").GetComponent<AccountMenuController>();
            Configure(menu);
            AssertFits((RectTransform)menu.transform.Find("Panel"), true);
        }

        [Test] public void OriginalHubTabWarehouseAndShopFitFiveViewports()
        {
            var scene = Open("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity");
            var inventories = Find<SinglePlayerInventoryController>(scene);
            Assert.That(inventories.Count, Is.GreaterThanOrEqualTo(3), "实际Hub必须同时包含Tab、仓库、商店内嵌库存。");
            foreach (var inventory in inventories) AssertInventory(inventory);
            var shops = Find<ShopController>(scene); Assert.That(shops.Count, Is.EqualTo(1));
            Configure(shops[0]);
            var panel = (GameObject)new SerializedObject(shops[0]).FindProperty("_panel").objectReferenceValue;
            AssertFits((RectTransform)panel.transform, true);
        }

        [Test] public void OriginalHomeAndSharedInventoryFitFiveViewports()
        {
            var scene = Open("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity");
            foreach (var inventory in Find<SinglePlayerInventoryController>(scene)) AssertInventory(inventory);
            var shared = Clone("Assets/_Project/Resources/Presentation/CombatInventory.prefab");
            AssertInventory(shared.GetComponentInChildren<SinglePlayerInventoryController>(true));
        }

        [Test] public void OriginalDungeonDialogFitsFiveViewports()
        {
            var dialog = Clone("Assets/_Project/Resources/Online/DungeonEntryDialog.prefab").GetComponent<DungeonEntryDialogController>();
            Configure(dialog);
            AssertFits((RectTransform)dialog.transform.Find("Backdrop/Panel"), true);
        }

        [Test] public void OriginalSharedHudKeepsAllTenSlotsAndEdgeRegionsInsideFiveViewports()
        {
            var hud = Clone("Assets/_Project/Resources/Presentation/CombatHud.prefab");
            var held = hud.GetComponentInChildren<HeldItemHud>(true).transform as RectTransform;
            var health = (RectTransform)hud.GetComponentInChildren<PlayerHealthBar>(true).transform;
            var boss = (RectTransform)hud.GetComponentInChildren<BossHealthBarController>(true).transform.Find("BossHUDRoot");
            var stageController = hud.GetComponentInChildren<DemoRunHudController>(true);
            var stage = (RectTransform)stageController.GameplayPresentationRoot.transform;
            var notification = (RectTransform)stageController.NotificationPresentationRoot.transform;
            // LayoutGroup只收集activeInHierarchy的rectChildren；inactive树的强制Rebuild不会排十格，
            // 直接测量会把槽位序列化的(0,0)当成真实布局。只激活去掉Game Adapter后的原UGUI树。
            foreach (var behaviour in hud.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour.GetType().Namespace?.StartsWith("Game.", StringComparison.Ordinal) == true)
                    Object.DestroyImmediate(behaviour);
            foreach (var region in new[] { held, health, boss, stage, notification })
                for (Transform node = region; node != null && node != hud.transform.parent; node = node.parent)
                    node.gameObject.SetActive(true);
            hud.transform.parent.gameObject.SetActive(true);
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(held);
            Assert.That(held.childCount, Is.EqualTo(10));
            for (int i = 0; i < held.childCount; i++) AssertFits((RectTransform)held.GetChild(i), false);
            AssertFits(health, false); AssertFits(boss, false);
            AssertFits(stage, false); AssertFits(notification, false);
        }

        [Test] public void OriginalResearchWorkbenchKeepsItsThreeRegionsInsideFiveViewports()
        {
            var root = new GameObject("Inactive research layout fixture"); root.SetActive(false); _roots.Add(root);
            var presenter = root.AddComponent<WandProgramWorkbenchPresenter>();
            var samples = presenter.GetType().GetField("_samples", Private);
            samples.SetValue(presenter, Array.CreateInstance(samples.FieldType.GetElementType(), 3));
            presenter.GetType().GetProperty("IsOwned").SetValue(presenter, true);
            var layout = typeof(WandProgramWorkbenchPresenter).Assembly.GetType("Game.UI.WandProgramWorkbenchLayout", true);
            layout.GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,
                new object[] { presenter, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") });
            var workbench = root.transform.Find("SpellProgrammingV2Canvas/Workbench");
            Assert.That(workbench, Is.Not.Null);
            foreach (var name in new[] { "SequencePanel", "SpellLibrary", "ExecutionPreview", "CloseResearch" })
                AssertFits((RectTransform)workbench.Find(name), false);
        }

        private static void AssertInventory(SinglePlayerInventoryController inventory)
        {
            Assert.That(inventory, Is.Not.Null); Configure(inventory);
            var panel = (GameObject)new SerializedObject(inventory).FindProperty("_panelRoot").objectReferenceValue;
            var position = ((RectTransform)panel.transform).anchoredPosition;
            Configure(inventory);
            Assert.That(((RectTransform)panel.transform).anchoredPosition, Is.EqualTo(position), "重复启用/商店共用根不得累计位移。");
            AssertFits((RectTransform)panel.transform, true);
        }

        private static void Configure(Component owner)
        {
            // 修复前没有纯布局入口，保留原树并让角点断言暴露裁切；修复后调用与生命周期相同的入口。
            // 不反射Awake/OnEnable：那会在EditMode无端订阅账号事件、创建输入并污染后续用例。
            owner.GetType().GetMethod("ConfigureViewportLayout", Private)?.Invoke(owner, null);
        }

        private static void AssertFits(RectTransform panel, bool centered)
        {
            foreach (var viewport in Viewports)
            {
                var bounds = Measure(panel, viewport); string context = panel.name + " " + viewport;
                Assert.That(bounds.xMin, Is.GreaterThanOrEqualTo(-.5f), context);
                Assert.That(bounds.yMin, Is.GreaterThanOrEqualTo(-.5f), context);
                Assert.That(bounds.xMax, Is.LessThanOrEqualTo(viewport.x + .5f), context);
                Assert.That(bounds.yMax, Is.LessThanOrEqualTo(viewport.y + .5f), context);
                if (!centered) continue;
                Assert.That(bounds.center.x, Is.EqualTo(viewport.x * .5f).Within(.5f), context);
                Assert.That(bounds.center.y, Is.EqualTo(viewport.y * .5f).Within(.5f), context);
            }
        }

        private static Rect Measure(RectTransform panel, Vector2 viewport)
        {
            var canvas = panel.GetComponentInParent<Canvas>(true).rootCanvas;
            var scaler = canvas.GetComponent<CanvasScaler>();
            // 独立按UGUI公开的ScaleMode语义计算指定viewport的设计面积；不调用产品Helper得出预期。
            // 随后交给真实RectTransform计算anchor/parent/pivot，不用字符串检查代替布局结果。
            float scale = scaler.scaleFactor;
            if (scaler.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize)
            {
                var ratio = viewport / scaler.referenceResolution;
                scale = scaler.screenMatchMode == CanvasScaler.ScreenMatchMode.Expand ? Mathf.Min(ratio.x, ratio.y)
                    : scaler.screenMatchMode == CanvasScaler.ScreenMatchMode.Shrink ? Mathf.Max(ratio.x, ratio.y)
                    : Mathf.Pow(2f, Mathf.Lerp(Mathf.Log(ratio.x, 2f), Mathf.Log(ratio.y, 2f), scaler.matchWidthOrHeight));
            }
            scaler.enabled = false; canvas.renderMode = RenderMode.WorldSpace;
            var rect = (RectTransform)canvas.transform;
            rect.localScale = Vector3.one; rect.pivot = Vector2.one * .5f;
            rect.sizeDelta = viewport / scale; rect.ForceUpdateRectTransforms(); panel.ForceUpdateRectTransforms();
            var corners = new Vector3[4]; panel.GetWorldCorners(corners);
            var lower = (Vector2)rect.InverseTransformPoint(corners[0]) * scale + viewport * .5f;
            var upper = (Vector2)rect.InverseTransformPoint(corners[2]) * scale + viewport * .5f;
            return Rect.MinMaxRect(lower.x, lower.y, upper.x, upper.y);
        }

        private Scene Open(string path)
        { var scene = EditorSceneManager.OpenPreviewScene(path); _scenes.Add(scene); return scene; }
        private GameObject Clone(string path)
        {
            var parent = new GameObject("Inactive UI layout fixture"); parent.SetActive(false); _roots.Add(parent);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path); Assert.That(prefab, Is.Not.Null, path);
            return Object.Instantiate(prefab, parent.transform, false);
        }
        private static List<T> Find<T>(Scene scene) where T : Component
        {
            var found = new List<T>();
            foreach (var root in scene.GetRootGameObjects()) found.AddRange(root.GetComponentsInChildren<T>(true));
            return found;
        }
    }
}
