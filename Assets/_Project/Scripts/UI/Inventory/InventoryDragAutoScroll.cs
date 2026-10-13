using UnityEngine;
using UnityEngine.UI;
using Unity.Profiling;

namespace Game.UI
{
    /// <summary>拖拽时连续滚动真实Viewport；只改变显示坐标，资产仍由Drop事务决定。</summary>
    public sealed class InventoryDragAutoScroll : MonoBehaviour
    {
        [SerializeField] private ScrollRect _scroll;
        // 新字段按屏幕像素定义，旧Scene里72/360的Local Space参数不迁入，统一获得本轮手感。
        [SerializeField,Min(1),Tooltip("距Viewport边缘多少屏幕像素开始滚动；小窗口最多占单侧45%，中央保留静止区。")]
        private float _edgeScreenPixels=96;
        [SerializeField,Min(1),Tooltip("最靠近边缘时的最高速度，单位为屏幕像素/秒；越靠近边缘越快。")]
        private float _maxScreenSpeed=1000;
        private static readonly ProfilerMarker Marker=new ProfilerMarker("Norn.Backpack.AutoScroll");
        public void Configure(ScrollRect scroll)=>_scroll=scroll;
        public bool Tick(Vector2 pointer,Camera camera,bool active,float unscaledDelta)
        {
            if(!active || _scroll==null || !_scroll.isActiveAndEnabled || _scroll.content==null || unscaledDelta<=0)return false;
            using var sample=Marker.Auto();
            RectTransform viewport=_scroll.viewport!=null?_scroll.viewport:(RectTransform)_scroll.transform;
            if(!RectTransformUtility.RectangleContainsScreenPoint(viewport,pointer,camera)
                || !RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport,pointer,camera,out var local))return false;
            Rect rect=viewport.rect;var content=_scroll.content;
            // ScrollRect使用Viewport Local Space，玩家感知屏幕像素。
            // 投影两条单位轴得到像素/Local unit，补偿CanvasScaler、Transform缩放和Camera投影。
            var screenOrigin=RectTransformUtility.WorldToScreenPoint(camera,viewport.TransformPoint(Vector3.zero));
            float scaleX=(RectTransformUtility.WorldToScreenPoint(camera,viewport.TransformPoint(Vector3.right))-screenOrigin).magnitude;
            float scaleY=(RectTransformUtility.WorldToScreenPoint(camera,viewport.TransformPoint(Vector3.up))-screenOrigin).magnitude;
            if(scaleX<.0001f || scaleY<.0001f)return false;
            // Local Space的Bounds补偿Content缩放；不把Canvas缩放后的屏幕像素当格子单位。
            // CalculateRelativeRectTransformBounds会遍历子RectTransform并分配查询数组；
            // Content本身已代表完整网格，直接变换它的两条边，无每帧集合分配。
            var edgeX=viewport.InverseTransformVector(content.TransformVector(Vector3.right*content.rect.width));
            var edgeY=viewport.InverseTransformVector(content.TransformVector(Vector3.up*content.rect.height));
            float overflowX=Mathf.Abs(edgeX.x)+Mathf.Abs(edgeY.x)-rect.width;
            float overflowY=Mathf.Abs(edgeX.y)+Mathf.Abs(edgeY.y)-rect.height;
            float dx=Axis(local.x,rect.xMin,rect.xMax,_edgeScreenPixels/scaleX)*_maxScreenSpeed/scaleX*unscaledDelta;
            float dy=Axis(local.y,rect.yMin,rect.yMax,_edgeScreenPixels/scaleY)*_maxScreenSpeed/scaleY*unscaledDelta;
            Vector2 before=_scroll.normalizedPosition,next=before;
            if(_scroll.horizontal && overflowX>.01f)next.x=Mathf.Clamp01(before.x+dx/overflowX);
            if(_scroll.vertical && overflowY>.01f)next.y=Mathf.Clamp01(before.y+dy/overflowY);
            if(next==before)return false;
            // StopMovement阻止原惯性和自动滚动竞争；取消拖拽不会留下持续滚动速度。
            _scroll.StopMovement();_scroll.normalizedPosition=next;return true;
        }
        private static float Axis(float point,float min,float max,float desiredEdge)
        {
            float edge=Mathf.Min(desiredEdge,(max-min)*.45f);
            if(edge<=0)return 0;
            float signed=point<min+edge?-(min+edge-point)/edge:point>max-edge?(point-(max-edge))/edge:0;
            // Ease-out曲线t(2-t)：从0连续起步，中段响应更快，边缘封顶，避免突然开关或超速。
            float t=Mathf.Clamp01(Mathf.Abs(signed));
            return Mathf.Sign(signed)*t*(2-t);
        }
    }
}
