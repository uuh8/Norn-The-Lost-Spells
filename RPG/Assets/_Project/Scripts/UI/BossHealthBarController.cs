using Game.Combat;
using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// P8 Boss HUD 的只读 Presentation Adapter。
    /// 它只消费跨模块 Event Snapshot，不引用 WizardBossController 或 BossDefinition，
    /// 因此移动 Marker、替换 Text/Image 都不会反向改变 Gameplay Phase。
    /// </summary>
    public sealed class BossHealthBarController : MonoBehaviour
    {
        [Header("Root")]
        [SerializeField] private GameObject _hudRoot;

        [Header("Text")]
        [SerializeField] private Text _bossNameText;
        [SerializeField] private Text _phaseText;

        [Header("Health")]
        [SerializeField] private Image _healthFill;
        [SerializeField] private RectTransform _phase2ThresholdMarker;
        [SerializeField] private RectTransform _phase3ThresholdMarker;

        private RectTransform _healthBarRoot;
        private int _bossId;
        private float _maxHp;
        private bool _isInitialized;

        private const float ThresholdLabelGap = 4f;

        private void Awake()
        {
            ResolveNamedHierarchy();
            ApplyBossSceneUiPolicy();
            ConfigureRuntimePresentation();
        }

        private void OnEnable()
        {
            EventBus<BossHudInitializedEvent>.Subscribe(OnInitialized);
            EventBus<DamageReceivedEvent>.Subscribe(OnDamageReceived);
            EventBus<BossPhaseChangedEvent>.Subscribe(OnPhaseChanged);
            EventBus<DeathEvent>.Subscribe(OnDeath);
            EventBus<RunStateChangedEvent>.Subscribe(OnRunStateChanged);
        }

        private void Start()
        {
            SetVisible(false);
        }

        private void OnDisable()
        {
            EventBus<BossHudInitializedEvent>.Unsubscribe(OnInitialized);
            EventBus<DamageReceivedEvent>.Unsubscribe(OnDamageReceived);
            EventBus<BossPhaseChangedEvent>.Unsubscribe(OnPhaseChanged);
            EventBus<DeathEvent>.Unsubscribe(OnDeath);
            EventBus<RunStateChangedEvent>.Unsubscribe(OnRunStateChanged);
            _isInitialized = false;
            _bossId = 0;
            _maxHp = 0f;
        }

        private void OnInitialized(BossHudInitializedEvent initializedEvent)
        {
            if (initializedEvent.BossId == 0 ||
                initializedEvent.MaxHp <= 0f)
            {
                GameLog.Warn(
                    "Boss HUD 收到无效初始化快照，已保持隐藏。",
                    "UI");
                return;
            }

            _bossId = initializedEvent.BossId;
            _maxHp = initializedEvent.MaxHp;
            _isInitialized = true;

            if (_bossNameText != null)
            {
                _bossNameText.text =
                    string.IsNullOrWhiteSpace(initializedEvent.DisplayName)
                        ? "Boss"
                        : initializedEvent.DisplayName;
            }

            SetPhase(initializedEvent.CurrentPhase);
            SetHealth(initializedEvent.CurrentHp);
            PositionThresholdMarker(
                _phase2ThresholdMarker,
                initializedEvent.Phase2Threshold);
            RefreshThresholdLabel(
                _phase2ThresholdMarker,
                initializedEvent.Phase2Threshold);
            PositionThresholdMarker(
                _phase3ThresholdMarker,
                initializedEvent.Phase3Threshold);
            RefreshThresholdLabel(
                _phase3ThresholdMarker,
                initializedEvent.Phase3Threshold);
            SetVisible(true);
        }

        private void OnDamageReceived(DamageReceivedEvent damageEvent)
        {
            if (!_isInitialized || damageEvent.TargetId != _bossId)
            {
                return;
            }

            SetHealth(damageEvent.RemainingHp);
        }

        private void OnPhaseChanged(BossPhaseChangedEvent phaseEvent)
        {
            if (!_isInitialized || phaseEvent.BossId != _bossId)
            {
                return;
            }

            SetPhase(phaseEvent.CurrentPhase);
        }

        private void OnDeath(DeathEvent deathEvent)
        {
            if (_isInitialized && deathEvent.TargetId == _bossId)
            {
                SetVisible(false);
            }
        }

        private void OnRunStateChanged(RunStateChangedEvent runEvent)
        {
            if (runEvent.CurrentState == RunState.Completed ||
                runEvent.CurrentState == RunState.Failed)
            {
                SetVisible(false);
            }
        }

        private void SetHealth(float currentHp)
        {
            if (_healthFill != null)
            {
                _healthFill.fillAmount =
                    Mathf.Clamp01(currentHp / _maxHp);
            }
        }

        private void SetPhase(byte phase)
        {
            if (_phaseText != null)
            {
                _phaseText.text = $"Phase {Mathf.Max(1, phase)}";
            }
        }

        private void PositionThresholdMarker(
            RectTransform marker,
            float healthRatio)
        {
            if (marker == null || _healthFill == null)
            {
                return;
            }

            RectTransform fillRect = _healthFill.rectTransform;
            RectTransform markerParent = marker.parent as RectTransform;
            if (markerParent == null)
            {
                return;
            }

            // 不能直接把 Marker 的 Anchor X 设为 0.35 / 0.70：
            // Anchor 百分比针对父 Rect，而可见的 Fill 可能带左右边框和留白。
            // 这里把 Fill 左右端点转换到共同父坐标系，再在真实血条宽度上插值。
            Vector3 fillLeftWorld = fillRect.TransformPoint(
                new Vector3(fillRect.rect.xMin, fillRect.rect.center.y));
            Vector3 fillRightWorld = fillRect.TransformPoint(
                new Vector3(fillRect.rect.xMax, fillRect.rect.center.y));
            Vector3 fillLeftLocal =
                markerParent.InverseTransformPoint(fillLeftWorld);
            Vector3 fillRightLocal =
                markerParent.InverseTransformPoint(fillRightWorld);
            Vector3 targetLocal = Vector3.Lerp(
                fillLeftLocal,
                fillRightLocal,
                Mathf.Clamp01(healthRatio));

            // 固定为父 Rect 中心 Anchor 后，anchoredPosition 只表达目标点相对
            // 父中心的偏移。X/Y 都由 Runtime 几何计算唯一决定，不再混入
            // Scene 中遗留的 authored offset。
            marker.anchorMin = new Vector2(0.5f, 0.5f);
            marker.anchorMax = new Vector2(0.5f, 0.5f);
            marker.pivot = new Vector2(0.5f, 0.5f);
            Vector2 parentCenter = markerParent.rect.center;
            marker.anchoredPosition = new Vector2(
                targetLocal.x - parentCenter.x,
                targetLocal.y - parentCenter.y);

            StackThresholdMarkerChildren(marker);
        }

        private static void StackThresholdMarkerChildren(RectTransform marker)
        {
            RectTransform markerLine = null;
            RectTransform markerLabel = null;

            for (int i = 0; i < marker.childCount; i++)
            {
                RectTransform child = marker.GetChild(i) as RectTransform;
                if (child == null)
                {
                    continue;
                }

                if (child.GetComponent<Text>() != null)
                {
                    markerLabel = child;
                }
                else if (child.GetComponent<Image>() != null)
                {
                    markerLine = child;
                }
            }

            if (markerLine != null)
            {
                markerLine.anchorMin = new Vector2(0.5f, 0.5f);
                markerLine.anchorMax = new Vector2(0.5f, 0.5f);
                markerLine.pivot = new Vector2(0.5f, 0.5f);
                markerLine.anchoredPosition = Vector2.zero;
            }

            if (markerLabel == null)
            {
                return;
            }

            // Label 的 Pivot 放在顶部中央，其坐标就代表文字上沿。
            // 再减去 X 图案的半显示高度与固定间距，文字稳定排在 X 下方。
            float markerHalfHeight = markerLine != null
                ? markerLine.rect.height *
                  Mathf.Abs(markerLine.localScale.y) * 0.5f
                : 0f;
            markerLabel.anchorMin = new Vector2(0.5f, 0.5f);
            markerLabel.anchorMax = new Vector2(0.5f, 0.5f);
            markerLabel.pivot = new Vector2(0.5f, 1f);
            markerLabel.anchoredPosition =
                new Vector2(0f, -markerHalfHeight - ThresholdLabelGap);
        }

        private void ResolveNamedHierarchy()
        {
            Transform hudTransform = _hudRoot != null
                ? _hudRoot.transform
                : transform.Find("BossHUDRoot");
            if (hudTransform == null)
            {
                GameLog.Warn(
                    "BossHealthBarController 找不到 BossHUDRoot，Boss HUD 无法初始化。",
                    "UI");
                return;
            }

            _hudRoot = hudTransform.gameObject;
            if (_bossNameText == null)
            {
                _bossNameText = FindComponent<Text>(
                    hudTransform,
                    "BossNameText");
            }

            if (_phaseText == null)
            {
                _phaseText = FindComponent<Text>(
                    hudTransform,
                    "PhaseText");
            }

            Transform healthRootTransform = hudTransform.Find("HealthBarRoot");
            _healthBarRoot =
                healthRootTransform as RectTransform;
            if (_healthBarRoot == null)
            {
                GameLog.Warn(
                    "BossHealthBarController 找不到 HealthBarRoot，无法建立百分比坐标系。",
                    "UI");
                return;
            }

            if (_healthFill == null)
            {
                _healthFill = FindComponent<Image>(
                    healthRootTransform,
                    "HealthFill");
            }

            if (_phase2ThresholdMarker == null)
            {
                _phase2ThresholdMarker = FindRectTransform(
                    healthRootTransform,
                    "Phase2MarkerRoot");
            }

            if (_phase3ThresholdMarker == null)
            {
                _phase3ThresholdMarker = FindRectTransform(
                    healthRootTransform,
                    "Phase3MarkerRoot");
            }
        }

        private void ApplyBossSceneUiPolicy()
        {
            Transform canvasTransform = transform.parent;
            if (canvasTransform == null)
            {
                return;
            }

            Transform demoRunUiRoot =
                canvasTransform.Find("DemoRunUIRoot");
            if (demoRunUiRoot == null)
            {
                return;
            }

            // 共享容器还持有暂停/通知等组件；只隐藏当前 Boss 不使用的遭遇 HUD，
            // 不整体停用容器，以免 Unity 停止其余子节点的生命周期回调。
            demoRunUiRoot.gameObject.SetActive(true);
            SetChildActive(demoRunUiRoot, "GameplayHUD", false);
            SetChildActive(demoRunUiRoot, "NotificationRoot", false);
        }

        private void ConfigureRuntimePresentation()
        {
            if (_bossNameText != null)
            {
                _bossNameText.raycastTarget = false;
            }

            if (_healthFill != null)
            {
                // Filled Image 是血量变化所需的运行时语义；RectTransform、Sprite、
                // 颜色和材质仍由 Editor Authoring 决定。
                _healthFill.type = Image.Type.Filled;
                _healthFill.fillMethod = Image.FillMethod.Horizontal;
                _healthFill.fillOrigin = 0;
                _healthFill.raycastTarget = false;
            }

            if (_phaseText != null)
            {
                _phaseText.raycastTarget = false;
            }

            DisableMarkerRaycasts(_phase2ThresholdMarker);
            DisableMarkerRaycasts(_phase3ThresholdMarker);
        }

        private static void RefreshThresholdLabel(
            RectTransform marker,
            float healthRatio)
        {
            if (marker == null)
            {
                return;
            }

            string percentage =
                $"{Mathf.RoundToInt(Mathf.Clamp01(healthRatio) * 100f)}%";
            for (int i = 0; i < marker.childCount; i++)
            {
                Text label = marker.GetChild(i).GetComponent<Text>();
                if (label != null)
                {
                    label.text = percentage;
                    return;
                }
            }
        }

        private static void DisableMarkerRaycasts(RectTransform marker)
        {
            if (marker == null)
            {
                return;
            }

            // Boss HUD 仅用于展示；关闭 Graphic Raycast 可避免它遮挡准星、
            // Pause Menu 或后续加入的交互 UI。这里不修改任何视觉 Authoring 值。
            for (int i = 0; i < marker.childCount; i++)
            {
                Graphic graphic = marker.GetChild(i).GetComponent<Graphic>();
                if (graphic != null)
                {
                    graphic.raycastTarget = false;
                }
            }
        }

        private static T FindComponent<T>(
            Transform parent,
            string childName)
            where T : Component
        {
            Transform child = parent != null
                ? parent.Find(childName)
                : null;
            return child != null
                ? child.GetComponent<T>()
                : null;
        }

        private static RectTransform FindRectTransform(
            Transform parent,
            string childName)
        {
            return parent != null
                ? parent.Find(childName) as RectTransform
                : null;
        }

        private static void SetChildActive(
            Transform parent,
            string childName,
            bool active)
        {
            Transform child = parent.Find(childName);
            if (child != null)
            {
                child.gameObject.SetActive(active);
            }
        }

        private void SetVisible(bool visible)
        {
            if (_hudRoot != null)
            {
                _hudRoot.SetActive(visible);
            }
        }
    }
}
