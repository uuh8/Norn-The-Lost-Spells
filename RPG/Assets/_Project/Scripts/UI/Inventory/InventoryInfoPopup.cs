using System.Globalization;
using System.Text;
using Game.Run;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>当前Source的实例详情。仅格式化不可变快照，不选择/移动/消耗物品，不创建或重抽固定属性。</summary>
    public sealed class InventoryInfoPopup:MonoBehaviour,IPointerClickHandler
    {
        [SerializeField] private RectTransform _frame;
        [SerializeField] private Text _title,_body;
        [SerializeField] private Button _close;
        private ScrollRect _scroll;
        private RectTransform _viewport;
        private Vector2 _authoredSize;
        private bool _capturedLayout;
        public bool IsOpen=>gameObject.activeSelf;
        private void OnEnable(){_close?.onClick.AddListener(Hide);}
        private void OnDisable(){_close?.onClick.RemoveListener(Hide);}
        public void Show(string name,Vector2 pointer,Camera camera,Font font)
        {
            ShowContent(name,"详细信息待开发",false,pointer,camera,font);
        }
        /// <summary>原右键入口传入本次选中实例；非杖沿用原占位，缺失属性不能静默回退为另一套模板。</summary>
        public void Show(in InventoryItemSnapshot item,Vector2 pointer,Camera camera,Font font)
        {
            if(item.Kind!=ItemKind.Wand){Show(item.DisplayName,pointer,camera,font);return;}
            string text=item.FixedWandStats.HasValue?Describe(item):"法杖固定属性不可用";
            ShowContent(item.DisplayName,text,true,pointer,camera,font);
        }
        private static string Number(float value)=>value.ToString("R",CultureInfo.InvariantCulture);
        private static string Describe(in InventoryItemSnapshot item)
        {
            var stats=item.FixedWandStats.Value;
            // 只在显式查看时拼字；R保留非整数float读数，显示不修改固定属性或资源状态。
            var text=new StringBuilder(256);
            text.Append("Mana上限：").Append(Number(stats.MaxMana))
                .Append("\nMana恢复：").Append(Number(stats.ManaRegenPerSecond)).Append(" /s")
                .Append("\n基础施法延迟：").Append(Number(stats.BaseCastDelay)).Append(" s")
                .Append("\n基础重载时间：").Append(Number(stats.BaseReloadTime)).Append(" s")
                .Append("\n基础散布：").Append(Number(stats.BaseSpreadDegrees)).Append("°")
                .Append("\n程序容量：").Append(stats.ProgramCapacity)
                .Append("\n基础抽取：").Append(stats.BaseDraws)
                .Append("\n乱序：").Append(stats.Shuffle?"是":"否");
            var traits=stats.AlwaysCastContentIds;
            if(traits.Count==0)text.Append("\n固定法术：无");
            else
            {
                text.Append("\n固定法术（原顺序）：");
                for(int i=0;i<traits.Count;i++)text.Append('\n').Append(i+1).Append(". ").Append(item.GetFixedWandTraitDisplayName(i));
            }
            return text.ToString();
        }
        private void ShowContent(string name,string text,bool fullDetails,Vector2 pointer,Camera camera,Font font)
        {
            // Controller尚未创建动态字体的测试/旧调用仍可使用Prefab字体，不把合法正文变成零高度。
            if(font!=null){_title.font=font;_body.font=font;}
            if(_body.font==null)_body.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if(_title.font==null)_title.font=_body.font;
            _title.text=name;_body.text=text;
            gameObject.SetActive(true);transform.SetAsLastSibling();
            var parent=(RectTransform)transform.parent;
            if(!_capturedLayout){_authoredSize=_frame.rect.size;_capturedLayout=true;}
            if(fullDetails)EnsureScrollBody();
            FitBody(parent.rect);
            if(!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent,pointer,camera,out var position))position=parent.rect.center;
            // UGUI指针是Screen Space，夹紧在父面板Local Space内，右下角也能完整看见窗口。
            position+=new Vector2(18,-10);Rect bounds=parent.rect;
            position.x=Mathf.Clamp(position.x,bounds.xMin,bounds.xMax-_frame.rect.width);
            position.y=Mathf.Clamp(position.y,bounds.yMin+_frame.rect.height,bounds.yMax);
            _frame.position=parent.TransformPoint(position);
        }
        private void EnsureScrollBody()
        {
            if(_scroll!=null)return;
            // 只创建运行时包装，保留作者Body区域/字号和Frame美术，不修改Prefab或正式Scene。
            // 透明Image接收滚轮与拖拽，RectMask2D仅裁可滚动视口；完整正文始终保留在Content中。
            var body=_body.rectTransform;
            var view=new GameObject("Details scroll viewport",typeof(RectTransform),typeof(Image),typeof(RectMask2D));
            _viewport=(RectTransform)view.transform;_viewport.SetParent(body.parent,false);
            _viewport.anchorMin=body.anchorMin;_viewport.anchorMax=body.anchorMax;_viewport.pivot=body.pivot;
            _viewport.anchoredPosition=body.anchoredPosition;_viewport.sizeDelta=body.sizeDelta;
            var image=view.GetComponent<Image>();image.color=Color.clear;image.raycastTarget=true;
            body.SetParent(_viewport,false);body.anchorMin=new Vector2(0,1);body.anchorMax=Vector2.one;
            body.pivot=new Vector2(.5f,1);body.anchoredPosition=Vector2.zero;body.sizeDelta=Vector2.zero;
            _body.verticalOverflow=VerticalWrapMode.Overflow;
            _scroll=view.AddComponent<ScrollRect>();_scroll.viewport=_viewport;_scroll.content=body;
            _scroll.horizontal=false;_scroll.vertical=true;_scroll.movementType=ScrollRect.MovementType.Clamped;
            _scroll.inertia=false;_scroll.scrollSensitivity=32;
        }
        private void FitBody(Rect bounds)
        {
            // 先确定实际可用宽度，再测量换行后的正文；不能用原430宽测量后直接缩窄并裁字。
            float width=Mathf.Min(_authoredSize.x,bounds.width),maximumHeight=bounds.height;
            _frame.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal,width);
            _frame.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,Mathf.Min(_authoredSize.y,maximumHeight));
            _frame.ForceUpdateRectTransforms();
            var area=_viewport!=null?_viewport:_body.rectTransform;
            var settings=_body.GetGenerationSettings(new Vector2(Mathf.Max(1,area.rect.width),0));
            float preferred=_body.cachedTextGeneratorForLayout.GetPreferredHeight(_body.text,settings)/_body.pixelsPerUnit;
            float fraction=area.anchorMax.y-area.anchorMin.y;
            float desired=fraction>0?(preferred-area.sizeDelta.y)/fraction:_authoredSize.y;
            float height=Mathf.Min(maximumHeight,Mathf.Max(_authoredSize.y,desired));
            _frame.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,height);_frame.ForceUpdateRectTransforms();
            if(_scroll==null)return;
            _scroll.StopMovement();
            _body.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,Mathf.Max(preferred,_viewport.rect.height));
            _body.rectTransform.anchoredPosition=Vector2.zero;
            _scroll.vertical=preferred>_viewport.rect.height+.5f;
            _scroll.verticalNormalizedPosition=1;
        }
        public void Hide()=>gameObject.SetActive(false);
        public void OnPointerClick(PointerEventData data)
        {if(!RectTransformUtility.RectangleContainsScreenPoint(_frame,data.position,data.pressEventCamera))Hide();}
    }
}
