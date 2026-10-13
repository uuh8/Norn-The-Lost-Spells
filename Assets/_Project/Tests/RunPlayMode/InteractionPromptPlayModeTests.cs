#if UNITY_EDITOR
using System.Collections;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    /// <summary>统一 F 上下文：站点优先，离开站点后拾取；使用最新作者隔离 Review。</summary>
    public sealed class InteractionPromptPlayModeTests : BackpackWorkspacePlayModeTests
    {
        protected override string ReviewPath => "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub_UnifiedItemsReview.unity";
        protected override string EvidencePath => "TestResults/SinglePlayer/M6-T1-InteractionPrompt";
        private HubInteractionController _hub;
        private NearbyPickupListView _view;

        [UnitySetUp] public IEnumerator IsolateWorld()
        {
            Backpack.Close(); Warehouse?.Close(); Player.enabled = false;
            foreach (var root in Scene.GetRootGameObjects())
                foreach (var proxy in root.GetComponentsInChildren<WorldItemPickup>(true)) proxy.gameObject.SetActive(false);
            _hub = Find<HubInteractionController>(); _view = Find<NearbyPickupListView>();
            Assert.That(_hub, Is.Not.Null); Assert.That(_view, Is.Not.Null);
            yield return null;
        }
        private void Move(Vector3 position)
        {
            Player.CharacterController.enabled = false; Player.transform.position = position;
            Player.CharacterController.enabled = true; Physics.SyncTransforms();
            // Nearby使用低频查询；移动测试角色后立即刷新，避免将上一位置的候选缓存误当本次状态。
            Find<NearbyPickupController>().RefreshCandidates();
        }
        private HubAreaVolume Area(string field) => (HubAreaVolume)new SerializedObject(_hub).FindProperty(field).objectReferenceValue;
        private bool HasVisibleText(string text)
        {
            foreach (var label in _view.GetComponentsInChildren<Text>(true))
                if (label.gameObject.activeInHierarchy && label.text.Contains(text)) return true;
            return false;
        }
        private void Spawn(string name, int sequence)
        {
            var definition = AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/SinglePlayer/Items/Food/" + name + ".asset");
            Assert.That(Session.TryRegisterWorldItem(definition, Session.WorldSceneKey, "prompt." + sequence,
                Player.transform.position + Vector3.right * (.6f + .15f * sequence), Quaternion.identity), Is.True);
            // 注册采用候选Profile后代理重建；仅保留本用例投放，不能依赖上一代组件引用。
            foreach (var root in Scene.GetRootGameObjects()) foreach (var proxy in root.GetComponentsInChildren<WorldItemPickup>(true))
            {
                bool current = false;
                foreach (var record in Session.Profile.HubWorldItems)
                    if (record.InstanceId == proxy.ItemId && record.SpawnId != null && record.SpawnId.StartsWith("prompt.")) { current = true; break; }
                if (!current) proxy.gameObject.SetActive(false);
            }
        }
        [UnityTest] public IEnumerator PickupPanelIsNearCharacterAndCameraIndependent()
        {
            Move(Vector3.zero); Spawn("Apple", 0); Find<NearbyPickupController>().RefreshCandidates(); yield return null;
            Assert.That(_view.Panel.gameObject.activeInHierarchy, Is.True);
            Canvas.ForceUpdateCanvases(); var screen = RectTransformUtility.WorldToScreenPoint(null, _view.Panel.position);
            Assert.That(screen.x / Screen.width, Is.InRange(.54f, .59f), "提示左缘应靠近画面中央角色右侧，不能继续位于70%宽度。");
            Assert.That(screen.y / Screen.height, Is.InRange(.40f, .49f), "提示应接近角色身侧，不能悬在画面上半部。");
            Camera.main.transform.Rotate(0, 90, 0); yield return null;
            Assert.That(Vector2.Distance(screen, RectTransformUtility.WorldToScreenPoint(null, _view.Panel.position)), Is.LessThan(.5f));
            yield return new WaitForEndOfFrame(); Capture("near-character-pickup.png");
        }
        [UnityTest] public IEnumerator ShopAndWarehouseShareFEntryAndOpenCorrectPanel()
        {
            Move(Area("_shopArea").Center); yield return null; yield return null;
            Assert.That(HasVisibleText("F 商店"), Is.True, "没有附近物品时，区域也必须显示F提示。");
            Assert.That(_view.VisibleCount, Is.Zero);
            yield return new WaitForEndOfFrame(); Capture("shop-region-entry.png");
            Keys(Key.F); yield return null; Keys(); yield return null;
            var shop = Find<ShopController>(); Assert.That(shop.IsOpen, Is.True);
            Assert.That(_view.Panel.gameObject.activeInHierarchy, Is.False, "商店暂停界面不可被高排序交互HUD覆盖。");
            shop.Close(); Move(Vector3.one * 1000); yield return null;
            Assert.That(_view.Panel.gameObject.activeInHierarchy, Is.False);
            Move(Area("_warehouseArea").Center); yield return null;
            Assert.That(HasVisibleText("F 仓库"), Is.True);
            yield return new WaitForEndOfFrame(); Capture("warehouse-region-entry.png");
            Keys(Key.F); yield return null; Keys(); yield return null;
            Assert.That(Warehouse.IsOpen, Is.True); Assert.That(Backpack.IsOpen, Is.False);
            Assert.That(_view.Panel.gameObject.activeInHierarchy, Is.False);
        }
        [UnityTest] public IEnumerator StationUsesFirstExistingRowAndFDoesNotAlsoPickNearbyItem()
        {
            Move(Area("_shopArea").Center); Spawn("Apple", 0); Spawn("Beetroot", 1);
            var nearby = Find<NearbyPickupController>(); nearby.RefreshCandidates(); yield return null;
            Assert.That(HasVisibleText("F 商店"), Is.True); Assert.That(_view.VisibleCount, Is.EqualTo(2));
            Canvas.ForceUpdateCanvases();
            var labels = new SerializedObject(_view).FindProperty("_labels");
            Text stationLabel = null;
            foreach (var label in _view.GetComponentsInChildren<Text>(true)) if (label.text == "F 商店") stationLabel = label;
            Assert.That(stationLabel, Is.Not.Null);
            var firstLabel = (Text)labels.GetArrayElementAtIndex(1).objectReferenceValue;
            Assert.That(RectTransformUtility.WorldToScreenPoint(null, stationLabel.rectTransform.position).y,
                Is.GreaterThan(RectTransformUtility.WorldToScreenPoint(null, firstLabel.rectTransform.position).y + 10), "站点与物品条目不能重叠。");
            InputSystem.QueueStateEvent(Mouse, new MouseState { scroll = new Vector2(0, -120) }); InputSystem.Update(); yield return null;
            Assert.That(nearby.SelectedIndex, Is.EqualTo(0)); string selected = nearby.FocusedPickup.ItemId;
            Assert.That(HasVisibleText("F 商店"), Is.True);
            yield return new WaitForEndOfFrame(); Capture("station-and-items.png");
            Keys(Key.F); yield return null; Keys(); yield return null;
            Assert.That(Session.ReadOnly.TryGetView(selected, out var item), Is.True); Assert.That(item.Location.Kind, Is.EqualTo(ItemLocationKind.WorldItem));
            Assert.That(Find<ShopController>().IsOpen, Is.True,"同一 F 只打开优先站点，不能同时拾取。");
        }
        [UnityTest] public IEnumerator DisabledRegionAndLegacyFloatingLabelsAreNotPresented()
        {
            Move(Area("_shopArea").Center); yield return null;
            Assert.That(HasVisibleText("F 商店"), Is.True);
            Area("_shopArea").enabled = false; yield return null;
            Assert.That(_view.Panel.gameObject.activeInHierarchy, Is.False, "停用区域不能退回旧点半径提示。");
            foreach (var root in Scene.GetRootGameObjects()) foreach (var label in root.GetComponentsInChildren<TextMesh>(true))
                if (label.text == "据点商店 [E]" || label.text == "仓库与魔法整备 [E]")
                    Assert.That(label.gameObject.activeInHierarchy && label.GetComponent<Renderer>().enabled, Is.False, "旧世界站点文字不能与区域提示并存。");
        }
        [UnityTest] public IEnumerator StableStationAndPickupListHaveZeroScopedGc()
        {
            Move(Area("_shopArea").Center); Spawn("Apple", 0); Spawn("Beetroot", 1);
            Find<NearbyPickupController>().RefreshCandidates(); for(int i=0;i<20;i++)yield return null;
            Assert.That(HasVisibleText("F 商店"), Is.True); Assert.That(_view.VisibleCount, Is.EqualTo(2));
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;
            bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            var memoryLimit=UnityEngine.Profiling.Profiler.maxUsedMemory;
            try
            {
                // 只采稳定35帧并限制采样缓冲，避免此前大批次Profiler历史膨胀；退出必恢复用户设置。
                UnityEngine.Profiling.Profiler.maxUsedMemory=256*1024*1024;
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);
                UnityEditorInternal.ProfilerDriver.enabled=true;
                int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                for(int i=0;i<35;i++)yield return null;
                WriteProfile("interaction-prompt-profiler",first,UnityEditorInternal.ProfilerDriver.lastFrameIndex,
                    new[]{"Norn.Items.PickupList","Norn.Hub.RegionQuery"},"Stable shared F station plus two items; warmup/text changes excluded; temporary 256 MiB profiler buffer");
            }
            finally
            {
                UnityEditorInternal.ProfilerDriver.enabled=recording;
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);
                UnityEngine.Profiling.Profiler.maxUsedMemory=memoryLimit;
            }
        }
    }
}
#endif
