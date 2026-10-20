using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>只负责UGUI构造；布局和交互不包含任何Draw规则。所有创建均在打开面板/增加行时发生。</summary>
    internal static class WandWorkbenchWidgets
    {
        internal static readonly Color Paper = new Color(.023f,.037f,.053f), Ink = new Color(.94f,.90f,.80f);
        internal static readonly Color Green = new Color(.30f,.66f,.77f), Gold = new Color(.70f,.53f,.28f);
        internal static RectTransform Rect(string name, Transform parent)
        { var go=new GameObject(name,typeof(RectTransform));var rt=(RectTransform)go.transform;rt.SetParent(parent,false);return rt; }
        internal static void Place(RectTransform rect, float x, float y, float width, float height)
        { rect.anchorMin=rect.anchorMax=new Vector2(0,1);rect.pivot=new Vector2(0,1);
            rect.anchoredPosition=new Vector2(x,-y);rect.sizeDelta=new Vector2(width,height); }
        internal static RectTransform Panel(string name, Transform parent, Color color)
        { var r=Rect(name,parent);r.gameObject.AddComponent<Image>().color=color;return r; }
        internal static RectTransform Frame(string name,Transform parent)
        {
            var outer=Panel(name,parent,new Color(.56f,.43f,.24f,.75f));
            var fill=Panel("PanelFill",outer,new Color(.025f,.045f,.061f,.98f));
            fill.anchorMin=Vector2.zero;fill.anchorMax=Vector2.one;fill.offsetMin=Vector2.one*1.5f;fill.offsetMax=Vector2.one*-1.5f;
            fill.GetComponent<Image>().raycastTarget=false;return outer;
        }
        internal static Text Label(string name, Transform parent, Font font, int size=24)
        {
            var r=Rect(name,parent);var t=r.gameObject.AddComponent<Text>();t.font=font;t.fontSize=size;
            t.color=Ink;t.alignment=TextAnchor.UpperLeft;t.horizontalOverflow=HorizontalWrapMode.Wrap;
            t.verticalOverflow=VerticalWrapMode.Overflow;t.raycastTarget=false;return t;
        }
        internal static Button Button(string name, Transform parent, Font font, string text, Action action)
        {
            var r=Frame(name,parent);var b=r.gameObject.AddComponent<Button>();
            var t=Label("Text",r,font,23);t.alignment=TextAnchor.MiddleCenter;t.text=text;
            t.rectTransform.anchorMin=Vector2.zero;t.rectTransform.anchorMax=Vector2.one;
            t.rectTransform.offsetMin=new Vector2(8,3);t.rectTransform.offsetMax=new Vector2(-8,-3);
            if(action!=null)b.onClick.AddListener(()=>action());return b;
        }
        internal static RectTransform Scroll(string name, Transform parent, out RectTransform content)
        {
            var outer=Panel(name,parent,new Color(1,1,1,.55f));var scroll=outer.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal=false;scroll.movementType=ScrollRect.MovementType.Clamped;
            var viewport=Panel("Viewport",outer,new Color(1,1,1,.01f));
            viewport.anchorMin=Vector2.zero;viewport.anchorMax=Vector2.one;viewport.offsetMin=Vector2.zero;viewport.offsetMax=new Vector2(-22,0);
            viewport.gameObject.AddComponent<RectMask2D>();
            content=Rect("Content",viewport);content.anchorMin=new Vector2(0,1);content.anchorMax=Vector2.one;
            content.pivot=new Vector2(.5f,1);content.sizeDelta=Vector2.zero;
            var layout=content.gameObject.AddComponent<VerticalLayoutGroup>();layout.spacing=8;layout.padding=new RectOffset(10,10,10,10);
            layout.childControlWidth=true;layout.childControlHeight=true;layout.childForceExpandHeight=false;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit=ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport=viewport;scroll.content=content;
            // 显式滚动条让玩家知道仍有内容，不能只依赖鼠标滚轮的隐藏操作。
            var barRect=Panel("Scrollbar",outer,new Color(.82f,.85f,.81f));
            barRect.anchorMin=new Vector2(1,0);barRect.anchorMax=Vector2.one;barRect.pivot=new Vector2(1,.5f);
            barRect.sizeDelta=new Vector2(14,0);barRect.anchoredPosition=new Vector2(-4,0);
            var bar=barRect.gameObject.AddComponent<Scrollbar>();bar.direction=Scrollbar.Direction.BottomToTop;
            var handle=Panel("Handle",barRect,Green);handle.anchorMin=Vector2.zero;handle.anchorMax=Vector2.one;
            handle.offsetMin=new Vector2(1,1);handle.offsetMax=new Vector2(-1,-1);
            bar.handleRect=handle;bar.targetGraphic=handle.GetComponent<Image>();scroll.verticalScrollbar=bar;
            return outer;
        }
    }

    /// <summary>复用行对象和点击回调；步进仅改文字/颜色，不Destroy整棵Graph。</summary>
    internal sealed class WandWorkbenchRows
    {
        private readonly List<Button> _rows=new List<Button>();
        private readonly List<int> _ids=new List<int>();
        private readonly Transform _parent;
        private readonly Font _font;
        private readonly Action<int> _select;
        private int _used;
        internal WandWorkbenchRows(Transform parent,Font font,Action<int> select)
        { _parent=parent;_font=font;_select=select; }
        internal void Begin() { _used=0; }
        internal void Add(string text,int id,Color color,int height=65)
        {
            if(_used==_rows.Count)
            {
                int slot=_used;
                var b=WandWorkbenchWidgets.Button("Row",_parent,_font,"",()=>_select?.Invoke(_ids[slot]));
                b.gameObject.AddComponent<LayoutElement>();_rows.Add(b);_ids.Add(id);
            }
            var row=_rows[_used];_ids[_used]=id;row.gameObject.SetActive(true);
            row.GetComponent<Image>().color=color;row.interactable=id>=0;
            row.GetComponent<LayoutElement>().preferredHeight=height;
            var label=row.GetComponentInChildren<Text>();label.text=text;label.alignment=TextAnchor.MiddleLeft;
            _used++;
        }
        internal void End() { for(int i=_used;i<_rows.Count;i++)_rows[i].gameObject.SetActive(false); }
    }
}
