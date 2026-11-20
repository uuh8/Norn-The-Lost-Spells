#if UNITY_EDITOR
using System.Collections;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    /// <summary>使用最新作者布局Review，内存档与合成输入验证真正的装备/模型/F路径。</summary>
    public sealed class UnifiedItemsPlayModeTests : BackpackWorkspacePlayModeTests
    {
        protected override string ReviewPath => "Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_UnifiedItemsReview.unity";
        // 每轮独立保留截图/Profiler证据，不能覆盖历史阶段结果；不改变Profile或测试断言。
        private readonly string _evidencePath = "TestResults/UiAudit/UnifiedItemsPlayModeTests/" + System.DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + System.Guid.NewGuid().ToString("N");
        protected override string EvidencePath => _evidencePath;
        private Image Frame(int index)
        {
            var data = new SerializedObject(Find<HeldItemHud>());
            var icon = data.FindProperty("_cells").GetArrayElementAtIndex(index).FindPropertyRelative("Icon").objectReferenceValue as Image;
            return icon.transform.Find("Frame").GetComponent<Image>();
        }
        private static InventoryItemView ActiveGridItem(SinglePlayerInventoryController controller, string id)
        {
            InventoryItemView result = null;
            foreach (var grid in controller.GetComponentsInChildren<InventoryGridView>())
                foreach (var view in grid.GetComponentsInChildren<InventoryItemView>())
                    if (view.Snapshot.InstanceId == id)
                    { Assert.That(result, Is.Null, "同一背包实例不能重复显示：" + id); result = view; }
            Assert.That(result, Is.Not.Null, "可见背包网格必须包含实际实例：" + id); return result;
        }
        private static PointerEventData LeftPointer(Component view)
        {
            var rect = (RectTransform)view.transform;
            return new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left,
                position = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center)) };
        }
        [UnityTest] public IEnumerator SelectedSlotSwapsAuthorFrameSpriteAndKeepsBackgroundColors()
        {
            var hud = Find<HeldItemHud>(); var data = new SerializedObject(hud);
            var selected = data.FindProperty("_selectedFrame").objectReferenceValue as Sprite;
            Assert.That(selected, Is.Not.Null); Assert.That(Frame(0).sprite, Is.EqualTo(selected));
            Image background = data.FindProperty("_cells").GetArrayElementAtIndex(3).FindPropertyRelative("Selection").objectReferenceValue as Image;
            Color before = background.color; Sprite original = Frame(3).sprite;
            Bind(0, "b0"); Keys(Key.Digit4); yield return null; Keys(); yield return null;
            Assert.That(Frame(3).sprite, Is.EqualTo(selected)); Assert.That(Frame(0).sprite, Is.Not.EqualTo(selected));
            Assert.That(background.color, Is.EqualTo(before));
            Keys(Key.Digit1); yield return null; Keys(); yield return null;
            Assert.That(Frame(3).sprite, Is.EqualTo(original));
        }
        [UnityTest] public IEnumerator EquipmentLeavesBagAndDraggingOutReturnsTheSameFilledBottle()
        {
            Backpack.OpenBackpack(); yield return null; Bind(0, "b0");
            Assert.That(Session.ReadOnly.TryGetView("b0", out var bottle), Is.True);
            Assert.That(bottle.Location.Kind, Is.EqualTo(ItemLocationKind.EquippedTool)); Assert.That(bottle.ContainerTotal, Is.EqualTo(100));
            Assert.That(bottle.ContainerCapacity, Is.EqualTo(15000));
            Backpack.BeginShortcutDrag("b0", Vector2.zero, null); Backpack.SubmitToBackpack(6, 6, "bag"); yield return null;
            Assert.That(Session.ReadOnly.TryGetView("b0", out bottle), Is.True); Assert.That(bottle.Location.Kind, Is.EqualTo(ItemLocationKind.Backpack));
            Assert.That(bottle.Location.X, Is.EqualTo(6)); Assert.That(bottle.Location.Y, Is.EqualTo(6)); Assert.That(bottle.ContainerTotal, Is.EqualTo(100));
            Assert.That(Session.ToolbeltReadOnly.TryGetBinding(0, out _), Is.False);
        }
        [UnityTest] public IEnumerator GLaunchesSelectedBottleModelWithStrongVelocityAndMatchingTrajectory()
        {
            MoveToExperiment(); Bind(0, "b0"); Keys(Key.Digit4); yield return null; Keys(); yield return null;
            var use = Find<ContainerUseController>(); var line = use.GetComponentInChildren<LineRenderer>(true);
            Assert.That(line, Is.Not.Null); Assert.That(line.enabled, Is.False, "未按G不能永久显示轨迹");
            Keys(Key.G); yield return null; yield return null;
            Assert.That(line.enabled, Is.True); Assert.That(line.positionCount, Is.GreaterThan(2));
            Assert.That(Find<ThrownContainerProjectile>(), Is.Null, "按住G仅瞄准，松开才投掷");
            Vector3 predictedOrigin = line.GetPosition(0);
            yield return new WaitForEndOfFrame(); Capture("bottle-trajectory.png");
            Keys(); yield return null; yield return new WaitForEndOfFrame();
            var projectile = Find<ThrownContainerProjectile>(); Assert.That(projectile, Is.Not.Null);
            var model = projectile.transform.Find("ThrownContainerModel"); Assert.That(model, Is.Not.Null);
            Assert.That(model.GetComponentInChildren<Renderer>().enabled, Is.True);
            Assert.That(projectile.GetComponent<Renderer>()?.enabled ?? false, Is.False, "旧蓝色球体Renderer必须关闭");
            Assert.That(projectile.GetComponent<Rigidbody>().linearVelocity.magnitude, Is.GreaterThan(22));
            Assert.That(Vector3.Distance(predictedOrigin, projectile.transform.position), Is.LessThan(3), "出手点不能与预览分离");
            Assert.That(Session.ToolbeltReadOnly.TryGetBinding(0, out _), Is.False);
            Assert.That(Session.ReadOnly.TryGetView("b0", out var item), Is.True); Assert.That(item.Location.Kind, Is.EqualTo(ItemLocationKind.ThrownContainer));
            yield return new WaitForSeconds(.15f); yield return new WaitForEndOfFrame(); Capture("real-bottle-flight.png");
        }
        [UnityTest] public IEnumerator HeldGAimTracksCameraAndPauseCancelsRelease()
        {
            MoveToExperiment(); Bind(0, "b0"); Keys(Key.Digit4); yield return null; Keys(); yield return null;
            var use = Find<ContainerUseController>(); var line = use.GetComponentInChildren<LineRenderer>(true);
            Keys(Key.G); yield return null; yield return null;
            Assert.That(line.enabled, Is.True); Vector3 before = line.GetPosition(1) - line.GetPosition(0);
            var cameraRoot = (Transform)new SerializedObject(Player).FindProperty("_cameraRoot").objectReferenceValue;
            cameraRoot.rotation = Quaternion.Euler(-20, 80, 0); yield return null; yield return null;
            Vector3 after = line.GetPosition(1) - line.GetPosition(0);
            Assert.That(Vector3.Angle(before, after), Is.GreaterThan(20), "镜头变化必须改变预览方向");
            Vector3 facing = Camera.main.transform.forward; facing.y = 0; facing.Normalize();
            Assert.That(Vector3.Dot(Player.transform.forward, facing), Is.GreaterThan(.95f), "瞄准时角色同步水平镜头朝向");
            Backpack.OpenBackpack(); yield return null; Keys(); yield return null; Backpack.Close(); yield return null;
            Assert.That(line.enabled, Is.False); Assert.That(Find<ThrownContainerProjectile>(), Is.Null);
            Assert.That(Session.ReadOnly.TryGetView("b0", out var bottle), Is.True); Assert.That(bottle.ContainerTotal, Is.EqualTo(100));
        }
        [UnityTest] public IEnumerator LeftPourShowsArcAndWritesAtItsEndpoint()
        {
            MoveToExperiment(); Bind(0, "b0"); Keys(Key.Digit4); yield return null; Keys(); yield return null;
            // Review的元素域围绕实验台；测试朝域内俯视，不能把镜头朝天空域外的拒绝当成撒料输入失败。
            AimIntoExperiment();
            // 保持足够内容覆盖截图帧，避免首个200GMU预算就倒空100GMU测试瓶。
            var store = Session.Profile.Items; store.TryGet("b0", out var bottle);
            bottle.Composition.AddReserved(new[] { new MaterialAmount(1, 6000), new MaterialAmount(3, 4000) }, 2);
            var line = Find<ContainerUseController>().GetComponentInChildren<LineRenderer>(true);
            Assert.That(Session.ReadOnly.TryGetView("b0", out var before), Is.True);
            ulong initial = before.ContainerTotal;
            int requests = 0; Vector3 target = default;
            void Observe(ContainerUseRequestedEvent e) { if (e.Kind == ContainerUseKind.Pour) { requests++; target = e.TargetPosition; } }
            EventBus<ContainerUseRequestedEvent>.Subscribe(Observe);
            try
            {
                Click(true); yield return null;
                Assert.That(line.enabled, Is.True, "长按左键撒液体时应显示轨迹");
                float end = Time.realtimeSinceStartup + 1;
                while (requests == 0 && Time.realtimeSinceStartup < end) yield return null;
                Assert.That(requests, Is.GreaterThan(0));
                var result = Find<ContainerUseController>().LastResult;
                Assert.That(result.Success, Is.True, "实际撒料失败=" + result.Transfer.Failure + "；目标=" + target + "；角色=" + Player.transform.position);
                Assert.That(Session.ReadOnly.TryGetView("b0", out var after), Is.True);
                Assert.That(after.ContainerTotal, Is.LessThan(initial));
                Assert.That(Vector3.Distance(target, line.GetPosition(line.positionCount - 1)), Is.LessThan(.05f), "实际材料写入必须与预览终点相同");
                yield return new WaitForEndOfFrame(); Capture("left-pour-trajectory.png");
                Click(false); yield return new WaitForEndOfFrame(); Assert.That(line.enabled, Is.False);
                Assert.That(Find<ThrownContainerProjectile>(), Is.Null, "洒液体不掷出瓶壳");
            }
            finally { Click(false); EventBus<ContainerUseRequestedEvent>.Unsubscribe(Observe); }
        }
        [UnityTest] public IEnumerator SharedPortalIsAboveGroundAndShowsFPrompt()
        {
            var portal = Find<HubPracticePortal>(); Assert.That(portal, Is.Not.Null);
            var visual = portal.transform.Find("UnifiedPortalVisual"); Assert.That(visual, Is.Not.Null);
            PortalTransitionCoordinator.EnsureVisual(portal.transform);
            Assert.That(portal.GetComponentsInChildren<PortalVisualPlacement>(true).Length, Is.EqualTo(1), "接线和Awake不能叠两扇门");
            Assert.That(portal.GetComponentsInChildren<TextMesh>(true), Is.Empty, "正式Review不保留旧浮字");
            Assert.That(visual.position.y - portal.transform.position.y, Is.GreaterThan(1), "视觉原点必须在地面上方");
            Player.enabled=false;Player.CharacterController.enabled=false;Player.transform.position=portal.transform.position;Player.CharacterController.enabled=true;Physics.SyncTransforms();
            yield return null;yield return null;
            Assert.That(PortalInteractionRegistry.TryGetNearest(Player.CharacterController,out var target), Is.True);
            Assert.That(target.InteractionLabel, Does.StartWith("F "));
            // 站点/传送门早已复用Entry池，不能读取已退役的_stationLabel特殊行。
            var list = Find<NearbyPickupListView>(); Assert.That(list, Is.Not.Null);
            var data = new SerializedObject(list);
            var rows = data.FindProperty("_rows"); var labels = data.FindProperty("_labels");
            Assert.That(rows.arraySize, Is.GreaterThan(0)); Assert.That(labels.arraySize, Is.EqualTo(rows.arraySize));
            var firstRow = rows.GetArrayElementAtIndex(0).objectReferenceValue as GameObject;
            var label = labels.GetArrayElementAtIndex(0).objectReferenceValue as Text;
            Assert.That(list.Panel.gameObject.activeInHierarchy, Is.True); Assert.That(firstRow.activeInHierarchy, Is.True);
            Assert.That(label.gameObject.activeInHierarchy, Is.True); Assert.That(label.text, Is.EqualTo(target.InteractionLabel));
            yield return new WaitForEndOfFrame(); Capture("portal-ground-and-f-prompt.png");
            // 打开Tab后统一列表必须隐藏，避免暂停期间仍覆盖背包；不能只验证数据字符串。
            Backpack.OpenBackpack(); yield return null; yield return null;
            Assert.That(Backpack.IsOpen, Is.True); Assert.That(list.Panel.gameObject.activeInHierarchy, Is.False);
            Backpack.Close(); yield return null; yield return null;
            Assert.That(label.gameObject.activeInHierarchy, Is.True); Assert.That(label.text, Is.EqualTo(target.InteractionLabel));
        }
        [UnityTest] public IEnumerator FOpensShopAndStationHasPriorityOverNearbyPickup()
        {
            var hub = Find<HubInteractionController>(); var area = new SerializedObject(hub).FindProperty("_shopArea").objectReferenceValue as HubAreaVolume;
            Assert.That(area, Is.Not.Null); Player.enabled = false; Player.CharacterController.enabled = false; Player.transform.position = area.Center; Player.CharacterController.enabled = true; Physics.SyncTransforms(); yield return null;
            var pumpkin = AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Food/Item_Food_Pumpkin.asset");
            Assert.That(Session.TryRegisterWorldItem(pumpkin, Session.WorldSceneKey, "f.priority", Player.transform.position + Vector3.forward, Quaternion.identity), Is.True);
            var nearby = Find<NearbyPickupController>(); nearby.RefreshCandidates(); int count = nearby.Count; Assert.That(count, Is.GreaterThan(0));
            var shop = Find<ShopController>(); shop.Close(); Keys(Key.E); yield return null; Keys(); yield return null; Assert.That(shop.IsOpen, Is.False);
            Keys(Key.F); yield return null; Keys(); yield return null;
            Assert.That(shop.IsOpen, Is.True); Assert.That(nearby.Count, Is.EqualTo(count), "站点F不能顺带拾取地面物品");
        }
        [UnityTest] public IEnumerator EquippedBottleCanSubmitCollectionWithoutABackpackParentLocation()
        {
            MoveToExperiment(); Bind(0, "b0"); Keys(Key.Digit4); yield return null; Keys(); yield return null;
            int requests = 0; string itemId = null;
            System.Action<MaterialTransferRequestedEvent> observe = request => { if (!request.SolidItem && request.InventoryId == Session.ReadOnly.InventoryId) { requests++; itemId = request.ItemId; } };
            EventBus<MaterialTransferRequestedEvent>.Subscribe(observe);
            try { Find<MaterialCollectionController>().SubmitSample(Player.transform.position, false, 10); Assert.That(requests, Is.EqualTo(1)); Assert.That(itemId, Is.EqualTo("b0")); }
            finally { EventBus<MaterialTransferRequestedEvent>.Unsubscribe(observe); }
        }
        [UnityTest] public IEnumerator SelectedBottleRemainsPurchaseTargetAfterEquipping()
        {
            var hub = Find<HubInteractionController>();
            var area = new SerializedObject(hub).FindProperty("_shopArea").objectReferenceValue as HubAreaVolume;
            Player.enabled = false; Player.CharacterController.enabled = false; Player.transform.position = area.Center; Player.CharacterController.enabled = true; Physics.SyncTransforms(); yield return null;
            var shop = Find<ShopController>(); shop.Open(); yield return null;
            var embedded = new SerializedObject(shop).FindProperty("_embeddedInventory").objectReferenceValue as SinglePlayerInventoryController;
            Assert.That(embedded, Is.Not.Null); Assert.That(shop.IsOpen, Is.True);
            // 作者商店已移除道具行。使用真实中列选瓶，再到Tab现有装备槽装备同一实例；
            // 重开后购买仍锁定该实例，既覆盖EquippedTool购买资格，也不为测试重建已删除的UI。
            Canvas.ForceUpdateCanvases(); var card = ActiveGridItem(embedded, "b0");
            uint version = Session.ReadOnly.Version; long initialMoney = Session.Currency;
            var pointer = LeftPointer(card); card.OnPointerDown(pointer); card.OnPointerClick(pointer);
            Assert.That(shop.SelectedItemId, Is.EqualTo("b0"));
            Assert.That(Session.ReadOnly.Version, Is.EqualTo(version), "只选中实例不能移动或修改资产");
            shop.Close(); Backpack.OpenBackpack(); yield return null; Canvas.ForceUpdateCanvases();
            var tools = Backpack.GetComponentInChildren<ToolbeltView>(); Assert.That(tools, Is.Not.Null);
            ToolbeltSlotView cell = null;
            foreach (var candidate in tools.GetComponentsInChildren<ToolbeltSlotView>()) if (candidate.ToolSlot == 0) cell = candidate;
            Assert.That(cell, Is.Not.Null);
            card = ActiveGridItem(Backpack, "b0"); pointer = LeftPointer(card);
            card.OnPointerDown(pointer); card.OnBeginDrag(pointer); cell.OnDrop(LeftPointer(cell)); card.OnEndDrag(LeftPointer(cell));
            yield return null;
            Assert.That(Session.ToolbeltReadOnly.TryGetBinding(0, out var bound), Is.True); Assert.That(bound, Is.EqualTo("b0"));
            Assert.That(Session.ReadOnly.TryGetView("b0", out var bottle), Is.True);
            Assert.That(bottle.Location.Kind, Is.EqualTo(ItemLocationKind.EquippedTool)); Assert.That(bottle.ContainerTotal, Is.EqualTo(100));
            Backpack.Close(); shop.Open(); yield return null;
            Assert.That(shop.IsOpen, Is.True); Assert.That(shop.SelectedItemId, Is.EqualTo("b0"));
            Assert.That(Session.Currency, Is.EqualTo(initialMoney), "选择、装备与重开商店都不能提前扣款");
            var offers = Find<SinglePlayerShopSession>().ReadOnly; ShopOfferSnapshot offer = default;
            int wandCards = 0;
            var offersRoot = (RectTransform)new SerializedObject(shop).FindProperty("_offersRoot").objectReferenceValue;
            for (int i = 0; i < offers.Count; i++) if (offers.GetOffer(i).Kind == ItemKind.Wand)
            {
                var wand = offers.GetOffer(i); int shown = 0;
                foreach (var row in offersRoot.GetComponentsInChildren<ShopEntryView>()) if (row.OfferId == wand.OfferId)
                { var art = row.transform.Find("OfferIcon")?.GetComponent<Image>(); Assert.That(art, Is.Not.Null); Assert.That(art.enabled, Is.True); Assert.That(art.sprite, Is.EqualTo(wand.Icon)); shown++; }
                Assert.That(shown, Is.EqualTo(1), "每个当前法杖报价必须恰好显示一张匹配图标的卡"); wandCards++;
            }
            // 当前目录还含V2法杖；逐项验证真实报价，不把旧目录的三件写成UI容量限制。
            Assert.That(wandCards, Is.GreaterThan(0));
            for (int i = 0; i < offers.Count; i++) if (offers.GetOffer(i).MaterialKey == 1) { offer = offers.GetOffer(i); break; }
            Assert.That(offer.OfferId, Is.Not.Null); shop.SelectOffer(offer.OfferId); shop.Buy(); yield return null;
            var dialog = new SerializedObject(shop).FindProperty("_purchaseDialog").objectReferenceValue as ShopPurchaseDialog;
            Assert.That(dialog.IsOpen, Is.True); Assert.That(dialog.ContainerId, Is.EqualTo("b0"));
            Assert.That(Session.Currency, Is.EqualTo(initialMoney), "购买确认之前不能扣款");
            long money = Session.Currency; dialog.SetQuantity(10);
            var confirm = new SerializedObject(dialog).FindProperty("_confirm").objectReferenceValue as Button; confirm.onClick.Invoke(); yield return null;
            Assert.That(Session.ReadOnly.TryGetView("b0", out bottle), Is.True); Assert.That(bottle.ContainerTotal, Is.EqualTo(110));
            Assert.That(bottle.Location.Kind, Is.EqualTo(ItemLocationKind.EquippedTool)); Assert.That(Session.Currency, Is.EqualTo(money - offer.BuyPrice * 10));
            Assert.That(Session.ToolbeltReadOnly.TryGetBinding(0, out bound), Is.True); Assert.That(bound, Is.EqualTo("b0"));
            Assert.That(Session.ReadOnly.TryGetView("b1", out var otherBottle), Is.True);
            Assert.That(otherBottle.ContainerTotal, Is.EqualTo(100), "未选中的瓶子不能收到材料");
            Assert.That(otherBottle.Location.Kind, Is.EqualTo(ItemLocationKind.Backpack));
            yield return new WaitForEndOfFrame(); Capture("shop-equipped-bottle-purchase.png");
        }
        [UnityTest] public IEnumerator ThrownModelFlightHasNoScopedGcAfterWarmup()
        {
            MoveToExperiment(); Bind(0,"b0"); Keys(Key.Digit4); yield return null; Keys(); yield return null;
            Keys(Key.G); yield return null; Keys(); for(int i=0;i<5;i++) yield return null;
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;for(int i=0;i<30;i++)yield return null;
                int frames=0,samples=0;long bytes=0;
                for(int frame=first;frame<=UnityEditorInternal.ProfilerDriver.lastFrameIndex;frame++)
                {
                    using var data=UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(frame,0);if(!data.valid)continue;frames++;
                    for(int sample=0;sample<data.sampleCount;sample++)if(data.GetSampleName(sample)=="Norn.ContainerThrow.Flight")
                    {samples++;int stop=sample+data.GetSampleChildrenCountRecursive(sample);for(int child=sample+1;child<=stop;child++)if(data.GetSampleName(child)=="GC.Alloc"&&data.GetSampleMetadataCount(child)>0)bytes+=data.GetSampleMetadataAsLong(child,0);}
                }
                System.IO.Directory.CreateDirectory(EvidencePath);System.IO.File.WriteAllText(EvidencePath+"/flight-profiler.txt","Frames="+frames+"\nSamples="+samples+"\nGCBytes="+bytes);
                Assert.That(frames,Is.GreaterThanOrEqualTo(20));Assert.That(samples,Is.GreaterThan(0));Assert.That(bytes,Is.Zero);
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
        }
        [UnityTest] public IEnumerator VisibleTrajectoryHasNoScopedGcAfterWarmup()
        {
            MoveToExperiment(); Bind(0, "b0"); Keys(Key.Digit4); yield return null; Keys(); for (int i = 0; i < 5; i++) yield return null;
            Keys(Key.G); yield return null; Assert.That(Find<ContainerUseController>().GetComponentInChildren<LineRenderer>(true).enabled, Is.True);
            bool recording = UnityEditorInternal.ProfilerDriver.enabled; bool cpu = UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU, true); UnityEditorInternal.ProfilerDriver.enabled = true;
                int first = UnityEditorInternal.ProfilerDriver.lastFrameIndex + 1; for (int i = 0; i < 60; i++) yield return null;
                int frames = 0, samples = 0, portalSamples = 0; long bytes = 0, portalBytes = 0;
                for (int frame = first; frame <= UnityEditorInternal.ProfilerDriver.lastFrameIndex; frame++)
                {
                    using var data = UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(frame, 0); if (!data.valid) continue; frames++;
                    for (int sample = 0; sample < data.sampleCount; sample++)
                    {
                        bool trajectory = data.GetSampleName(sample) == "Norn.ContainerThrow.Trajectory";
                        bool portal = data.GetSampleName(sample) == "Norn.Portal.InteractionQuery";
                        if (!trajectory && !portal) continue;
                        if (trajectory) samples++; else portalSamples++;
                        int stop = sample + data.GetSampleChildrenCountRecursive(sample);
                        for (int child = sample + 1; child <= stop; child++) if (data.GetSampleName(child) == "GC.Alloc" && data.GetSampleMetadataCount(child) > 0)
                        { if (trajectory) bytes += data.GetSampleMetadataAsLong(child, 0); else portalBytes += data.GetSampleMetadataAsLong(child, 0); }
                    }
                }
                System.IO.Directory.CreateDirectory(EvidencePath); System.IO.File.WriteAllText(EvidencePath + "/trajectory-profiler.txt", "Frames=" + frames + "\nSamples=" + samples + "\nGCBytes=" + bytes);
                System.IO.File.WriteAllText(EvidencePath + "/portal-query-profiler.txt", "Frames=" + frames + "\nSamples=" + portalSamples + "\nGCBytes=" + portalBytes);
                Assert.That(frames, Is.GreaterThanOrEqualTo(30)); Assert.That(samples, Is.GreaterThan(0)); Assert.That(bytes, Is.Zero);
                Assert.That(portalSamples, Is.GreaterThan(0)); Assert.That(portalBytes, Is.Zero);
            }
            finally { UnityEditorInternal.ProfilerDriver.enabled = recording; UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU, cpu); }
        }
    }
}
#endif
