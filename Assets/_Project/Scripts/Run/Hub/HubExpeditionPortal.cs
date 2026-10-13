using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>范围内F申请出发；残留确认、可靠存档与加载仍由既有Run调用链处理。</summary>
    [RequireComponent(typeof(Collider))]
    public sealed class HubExpeditionPortal : MonoBehaviour, IPortalVisualSource, IPortalInteractionTarget
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private Transform _player;
        private bool _entered;
        private Collider _trigger;
        public Transform PortalTransform => transform;
        public string InteractionLabel => "F 进入远征";
        public bool CanInteract(Collider actor) => _player != null && actor != null && actor.transform.IsChildOf(_player)
            && _session?.RunCoordinator?.State.Phase == RaidPhase.HubReady && _session.RunCoordinator.ActionsAllowed
            && PortalInteractionRegistry.Contains(_trigger, actor);
        public bool Interact(Collider actor) { if (!CanInteract(actor)) return false; _entered = false; return TryEnter(actor); }
        public int PortalId => gameObject.GetInstanceID();
        private void Awake() { _trigger = GetComponent<Collider>(); PortalTransitionCoordinator.EnsureVisual(transform); }
        private void OnEnable() => PortalInteractionRegistry.Register(this);
        private void OnDisable() => PortalInteractionRegistry.Unregister(this);
        public bool TryEnter(Collider other)
        {
            if (_entered || other == null || _player == null || !other.transform.IsChildOf(_player)
                || _session?.ReadOnly == null || _session.RunCoordinator?.State.Phase != RaidPhase.HubReady) return false;
            // 多Collider重复进入只能发一份请求。失败/残留确认取消后走出再进入，避免每帧重复弹窗。
            _entered = true;
            _session.RunCoordinator.SetDeparturePortal(transform, PortalId);
            EventBus<HubDepartureReviewRequestedEvent>.Publish(new HubDepartureReviewRequestedEvent
            { InventoryId = _session.ReadOnly.InventoryId, Operation = HubDepartureReviewOperation.Depart });
            return true;
        }
        private void OnTriggerExit(Collider other)
        {
            // 出发会停用整个据点，物理系统也可能发Exit；这不是玩家主动走出。
            // 保留Gate到回据点后真正离开入口，避免原位置返回立即再次传送。
            if (_session?.RunCoordinator?.State.Phase == RaidPhase.HubReady && _player != null && other.transform.IsChildOf(_player)) _entered = false;
        }
    }
}
