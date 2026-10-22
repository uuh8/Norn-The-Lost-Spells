using Game.Materials;
using Game.Core;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Game.ElementField
{
    /// <summary>
    /// 关卡设计用的一次性 ElementWorld 写入源。
    ///
    /// 它只负责把 Inspector 配置转换为 ElementWriteRequest 并提交给场景中唯一的
    /// IElementWriteSink；不会直接访问 Chunk、Cell 或 Renderer。真正的写入仍由
    /// ElementWorldRuntime 在固定 Simulation Tick 中处理。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ElementWorldInitialDeposit : MonoBehaviour
    {
        private static readonly Color WaterGizmoColor = new Color(0.1f, 0.55f, 1f, 0.9f);
        private static readonly Color FireGizmoColor = new Color(1f, 0.3f, 0.05f, 0.9f);
        private static readonly Color PoisonGizmoColor = new Color(0.35f, 0.9f, 0.1f, 0.9f);
        private static readonly Color StickyGizmoColor = new Color(1f, 0.55f, 0.05f, 0.9f);
        private static readonly Color InvalidGizmoColor = new Color(1f, 0f, 1f, 0.9f);

        [Header("Initial Deposit")]
        [Tooltip("关卡开始时预置的元素。P7 支持 Water、Fire 和 Poison；液体会路由到共享 GPU PBF Pool。")]
        [SerializeField] private MaterialId _materialKind = MaterialId.Water;

        [Tooltip("向覆盖范围分配的元素总量；这是整次 Deposit 的总预算，不是每个 Cell 的数量。")]
        [SerializeField, Range(1, ushort.MaxValue)] private int _totalAmount = 220;

        [Tooltip("以该 GameObject 世界坐标为圆心的写入半径（米）。0 表示只写入所在 Cell。")]
        [SerializeField, Min(0f)] private float _radius = 0.75f;

        [Tooltip("启用后中心 Cell 权重更高；关闭后覆盖范围内近似均匀分配。")]
        [SerializeField] private bool _useLinearFalloff = true;

        [Tooltip("启用后在 Start 自动提交一次。关闭时可由其他一次性关卡流程显式调用 TryQueueDeposit。")]
        [SerializeField] private bool _depositOnStart = true;

        private bool _hasSubmitted;
        private bool _diagnosticIssued;

        /// <summary>
        /// 表示本轮配置是否已经成功进入唯一 Runtime 的写入队列。
        /// 注意：入队成功不等于 Cell 已在同一帧发生变化，应用发生在后续 Simulation Tick。
        /// </summary>
        public bool HasSubmitted => _hasSubmitted;

        private void Start()
        {
            // ElementWorldRuntime 在 Awake/OnEnable 注册 Sink，所有组件的 Start 都发生在它们之后，
            // 因此这里无需延迟一帧，也不会因 Script Execution Order 产生偶发漏写。
            if (_depositOnStart)
                TryQueueDeposit();
        }

        /// <summary>
        /// 把 Authoring 参数转换成不可变 Request。保持为纯配置边界，便于 EditMode Test
        /// 验证数据，而不必创建完整 ElementWorldRuntime。
        /// </summary>
        public bool TryBuildRequest(Vector3 worldPosition, out ElementWriteRequest request)
        {
            if (!IsSupportedMaterial(_materialKind)
                || _totalAmount <= 0
                || float.IsNaN(_radius)
                || float.IsInfinity(_radius))
            {
                request = default;
                return false;
            }

            request = new ElementWriteRequest(
                worldPosition,
                _materialKind,
                (ushort)Mathf.Clamp(_totalAmount, 1, ushort.MaxValue),
                Mathf.Max(0f, _radius),
                _useLinearFalloff);
            return true;
        }

        /// <summary>
        /// 尝试把当前 Transform 位置提交一次。失败不会消耗 One-shot 状态，
        /// 因此修正缺失 Runtime 或 Queue 容量问题后仍可显式重试。
        /// </summary>
        public bool TryQueueDeposit()
        {
            if (_hasSubmitted)
                return false;

            if (!TryBuildRequest(transform.position, out ElementWriteRequest request))
            {
                IssueDiagnosticOnce(
                    "Initial Deposit 配置无效：Material 必须是已定义的非 Empty 材料，Amount 必须大于 0，Radius 必须是有限数值。",
                    isError: true);
                return false;
            }

            if (ElementRuntimeRegistry.ActiveSink == null)
            {
                IssueDiagnosticOnce(
                    "Initial Deposit 未提交：场景中没有可用的 Element Write Sink。请检查 ElementWorldRuntime 是否启用并完成初始化。",
                    isError: false);
                return false;
            }

            if (!ElementRuntimeRegistry.TryEnqueueWrite(in request))
            {
                // Registry 已保证不会偷偷创建第二套 Field。这里的常见原因是 Runtime 未初始化，
                // 或 Max Pending Writes 已满；真正应用时的 Resident 上限拒绝仍记录在 Runtime LastStats。
                IssueDiagnosticOnce(
                    "Initial Deposit 被 Element Runtime 拒绝。请检查 Runtime 初始化状态、Max Pending Writes 与 LastStats.RejectedWrites。",
                    isError: false);
                return false;
            }

            _hasSubmitted = true;
            _diagnosticIssued = false;
            return true;
        }

        /// <summary>
        /// 显式开启新一轮并立即提交一次，供关卡重置/Debug Context Menu 使用。
        /// 普通 Gameplay 不应每帧调用，否则会不断增加元素。
        /// </summary>
        [ContextMenu("Reset And Queue Initial Deposit")]
        public void ResetAndQueueDeposit()
        {
            _hasSubmitted = false;
            _diagnosticIssued = false;
            TryQueueDeposit();
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
            _totalAmount = Mathf.Clamp(_totalAmount, 1, ushort.MaxValue);
            if (!float.IsNaN(_radius) && !float.IsInfinity(_radius))
                _radius = Mathf.Max(0f, _radius);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = _materialKind == MaterialId.Water
                ? WaterGizmoColor
                : _materialKind == MaterialId.Fire
                    ? FireGizmoColor
                    : _materialKind == MaterialId.Poison
                        ? PoisonGizmoColor
                        : _materialKind == MaterialId.Sticky
                            ? StickyGizmoColor
                            : InvalidGizmoColor;
            Gizmos.DrawWireSphere(transform.position, Mathf.Max(0f, _radius));

#if UNITY_EDITOR
            // Handles.Label 只参与 Editor Scene View，不会进入 Player Build 或 Gameplay 热路径。
            Handles.Label(
                transform.position + Vector3.up * (Mathf.Max(0f, _radius) + 0.25f),
                $"{_materialKind} | Amount {_totalAmount} | Radius {_radius:0.##}m");
#endif
        }

        private static bool IsSupportedMaterial(MaterialId materialKind)
        {
            // MaterialId 使用连续稳定 byte 协议；真正的 Backend 支持性仍由 Runtime Route 判定。
            return materialKind >= MaterialId.Water && materialKind <= MaterialId.Sticky;
        }
    }
}
