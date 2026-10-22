using UnityEngine;
using Game.Core;

namespace Game.Run
{
    /// <summary>教学/Boss练习门。保存成功才切Scene，与正式Raid携装结算完全隔离。</summary>
    [RequireComponent(typeof(Collider))]
    public sealed class HubPracticePortal : MonoBehaviour, IPortalVisualSource, IPortalInteractionTarget
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private Transform _player;
        [SerializeField] private string _scenePath;
        [SerializeField] private GameObject _returnPortalPrefab;
        [SerializeField] private bool _returnToHub;
        private bool _entered;
        private Collider _trigger;
        private DungeonEntryChoiceGate _choice;
        public Transform PortalTransform => transform;
        public string InteractionLabel => _returnToHub ? "F 返回据点" : _scenePath != null && _scenePath.Contains("P7") ? "F 进入P7练习" : "F 进入P8练习";
        public bool CanInteract(Collider actor) => !_entered && _player != null && actor != null && actor.transform.IsChildOf(_player)
            && PortalInteractionRegistry.Contains(_trigger, actor) && (_returnToHub ? HubPracticeSession.Current != null && !HubPracticeSession.Current.Loading
                : _session?.RunCoordinator?.State.Phase == RaidPhase.HubReady && _session.RunCoordinator.ActionsAllowed);
        public bool Interact(Collider actor) => CanInteract(actor) && (_returnToHub ? TryEnter(actor) : _choice.Request(actor));
        public int PortalId => gameObject.GetInstanceID();
        private void Awake()
        {
            _trigger = GetComponent<Collider>(); PortalTransitionCoordinator.EnsureVisual(transform);
            _choice = new DungeonEntryChoiceGate(PortalId, _scenePath, InteractionLabel, CanInteract, TryEnter, BeginChoice, EndChoice);
        }
        private bool BeginChoice() => _session?.RunCoordinator?.TryBeginDungeonEntryChoice() == true;
        private void EndChoice() => _session?.RunCoordinator?.EndDungeonEntryChoice();
        private void OnEnable() { _choice?.Enable(); PortalInteractionRegistry.Register(this); }
        private void OnDisable() { _choice?.Disable(); PortalInteractionRegistry.Unregister(this); }
        public string ScenePath=>_scenePath;
        public void ConfigureReturn(Transform player){_player=player;_returnToHub=true;}
        public bool TryEnter(Collider other)
        {
            if(_entered || other==null || _player==null || !other.transform.IsChildOf(_player))return false;
            bool accepted=_returnToHub?HubPracticeSession.Current!=null && HubPracticeSession.Current.ReturnToHub(_player, transform, PortalId)
                :_session?.RunCoordinator?.State.Phase==RaidPhase.HubReady && HubPracticeSession.TryBegin(_session,_scenePath,_returnPortalPrefab, transform, _player, PortalId);
            if(accepted)_entered=true;return accepted;
        }
        private void OnTriggerExit(Collider other){if(_player!=null && other.transform.IsChildOf(_player))_entered=false;}
    }
}
