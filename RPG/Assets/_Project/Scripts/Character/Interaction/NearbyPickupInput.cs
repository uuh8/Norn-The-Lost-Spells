using Game.Run;
using Game.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Character
{
    /// <summary>上下文F拾取，站点/传送门优先；4–0选择道具。</summary>
    public sealed class NearbyPickupInput:MonoBehaviour
    {
        [SerializeField] private NearbyPickupController _pickups;
        private InputAction _pickup,_scroll;
        private bool _focused=true;
        private bool _online, _onlineSubscribed;
        /// <summary>UI Scope只创建Run Controller；Character在同Actor注入该实例，不让UI反向依赖Character。</summary>
        public void ConfigureOnline(NearbyPickupController pickups)
        {
            _online = true;
            _pickups = pickups != null && pickups.gameObject == gameObject ? pickups : GetComponent<NearbyPickupController>();
        }
        internal void ReleaseOnline(bool dispose)
        {
            if (!_online) return;
            if(dispose)OnDestroy();else OnDisable();
            enabled=false;
        }
        private void OnOnlineList(NearbyPickupListChangedEvent change)
        { if (_online) ConfigureOnline(GetComponent<NearbyPickupController>()); }
        internal static NearbyPickupInput Current { get; private set; }
        internal static int LastPickupFrame { get; private set; } = -1;
        internal bool HasPickup => _pickups != null && _pickups.isActiveAndEnabled && _pickups.Count > 0;
        private bool CanUse=>_focused && Time.timeScale>0 && HasPickup && !(HubInteractInput.Current?.HasStation ?? false);
        private void Awake(){if(_pickup!=null)return;_pickup=new InputAction("PickupNearbyItem",InputActionType.Button,"<Keyboard>/f");_pickup.performed+=OnPickup;
            _scroll=new InputAction("SelectNearbyItem",InputActionType.Value,"<Mouse>/scroll");_scroll.performed+=OnScroll;}
        private void OnEnable(){Awake();Current=this;_pickup?.Enable();_scroll?.Enable();
            if(!_onlineSubscribed){EventBus<NearbyPickupListChangedEvent>.Subscribe(OnOnlineList);_onlineSubscribed=true;}
            if(_online)ConfigureOnline(GetComponent<NearbyPickupController>());}
        private void OnDisable(){if(Current==this)Current=null;_pickup?.Disable();_scroll?.Disable();GameplayScrollCapture.Release(this);
            if(_onlineSubscribed){EventBus<NearbyPickupListChangedEvent>.Unsubscribe(OnOnlineList);_onlineSubscribed=false;}}
        private void OnApplicationFocus(bool focus){_focused=focus;if(!focus)GameplayScrollCapture.Release(this);}
        private void Update(){if(CanUse)GameplayScrollCapture.Acquire(this);else GameplayScrollCapture.Release(this);}
        private void OnDestroy(){OnDisable();if(_pickup!=null){_pickup.performed-=OnPickup;_pickup.Dispose();_pickup=null;}if(_scroll!=null){_scroll.performed-=OnScroll;_scroll.Dispose();_scroll=null;}GameplayScrollCapture.Release(this);}
        private void OnPickup(InputAction.CallbackContext _){if(CanUse && !PortalInteractInput.HasTarget){LastPickupFrame=Time.frameCount;_pickups.TryCollectFocused();}}
        private void OnScroll(InputAction.CallbackContext context){if(CanUse){float y=context.ReadValue<Vector2>().y;if(Mathf.Abs(y)>.01f)_pickups.SelectRelative(y>0?-1:1);}}
    }
}
