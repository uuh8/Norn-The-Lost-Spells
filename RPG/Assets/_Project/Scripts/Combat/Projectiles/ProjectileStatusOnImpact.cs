using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 为投射物提供一次性的直接命中状态。它与环境沉积分离：Combat 只处理目标状态，
    /// ElementField 仍独立处理落点流体，因此零伤害控制球也能同时拥有即时反馈和持续区域控制。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ProjectileStatusOnImpact : MonoBehaviour
    {
        [SerializeField] private StatusKind _status = StatusKind.Wet;
        [SerializeField, Min(0f)] private float _amount = 15f;

        private ProjectileBase _projectile;

        private void Awake()
        {
            _projectile = GetComponent<ProjectileBase>();
        }

        private void OnEnable()
        {
            if (_projectile == null)
                _projectile = GetComponent<ProjectileBase>();

            if (_projectile != null)
                _projectile.SurfaceImpacted += OnSurfaceImpacted;
        }

        private void OnDisable()
        {
            if (_projectile != null)
                _projectile.SurfaceImpacted -= OnSurfaceImpacted;
        }

        private void OnSurfaceImpacted(ProjectileImpactContext context)
        {
            TryApply(in context);
        }

        /// <summary>
        /// 返回值只表示本次是否确实写入状态，便于 Focused Test 与命中诊断复用同一真实逻辑。
        /// 环境碰撞没有 IDamageable Target，因此自然返回 false，但不会阻止其他 Deposit 订阅者执行。
        /// </summary>
        public bool TryApply(in ProjectileImpactContext context)
        {
            if (_amount <= 0f || context.Target == null)
                return false;

            Component targetComponent = context.Target as Component;
            StatusController controller = targetComponent != null
                ? targetComponent.GetComponent<StatusController>()
                : null;
            if (controller == null)
                return false;

            controller.ApplyStatus(
                _status,
                _amount,
                context.AttackerId,
                context.AttackerTeam);
            return true;
        }

#if UNITY_INCLUDE_TESTS
        public void ConfigureForTests(StatusKind status, float amount)
        {
            _status = status;
            _amount = amount;
        }
#endif
    }
}
