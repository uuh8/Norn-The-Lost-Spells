using System.Collections.Generic;
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 元素反应范围伤害的 Unity Physics 执行层。Runtime 只产生 DamageCommand，
    /// Resolver 才查询场景并进入统一 IDamageable 漏斗，保持纯规则与 Unity API 分离。
    /// </summary>
    public sealed class AreaReactionDamageResolver
    {
        // NonAlloc API 需要调用方提供数组；容量满时会截断本次查询，这是固定性能预算的取舍。
        private const int MaxColliders = 32;

        private readonly Collider[] _colliders = new Collider[MaxColliders];
        private readonly HashSet<int> _damagedTargetIds = new HashSet<int>(MaxColliders);

        public void Resolve(in ReactionDamageCommand command, Vector3 center)
        {
            if (command.Amount <= 0f || command.Radius <= 0f)
                return;

            _damagedTargetIds.Clear();

            // OverlapSphereNonAlloc 把命中写入复用数组，不像 OverlapSphere 那样每次返回新数组并产生 GC Alloc。
            int count = Physics.OverlapSphereNonAlloc(
                center,
                command.Radius,
                _colliders,
                Physics.AllLayers,
                QueryTriggerInteraction.Collide);

            for (int i = 0; i < count; i++)
            {
                Collider hit = _colliders[i];
                if (hit == null)
                    continue;

                IDamageable target = hit.GetComponentInParent<IDamageable>();
                if (target == null || !target.IsAlive || target.TeamId == command.Source.Team)
                    continue;

                // Physics 命中的是 Collider；用承载 IDamageable 的 Component 身份去重，
                // 避免一个角色的胶囊体、受击盒等多个 Collider 重复结算同次毒爆。
                Component targetComponent = target as Component;
                if (targetComponent == null || !_damagedTargetIds.Add(targetComponent.GetInstanceID()))
                    continue;

                Vector3 direction = targetComponent.transform.position - center;

                // sqrMagnitude 避免只为判零执行开方；非零时再 Normalize 得到单位受击方向。
                if (direction.sqrMagnitude > 0.000001f)
                    direction.Normalize();
                else
                    direction = Vector3.up;

                var request = new DamageRequest(
                    command.Source.Id,
                    command.Source.Team,
                    command.Amount,
                    DamageType.Magical,
                    center,
                    direction,
                    // 范围反应可能同时命中多个对象，不触发 Hit Reaction 可避免连锁硬直滥用。
                    triggerHitReaction: false);
                target.ReceiveHit(in request);
            }
        }
    }
}
