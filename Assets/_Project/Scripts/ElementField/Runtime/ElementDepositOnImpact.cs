using Game.Materials;
using Game.Combat;
using Game.Core;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 把 ProjectileBase 的“命中事件”适配成 ElementField 的“延迟写入请求”。
    ///
    /// 这个组件不直接修改 Grid：Projectile 的 Collision 发生在 Unity Physics 的时间线上，
    /// ElementField 则必须在固定 Simulation Tick 中集中处理写入。通过 ElementWriteRequest Queue
    /// 解耦两条时间线，可以让同一帧内多个 Projectile 的结果仍以稳定、可测试的顺序进入模拟。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ElementDepositOnImpact : MonoBehaviour
    {
        [Header("Deposit Request")]
        [Tooltip("命中后写入统一元素世界的材料身份；具体存储、移动和阻挡由该材料的 Route 与 Behavior 决定。")]
        [SerializeField] private MaterialId _materialKind = MaterialId.Water;

        [Tooltip("本次命中向覆盖范围分配的元素总量。它是总预算，不是每个 Cell 都写入这个值。")]
        [SerializeField, Range(1, ushort.MaxValue)] private int _totalAmount = 220;

        [Tooltip("以命中点为中心的世界空间半径（米）。0 表示只写入命中点所在的 Cell。")]
        [SerializeField, Min(0f)] private float _radius = 0.75f;

        [Tooltip("启用后，越靠近圆心的 Cell 获得越多 Amount；关闭后，覆盖范围内近似均匀分配。")]
        [SerializeField] private bool _useLinearFalloff = true;

        [Tooltip("Water 命中方向转换成 PBF 粒子初速度的倍率。0 表示不继承命中动量；旧 Prefab 默认保持 0，Task 13 再显式配置可玩速度。Legacy Cell 与 Fire 会忽略该向量。")]
        [SerializeField, Min(0f)] private float _fluidInitialSpeed;

        private ProjectileBase _projectile;
        private bool _missingFieldWarningIssued;

        private void Awake()
        {
            // 不使用 RequireComponent(typeof(ProjectileBase))：ProjectileBase 是 abstract，Unity 无法自动添加它。
            // Prefab 可以选择任意具体 Projectile 子类，本组件只依赖其公共 Impacted 事件契约。
            _projectile = GetComponent<ProjectileBase>();
        }

        private void OnEnable()
        {
            // 支持在 Editor 中调整组件顺序或运行时重新启用对象；订阅只在组件有效期间存在。
            if (_projectile == null)
                _projectile = GetComponent<ProjectileBase>();

            if (_projectile != null)
                _projectile.SurfaceImpacted += OnProjectileSurfaceImpacted;
        }

        private void Start()
        {
            // Start 不会在普通 EditMode 构造测试中执行，因此不会污染纯配置测试的日志；
            // 真正进入 Play Mode 时若 Prefab 漏配 Projectile，则给出明确诊断信息。
            if (_projectile == null)
            {
                GameLog.Error(
                    "ElementDepositOnImpact 需要与一个具体的 ProjectileBase 子类挂在同一 GameObject 上。",
                    "ElementField");
            }
        }

        private void OnDisable()
        {
            // Event Subscription 必须成对释放，否则对象禁用后仍可能收到命中回调，形成幽灵写入。
            if (_projectile != null)
                _projectile.SurfaceImpacted -= OnProjectileSurfaceImpacted;
        }

        /// <summary>
        /// 把 Inspector 配置转换为不可变的值类型请求。该方法不访问全局 Runtime，便于 EditMode Test
        /// 独立验证参数规则，也避免 Projectile 必须理解 Grid、Cell 或 Chunk 的内部结构。
        /// </summary>
        public bool TryBuildRequest(Vector3 worldPosition, out ElementWriteRequest request)
        {
            return TryBuildRequest(worldPosition, Vector3.zero, out request);
        }

        public bool TryBuildImpactRequest(
            Vector3 worldPosition,
            Vector3 hitDirection,
            out ElementWriteRequest request)
        {
            return TryBuildImpactRequest(
                worldPosition,
                hitDirection,
                Vector3.zero,
                out request);
        }

        public bool TryBuildImpactRequest(
            Vector3 worldPosition,
            Vector3 hitDirection,
            Vector3 surfaceNormal,
            out ElementWriteRequest request)
        {
            if (!FluidSpawnRequestValidator.IsFinite(worldPosition)
                || !FluidSpawnRequestValidator.IsFinite(hitDirection)
                || !FluidSpawnRequestValidator.IsFinite(surfaceNormal)
                || float.IsNaN(_fluidInitialSpeed)
                || float.IsInfinity(_fluidInitialSpeed))
            {
                request = default;
                return false;
            }

            // Fire 使用 Cell Backend，不继承球体飞行速度；其余非空材料由 Route 决定具体 Liquid Backend。
            // 这里不维护 Water/Poison/Sticky 白名单，避免新增液体时再次遗漏下落初速度。
            Vector3 initialVelocity = _materialKind != MaterialId.Empty
                && _materialKind != MaterialId.Fire
                && hitDirection.sqrMagnitude > 0f
                ? hitDirection.normalized * Mathf.Max(0f, _fluidInitialSpeed)
                : Vector3.zero;
            Vector3 resolvedNormal = surfaceNormal.sqrMagnitude > 1e-12f
                ? surfaceNormal
                : -hitDirection.normalized;
            if (resolvedNormal.sqrMagnitude <= 1e-12f)
            {
                request = default;
                return false;
            }

            return TryBuildRequest(worldPosition, initialVelocity, resolvedNormal, out request);
        }

        private bool TryBuildRequest(
            Vector3 worldPosition,
            Vector3 initialVelocity,
            out ElementWriteRequest request)
        {
            return TryBuildRequest(worldPosition, initialVelocity, Vector3.zero, out request);
        }

        private bool TryBuildRequest(
            Vector3 worldPosition,
            Vector3 initialVelocity,
            Vector3 surfaceNormal,
            out ElementWriteRequest request)
        {
            // 反射、旧 Prefab 或运行时代码仍可能绕过 OnValidate，因此运行时边界也必须防御。
            if (_totalAmount <= 0
                || !FluidSpawnRequestValidator.IsFinite(worldPosition)
                || !FluidSpawnRequestValidator.IsFinite(initialVelocity)
                || !FluidSpawnRequestValidator.IsFinite(surfaceNormal)
                || float.IsNaN(_radius)
                || float.IsInfinity(_radius))
            {
                request = default;
                return false;
            }

            ushort amount = (ushort)Mathf.Clamp(_totalAmount, 1, ushort.MaxValue);
            float radius = Mathf.Max(0f, _radius);
            request = new ElementWriteRequest(
                worldPosition,
                _materialKind,
                amount,
                radius,
                _useLinearFalloff,
                initialVelocity,
                surfaceNormal);
            return true;
        }

        /// <summary>
        /// 尝试把命中转为写入 Queue。这里只入队，不会立即让 Cell 变化；真正修改发生在之后的
        /// ElementField Simulation Tick，因此 Physics 帧率不会直接决定元素反应速度。
        /// </summary>
        public bool TryDepositAt(Vector3 worldPosition)
        {
            if (!TryBuildRequest(worldPosition, out ElementWriteRequest request))
                return false;

            return TryDeposit(in request);
        }

        private void OnProjectileSurfaceImpacted(ProjectileImpactContext context)
        {
            if (!TryBuildImpactRequest(
                    context.Point,
                    context.IncomingDirection,
                    context.SurfaceNormal,
                    out ElementWriteRequest request))
                return;

            TryDeposit(in request);
        }

        private bool TryDeposit(in ElementWriteRequest request)
        {
            if (ElementRuntimeRegistry.ActiveSink == null)
            {
                if (!_missingFieldWarningIssued)
                {
                    _missingFieldWarningIssued = true;
                    GameLog.Warn(
                        "元素沉积已忽略：当前场景中不存在可用的 Element Write Sink。",
                        "ElementField");
                }

                return false;
            }

            return ElementRuntimeRegistry.TryEnqueueWrite(in request);
        }

        private void OnValidate()
        {
            // OnValidate 只改善 Inspector 编辑体验；TryBuildRequest 仍保留运行时校验，不能依赖它保证安全。
            _totalAmount = Mathf.Clamp(_totalAmount, 1, ushort.MaxValue);
            _radius = Mathf.Max(0f, _radius);
            _fluidInitialSpeed = Mathf.Max(0f, _fluidInitialSpeed);
        }
    }
}
