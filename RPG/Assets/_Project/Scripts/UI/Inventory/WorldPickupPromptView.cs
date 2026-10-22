using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.UI;
using Unity.Profiling;

namespace Game.UI
{
    /// <summary>共享单焦点World Space提示；事件变更时写文字，LateUpdate只跟随位置/镜头。</summary>
    [DefaultExecutionOrder(10001)]
    public sealed class WorldPickupPromptView:MonoBehaviour
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private SinglePlayerInventoryController _inventory;
        [SerializeField] private GameObject _visual;
        [SerializeField] private Text _label;
        [SerializeField] private Image _icon;
        [SerializeField] private Camera _camera;
        [SerializeField] private Vector3 _offset=new Vector3(0,.65f,0);
        [SerializeField,Min(80)] private float _screenWidth=300;
        [SerializeField] private Vector2 _screenOffset=new Vector2(190,25);
        private Transform _anchor;
        private bool _visible;
        private static readonly ProfilerMarker Marker=new ProfilerMarker("Norn.Items.PromptBillboard");
        private void OnEnable()=>EventBus<NearbyPickupFocusChangedEvent>.Subscribe(OnFocus);
        private void OnDisable(){EventBus<NearbyPickupFocusChangedEvent>.Unsubscribe(OnFocus);if(_visual!=null)_visual.SetActive(false);}
        private void OnFocus(NearbyPickupFocusChangedEvent e)
        {
            if(_session?.ReadOnly==null || e.InventoryId!=_session.ReadOnly.InventoryId)return;
            _anchor=e.Anchor;_visible=e.Visible;
            if(e.Visible && _session.ReadOnly.TryGetView(e.ItemId,out var item))
            { if(_inventory!=null && _inventory.UiFont!=null)_label.font=_inventory.UiFont;
                _label.text="F 拾取 · "+item.DisplayName;if(_icon!=null){_icon.sprite=item.Icon;_icon.enabled=item.Icon!=null;} }
        }
        private void LateUpdate()
        {
            using var sample=Marker.Auto();
            if(_inventory!=null && _inventory.UiFont!=null && _label.font!=_inventory.UiFont)_label.font=_inventory.UiFont;
            bool show=_visible && _anchor!=null && _anchor.gameObject.activeInHierarchy && Time.timeScale>0;
            if(_visual==null)return;
            if(show && (_camera==null || !_camera.isActiveAndEnabled))_camera=Camera.main;
            Vector3 anchor=show?_anchor.position+Vector3.up*_offset.y:Vector3.zero;
            float depth=show && _camera!=null?Vector3.Dot(anchor-_camera.transform.position,_camera.transform.forward):0;
            show=show && _camera!=null && depth>_camera.nearClipPlane;
            // 先裁定最终可见性再SetActive，镜头背后的锚点不会每帧启停Canvas触发布局重建。
            if(_visual.activeSelf!=show)_visual.SetActive(show);if(!show)return;
            // camera.rotation使Canvas的local XY与画面XY一致，避免LookAt把文字背面朝镜头。
            // 保持World Space锚点与深度，同时把目标像素宽换算成世界尺寸，避免远近变化使文字不可读。
            float worldPerPixel=(_camera.orthographic?2*_camera.orthographicSize:2*depth*Mathf.Tan(_camera.fieldOfView*Mathf.Deg2Rad*.5f))/Mathf.Max(1,_camera.pixelHeight);
            float width=((RectTransform)_visual.transform).rect.width;
            _visual.transform.localScale=Vector3.one*(worldPerPixel*_screenWidth/Mathf.Max(1,width));
            _visual.transform.SetPositionAndRotation(anchor+_camera.transform.right*(_offset.x+worldPerPixel*_screenOffset.x)+_camera.transform.up*(worldPerPixel*_screenOffset.y),_camera.transform.rotation);
        }
    }
}
