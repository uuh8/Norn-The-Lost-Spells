#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    public sealed class NearbyDragPreviewPlayModeTests : BackpackWorkspacePlayModeTests
    {
        protected override string EvidencePath => "TestResults/SinglePlayer/M6-T18-Dungeon";

        [UnityTest]
        public IEnumerator NearbyDragUsesGreenCellsAndCancelRestoresAuthorColor()
        {
            Backpack.OpenBackpack(); yield return null;
            var nearby=Backpack.GetComponentInChildren<NearbyItemGridView>();
            Assert.That(nearby,Is.Not.Null);
            Assert.That(Session.ReadOnly.TryGetView("fire",out var item),Is.True);
            Vector2 pointer=RectTransformUtility.WorldToScreenPoint(null,nearby.transform.TransformPoint(
                new Vector3(nearby.CellSize*.5f,-nearby.CellSize*.5f,0)));
            var colors=new List<Color>();
            foreach(var image in nearby.GetComponentsInChildren<Image>())
                if(image.name=="Nearby grid cell")colors.Add(image.color);
            // 走实际Controller拖拽入口，不能仅验证一个新ShowPreview函数。
            Backpack.BeginDrag(item,pointer,null,true);
            Backpack.MoveGhost(pointer,null);yield return null;
            bool green=false;
            foreach(var image in nearby.GetComponentsInChildren<Image>())
                if(image.name=="Nearby grid cell" && image.color==new Color(.18f,.65f,.34f,1))green=true;
            Assert.That(green,Is.True,"附近目标和背包目标必须同样显示绿色占格轮廓。");
            Backpack.RefreshPresentation();Backpack.MoveGhost(pointer,null);yield return null;
            green=false;foreach(var image in nearby.GetComponentsInChildren<Image>())
                if(image.name=="Nearby grid cell"&&image.color==new Color(.18f,.65f,.34f,1))green=true;
            Assert.That(green,Is.True,"重排恢复底色后，静止指针也必须恢复绿色预览。");
            yield return new WaitForEndOfFrame();Directory.CreateDirectory(EvidencePath);
            ScreenCapture.CaptureScreenshot(EvidencePath+"/nearby-green.png");
            Backpack.CancelDrag();yield return null;
            foreach(var image in nearby.GetComponentsInChildren<Image>())
                if(image.name=="Nearby grid cell")Assert.That(image.color,Is.Not.EqualTo(new Color(.18f,.65f,.34f,1)));
            // 再走实际地面物品作为来源的整理路径，绿色不能只支持从背包丢到地面。
            Backpack.BeginDrag(item,pointer,null,true);Backpack.SubmitToTarget(InventoryDropKind.Ground);yield return null;
            Assert.That(Session.ReadOnly.TryGetView("fire",out item),Is.True);
            Assert.That(item.Location.Kind,Is.EqualTo(ItemLocationKind.WorldItem));
            pointer=RectTransformUtility.WorldToScreenPoint(null,nearby.transform.TransformPoint(new Vector3(nearby.CellSize*2.5f,-nearby.CellSize*1.5f,0)));
            Backpack.BeginDrag(item,pointer,null,true);Backpack.MoveGhost(pointer,null);yield return null;
            green=false;foreach(var image in nearby.GetComponentsInChildren<Image>())
                if(image.name=="Nearby grid cell"&&image.color==new Color(.18f,.65f,.34f,1))green=true;
            Assert.That(green,Is.True,"附近物品自身拖动必须有绿色轮廓。");
            yield return new WaitForEndOfFrame();Capture("nearby-world-source-green.png");Backpack.CancelDrag();
            Session.ReadOnly.TryGetView("ice",out var occupied);
            Backpack.BeginDrag(occupied,Vector2.zero,null,true);Backpack.SubmitToTarget(InventoryDropKind.Ground);yield return null;
            // 选择Viewport中部作为重叠目标，避免边缘AutoScroll在下一帧改变目标格。
            nearby.MoveItem("ice",occupied.Rules.Shape,new GridPlacement(2,1,0));Backpack.RefreshPresentation();
            InventoryItemView source=null;foreach(var view in nearby.GetComponentsInChildren<InventoryItemView>())
                if(view.Snapshot.InstanceId=="fire")source=view;
            Assert.That(source,Is.Not.Null);
            Backpack.BeginDrag(item,Vector2.zero,null,true,sourceRect:source.Rect,sourceCellSize:nearby.CellSize);
            Assert.That(nearby.TryPlacement("ice",out var placement),Is.True);
            pointer=RectTransformUtility.WorldToScreenPoint(null,nearby.transform.TransformPoint(new Vector3((placement.X+.5f)*nearby.CellSize,-(placement.Y+.5f)*nearby.CellSize,0)));
            Backpack.MoveGhost(pointer,null);yield return null;
            Assert.That(nearby.CanPreview("fire",item.Rules.Shape,placement),Is.False,"测试重叠目标应确实被ice占用。");
            var placementArgs=new object[]{default(Vector2Int),false};
            bool found=(bool)typeof(SinglePlayerInventoryController).GetMethod("TryGridPlacement",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(Backpack,placementArgs);
            Assert.That(found,Is.True);Assert.That(placementArgs[1],Is.EqualTo(true));
            Assert.That((Vector2Int)placementArgs[0],Is.EqualTo(new Vector2Int(placement.X,placement.Y)),"实际Ghost目标必须等于重叠目标。");
            bool red=false;foreach(var image in nearby.GetComponentsInChildren<Image>())
                if(image.name=="Nearby grid cell"&&image.color==new Color(.8f,.2f,.2f,1))red=true;
            Assert.That(red,Is.True,"另一件地面物品的占格应显示红色冲突预览。");Backpack.CancelDrag();
        }

        [UnityTest]
        public IEnumerator DroppedSpellsUseAuthoredRewardCubeAndIndependentIcon()
        {
            Session.ReadOnly.TryGetView("fire",out var item);
            Backpack.OpenBackpack();yield return null;
            Backpack.BeginDrag(item,Vector2.zero,null,true);Backpack.SubmitToTarget(InventoryDropKind.Ground);yield return null;
            var proxy=Session.FindWorldProxy("fire");Assert.That(proxy,Is.Not.Null);
            var cube=proxy.transform.Find("Visual/Visual/Cube");
            Assert.That(cube,Is.Not.Null,"地面法术应使用提供的Rewards/Spell视觉，而不是Fallback方块。");
            var renderer=cube.GetComponent<MeshRenderer>();var block=new MaterialPropertyBlock();renderer.GetPropertyBlock(block);
            Assert.That(block.GetTexture("_BaseMap"),Is.EqualTo(item.Icon.texture));
            Assert.That(proxy.GetComponentsInChildren<SpellRewardPickup>(true),Is.Empty,"视觉复制体不得再次授予关后奖励。");
            Assert.That(Session.ReadOnly.TryGetView("ice",out var ice),Is.True);
            Backpack.BeginDrag(ice,Vector2.zero,null,true);Backpack.SubmitToTarget(InventoryDropKind.Ground);yield return null;
            var iceCube=Session.FindWorldProxy("ice").transform.Find("Visual/Visual/Cube").GetComponent<MeshRenderer>();
            var iceBlock=new MaterialPropertyBlock();iceCube.GetPropertyBlock(iceBlock);
            Assert.That(iceBlock.GetTexture("_BaseMap"),Is.EqualTo(ice.Icon.texture));
            var rect=ice.Icon.textureRect;var tex=ice.Icon.texture;
            Assert.That(iceBlock.GetVector("_BaseMap_ST"),Is.EqualTo(new Vector4(rect.width/tex.width,rect.height/tex.height,rect.x/tex.width,rect.y/tex.height)));
            renderer.GetPropertyBlock(block);Assert.That(block.GetTexture("_BaseMap"),Is.EqualTo(item.Icon.texture),"另一个法术不能覆盖前一个实例的贴图。");
            Assert.That(iceCube.sharedMaterial,Is.SameAs(renderer.sharedMaterial));
        }

        [UnityTest]public IEnumerator StableNearbyDragPreviewHasNoGcAlloc()
        {
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;
            bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                Backpack.OpenBackpack();yield return null;Session.ReadOnly.TryGetView("fire",out var item);
                var grid=Backpack.GetComponentInChildren<NearbyItemGridView>();
                var pointer=RectTransformUtility.WorldToScreenPoint(null,grid.transform.TransformPoint(new Vector3(grid.CellSize*2.5f,-grid.CellSize*1.5f,0)));
                Backpack.BeginDrag(item,pointer,null,true);Backpack.MoveGhost(pointer,null);
                for(int i=0;i<10;i++)yield return null;int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                for(int i=0;i<60;i++){Backpack.MoveGhost(pointer,null);yield return null;}
                WriteProfile("nearby-profiler",first,UnityEditorInternal.ProfilerDriver.lastFrameIndex,
                    new[]{"Norn.Backpack.DragPreview","Norn.Backpack.DragFollow"},"Nearby grid fixed-pointer drag; initialization excluded");
                Backpack.CancelDrag();
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
        }
    }
}
#endif
