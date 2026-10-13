using System.Collections;
using Game.Core;
using Game.Run;
using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// Player 的 Portal Transit 表现实现。
    /// 只移动 Visual Root，刻意不移动承载 CharacterController 与 CameraRoot 的 Gameplay Root。
    /// </summary>
    public sealed class PlayerPortalTraveler : MonoBehaviour, IPortalTraveler, IPortalTransitRestorer
    {
        [SerializeField]
        [Tooltip("只参与吸入表现的角色模型根；不要绑定 WizardPlayer 或 CameraRoot。")]
        private Transform _visualRoot;
        [SerializeField, Range(0f, 1f)]
        [Tooltip("到达 Portal 时相对于初始 Local Scale 的倍率。0.1 表示缩小到十分之一。")]
        private float _targetScaleMultiplier = 0.1f;

        private Coroutine _transitRoutine;
        private bool _isTransiting;
        private Vector3 _originalLocalPosition, _originalScale;
        private bool _hasOriginal;
        private void Awake()
        {
            if (_visualRoot == null) _visualRoot = GetComponentInChildren<Animator>(true)?.transform;
        }

        public bool IsTransiting => _isTransiting;

        public bool BeginPortalTransit(Transform destination, float duration)
        {
            if (_isTransiting)
            {
                return false;
            }

            if (_visualRoot == null || destination == null)
            {
                GameLog.Warn(
                    "PlayerPortalTraveler 缺少 Visual Root 或 Destination，无法播放角色吸入表现。",
                    "Character");
                return false;
            }

            _isTransiting = true;
            _originalLocalPosition = _visualRoot.localPosition; _originalScale = _visualRoot.localScale; _hasOriginal = true;
            Vector3 startPosition = _visualRoot.position;
            Vector3 startScale = _visualRoot.localScale;
            Vector3 targetScale =
                startScale * Mathf.Clamp01(_targetScaleMultiplier);

            if (duration <= 0f)
            {
                CompleteTransit(destination.position, targetScale);
                return true;
            }

            _transitRoutine = StartCoroutine(TransitRoutine(
                destination,
                duration,
                startPosition,
                startScale,
                targetScale));
            return true;
        }

        private IEnumerator TransitRoutine(
            Transform destination,
            float duration,
            Vector3 startPosition,
            Vector3 startScale,
            Vector3 targetScale)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                // Gameplay 在 Portal Close 期间已暂停；unscaledDeltaTime 让纯表现继续播放。
                elapsed = Mathf.Min(duration, elapsed + Time.unscaledDeltaTime);
                float normalizedTime = elapsed / duration;
                float easedTime = Mathf.SmoothStep(0f, 1f, normalizedTime);

                _visualRoot.position = Vector3.LerpUnclamped(
                    startPosition,
                    destination.position,
                    easedTime);
                _visualRoot.localScale = Vector3.LerpUnclamped(
                    startScale,
                    targetScale,
                    easedTime);
                yield return null;
            }

            // 最后一帧写精确终值，避免逐帧浮点累积留下可见缝隙。
            CompleteTransit(destination.position, targetScale);
        }

        private void CompleteTransit(
            Vector3 destinationPosition,
            Vector3 targetScale)
        {
            _visualRoot.position = destinationPosition;
            _visualRoot.localScale = targetScale;
            _visualRoot.gameObject.SetActive(false);
            _transitRoutine = null;
            _isTransiting = false;
        }

        private void OnDisable()
        {
            if (_transitRoutine != null)
            {
                StopCoroutine(_transitRoutine);
                _transitRoutine = null;
            }

            _isTransiting = false;
            RestorePortalVisual();
        }
        public void RestorePortalVisual()
        {
            if (!_hasOriginal || _visualRoot == null) return;
            if (_transitRoutine != null) StopCoroutine(_transitRoutine);
            _transitRoutine = null; _isTransiting = false;
            _visualRoot.localPosition = _originalLocalPosition; _visualRoot.localScale = _originalScale;
            _visualRoot.gameObject.SetActive(true); _hasOriginal = false;
        }
    }
}
