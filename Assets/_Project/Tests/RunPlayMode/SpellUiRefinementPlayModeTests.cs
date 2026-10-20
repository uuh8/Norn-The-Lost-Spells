#if UNITY_EDITOR
using System.Collections;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    /// <summary>真实拖拽验证两个耗蓝口径，避免把未抽取的指令成本误当本次扣费。</summary>
    public sealed class SpellUiRefinementPlayModeTests : BackpackWorkspacePlayModeTests
    {
        protected override string EvidencePath => "TestResults/SinglePlayer/M6-T20-SpellUi";
        private static T Ref<T>(Object target,string field) where T:Object =>
            (T)new SerializedObject(target).FindProperty(field).objectReferenceValue;
        private void Drop(SinglePlayerInventoryController controller,string id,int slot)
        {
            Session.ReadOnly.TryGetView(id,out var item);controller.BeginDrag(item,Vector2.zero,null,true);
            controller.SubmitToTarget(InventoryDropKind.ProgramSlot,"w0",slot);
        }
        [UnityTest] public IEnumerator PreviewManaEditsUpdateWarehouseAndBackpackWithColoredAttributes()
        {
            var area=Ref<HubAreaVolume>(Find<HubInteractionController>(),"_warehouseArea");
            Player.CharacterController.enabled=false;Player.enabled=false;Player.transform.position=area.Center;Physics.SyncTransforms();
            Warehouse.OpenWarehouse();yield return null;Warehouse.InspectWandSlot(0);
            var view=Warehouse.GetComponentInChildren<WandInventoryView>();var stats=Ref<Text>(view,"_stats");
            Drop(Warehouse,"fire",0);yield return null;
            Session.ReadOnly.TryGetView("fire",out var placed);Assert.That(placed.Location.OwnerId,Is.EqualTo("w0"));
            Assert.That(stats.text,Does.Contain("本次预估蓝耗 3 · 槽内合计 3"));
            Drop(Warehouse,"trigger",1);yield return null;
            Assert.That(stats.text,Does.Contain("本次预估蓝耗 3 · 槽内合计 8"));
            Assert.That(stats.text,Does.Not.Contain("保护法术").And.Not.Contain("基础抽取"));
            Assert.That(stats.text.IndexOf("Mana"),Is.LessThan(stats.text.IndexOf("恢复")));
            Assert.That(stats.text.IndexOf("恢复"),Is.LessThan(stats.text.IndexOf("冷却")));
            Assert.That(stats.text,Does.Contain("#389CFF").And.Contain("#9BDFFF").And.Contain("#FFD65C"));
            Assert.That(stats.fontSize,Is.GreaterThanOrEqualTo(20));Assert.That(stats.supportRichText,Is.True);
            var title=Ref<Text>(view,"_programTitle");Assert.That(title.text,Does.Contain("法术槽").And.Not.Contain("程序槽"));
            Assert.That(title.fontSize,Is.GreaterThanOrEqualTo(22));
            yield return new WaitForEndOfFrame();Capture("warehouse-colored-mana.png");
            Warehouse.Close();Backpack.OpenBackpack();yield return null;
            var panels=Backpack.GetComponentsInChildren<WandProgramPanelView>();
            var header=Ref<Text>(panels[0],"_header");
            Assert.That(header.text,Does.Contain("本次预估蓝耗 3 · 槽内合计 8").And.Not.Contain("受保护法术"));
            Assert.That(header.text,Does.Contain("#389CFF").And.Contain("#9BDFFF").And.Contain("#FFD65C"));
            Session.ReadOnly.TryGetView("fire",out var fire);Backpack.BeginDrag(fire,Vector2.zero,null,true);
            Backpack.SubmitToTarget(InventoryDropKind.SpellStorage,Session.ReadOnly.ActorId,0);yield return null;
            Assert.That(header.text,Does.Contain("本次预估蓝耗 5 · 槽内合计 5"));
            yield return new WaitForEndOfFrame();Capture("backpack-colored-mana-bottom-count.png");
        }
    }
}
#endif
