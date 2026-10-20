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
        protected override string EvidencePath => "TestResults/SinglePlayer/M6-T1-SpellStorage";

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
            EventBus<RaidDepartureRequestedEvent>.Publish(new RaidDepartureRequestedEvent{InventoryId=Session.ReadOnly.InventoryId,Command=RaidCommand.Protect,ItemId="fire"});yield return null;
            var panel=Backpack.GetComponentsInChildren<WandProgramPanelView>(true)[0];
            var header=(Text)new UnityEditor.SerializedObject(panel).FindProperty("_header").objectReferenceValue;
            Assert.That(header.text,Does.Contain("基础抽取"));Assert.That(header.text,Does.Contain("受保护法术 1"));
            EventBus<RaidDepartureRequestedEvent>.Publish(new RaidDepartureRequestedEvent{InventoryId=Session.ReadOnly.InventoryId,Command=RaidCommand.Unprotect,ItemId="fire"});yield return null;
            Assert.That(header.text,Does.Contain("受保护法术 0"));
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
            Assert.That(((Text)cells.GetArrayElementAtIndex(9).FindPropertyRelative("Title").objectReferenceValue).text, Does.StartWith("0 "));
            Keys(Key.Digit1); yield return null; Keys(); yield return null;
            Assert.That(Find<HeldItemInputController>().IsWandMode, Is.True);
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
