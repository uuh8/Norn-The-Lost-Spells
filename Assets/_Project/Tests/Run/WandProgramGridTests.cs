using System.Collections.Generic;
using Game.Skills;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    public sealed class WandProgramGridTests
    {
        private readonly List<Object> _owned=new List<Object>();
        [TearDown] public void Cleanup(){foreach(var o in _owned)if(o!=null)Object.DestroyImmediate(o);_owned.Clear();}
        private SpellDefinition Card(SpellKind kind,bool trigger=false,float cost=0)
        {
            var c=ScriptableObject.CreateInstance<SpellDefinition>();_owned.Add(c);c.Kind=kind;c.ManaCost=cost;
            c.ExtraDraws=1;c.ModDamageAddFlat=6;c.BaseDamage=10;c.PayloadTrigger=trigger?PayloadTriggerMode.OnImpact:PayloadTriggerMode.None;return c;
        }
        private SpellPreviewRoot Root(SpellDefinition[] cards,float mana=100,int baseDraws=1)
        {
            Assert.That(SpellProgramCompiler.TryCompile(cards,new SpellProgramCompileInputs(baseDraws),out var p,out var d),Is.True,d.Message);
            var b=new SpellPreviewBuffer();Assert.That(SpellProgramPreview.TryPreview(p,WandDrawSnapshot.CreateInitial(p),
                new SpellExecutionInputs(mana),new SpellPreviewOptions(1),b),Is.True,b.Diagnostic.Message);return b.Roots[0];
        }
        [Test] public void SevenCards_AreGroupedByActualScopeAndKeepModifiersAndMulticast()
        {
            var m=Card(SpellKind.Multicast);var f=Card(SpellKind.Emit);
            var grid=new WandProgramGridProjection(Root(new[]{m,Card(SpellKind.Emit,true),m,f,Card(SpellKind.Modify),f,f}));
            Assert.That(grid.Rows.Count,Is.EqualTo(2));
            var root=new List<int>();foreach(var c in grid.Rows[0].Cards)root.Add(c.CardIndex);
            var child=new List<int>();foreach(var c in grid.Rows[1].Cards)child.Add(c.CardIndex);
            Assert.That(root,Is.EqualTo(new[]{0,1,6}));Assert.That(child,Is.EqualTo(new[]{2,3,4,5}));
            Assert.That(grid.Rows[1].OwnerOutputId,Is.EqualTo(0));
        }
        [Test] public void SkippedUnaffordableCard_IsVisibleButNeverBecomesAnOutput()
        {
            var grid=new WandProgramGridProjection(Root(new[]{Card(SpellKind.Emit,true,10),Card(SpellKind.Emit,false,5)},5));
            Assert.That(grid.Rows.Count,Is.EqualTo(1));Assert.That(grid.Rows[0].Cards.Count,Is.EqualTo(2));
            Assert.That(grid.Rows[0].Cards[0].Skipped,Is.True);Assert.That(grid.Rows[0].Cards[0].OutputId,Is.EqualTo(-1));
            Assert.That(grid.Rows[0].Cards[1].Skipped,Is.False);
        }
        [Test] public void LongRow_ExpandsCanvasAndPreservesEveryInstruction()
        {
            var cards=new SpellDefinition[129];cards[0]=Card(SpellKind.Multicast);cards[0].ExtraDraws=127;
            var f=Card(SpellKind.Emit);for(int i=1;i<cards.Length;i++)cards[i]=f;
            var grid=new WandProgramGridProjection(Root(cards));
            Assert.That(grid.Rows.Count,Is.EqualTo(1));Assert.That(grid.Rows[0].Cards.Count,Is.EqualTo(129));
            var last=grid.Rows[0].Cards[128];Assert.That(grid.ContentSize.x,Is.GreaterThanOrEqualTo(last.Position.x+WandProgramGridProjection.CardWidth));
            Assert.That(grid.ContentSize.x,Is.GreaterThan(12000));
        }
        [Test] public void SixteenNestedTriggers_ExpandVerticallyAndRemainLinkedToTheirOwner()
        {
            var cards=new SpellDefinition[17];for(int i=0;i<16;i++)cards[i]=Card(SpellKind.Emit,true);cards[16]=Card(SpellKind.Emit);
            var grid=new WandProgramGridProjection(Root(cards));Assert.That(grid.Rows.Count,Is.EqualTo(17));
            for(int i=1;i<grid.Rows.Count;i++)Assert.That(grid.Rows[i].ParentScopeId,Is.EqualTo(i-1));
            Assert.That(grid.ContentSize.y,Is.GreaterThan(2000));
        }
        private WandProgramCanvasViewport View(out ScrollRect scroll,out Slider slider)
        {
            var host=new GameObject("ViewportTest",typeof(RectTransform),typeof(ScrollRect));_owned.Add(host);
            var rt=(RectTransform)host.transform;rt.sizeDelta=new Vector2(800,480);scroll=host.GetComponent<ScrollRect>();scroll.viewport=rt;
            var content=new GameObject("Extent",typeof(RectTransform));content.transform.SetParent(rt,false);scroll.content=(RectTransform)content.transform;
            scroll.content.anchorMin=scroll.content.anchorMax=new Vector2(0,1);scroll.content.pivot=new Vector2(0,1);
            var drawing=new GameObject("Drawing",typeof(RectTransform));drawing.transform.SetParent(content.transform,false);
            var drawingRect=(RectTransform)drawing.transform;drawingRect.anchorMin=drawingRect.anchorMax=new Vector2(0,1);drawingRect.pivot=new Vector2(0,1);
            var knob=new GameObject("ZoomSlider",typeof(RectTransform),typeof(Slider));knob.transform.SetParent(rt,false);slider=knob.GetComponent<Slider>();
            var view=host.AddComponent<WandProgramCanvasViewport>();view.Initialize(scroll,drawingRect,slider);return view;
        }
        [Test] public void ZoomSlider_ChangesVisualScaleAndScrollExtentTogether()
        {
            var view=View(out var scroll,out var slider);view.SetLogicalSize(new Vector2(2400,1500));slider.value=.5f;
            Assert.That(view.Zoom,Is.EqualTo(.5f).Within(.0001));Assert.That(scroll.content.rect.width,Is.EqualTo(1200).Within(.01));
            Assert.That(scroll.content.rect.height,Is.EqualTo(750).Within(.01));
        }
        [Test] public void FitAll_MakesWholeLargeGraphVisibleUsingBothAxes()
        {
            var view=View(out var scroll,out _);view.SetLogicalSize(new Vector2(18000,6000));view.FitAll();
            Assert.That(view.Zoom,Is.LessThan(.1f));
            Assert.That(view.LogicalSize.x*view.Zoom,Is.LessThanOrEqualTo(800.01));
            Assert.That(view.LogicalSize.y*view.Zoom,Is.LessThanOrEqualTo(480.01));
            Assert.That(scroll.content.anchoredPosition,Is.EqualTo(Vector2.zero));
        }
        [Test] public void Zoom_PreservesTheVisibleCenterWhenThereIsRoomToPan()
        {
            var view=View(out var scroll,out _);view.SetLogicalSize(new Vector2(2400,1500));scroll.content.anchoredPosition=new Vector2(-300,180);
            var center=view.ViewCenterInDrawing;view.SetZoom(.8f);
            Assert.That(Vector2.Distance(center,new Vector2(700,-420)),Is.LessThan(.01));
            Assert.That(Vector2.Distance(center,view.ViewCenterInDrawing),Is.LessThan(.01));
        }
        [Test] public void ResizingViewport_RecalculatesFitAndKeepsTheFullGraph()
        {
            var view=View(out var scroll,out _);view.SetLogicalSize(new Vector2(12000,3000));view.FitAll();
            var original=view.LogicalSize;scroll.viewport.sizeDelta=new Vector2(420,700);view.FitAll();
            Assert.That(view.LogicalSize,Is.EqualTo(original));
            Assert.That(view.LogicalSize.x*view.Zoom,Is.LessThanOrEqualTo(420.01f));
            Assert.That(view.LogicalSize.y*view.Zoom,Is.LessThanOrEqualTo(700.01f));
            Assert.That(scroll.content.rect.width,Is.GreaterThanOrEqualTo(420));
        }
        [Test] public void ShrinkingContent_AfterFitDoesNotDesyncSliderAndVisualScale()
        {
            var view=View(out _,out var slider);view.SetLogicalSize(new Vector2(18000,6000));view.FitAll();
            float zoom=view.Zoom;view.SetLogicalSize(new Vector2(600,300));
            Assert.That(slider.value,Is.EqualTo(view.Zoom).Within(.00001),"折叠大图后，Slider不能显示另一种缩放倍率。");
            Assert.That(view.Zoom,Is.EqualTo(zoom).Within(.00001),"内容变化保持用户当前视野倍率。");
        }
    }
}
