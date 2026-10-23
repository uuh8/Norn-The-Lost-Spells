using UnityEngine;
using Game.Core;
using Game.Combat;

namespace Game.Character
{
    /// <summary>
    /// 感知（Sense）：每帧判断玩家是否位于有限高度圆柱内；用滞回(进入使用 DetectRadius/DetectHeight、
    /// 已锁定使用更大的 LoseRadius/LoseHeight)防止边界抖动。感知不受墙体或朝向影响。
    /// 单机使用 PlayerControllerBase.Current；在线只读已绑定 Roster，绝不把“最后启用的玩家”当成四人的身份来源。
    /// </summary>
    public class EnemyPerception
    {
        private readonly EnemyControllerBase _enemy;
        private ICombatActorRosterReadOnly _roster;
        private bool _hasRosterBinding;

        public bool HasTarget { get; private set; }
        public Transform Target { get; private set; }
        public ulong TargetActorId { get; private set; }
        public Vector3 TargetPosition { get; private set; }
        public float DistanceToTarget { get; private set; }
        public float VerticalDistanceToTarget { get; private set; }

        public EnemyPerception(EnemyControllerBase enemy)
        {
            _enemy = enemy;
            ClearTarget();
        }

        /// <summary>
        /// 在线组合提供只读 Actor 列表。null 也表示已进入在线职责但当前无角色，
        /// 不能因此回退到单机 Current；只有从未绑定的感知器使用单机来源。
        /// </summary>
        public void BindRoster(ICombatActorRosterReadOnly roster)
        {
            _hasRosterBinding = true;
            _roster = roster;
            ClearTarget();
        }

        public void Tick()
        {
            EnemyDefinition def = _enemy.Definition;
            if (_hasRosterBinding)
            {
                TickRoster(def);
                return;
            }

            PlayerControllerBase player = PlayerControllerBase.Current;
            if (def == null || player == null)
            {
                if (HasTarget) GameLog.Info("丢失目标(玩家不存在)", "Enemy");
                ClearTarget();
                return;
            }

            Vector3 enemyPosition = _enemy.transform.position;
            Vector3 playerPosition = player.transform.position;
            float radius = HasTarget ? def.LoseRadius : def.DetectRadius;
            float height = HasTarget ? def.LoseHeight : def.DetectHeight;

            if (EnemyPerceptionMath.IsWithinVerticalCylinder(
                    enemyPosition,
                    playerPosition,
                    radius,
                    height))
            {
                if (!HasTarget) GameLog.Info("发现玩家 → 进入战斗", "Enemy");
                SetTarget(0, player.transform, playerPosition, enemyPosition);
            }
            else
            {
                if (HasTarget) GameLog.Info("玩家脱离 → 脱战", "Enemy");
                ClearTarget();
            }
        }

        private void TickRoster(EnemyDefinition definition)
        {
            if (definition == null || _roster == null)
            {
                ClearTarget();
                return;
            }

            Vector3 observerPosition = _enemy.transform.position;
            int count = _roster.Count;
            // 先检查原锁定者的 Lose 圆柱，保留合法目标而不每 Tick 切到更近的人。
            // 这个 O(n) 扫描只读已有值快照，不创建列表或在热路径分配迭代器。
            if (HasTarget && TargetActorId != 0)
            {
                for (int i = 0; i < count; i++)
                {
                    if (!_roster.TryGetActor(i, out CombatActorSnapshot actor) ||
                        actor.ActorId != TargetActorId || !IsEligibleActor(actor) ||
                        !EnemyPerceptionMath.IsWithinVerticalCylinder(
                            observerPosition, actor.Position, definition.LoseRadius, definition.LoseHeight))
                        continue;

                    SetTarget(actor.ActorId, actor.Transform, actor.Position, observerPosition);
                    return;
                }
            }

            bool found = false;
            float nearestDistanceSquared = float.PositiveInfinity;
            CombatActorSnapshot selected = default;
            for (int i = 0; i < count; i++)
            {
                if (!_roster.TryGetActor(i, out CombatActorSnapshot actor) || !IsEligibleActor(actor) ||
                    !EnemyPerceptionMath.IsWithinVerticalCylinder(
                        observerPosition, actor.Position, definition.DetectRadius, definition.DetectHeight))
                    continue;

                Vector3 delta = actor.Position - observerPosition;
                float horizontalDistanceSquared = delta.x * delta.x + delta.z * delta.z;
                // 同距时按稳定 ActorId 排序，结果不依赖连接顺序或 Roster 槽位顺序。
                if (found && (horizontalDistanceSquared > nearestDistanceSquared ||
                    (horizontalDistanceSquared == nearestDistanceSquared && actor.ActorId >= selected.ActorId)))
                    continue;

                found = true;
                nearestDistanceSquared = horizontalDistanceSquared;
                selected = actor;
            }

            if (found)
                SetTarget(selected.ActorId, selected.Transform, selected.Position, observerPosition);
            else
                ClearTarget();
        }

