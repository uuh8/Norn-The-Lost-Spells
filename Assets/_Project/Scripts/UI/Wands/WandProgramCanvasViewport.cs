using UnityEngine;
using UnityEngine.UI;
using Unity.Profiling;

namespace Game.UI
{
    /// <summary>Extent管理实际滚动边界，Drawing管理缩放；两者不能同时缩放。</summary>
    public sealed class WandProgramCanvasViewport : MonoBehaviour
    {
        public float Zoom { get; private set; }=1;
        public Vector2 LogicalSize { get; private set; }
        private ScrollRect _scroll;
        private RectTransform _drawing;
        private Slider _slider;
        private static readonly ProfilerMarker ZoomMarker=new ProfilerMarker("Norn.WandPreview.Zoom");
        public RectTransform Drawing => _drawing;
        public ScrollRect Scroll => _scroll;
        private Vector2 ViewSize => _scroll==null?Vector2.one:_scroll.viewport.rect.size;
        private Vector2 Center => new Vector2(ViewSize.x*.5f,-ViewSize.y*.5f);
        public Vector2 ViewCenterInDrawing => _scroll==null?Vector2.zero:(Center-_scroll.content.anchoredPosition)/Zoom;
        public void Initialize(ScrollRect scroll,RectTransform drawing,Slider zoomSlider)
        {
            _scroll=scroll;_drawing=drawing;_slider=zoomSlider;
            _slider.minValue=.01f;_slider.maxValue=2;_slider.wholeNumbers=false;_slider.SetValueWithoutNotify(1);
            _slider.onValueChanged.AddListener(SetZoom);
        }
        private void OnDestroy(){if(_slider!=null)_slider.onValueChanged.RemoveListener(SetZoom);}
        public void SetLogicalSize(Vector2 size)
        {
            if(!float.IsFinite(size.x) || !float.IsFinite(size.y))return;
            LogicalSize=new Vector2(Mathf.Max(1,size.x),Mathf.Max(1,size.y));
            _drawing.sizeDelta=LogicalSize;UpdateRange();UpdateExtent();
        }
        public void SetZoom(float zoom)
        {
            if(_scroll==null || !float.IsFinite(zoom) || LogicalSize.x<=0)return;
            using(ZoomMarker.Auto())
            {
                var focus=ViewCenterInDrawing;Zoom=Mathf.Clamp(zoom,_slider.minValue,_slider.maxValue);
                _drawing.localScale=new Vector3(Zoom,Zoom,1);_slider.SetValueWithoutNotify(Zoom);
                UpdateExtent();SetPosition(Center-focus*Zoom);
            }
        }
        public void FitAll()
        {
            if(_scroll==null || LogicalSize.x<=0)return;
            UpdateRange();float fit=Mathf.Min(1,Mathf.Min(ViewSize.x/LogicalSize.x,ViewSize.y/LogicalSize.y));
            SetZoom(fit);_scroll.StopMovement();_scroll.content.anchoredPosition=Vector2.zero;
        }
        public void ResetView()
        { if(_scroll==null)return;SetZoom(1);_scroll.StopMovement();_scroll.content.anchoredPosition=Vector2.zero; }
        private void OnRectTransformDimensionsChange()
        {
            if(_scroll==null || _drawing==null || _slider==null || LogicalSize.x<=0)return;
            UpdateRange();UpdateExtent();
        }
        private void UpdateRange()
        {
            float fit=Mathf.Min(Mathf.Max(1,ViewSize.x)/LogicalSize.x,Mathf.Max(1,ViewSize.y)/LogicalSize.y);
            // Slider修改范围时也可能触发onValueChanged；折叠/Resize必须把现有Zoom留在合法范围。
            _slider.minValue=Mathf.Max(.00001f,Mathf.Min(.1f,Mathf.Min(fit*.99f,Zoom)));
            _slider.SetValueWithoutNotify(Zoom);
        }
        private void UpdateExtent()
        {
            // ScrollRect的Content保持scale=1，滚动条看到的边界才与放大后的画面一致。
            _scroll.content.sizeDelta=new Vector2(Mathf.Max(ViewSize.x,LogicalSize.x*Zoom),Mathf.Max(ViewSize.y,LogicalSize.y*Zoom));
            SetPosition(_scroll.content.anchoredPosition);
        }
        private void SetPosition(Vector2 position)
        {
            var extra=_scroll.content.sizeDelta-ViewSize;
            _scroll.content.anchoredPosition=new Vector2(Mathf.Clamp(position.x,-Mathf.Max(0,extra.x),0),Mathf.Clamp(position.y,0,Mathf.Max(0,extra.y)));
        }
    }
}
