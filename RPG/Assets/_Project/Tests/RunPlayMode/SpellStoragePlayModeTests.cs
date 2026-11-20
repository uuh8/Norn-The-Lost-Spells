#if UNITY_EDITOR
using System.Collections;
using Game.Character;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    public sealed class SpellStoragePlayModeTests : BackpackWorkspacePlayModeTests
    {
        protected override string ReviewPath => "Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_SpellStorageReview.unity";
        // 每轮独立保留截图/Profiler证据，不能覆盖历史阶段结果；不改变Profile或测试断言。
        private readonly string _evidencePath = "TestResults/UiAudit/SpellStoragePlayModeTests/" + System.DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + System.Guid.NewGuid().ToString("N");
        protected override string EvidencePath => _evidencePath;

        [UnityTest] public IEnumerator CapturePopulatedTrayAndSevenToolLayout()
        {
            MoveToExperiment(); Backpack.OpenBackpack(); yield return null;
            Session.Profile.Items.TryGet("fire", out var original);
            for (int i=0;i<12;i++) Assert.That(Session.Profile.Items.TryAdd(new RuntimeItemState("capture"+i, original.ContentId, i+1),
                new ItemLocation(ItemLocationKind.SpellStorage,Session.ReadOnly.ActorId,0,0,0,i)),Is.True);
            Session.ReadOnly.TryGetView("fire",out var fire);Backpack.BeginDrag(fire,Vector2.zero,null,true);Backpack.SubmitToTarget(InventoryDropKind.ProgramSlot,"w0",0);
            Session.ReadOnly.TryGetView("ice",out var ice);Backpack.BeginDrag(ice,Vector2.zero,null,true);Backpack.SubmitToTarget(InventoryDropKind.ProgramSlot,"w1",0);
            Bind(0,"b0");Bind(6,"b1");Backpack.RefreshPresentation();yield return null;Canvas.ForceUpdateCanvases();
            Assert.That(Backpack.GetComponentsInChildren<ToolbeltSlotView>(true).Length,Is.EqualTo(7));
            yield return new WaitForEndOfFrame(); Capture("backpack-populated.png");
            Backpack.Close(); Keys(Key.Digit0);yield return null;Keys();yield return null;
            yield return new WaitForEndOfFrame(); Capture("hud-seven-tools.png");
        }

        [UnityTest] public IEnumerator ProtectionChangesRefreshOnlySpecificSpellMarkers()
        {
            Backpack.OpenBackpack();yield return null;
            Session.ReadOnly.TryGetView("fire",out var fire);Backpack.BeginDrag(fire,Vector2.zero,null,true);Backpack.SubmitToTarget(InventoryDropKind.ProgramSlot,"w0",0);yield return null;
            var coordinator=Session.RunCoordinator;Assert.That(coordinator,Is.Not.Null);
            Assert.That(Session.ReadOnly.TryGetView("fire",out fire),Is.True);
            Assert.That(fire.Location.Kind,Is.EqualTo(ItemLocationKind.WandSlot));Assert.That(fire.Location.OwnerId,Is.EqualTo("w0"));
            var originalLocation=fire.Location;int originalCharges=fire.RemainingCharges;uint originalVersion=Session.ReadOnly.Version;
            Assert.That(coordinator.Protection.Count,Is.Zero);
            EventBus<RaidDepartureRequestedEvent>.Publish(new RaidDepartureRequestedEvent{InventoryId=Session.ReadOnly.InventoryId,Command=RaidCommand.Protect,ItemId="fire"});yield return null;
            var panel=Backpack.GetComponentsInChildren<WandProgramPanelView>(true)[0];
            var header=(Text)new UnityEditor.SerializedObject(panel).FindProperty("_header").objectReferenceValue;
            // M6-T20已取消法杖标题中的保护数量。保护仍属于具体实例，必须验证真实Owner状态，
            // 不能把退休文案删掉后只剩“没有异常”，也不能把整根法杖或另一张卡误当成受保护。
            Assert.That(coordinator.Protection.Count,Is.EqualTo(1));Assert.That(coordinator.Protection.Contains("fire"),Is.True);
            Assert.That(coordinator.Protection.Contains("ice"),Is.False);Assert.That(coordinator.Protection.Contains("w0"),Is.False);
            Assert.That(header.text,Does.Contain("基础抽取").And.Contain("本次预估蓝耗").And.Contain("槽内合计"));
            Assert.That(header.text,Does.Not.Contain("保护"));
            Assert.That(Session.ReadOnly.TryGetView("fire",out fire),Is.True);
            Assert.That(fire.Location,Is.EqualTo(originalLocation));Assert.That(fire.RemainingCharges,Is.EqualTo(originalCharges));
            Assert.That(Session.ReadOnly.Version,Is.EqualTo(originalVersion),"保护选择不能移动或消耗具体卡。");
            EventBus<RaidDepartureRequestedEvent>.Publish(new RaidDepartureRequestedEvent{InventoryId=Session.ReadOnly.InventoryId,Command=RaidCommand.Unprotect,ItemId="fire"});yield return null;
            Assert.That(coordinator.Protection.Count,Is.Zero);Assert.That(coordinator.Protection.Contains("fire"),Is.False);
            Assert.That(coordinator.Protection.Contains("ice"),Is.False);Assert.That(coordinator.Protection.Contains("w0"),Is.False);
            Assert.That(header.text,Does.Not.Contain("保护"));
            Assert.That(Session.ReadOnly.TryGetView("fire",out fire),Is.True);
            Assert.That(fire.Location,Is.EqualTo(originalLocation));Assert.That(fire.RemainingCharges,Is.EqualTo(originalCharges));
            Assert.That(Session.ReadOnly.Version,Is.EqualTo(originalVersion));
        }

        [UnityTest] public IEnumerator YellowRegionAcceptsRealSpellAndReturnsToProgramAndBag()
        {
            Backpack.OpenBackpack(); yield return null;
            var view = Find<SpellStorageView>(); Assert.That(view, Is.Not.Null);
            Assert.That(view.VisibleSlotCount, Is.EqualTo(12));
            Session.ReadOnly.TryGetView("fire", out var fire);
            Backpack.BeginDrag(fire, Vector2.zero, null, true);
            Backpack.SubmitToTarget(InventoryDropKind.SpellStorage, Session.ReadOnly.ActorId, 11); yield return null;
            Session.ReadOnly.TryGetView("fire", out fire);
            Assert.That(fire.Location.Kind, Is.EqualTo(ItemLocationKind.SpellStorage)); Assert.That(fire.RemainingCharges, Is.EqualTo(8));
            Backpack.BeginDrag(fire, Vector2.zero, null, true); Backpack.SubmitToTarget(InventoryDropKind.ProgramSlot, "w1", 0); yield return null;
            Session.ReadOnly.TryGetView("fire", out fire); Assert.That(fire.Location.OwnerId, Is.EqualTo("w1"));
            Backpack.BeginDrag(fire, Vector2.zero, null, true); Backpack.SubmitToBackpack(0, 4, "bag"); yield return null;
            Session.ReadOnly.TryGetView("fire", out fire); Assert.That(fire.Location.Kind, Is.EqualTo(ItemLocationKind.Backpack));
            Assert.That(fire.RemainingCharges, Is.EqualTo(8));
        }

        [UnityTest] public IEnumerator ZeroKeySelectsSeventhToolAndHudUsesZeroLabel()
        {
            Bind(6, "b0"); Keys(Key.Digit0); yield return null; Keys(); yield return null;
            Assert.That(Session.ToolbeltReadOnly.SelectedSlot, Is.EqualTo(9));
            Assert.That(Session.ToolbeltReadOnly.SelectedItemId, Is.EqualTo("b0"));
            var hud = Find<HeldItemHud>();
            var cells = new UnityEditor.SerializedObject(hud).FindProperty("_cells");
            Assert.That(cells.arraySize, Is.EqualTo(10));
            var last=cells.GetArrayElementAtIndex(9);
            var title=(Text)last.FindPropertyRelative("Title").objectReferenceValue;
            var icon=(Image)last.FindPropertyRelative("Icon").objectReferenceValue;
            // M6-T17道具栏只保留快捷键数字和图标。名称不再拼接在Title后面，精确验证"0"，
            // 同时验证绑定内容的图标，不能让标签正确但图标缺失/错误的HUD通过。
            Assert.That(title,Is.Not.Null);Assert.That(title.text,Is.EqualTo("0"));
            Assert.That(Session.ReadOnly.TryGetView("b0",out var bottle),Is.True);Assert.That(bottle.Icon,Is.Not.Null);
            Assert.That(icon,Is.Not.Null);Assert.That(icon.enabled && icon.gameObject.activeInHierarchy,Is.True);
            Assert.That(icon.sprite,Is.SameAs(bottle.Icon));
            Keys(Key.Digit1); yield return null; Keys(); yield return null;
            Assert.That(Find<HeldItemInputController>().IsWandMode, Is.True);
            Assert.That(Session.ToolbeltReadOnly.SelectedSlot,Is.Zero);Assert.That(Session.ToolbeltReadOnly.SelectedItemId,Is.EqualTo("w0"));
            Assert.That(title.text,Is.EqualTo("0"));Assert.That(icon.sprite,Is.SameAs(bottle.Icon),"切换手持不能清掉第七道具格的绑定图标。");
        }

        [UnityTest] public IEnumerator NearbySpellUsesTrayThenBagAndBothFullLeavesWorldItem()
        {
            var nearby = Find<NearbyPickupController>(); Assert.That(nearby, Is.Not.Null);
            var store = Session.Profile.Items;
            Assert.That(store.TryGet("fire", out var original), Is.True);
            Assert.That(store.TryMove("fire", new ItemLocation(ItemLocationKind.WorldItem, "world.test", 0, 0, 0, -1)).Success, Is.True);
            var go = new GameObject("TestPickup"); UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, Scene);
            go.transform.position = Player.transform.position;
            var pickup = go.AddComponent<WorldItemPickup>();
            Assert.That(pickup.TryBind(store, "fire", "world.test", Session.ReadOnly.InventoryId), Is.True);
            pickup.ConfigureReach(Player.transform, 3);
            Assert.That(nearby.TryCollect("fire"), Is.True);
            Session.ReadOnly.TryGetView("fire", out var collected);
            Assert.That(collected.Location.Kind, Is.EqualTo(ItemLocationKind.SpellStorage));
            for (int i = 1; i < 12; i++)
                Assert.That(store.TryAdd(new RuntimeItemState("tray" + i, original.ContentId, 8),
                    new ItemLocation(ItemLocationKind.SpellStorage, Session.ReadOnly.ActorId, 0, 0, 0, i)), Is.True);
            yield return null;
            var next = new GameObject("OverflowPickup"); UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(next, Scene);
            next.transform.position = Player.transform.position;
            Assert.That(store.TryAdd(new RuntimeItemState("overflow", original.ContentId, 8),
                new ItemLocation(ItemLocationKind.WorldItem, "overflow.world", 0, 0, 0, -1)), Is.True);
            var overflow = next.AddComponent<WorldItemPickup>(); Assert.That(overflow.TryBind(store, "overflow", "overflow.world", Session.ReadOnly.InventoryId), Is.True);
            overflow.ConfigureReach(Player.transform, 3);
            Assert.That(nearby.TryCollect("overflow"), Is.True);
            Session.ReadOnly.TryGetView("overflow", out collected); Assert.That(collected.Location.Kind, Is.EqualTo(ItemLocationKind.Backpack));
            store.Catalog.TryGetRules(original.ContentId, out var rules);
            // 填满所有真正空格；已有大瓶形状的重叠会被Store拒绝，不能伪造“已满”标记。
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
                store.TryAdd(new RuntimeItemState("fill" + x + "." + y, original.ContentId, 8), new ItemLocation(ItemLocationKind.Backpack, "bag", x, y, 0, -1));
            var last = new GameObject("FullPickup"); UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(last, Scene); last.transform.position = Player.transform.position;
            Assert.That(store.TryAdd(new RuntimeItemState("full", original.ContentId, 8), new ItemLocation(ItemLocationKind.WorldItem, "full.world", 0, 0, 0, -1)), Is.True);
            var full = last.AddComponent<WorldItemPickup>(); Assert.That(full.TryBind(store, "full", "full.world", Session.ReadOnly.InventoryId), Is.True);
            full.ConfigureReach(Player.transform, 3); uint version = store.Version;
            Assert.That(nearby.TryCollect("full"), Is.False); Assert.That(store.Version, Is.EqualTo(version));
            Assert.That(Backpack.IsOpen, Is.True); Assert.That(full.gameObject.activeSelf, Is.True);
        }
    }
}
#endif