        private bool IsEligibleActor(in CombatActorSnapshot actor)
        {
            return actor.ActorId != 0 && actor.IsAlive && actor.IsConnected && actor.Targetable &&
                   actor.TeamId != _enemy.TeamId;
        }

        private void SetTarget(ulong actorId, Transform target, Vector3 position, Vector3 observerPosition)
        {
            HasTarget = true;
            TargetActorId = actorId;
            Target = target;
            // Position 属于 Authority 的值快照，Transform 仅保留为旧瞄准代码的本地映射。
            // 在线运动不会通过 Transform 反查位置，避免表现插值反过来影响 AI 决策。
            TargetPosition = position;
            Vector3 delta = position - observerPosition;
            VerticalDistanceToTarget = Mathf.Abs(delta.y);
            DistanceToTarget = Mathf.Sqrt(delta.x * delta.x + delta.z * delta.z);
        }

        private void ClearTarget()
        {
            HasTarget = false;
            Target = null;
            TargetActorId = 0;
            TargetPosition = default;
            DistanceToTarget = float.PositiveInfinity;
            VerticalDistanceToTarget = float.PositiveInfinity;
        }
    }

    /// <summary>
    /// Enemy 感知与远程攻击共用的纯空间规则。这里只比较坐标和阈值，
    /// 不读取 Transform、Physics 或 NavMesh，方便用 EditMode Test 覆盖高度边界。
    /// </summary>
    public static class EnemyPerceptionMath
    {
        /// <summary>
        /// 判断目标是否位于以观察者为中心、沿世界 Y 轴延伸的有限圆柱内。
        /// horizontalRadius 约束 XZ 平面距离，verticalHalfHeight 约束绝对高度差。
        /// </summary>
        public static bool IsWithinVerticalCylinder(
            Vector3 observerPosition,
            Vector3 targetPosition,
            float horizontalRadius,
            float verticalHalfHeight)
        {
            if (!IsFinite(observerPosition) || !IsFinite(targetPosition))
                return false;

            float radius = Mathf.Max(0f, horizontalRadius);
            float height = Mathf.Max(0f, verticalHalfHeight);
            Vector3 delta = targetPosition - observerPosition;
            float horizontalSqr = delta.x * delta.x + delta.z * delta.z;

            return horizontalSqr <= radius * radius &&
                   Mathf.Abs(delta.y) <= height;
        }

        /// <summary>
        /// 远程后撤只处理“水平接近且高度相近”的贴脸威胁；玩家位于高台下方时，
        /// 垂直隔离已经提供安全距离，敌人应保留高台站位并尝试远程攻击。
        /// </summary>
        public static bool ShouldRetreat(
            float horizontalDistance,
            float verticalDistance,
            float retreatDistance,
            float retreatHeight)
        {
            return Mathf.Max(0f, horizontalDistance) < Mathf.Max(0f, retreatDistance) &&
                   Mathf.Max(0f, verticalDistance) <= Mathf.Max(0f, retreatHeight);
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                   !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                   !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }
    }
}
