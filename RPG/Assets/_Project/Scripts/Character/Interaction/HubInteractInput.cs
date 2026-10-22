using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Character
{
    /// <summary>F站点交互优先；共享上下文避免同一次F又拾取又采集。</summary>
    public sealed class HubInteractInput : MonoBehaviour
    {
        [SerializeField]private HubInteractionController _hub;
        [SerializeField]private NearbyPickupController _pickups;
        private InputAction _interact;
        internal static HubInteractInput Current { get; private set; }
        internal bool HasStation => _hub != null && _hub.TryGetStation(out var station) && station.Kind != HubStationKind.Expedition;
        private void Awake(){_interact=new InputAction("HubInteract",InputActionType.Button,"<Keyboard>/f");_interact.performed+=OnInteract;}
        private void OnEnable(){Current=this;_interact.Enable();}
        private void OnDisable(){if(Current==this)Current=null;_interact.Disable();}
        private void OnDestroy(){_interact.performed-=OnInteract;_interact.Dispose();}
        private void OnInteract(InputAction.CallbackContext _)
        {
            if(Time.timeScale<=0)return;
            // 拾取与材料输入也检查同一站点条件，事件回调注册顺序不影响优先级。
            if(_hub!=null && _hub.TryGetStation(out var station) && station.Kind!=HubStationKind.Expedition)
                {_hub.Request((HubInteractionKind)station.Kind);return;}
        }
    }
}
