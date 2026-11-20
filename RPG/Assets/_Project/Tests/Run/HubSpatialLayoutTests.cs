using System;
using System.Reflection;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    public sealed class HubSpatialLayoutTests
    {
        private Scene _scene;
        private AccountInventoryPresentationSource _source;
        private SinglePlayerInventoryController _boundController;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [SetUp] public void Load() => _scene = EditorSceneManager.OpenPreviewScene(SessionState.GetString("Norn.HubAlignment.ReviewPath", "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity"));
        [TearDown] public void Close()
        {
            if (_boundController != null) _boundController.BindPresentationSource(null);
            _source?.Dispose(); _source = null; _boundController = null;
            if (_scene.IsValid()) EditorSceneManager.ClosePreviewScene(_scene);
        }

        [Test] public void StationRegionsUseTheAuthoredBoxesAndDisableWithoutFallback()
        {
            var hub = Find<HubInteractionController>(); var prompt = Find<NearbyPickupListView>();
            Assert.That(hub, Is.Not.Null); Assert.That(prompt, Is.Not.Null);
            Assert.That(new SerializedObject(prompt).FindProperty("_hub").objectReferenceValue, Is.SameAs(hub),
                "屏幕提示必须查询同一站点Owner，不能靠旧世界TextMesh或另一个距离规则决定交互。");
            foreach (string field in new[] { "_warehouseArea", "_shopArea", "_researchArea" })
            {
                var area = new SerializedObject(hub).FindProperty(field).objectReferenceValue as HubAreaVolume;
                Assert.That(area, Is.Not.Null, field);
                var box = area.Volume; Assert.That(box, Is.Not.Null); Assert.That(box.isTrigger, Is.True);
                Assert.That(area.Contains(area.Center), Is.True, field + "作者区域中心必须可进入。");
                Vector3 outside = box.transform.TransformPoint(box.center + Vector3.right * (box.size.x * .5f + .1f));
                Assert.That(area.Contains(outside), Is.False, field + "必须按实际Box边界拒绝区外点。");
                bool enabled = area.enabled;
                try { area.enabled = false; Assert.That(area.Contains(area.Center), Is.False, "停用区域不能恢复旧点半径。"); }
                finally { area.enabled = enabled; }
            }
        }

        [TestCase(0, "F 仓库")]
        [TestCase(1, "F 商店")]
        [TestCase(3, "F 法术研究台")]
        public void ResolvedStationHintReusesTheFirstAuthoredRowWithoutMovingAssets(int station, string expected)
        {
            var view = Find<NearbyPickupListView>(); Assert.That(view, Is.Not.Null);
            var data = new SerializedObject(view);
            _boundController = data.FindProperty("_inventory").objectReferenceValue as SinglePlayerInventoryController;
            Assert.That(_boundController, Is.Not.Null);
            var session = data.FindProperty("_session").objectReferenceValue as SinglePlayerInventorySession;
            Assert.That(session, Is.Not.Null);
            var catalog = new SerializedObject(session).FindProperty("_catalog").objectReferenceValue as ItemCatalog;
            Assert.That(catalog, Is.Not.Null);
            Assert.That(catalog.TryCreateIndex(out var index, out var error), Is.True, error);
            Assert.That(AccountInventoryPresentationSource.TryCreate(index, new OnlineAccountInventorySnapshot {
                SchemaVersion = OnlineAccountInventorySnapshot.CurrentSchemaVersion, PlayerId = "hub-prompt-fixture",
                Version = 9, Gold = 137, ContentManifestHash = new string('a', 64), Items = Array.Empty<OnlineOwnedItemSnapshot>()
            }, out _source, out error), Is.True, error);
            _boundController.BindPresentationSource(_source);
            var rows = data.FindProperty("_rows"); var labels = data.FindProperty("_labels");
            Assert.That(rows.arraySize, Is.EqualTo(5)); Assert.That(labels.arraySize, Is.EqualTo(rows.arraySize));
            var anchor = view.Panel.anchorMin; var position = view.Panel.anchoredPosition;
            // 只输入已解析的站点显示事实，不冒充身份或提交F命令；实际区域门由上例/PlayMode覆盖。
            typeof(NearbyPickupListView).GetField("_stationKind", Private).SetValue(view, station);
            view.RefreshNow();
            for (int i = 0; i < rows.arraySize; i++)
                Assert.That(((GameObject)rows.GetArrayElementAtIndex(i).objectReferenceValue).activeSelf, Is.EqualTo(i == 0));
            Assert.That(((Text)labels.GetArrayElementAtIndex(0).objectReferenceValue).text, Is.EqualTo(expected));
            Assert.That(view.VisibleCount, Is.Zero, "站点行不是虚构的地面物品。");
            Assert.That(_source.ReadOnly.Count, Is.Zero); Assert.That(_source.Version, Is.EqualTo(9)); Assert.That(_source.Currency, Is.EqualTo(137));
            Assert.That(view.Panel.anchorMin, Is.EqualTo(anchor)); Assert.That(view.Panel.anchoredPosition, Is.EqualTo(position));
            typeof(NearbyPickupListView).GetField("_stationKind", Private).SetValue(view, -1); view.RefreshNow();
            for (int i = 0; i < rows.arraySize; i++)
                Assert.That(((GameObject)rows.GetArrayElementAtIndex(i).objectReferenceValue).activeSelf, Is.False,
                    "离开站点且无物品后不能留下上一站点文字。");
        }

        [Test] public void ContextHintStaysNearTheCharacterWhilePersistentNavigationStaysAtTheEdge()
        {
            var nearby = Find<NearbyPickupListView>(); Assert.That(nearby, Is.Not.Null); Assert.That(nearby.Panel, Is.Not.Null);
            var canvas = nearby.GetComponentInParent<Canvas>(true); Assert.That(canvas, Is.Not.Null);
            Assert.That(canvas.renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay), "区域行不再需要面向相机的世界TextMesh。");
            Assert.That(nearby.Panel.anchorMin, Is.EqualTo(nearby.Panel.anchorMax));
            Assert.That(nearby.Panel.anchorMin.x, Is.InRange(.54f, .59f));
            Assert.That(nearby.Panel.anchorMin.y, Is.InRange(.40f, .49f));
            var navigation = Find<HubNavigationView>(); Assert.That(navigation, Is.Not.Null);
            var prompt = new SerializedObject(navigation).FindProperty("_prompt").objectReferenceValue as Text;
            Assert.That(prompt, Is.Not.Null, "Hub保留原常驻导航提示，不再要求副本专用Extraction HUD。");
            Assert.That(prompt.rectTransform.anchorMin.x, Is.EqualTo(0));
            Assert.That(prompt.rectTransform.anchoredPosition.x, Is.LessThanOrEqualTo(32));
        }
        [Test] public void ExpeditionIsAtSouthwestAndExperimentIsWestOfShop()
        {
            HubDefinition definition = null;
            foreach (var root in _scene.GetRootGameObjects())
            { var hub=root.GetComponentInChildren<HubInteractionController>(true); if(hub!=null)definition=hub.Definition; }
            Assert.That(definition,Is.Not.Null);
            Assert.That(definition.ExperimentCenter.x,Is.LessThan(-25));Assert.That(definition.ExperimentCenter.z,Is.GreaterThan(5));
            foreach(var station in definition.Stations) if(station.Kind==HubStationKind.Expedition)
            { Assert.That(station.Position.x,Is.LessThan(-25));Assert.That(station.Position.z,Is.LessThan(-30)); }
        }
        private T Find<T>() where T : Component
        {
            foreach (var root in _scene.GetRootGameObjects())
            { var found = root.GetComponentInChildren<T>(true); if (found != null) return found; }
            return null;
        }
    }
}
