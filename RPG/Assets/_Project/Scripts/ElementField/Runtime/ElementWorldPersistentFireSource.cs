using Game.Materials;
using System.Collections;
using Game.Core;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Game.ElementField
{
    /// <summary>
    /// 关卡 Authoring 使用的持续 Fire Source。
    ///
    /// 它不会把 Fire Cell 标记为永久，也不修改 P6 Solver 的 Decay；而是按低频 Pulse
    /// 持续向唯一 IElementWriteSink 提交普通 Fire Request。Water 仍可按既有 Reaction
    /// 抵消 Fire，Water 被后续 Pulse 消耗后，火源会自然重新点燃。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ElementWorldPersistentFireSource : MonoBehaviour
    {
        public const float MinimumPulseIntervalSeconds = 0.25f;

        private static readonly Color FireGizmoColor = new Color(1f, 0.25f, 0.02f, 0.9f);

        [Header("Persistent Fire Pulse")]
        [Tooltip("每次 Pulse 向覆盖范围分配的 Fire 总量；这是整次写入预算，不是每个 Cell 的 Amount。")]
        [SerializeField, Range(1, ushort.MaxValue)] private int _pulseAmount = 512;

        [Tooltip("以该 GameObject 世界坐标为中心的 Fire 写入半径（米）。")]
        [SerializeField, Min(0f)] private float _radius = 0.35f;

        [Tooltip("启用后中心 Cell 获得更多 Fire；关闭后覆盖范围内近似均匀分配。")]
        [SerializeField] private bool _useLinearFalloff = true;

        [Tooltip("两次 Fire Pulse 的间隔（秒）。使用 Scaled Time，Pause 时会与 ElementWorld Simulation 一起暂停。")]
        [SerializeField, Min(MinimumPulseIntervalSeconds)] private float _pulseInterval = 2f;

        [Header("Runtime Debug (Read Only In Play Mode)")]
        [SerializeField] private int _successfulPulseCount;
        [SerializeField] private int _rejectedPulseCount;

        private WaitForSeconds _pulseWait;
        private Coroutine _pulseRoutine;
        private bool _hasStarted;
        private bool _diagnosticIssued;

        public int SuccessfulPulseCount => _successfulPulseCount;
        public int RejectedPulseCount => _rejectedPulseCount;
        public float PulseIntervalSeconds => SanitizePulseInterval(_pulseInterval);

        private void Awake()
        {
            RebuildPulseWait();
        }

        private void OnEnable()
        {
            // 首次 Enable 发生在 Start 之前，此时其他 GameObject 的 OnEnable 注册顺序不应被假定。
            // 重新启用已经 Start 过的 Source 时才从这里恢复；首次启动统一留给 Start。
            if (_hasStarted)
                BeginPulsing();
        }

        private void Start()
        {
            _hasStarted = true;
            BeginPulsing();
        }

        private void OnDisable()
        {
            if (_pulseRoutine == null)
                return;

            StopCoroutine(_pulseRoutine);
            _pulseRoutine = null;
        }

        /// <summary>
        /// 把 Authoring 参数转换成普通 Fire Request。该方法不读取 Runtime，
        /// 因而可以在 EditMode Test 中独立验证配置边界。
        /// </summary>
        public bool TryBuildPulseRequest(
            Vector3 worldPosition,
            out ElementWriteRequest request)
        {
            if (_pulseAmount <= 0
                || float.IsNaN(_radius)
                || float.IsInfinity(_radius))
            {
                request = default;
                return false;
            }

            request = new ElementWriteRequest(
                worldPosition,
                MaterialId.Fire,
                (ushort)Mathf.Clamp(_pulseAmount, 1, ushort.MaxValue),
                Mathf.Max(0f, _radius),
                _useLinearFalloff);
            return true;
        }

        /// <summary>
        /// 向当前唯一 Runtime 提交一次 Fire Pulse。这里只入队；Cell 修改、Decay、
        /// Water/Fire Reaction 与 Rendering 仍由既有 ElementWorld 管线负责。
        /// </summary>
        public bool TryPulse()
        {
            if (!TryBuildPulseRequest(transform.position, out ElementWriteRequest request))
            {
                _rejectedPulseCount++;
                IssueDiagnosticOnce(
                    "Persistent Fire Source 配置无效：Pulse Amount 必须大于 0，Radius 必须是有限数值。",
                    isError: true);
                return false;
            }

            if (ElementRuntimeRegistry.ActiveSink == null)
            {
                _rejectedPulseCount++;
                IssueDiagnosticOnce(
                    "Persistent Fire Pulse 未提交：场景中没有可用的 Element Write Sink。",
                    isError: false);
                return false;
            }

            if (!ElementRuntimeRegistry.TryEnqueueWrite(in request))
            {
                _rejectedPulseCount++;
                IssueDiagnosticOnce(
                    "Persistent Fire Pulse 被 Element Runtime 拒绝。请检查 Runtime 初始化、Max Pending Writes 与 LastStats.RejectedWrites。",
                    isError: false);
                return false;
            }

            _successfulPulseCount++;
            _diagnosticIssued = false;
            return true;
        }

        [ContextMenu("Pulse Fire Once")]
        private void PulseOnceFromContextMenu()
        {
            TryPulse();
        }

        private void BeginPulsing()
        {
            if (_pulseRoutine != null)
                return;

            // Start 时立即写入，使 Scene 第一轮 Simulation Tick 就能看到 Fire；
            // 后续才等待 Interval，避免进入场景后先空白数秒。
            TryPulse();
            _pulseRoutine = StartCoroutine(PulseRoutine());
        }

        private IEnumerator PulseRoutine()
        {
            while (true)
            {
                // WaitForSeconds 在 Awake/OnValidate 创建并复用；循环本身不会每轮 new YieldInstruction。
                yield return _pulseWait;
                TryPulse();
            }
        }

        private void RebuildPulseWait()
        {
            _pulseInterval = SanitizePulseInterval(_pulseInterval);
            _pulseWait = new WaitForSeconds(_pulseInterval);
        }

        private void IssueDiagnosticOnce(string message, bool isError)
        {
            if (_diagnosticIssued)
                return;

            _diagnosticIssued = true;
            if (isError)
                GameLog.Error(message, "ElementField");
            else
                GameLog.Warn(message, "ElementField");
        }

        private void OnValidate()
        {
            _pulseAmount = Mathf.Clamp(_pulseAmount, 1, ushort.MaxValue);
            if (!float.IsNaN(_radius) && !float.IsInfinity(_radius))
                _radius = Mathf.Max(0f, _radius);

            RebuildPulseWait();
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = FireGizmoColor;
            Gizmos.DrawWireSphere(transform.position, Mathf.Max(0f, _radius));

#if UNITY_EDITOR
            Handles.Label(
                transform.position + Vector3.up * (Mathf.Max(0f, _radius) + 0.25f),
                $"Persistent Fire | Pulse {_pulseAmount} | Every {SanitizePulseInterval(_pulseInterval):0.##}s");
#endif
        }

        private static float SanitizePulseInterval(float interval)
        {
            return float.IsNaN(interval) || float.IsInfinity(interval)
                ? 2f
                : Mathf.Max(MinimumPulseIntervalSeconds, interval);
        }
    }
}
