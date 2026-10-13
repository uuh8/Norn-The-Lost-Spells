using System.Collections;
using Game.Core;
using Game.Skills;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 地图一出口的 Gameplay Authority。Encounter 未完成时 Collider 禁用；
    /// 有效进入时先补齐法术、再更新跨 Scene Session，最后才请求表现层 Fade/Load。
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public sealed class StageExitPortal : MonoBehaviour, IPortalInteractionTarget
    {
        [SerializeField] private RunSpellSession _session;
        [SerializeField] private RunSessionCoordinator _sessionCoordinator;
        [SerializeField]
        [Tooltip("Portal Close 播放期间立即冻结 Gameplay；Fade Controller 会继续持有同一个 SceneTransition Reason。")]
        private RunPauseCoordinator _pauseCoordinator;
        [SerializeField] private SpellLibrary _fullLibrary;
        [SerializeField, Min(0)] private int _encounterIndex;
        [SerializeField] private LayerMask _playerLayers;
        [SerializeField] private string _targetSceneName = "P8_BossField";
        [SerializeField, Min(0f)]
        [Tooltip("PortalBlueClose 的主要关闭动画时长；结束后才开始 Fade。使用 unscaled time，暂停不会卡住等待。")]
        private float _closeDuration = 1f;
        [SerializeField]
        [Tooltip("角色视觉被吸入的世界空间终点。显式 Anchor 可避免假设 Portal 模型沿 local X 或 local Z 朝向。")]
        private Transform _travelerAnchor;
        [SerializeField, Min(0f)]
        [Tooltip("只移动角色视觉根的吸入时长；使用 unscaled time，可与暂停后的 Portal Close 同时播放。")]
        private float _travelerTransitDuration = 0.6f;

        private Collider _trigger;
        private bool _available;
        private bool _transitionRequested;
        private bool _transitionEventPublished;

        public int PortalId => gameObject.GetInstanceID();
        public bool IsAvailable => _available;
        public Transform PortalTransform => transform;
        public string InteractionLabel => "F 返回据点";
        public bool CanInteract(Collider actor) => _available && !_transitionRequested && IsPlayerCollider(actor) && PortalInteractionRegistry.Contains(_trigger, actor);
        public bool Interact(Collider actor) => CanInteract(actor) && TryEnter(actor);
        public bool IsTransitionRequested => _transitionRequested;
        public Transform TravelerAnchor
        {
            get => _travelerAnchor;
            set => _travelerAnchor = value;
        }
        public float TravelerTransitDuration
        {
            get => _travelerTransitDuration;
            set => _travelerTransitDuration = Mathf.Max(0f, value);
        }
        public float CloseDuration
        {
            get => _closeDuration;
            set => _closeDuration = Mathf.Max(0f, value);
        }

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
            if (_transitionRequested && !_transitionEventPublished)
            {
                // MonoBehaviour 或 GameObject 被 Disable 时，挂在它上面的 Coroutine 会被终止。
                // 这条日志用于区分“EventBus 丢失”与“发布事件之前 Coroutine 已被 Unity 取消”。
                GameLog.Warn(
                    "StageExitPortal 在 Scene Transition Event 发布前被 Disable；Close 等待 Coroutine 已中止。",
                    "Run");
            }
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
                if (accepted) { _transitionRequested = true; _available = false; _transitionEventPublished = true; SetTriggerEnabled(false); }
                return accepted;
            }

            RunSpellSession session = ResolveSession();
            RunSessionCoordinator coordinator = ResolveCoordinator();
            SpellLibrary fullLibrary = _fullLibrary != null
                ? _fullLibrary
                : session != null ? session.FullLibrary : null;

            if (session == null ||
                coordinator == null ||
                _pauseCoordinator == null ||
                fullLibrary == null)
            {
                GameLog.Error(
                    "StageExitPortal 缺少 RunSpellSession、RunSessionCoordinator、RunPauseCoordinator 或 Full SpellLibrary。",
                    "Run");
                return false;
            }

            if (string.IsNullOrWhiteSpace(_targetSceneName))
            {
                GameLog.Error("StageExitPortal 的 Target Scene Name 为空。", "Run");
                return false;
            }

            _transitionRequested = true;
            _available = false;
            SetTriggerEnabled(false);
            GameLog.Info(
                $"StageExitPortal accepted Player; close wait={_closeDuration:0.###}s, target='{_targetSceneName}'.",
                "Run");

            // 顺序是可靠性边界：即使玩家跳过世界 Reward，Scene 请求发出前也必须完成全解锁。
            session.EnsureAllSpellsUnlocked(fullLibrary);
            coordinator.PrepareBossFieldEntry();

            // Close 播放在 Fade 之前，因此不能依赖 Fade Canvas 阻挡 Input。
            // 与 Fade Controller 共用 SceneTransition Reason，直到加载边界统一清理。
            _pauseCoordinator.RequestPause(RunPauseReason.SceneTransition);
            TryBeginTravelerTransit(other);
            EventBus<PortalStateChangedEvent>.Publish(new PortalStateChangedEvent
            {
                PortalId = PortalId,
                State = PortalVfxState.Closing
            });
            StartCoroutine(CloseThenRequestTransition());
            GameLog.Info(
                "StageExitPortal started CloseThenRequestTransition Coroutine.",
                "Run");
            return true;
        }

        private void TryBeginTravelerTransit(Collider other)
        {
            if (_travelerAnchor == null && transform.Find("UnifiedPortalVisual") == null)
            {
                GameLog.Warn(
                    "StageExitPortal 缺少 Traveler Anchor；将跳过角色吸入表现并继续 Close/Fade。",
                    "Run");
                return;
            }

            IPortalTraveler traveler = other.GetComponentInParent<IPortalTraveler>();
            if (traveler == null)
            {
                GameLog.Warn(
                    "Player 层 Collider 的父级缺少 IPortalTraveler；将跳过角色吸入表现并继续 Close/Fade。",
                    "Run");
                return;
            }

            if (!traveler.BeginPortalTransit(
                    PortalTransitionCoordinator.TransitDestination(transform, _travelerAnchor),
                    _travelerTransitDuration))
            {
                GameLog.Warn(
                    "IPortalTraveler 拒绝了 Transit 请求；将继续 Portal Close/Fade 主流程。",
                    "Run");
            }
        }

        private IEnumerator CloseThenRequestTransition()
        {
            if (_closeDuration > 0f)
            {
                // Gameplay 已暂停，必须使用 Realtime 等待；WaitForSeconds 会随 timeScale=0 永久停住。
                yield return new WaitForSecondsRealtime(_closeDuration);
            }

            _transitionEventPublished = true;
            GameLog.Info(
                $"StageExitPortal publishing SceneTransitionRequestedEvent for '{_targetSceneName}'.",
                "Run");
            EventBus<SceneTransitionRequestedEvent>.Publish(
                new SceneTransitionRequestedEvent
                {
                    SceneName = _targetSceneName
                });
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

        private RunSpellSession ResolveSession()
        {
            if (_session == null)
            {
                _session = RunSpellSession.Current;
            }

            return _session;
        }

        private RunSessionCoordinator ResolveCoordinator()
        {
            if (_sessionCoordinator == null)
            {
                _sessionCoordinator = RunSessionCoordinator.Current;
            }

            return _sessionCoordinator;
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
