using UnityEngine;
using Game.Core;

namespace Game.Run
{
    /// <summary>家的和平入口，复用F交互Registry；从不建立Raid或在TriggerEnter自动传送。</summary>
    [RequireComponent(typeof(BoxCollider))]
    public sealed class HomePortal : MonoBehaviour, IPortalVisualSource, IPortalInteractionTarget
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private Transform _player;
        [SerializeField] private string _targetScene;
        [SerializeField] private bool _returnToHub;
        [SerializeField] private Vector3 _arrivalPosition;
        [SerializeField] private Vector3 _arrivalEuler;
        [SerializeField] private Transform _outsidePoint;
        private BoxCollider _trigger;
        private DungeonEntryChoiceGate _accountEntry;
        public Transform PortalTransform=>transform;
        public int PortalId=>gameObject.GetInstanceID();
        public string InteractionLabel=>_returnToHub?"F 返回据点":"F 进入家";
        public bool Returning=>_returnToHub;
        internal string TargetScene=>_targetScene;
        internal Vector3 ArrivalPosition=>_arrivalPosition;
        internal Quaternion ArrivalRotation=>Quaternion.Euler(_arrivalEuler);
        internal Vector3 OutsidePosition=>_outsidePoint!=null?_outsidePoint.position:transform.position-transform.forward*3;
        private void Awake()
        {
            _trigger=GetComponent<BoxCollider>();
            _accountEntry = new DungeonEntryChoiceGate(PortalId, _targetScene, InteractionLabel, CanInteract,
                () => _session != null && _session.RunCoordinator != null && _session.RunCoordinator.TryBeginDungeonEntryChoice(),
                () => { if (_session != null && _session.RunCoordinator != null) _session.RunCoordinator.EndDungeonEntryChoice(); });
        }
        private void OnEnable(){_accountEntry?.Enable();PortalInteractionRegistry.Register(this);}
        private void OnDisable(){_accountEntry?.Disable();PortalInteractionRegistry.Unregister(this);}
        public bool CanInteract(Collider actor)
        {
            // 账号Home走已确认的单人DS入口；只读摘要不代表可写本地Profile。
            if (_session != null && _session.AccountInventoryReadOnly)
                return !_returnToHub && _player != null && actor != null && actor.transform.IsChildOf(_player)
                    && PortalInteractionRegistry.Contains(_trigger, actor) && _session.TryGetAccountHubSummary(out _)
                    && _session.RunCoordinator != null && _session.RunCoordinator.CanChooseDungeonEntry;
            return Time.timeScale>0 && _session?.ReadOnly!=null && _player!=null && actor!=null
            && actor.transform.IsChildOf(_player) && PortalInteractionRegistry.Contains(_trigger,actor)
            && _session.RunCoordinator?.State.Phase==RaidPhase.HubReady && _session.RunCoordinator.ActionsAllowed
            && (HomeSceneTransfer.Current==null || !HomeSceneTransfer.Current.Loading);
        }
        public bool Interact(Collider actor)=>CanInteract(actor) && (_session.AccountInventoryReadOnly
            ? _accountEntry != null && _accountEntry.RequestSinglePlayer(actor) : HomeSceneTransfer.TryTravel(_session,this,_player));
    }
}
