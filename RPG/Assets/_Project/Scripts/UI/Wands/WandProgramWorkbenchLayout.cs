using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>独立、自适应的格子工作台；游戏规则来自Presenter，布局只管理UGUI可视区域。</summary>
    internal static class WandProgramWorkbenchLayout
    {
        internal static void Build(WandProgramWorkbenchPresenter p,Font font)
        {
            var canvasRect=WandWorkbenchWidgets.Rect("SpellProgrammingV2Canvas",p.transform);
            var canvas=canvasRect.gameObject.AddComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.sortingOrder=100;
            var scaler=canvasRect.gameObject.AddComponent<CanvasScaler>();scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution=new Vector2(1920,1080);scaler.matchWidthOrHeight=.5f;
            canvasRect.gameObject.AddComponent<GraphicRaycaster>();
            var root=WandWorkbenchWidgets.Panel("Workbench",canvasRect,WandWorkbenchWidgets.Paper);
            Stretch(root,Vector2.zero,Vector2.one,Vector2.zero,Vector2.zero);
            var title=WandWorkbenchWidgets.Label("Title",root,font,34);title.text="法术研究台";title.color=WandWorkbenchWidgets.Gold;
            WandWorkbenchWidgets.Place(title.rectTransform,30,20,380,48);
            var identity=WandWorkbenchWidgets.Label("PreviewIdentity",root,font,18);identity.text="规则推演";
            WandWorkbenchWidgets.Place(identity.rectTransform,32,72,400,28);
            string[] names={"法杖一","法杖二","法杖三"};
            var tabs=new System.Collections.Generic.List<Image>();
            for(int i=0;i<p.Samples.Count;i++)
            {
                int index=i;var tab=WandWorkbenchWidgets.Button("Sample"+i,root,font,i<3?names[i]:"法杖"+(i+1),()=>p.SelectSample(index));
                WandWorkbenchWidgets.Place((RectTransform)tab.transform,470+i*(p.IsOwned?176:254),28,p.IsOwned?164:238,52);
                tabs.Add(tab.GetComponent<Image>());
            }
            p.Changed+=()=>
            {
                for(int i=0;i<tabs.Count;i++)tabs[i].color=p.SampleIndex==i?new Color(1,.76f,.34f):new Color(.30f,.31f,.30f);
            };
            if (p.IsOwned)
            {
                var close=WandWorkbenchWidgets.Button("CloseResearch",root,font,"×",p.CloseWorkbench);
                PlaceRight((RectTransform)close.transform,30,70);
                var ghost=WandSpellCellView.Create(root,font,null);ghost.name="DragGhost";
                ghost.gameObject.AddComponent<CanvasGroup>().blocksRaycasts=false;
                ghost.Rect.anchorMin=ghost.Rect.anchorMax=Vector2.one*.5f;ghost.Rect.pivot=Vector2.one*.5f;
                ghost.Rect.sizeDelta=new Vector2(96,104);ghost.Rect.localScale=Vector3.one*.8f;
                p.DragGhost=ghost.Rect;ghost.gameObject.SetActive(false);
            }
            var sourcePanel=WandWorkbenchWidgets.Frame("SequencePanel",root);
            Stretch(sourcePanel,new Vector2(0,1),Vector2.one,new Vector2(24,-288),new Vector2(-392,-108));
            var sourceTitle=WandWorkbenchWidgets.Label("SequenceTitle",sourcePanel,font,25);sourceTitle.text="法术序列";sourceTitle.color=WandWorkbenchWidgets.Gold;
            WandWorkbenchWidgets.Place(sourceTitle.rectTransform,18,10,400,38);
            var sourceScroll=Scroll("SequenceScroll",sourcePanel,true,false);
            Stretch((RectTransform)sourceScroll.transform,Vector2.zero,Vector2.one,new Vector2(10,10),new Vector2(-10,-52));

            var libraryPanel=WandWorkbenchWidgets.Frame("SpellLibrary",root);
            Stretch(libraryPanel,new Vector2(1,0),Vector2.one,new Vector2(-368,24),new Vector2(-24,-108));
            var libraryTitle=WandWorkbenchWidgets.Label("LibraryTitle",libraryPanel,font,25);libraryTitle.text="法术库";libraryTitle.color=WandWorkbenchWidgets.Gold;
            WandWorkbenchWidgets.Place(libraryTitle.rectTransform,18,12,280,38);
            var libraryScroll=Scroll("LibraryScroll",libraryPanel,false,true);
            Stretch((RectTransform)libraryScroll.transform,Vector2.zero,Vector2.one,new Vector2(8,10),new Vector2(-8,-60));

            var graphPanel=WandWorkbenchWidgets.Frame("ExecutionPreview",root);
            Stretch(graphPanel,Vector2.zero,Vector2.one,new Vector2(24,24),new Vector2(-392,-306));
            var graphTitle=WandWorkbenchWidgets.Label("GraphTitle",graphPanel,font,26);graphTitle.text="执行预览";graphTitle.color=WandWorkbenchWidgets.Gold;
            WandWorkbenchWidgets.Place(graphTitle.rectTransform,18,12,180,42);
            var graphScroll=Scroll("ScopeGraph",graphPanel,true,true);
            Stretch((RectTransform)graphScroll.transform,Vector2.zero,Vector2.one,new Vector2(12,14),new Vector2(-12,-72));
            var drawing=WandWorkbenchWidgets.Rect("Drawing",graphScroll.content);drawing.anchorMin=drawing.anchorMax=new Vector2(0,1);drawing.pivot=new Vector2(0,1);
            var viewport=graphScroll.gameObject.AddComponent<WandProgramCanvasViewport>();
            var zoomRect=WandWorkbenchWidgets.Panel("ZoomSlider",graphPanel,new Color(.19f,.19f,.17f));
            Stretch(zoomRect,new Vector2(1,1),Vector2.one,new Vector2(-216,-37),new Vector2(-26,-27));
            var slider=zoomRect.gameObject.AddComponent<Slider>();
            var handle=WandWorkbenchWidgets.Panel("Handle",zoomRect,WandWorkbenchWidgets.Gold);handle.sizeDelta=new Vector2(14,26);
            slider.handleRect=handle;slider.targetGraphic=handle.GetComponent<Image>();viewport.Initialize(graphScroll,drawing,slider);
            var fit=WandWorkbenchWidgets.Button("FitAll",graphPanel,font,"适应全部",viewport.FitAll);
            Stretch((RectTransform)fit.transform,new Vector2(1,1),Vector2.one,new Vector2(-420,-53),new Vector2(-296,-9));
            var zoomTitle=WandWorkbenchWidgets.Label("ZoomTitle",graphPanel,font,18);zoomTitle.text="缩放";
            Stretch(zoomTitle.rectTransform,new Vector2(1,1),Vector2.one,new Vector2(-280,-47),new Vector2(-222,-17));

            var sequence=libraryPanel.gameObject.AddComponent<WandProgramSequenceView>();
            var play=WandWorkbenchWidgets.Button("Playback",graphPanel,font,"播放",sequence.TogglePlayback);
            WandWorkbenchWidgets.Place((RectTransform)play.transform,190,10,70,44);
            // 玩家只需播放观察；单步、历史和循环调试继续留在内部接口，不占据工具栏。
            sequence.Initialize(p,font,sourceScroll.content,libraryScroll,play.GetComponentInChildren<Text>(),identity);
            graphScroll.gameObject.AddComponent<WandScopeGraphView>().Initialize(p,viewport,font);
        }

        private static void Stretch(RectTransform r,Vector2 min,Vector2 max,Vector2 lower,Vector2 upper)
        { r.anchorMin=min;r.anchorMax=max;r.offsetMin=lower;r.offsetMax=upper; }
        private static void PlaceRight(RectTransform rect,float inset,float width)
        {rect.anchorMin=rect.anchorMax=rect.pivot=Vector2.one;rect.anchoredPosition=new Vector2(-inset,-28);rect.sizeDelta=new Vector2(width,52);}

        private static ScrollRect Scroll(string name,Transform parent,bool horizontal,bool vertical)
        {
            var outer=WandWorkbenchWidgets.Panel(name,parent,new Color(.018f,.03f,.042f));
            var scroll=outer.gameObject.AddComponent<ScrollRect>();scroll.horizontal=horizontal;scroll.vertical=vertical;
            scroll.inertia=false;scroll.movementType=ScrollRect.MovementType.Clamped;scroll.scrollSensitivity=44;
            var viewport=WandWorkbenchWidgets.Panel("Viewport",outer,new Color(1,1,1,.001f));
            Stretch(viewport,Vector2.zero,Vector2.one,new Vector2(2,horizontal?18:2),new Vector2(vertical?-18:-2,-2));
            viewport.gameObject.AddComponent<RectMask2D>();
            var content=WandWorkbenchWidgets.Rect("Extent",viewport);content.anchorMin=content.anchorMax=new Vector2(0,1);content.pivot=new Vector2(0,1);
            scroll.viewport=viewport;scroll.content=content;
            if(vertical)scroll.verticalScrollbar=Bar(outer,true);
            if(horizontal)scroll.horizontalScrollbar=Bar(outer,false);
            return scroll;
        }
        private static Scrollbar Bar(RectTransform parent,bool vertical)
        {
            var rect=WandWorkbenchWidgets.Panel(vertical?"VerticalScrollbar":"HorizontalScrollbar",parent,new Color(.12f,.13f,.13f));
            if(vertical)Stretch(rect,new Vector2(1,0),Vector2.one,new Vector2(-12,18),new Vector2(-2,-2));
            else Stretch(rect,Vector2.zero,new Vector2(1,0),new Vector2(2,2),new Vector2(-18,12));
            var bar=rect.gameObject.AddComponent<Scrollbar>();bar.direction=vertical?Scrollbar.Direction.BottomToTop:Scrollbar.Direction.LeftToRight;
            var handle=WandWorkbenchWidgets.Panel("Handle",rect,new Color(.45f,.36f,.23f));
            handle.anchorMin=Vector2.zero;handle.anchorMax=Vector2.one;handle.offsetMin=Vector2.zero;handle.offsetMax=Vector2.zero;
            bar.handleRect=handle;bar.targetGraphic=handle.GetComponent<Image>();return bar;
        }
    }
}
