using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>中键直接捕获Input System，使物品IDragHandler不会截断Viewport平移；只改变视图坐标。</summary>
    public sealed class InventoryViewportPan:MonoBehaviour
    {
        private ScrollRect _scroll;
        private Camera _camera;
        private Vector2 _previousPointer;
        private static InventoryViewportPan _owner;
        private static readonly ProfilerMarker Marker=new ProfilerMarker("Norn.Backpack.MiddlePan");
        public bool IsPanning=>_owner==this;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetCapture()=>_owner=null;
        public void Configure(ScrollRect scroll)
        {
            _scroll=scroll;var canvas=GetComponentInParent<Canvas>();
            _camera=canvas!=null && canvas.rootCanvas.renderMode!=RenderMode.ScreenSpaceOverlay?canvas.rootCanvas.worldCamera:null;
        }
        public static InventoryViewportPan Ensure(ScrollRect scroll)
        {
            if(scroll==null)return null;
            var pan=scroll.GetComponent<InventoryViewportPan>();if(pan==null)pan=scroll.gameObject.AddComponent<InventoryViewportPan>();
            pan.Configure(scroll);return pan;
        }
        public void Cancel(){if(_owner==this)_owner=null;}
        private void OnDisable()=>Cancel();
        private void OnApplicationFocus(bool focus){if(!focus)Cancel();}
        public bool Tick(bool available)
        {
            using var sample=Marker.Auto();var mouse=Mouse.current;
            if(!available || !isActiveAndEnabled || _scroll==null || !_scroll.isActiveAndEnabled || _scroll.content==null
                || mouse==null || !Application.isFocused || !mouse.middleButton.isPressed){Cancel();return false;}
            var viewport=_scroll.viewport!=null?_scroll.viewport:(RectTransform)_scroll.transform;Vector2 pointer=mouse.position.ReadValue();
            if(_owner==null && mouse.middleButton.wasPressedThisFrame && RectTransformUtility.RectangleContainsScreenPoint(viewport,pointer,_camera))
            {_owner=this;_previousPointer=pointer;_scroll.StopMovement();return false;}
            if(_owner!=this)return false;
            // 屏幕位移转Viewport Local Space，补偿CanvasScaler与父级Scale；捕获后允许移出Viewport继续拖动。
            if(!RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport,_previousPointer,_camera,out var previous)
                || !RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport,pointer,_camera,out var current)){Cancel();return false;}
            _previousPointer=pointer;Vector2 delta=current-previous;var content=_scroll.content;
            var edgeX=viewport.InverseTransformVector(content.TransformVector(Vector3.right*content.rect.width));
            var edgeY=viewport.InverseTransformVector(content.TransformVector(Vector3.up*content.rect.height));
            float overflowX=Mathf.Abs(edgeX.x)+Mathf.Abs(edgeY.x)-viewport.rect.width;
            float overflowY=Mathf.Abs(edgeX.y)+Mathf.Abs(edgeY.y)-viewport.rect.height;
            Vector2 before=_scroll.normalizedPosition,next=before;
            if(_scroll.horizontal && overflowX>.01f)next.x=Mathf.Clamp01(before.x-delta.x/overflowX);
            if(_scroll.vertical && overflowY>.01f)next.y=Mathf.Clamp01(before.y-delta.y/overflowY);
            _scroll.StopMovement();if(next==before)return false;_scroll.normalizedPosition=next;return true;
        }
    }
}
