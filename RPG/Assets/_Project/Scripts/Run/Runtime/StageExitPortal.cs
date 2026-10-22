using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 地图一出口的 Gameplay Authority。Encounter 未完成时 Collider 禁用；
    /// 有效进入时提交携带库存，再由据点上下文执行回程；不再发开场法术或自动解锁全集。
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public sealed class StageExitPortal : MonoBehaviour, IPortalInteractionTarget
    {
        [SerializeField, Min(0)] private int _encounterIndex;
        [SerializeField] private LayerMask _playerLayers;
        private Collider _trigger;
        private bool _available;
        private bool _transitionRequested;

        public int PortalId => gameObject.GetInstanceID();
        public bool IsAvailable => _available;
        public Transform PortalTransform => transform;
        public string InteractionLabel => "F 返回据点";
        public bool CanInteract(Collider actor) => _available && !_transitionRequested && IsPlayerCollider(actor) && PortalInteractionRegistry.Contains(_trigger, actor);
        public bool Interact(Collider actor) => CanInteract(actor) && TryEnter(actor);
        public bool IsTransitionRequested => _transitionRequested;
        private void Awake()
        {
            _trigger = GetComponent<Collider>();
            if (_trigger != null && !_trigger.isTrigger)
            {
                GameLog.Warn("StageExitPortal 的 Collider 应启用 Is Trigger。", "Run");
            }

            SetTriggerEnabled(false);
            PortalTransitionCoordinator.EnsureVisual(transform);
        }

        private void OnEnable()
        {
            PortalInteractionRegistry.Register(this);
            EventBus<EncounterCompletedEvent>.Subscribe(OnEncounterCompleted);
        }

        private void OnDisable()
        {
            PortalInteractionRegistry.Unregister(this);
            EventBus<EncounterCompletedEvent>.Unsubscribe(OnEncounterCompleted);
        }

        public void BindEncounter(int encounterIndex)
        {
            _encounterIndex = encounterIndex;
        }

        /// <summary>
        /// 返回 true 只表示首次有效进入已提交 Transition 请求，不表示异步 Scene 已加载完成。
        /// 一开始就锁定 _transitionRequested，防止 CharacterController 多 Collider 同帧重复触发。
        /// </summary>
        public bool TryEnter(Collider other)
        {
            if (!_available || _transitionRequested || !IsPlayerCollider(other))
            {
                return false;
            }

            // 新版出口只提交回程，不把“收下全集”当成离开条件。
            // 满背包/法术位时奖励适配器会要求整理；若先领奖，就会弹出背包并阻断真实F回程。
            if (HubPracticeSession.Current != null && HubPracticeSession.Current.OwnsScene(gameObject.scene))
            {
                bool accepted = HubPracticeSession.Current.ReturnToHub(other.GetComponentInParent<CharacterController>()?.transform, transform, PortalId);
                if (accepted) { _transitionRequested = true; _available = false; SetTriggerEnabled(false); }
                return accepted;
            }

            // 没有携带库存上下文就不允许返回，更不能通过出口自动解锁全集。
            return false;
        }

        private void OnEncounterCompleted(EncounterCompletedEvent e)
        {
            if (_available || _transitionRequested || e.EncounterIndex != _encounterIndex)
            {
                return;
            }

            _available = true;
            SetTriggerEnabled(true);
            EventBus<PortalStateChangedEvent>.Publish(new PortalStateChangedEvent
            {
                PortalId = PortalId,
                State = PortalVfxState.Opening
            });
        }

        private bool IsPlayerCollider(Collider other)
        {
            if (other == null)
            {
                return false;
            }

            int layerBit = 1 << other.gameObject.layer;
            return (_playerLayers.value & layerBit) != 0;
        }

        private void SetTriggerEnabled(bool enabledState)
        {
            if (_trigger != null)
            {
                _trigger.enabled = enabledState;
            }
        }
    }
}
