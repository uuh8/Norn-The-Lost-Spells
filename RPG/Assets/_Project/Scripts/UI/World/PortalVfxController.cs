using System.Collections;
using Game.Core;
using Game.Run;
using UnityEngine;

namespace Game.UI
{
    /// <summary>
    /// Portal 的纯表现 Adapter：复用 Open/Idle/Close Prefab，但不拥有 Trigger 或 Scene Load 权限。
    /// 三个视觉实例只在 Awake 创建一次，状态切换仅 SetActive，避免重复 Instantiate。
    /// </summary>
    public sealed class PortalVfxController : MonoBehaviour
    {
        [SerializeField] private StageExitPortal _portal;
        [SerializeField] private Transform _visualRoot;
        [SerializeField] private GameObject _openPrefab;
        [SerializeField] private GameObject _idlePrefab;
        [SerializeField] private GameObject _idleVisual;
        [SerializeField] private GameObject _closePrefab;
        [SerializeField, Min(0f)]
        [Tooltip("略早于 Open 粒子的终止帧切到循环 Idle，可避免终止 Burst 与 Idle Prewarm 叠成亮度闪变。")]
        private float _openDuration = 0.8f;

        private GameObject _openInstance;
        private GameObject _idleInstance;
        private GameObject _closeInstance;
        private Coroutine _openRoutine;
        private IPortalVisualSource _genericPortal;
        private int PortalId => _portal != null ? _portal.PortalId : _genericPortal?.PortalId ?? 0;

        private void Awake()
        {
            _genericPortal = GetComponentInParent<IPortalVisualSource>();
            if (_portal == null) _portal = GetComponentInParent<StageExitPortal>();
            Transform parent = _visualRoot != null ? _visualRoot : transform;
            _openInstance = CreateVisual(_openPrefab, parent);
            _idleInstance = _idleVisual != null ? _idleVisual : CreateVisual(_idlePrefab, parent);
            _closeInstance = CreateVisual(_closePrefab, parent);
            ConfigureUnscaledParticleTime(_closeInstance);
            HideAll();
        }

        private void OnEnable()
        {
            EventBus<PortalStateChangedEvent>.Subscribe(OnPortalStateChanged);
            if (_genericPortal != null && _portal == null) BeginOpening();
        }

        private void Start()
        {
            // 表现组件可能晚于 Gameplay Portal 启用；Start 时读取只读状态可补回错过的 Opening Event。
            if (_portal != null && _portal.IsAvailable || _portal == null && _genericPortal != null)
            {
                BeginOpening();
            }
        }

        private void OnDisable()
        {
            EventBus<PortalStateChangedEvent>.Unsubscribe(OnPortalStateChanged);
            StopOpeningRoutine();
        }

        private void OnPortalStateChanged(PortalStateChangedEvent portalEvent)
        {
            if (PortalId == 0 || portalEvent.PortalId != PortalId)
            {
                return;
            }

            switch (portalEvent.State)
            {
                case PortalVfxState.Opening:
                    BeginOpening();
                    break;
                case PortalVfxState.Idle:
                    StopOpeningRoutine();
                    ShowOnly(_idleInstance);
                    break;
                case PortalVfxState.Closing:
                    // Closing 取得视觉所有权后，旧的 Open Coroutine 不能再于稍后切回 Idle。
                    StopOpeningRoutine();
                    ShowOnly(_closeInstance);
                    break;
            }
        }

        private void BeginOpening()
        {
            StopOpeningRoutine();
            _openRoutine = StartCoroutine(OpenThenIdleRoutine());
        }

        private void StopOpeningRoutine()
        {
            if (_openRoutine == null)
            {
                return;
            }

            StopCoroutine(_openRoutine);
            _openRoutine = null;
        }

        private IEnumerator OpenThenIdleRoutine()
        {
            ShowOnly(_openInstance);
            if (_openDuration > 0f)
            {
                yield return new WaitForSecondsRealtime(_openDuration);
            }

            ShowOnly(_idleInstance);
            _openRoutine = null;
        }

        private static GameObject CreateVisual(GameObject prefab, Transform parent)
        {
            if (prefab == null)
            {
                return null;
            }

            GameObject instance = Instantiate(prefab, parent, false);
            instance.SetActive(false);
            return instance;
        }

        private static void ConfigureUnscaledParticleTime(GameObject visual)
        {
            if (visual == null)
            {
                return;
            }

            // Portal Close 播放期间 Gameplay 的 timeScale 为 0。逐个改用 unscaled time，
            // 让一次性粒子继续推进；只在 Awake 分配数组，不进入逐帧 Hot Path。
            ParticleSystem[] particles =
                visual.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < particles.Length; i++)
            {
                ParticleSystem.MainModule main = particles[i].main;
                main.useUnscaledTime = true;
            }
        }

        private void HideAll()
        {
            SetActive(_openInstance, false);
            SetActive(_idleInstance, false);
            SetActive(_closeInstance, false);
        }

        private void ShowOnly(GameObject activeInstance)
        {
            SetActive(_openInstance, ReferenceEquals(activeInstance, _openInstance));
            SetActive(_idleInstance, ReferenceEquals(activeInstance, _idleInstance));
            SetActive(_closeInstance, ReferenceEquals(activeInstance, _closeInstance));
        }

        private static void SetActive(GameObject target, bool active)
        {
            if (target != null)
            {
                target.SetActive(active);
            }
        }
    }
}
