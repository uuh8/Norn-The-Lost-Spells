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
    public sealed class InventoryViewportPanPlayModeTests:BackpackWorkspacePlayModeTests
    {
        protected override string ReviewPath=>Review;
        protected override string EvidencePath=>"TestResults/SinglePlayer/M6-T1-WorldItemLifecycle";
        [UnityTest] public IEnumerator MiddleDragFromItemPansBackpackWhilePausedWithoutAssetChange()
        {
            Backpack.OpenBackpack();yield return null;Canvas.ForceUpdateCanvases();
            var grid=Backpack.GetComponentInChildren<InventoryGridView>();var scroll=grid.GetComponentInParent<ScrollRect>();
            scroll.normalizedPosition=new Vector2(0,1);Canvas.ForceUpdateCanvases();
            var item=grid.GetComponentInChildren<InventoryItemView>();Assert.That(item,Is.Not.Null);
            var pointer=(Vector2)RectTransformUtility.WorldToScreenPoint(null,item.Rect.TransformPoint(item.Rect.rect.center));
            uint version=Session.ReadOnly.Version;var before=scroll.normalizedPosition;
            InputSystem.QueueStateEvent(Mouse,new MouseState{position=pointer,buttons=4});InputSystem.Update();yield return null;
            InputSystem.QueueStateEvent(Mouse,new MouseState{position=pointer+new Vector2(-65,80),buttons=4});InputSystem.Update();yield return null;
            Assert.That(scroll.normalizedPosition.y,Is.LessThan(before.y),"中键从物品上按下也要拖动Viewport内容。");
            Assert.That(Backpack.IsDragging,Is.False);Assert.That(Session.ReadOnly.Version,Is.EqualTo(version));Assert.That(Time.timeScale,Is.Zero);
            InputSystem.QueueStateEvent(Mouse,new MouseState{position=pointer});InputSystem.Update();yield return null;
        }
        [UnityTest] public IEnumerator MiddleDragNearbyGridIsIndependentAndStopsOnRelease()
        {
            Backpack.OpenBackpack();yield return null;Canvas.ForceUpdateCanvases();
            var nearby=(NearbyItemGridView)new SerializedObject(Backpack).FindProperty("_nearbyGrid").objectReferenceValue;
            var scroll=nearby.GetComponentInParent<ScrollRect>();scroll.content.sizeDelta=new Vector2(900,900);Canvas.ForceUpdateCanvases();
            scroll.normalizedPosition=new Vector2(.5f,.5f);var bag=Backpack.GetComponentInChildren<InventoryGridView>().GetComponentInParent<ScrollRect>();var bagBefore=bag.normalizedPosition;
            var pointer=(Vector2)RectTransformUtility.WorldToScreenPoint(null,scroll.viewport.TransformPoint(scroll.viewport.rect.center));var before=scroll.normalizedPosition;
            InputSystem.QueueStateEvent(Mouse,new MouseState{position=pointer,buttons=4});InputSystem.Update();yield return null;
            InputSystem.QueueStateEvent(Mouse,new MouseState{position=pointer+new Vector2(40,-40),buttons=4});InputSystem.Update();yield return null;
            Assert.That(scroll.normalizedPosition,Is.Not.EqualTo(before));Assert.That(bag.normalizedPosition,Is.EqualTo(bagBefore));
            InputSystem.QueueStateEvent(Mouse,new MouseState{position=pointer});InputSystem.Update();yield return null;var released=scroll.normalizedPosition;
            InputSystem.QueueStateEvent(Mouse,new MouseState{position=pointer+Vector2.one*90});InputSystem.Update();yield return null;
            Assert.That(scroll.normalizedPosition,Is.EqualTo(released));
        }
        [UnityTest] public IEnumerator MiddlePanClampsOutsideViewportAndCannotStealLeftItemDrag()
        {
            Backpack.OpenBackpack();yield return null;Canvas.ForceUpdateCanvases();
            var grid=Backpack.GetComponentInChildren<InventoryGridView>();var scroll=grid.GetComponentInParent<ScrollRect>();
            var pointer=(Vector2)RectTransformUtility.WorldToScreenPoint(null,scroll.viewport.TransformPoint(scroll.viewport.rect.center));
            scroll.normalizedPosition=new Vector2(.5f,.5f);
            InputSystem.QueueStateEvent(Mouse,new MouseState{position=pointer,buttons=4});InputSystem.Update();yield return null;
            InputSystem.QueueStateEvent(Mouse,new MouseState{position=pointer+new Vector2(4000,4000),buttons=4});InputSystem.Update();yield return null;
            Assert.That(scroll.normalizedPosition.x,Is.EqualTo(0).Within(.00001f));Assert.That(scroll.normalizedPosition.y,Is.EqualTo(0).Within(.00001f));
            InputSystem.QueueStateEvent(Mouse,new MouseState{position=pointer});InputSystem.Update();yield return null;
            var pan=scroll.GetComponent<InventoryViewportPan>();Assert.That(pan.IsPanning,Is.False);
            Session.ReadOnly.TryGetView("b0",out var item);Backpack.BeginDrag(item,pointer,null,true);
            InputSystem.QueueStateEvent(Mouse,new MouseState{position=pointer,buttons=4});InputSystem.Update();yield return null;
            Assert.That(pan.IsPanning,Is.False);Assert.That(Backpack.IsDragging,Is.True);
            Backpack.CancelDrag();Backpack.Close();Assert.That(pan.IsPanning,Is.False);
        }
        [UnityTest] public IEnumerator StableMiddlePanHasZeroScopedGc()
        {
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                Backpack.OpenBackpack();yield return null;Canvas.ForceUpdateCanvases();
                var scroll=Backpack.GetComponentInChildren<InventoryGridView>().GetComponentInParent<ScrollRect>();scroll.normalizedPosition=new Vector2(.5f,.5f);
                var pointer=(Vector2)RectTransformUtility.WorldToScreenPoint(null,scroll.viewport.TransformPoint(scroll.viewport.rect.center));
                InputSystem.QueueStateEvent(Mouse,new MouseState{position=pointer,buttons=4});InputSystem.Update();yield return null;
                for(int i=0;i<10;i++)yield return null;int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                for(int i=0;i<60;i++){InputSystem.QueueStateEvent(Mouse,new MouseState{position=pointer+new Vector2(i%2==0?10:-10,i%2==0?10:-10),buttons=4});InputSystem.Update();yield return null;}
                WriteProfile("middle-pan-profiler",first,UnityEditorInternal.ProfilerDriver.lastFrameIndex,new[]{"Norn.Backpack.MiddlePan"},"Paused Input System middle drag; input-event test harness allocations outside marker excluded");
                yield return new WaitForEndOfFrame();Capture("middle-pan-backpack.png");
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
        }
    }
}
#endif
