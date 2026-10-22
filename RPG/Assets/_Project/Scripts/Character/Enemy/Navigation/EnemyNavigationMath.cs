using System;
using UnityEngine;

namespace Game.Character
{
    public enum BossCombatMoveMode : byte
    {
        Approach = 0,
        Orbit = 1,
        Retreat = 2
    }

    /// <summary>
    /// Boss 战斗走位的纯规则。状态记忆只由 isEngaged 传入，既能形成进入/退出双阈值，
    /// 也不依赖 MonoBehaviour 或 NavMesh，便于 EditMode 精确验证边界行为。
    /// </summary>
    public static class BossCombatMovementMath
    {
        private const float DirectionEpsilon = 1e-6f;
        private const float OrbitWeightDuringCorrection = 0.45f;

        public static bool ResolveCombatEngagement(
            bool isEngaged,
            float horizontalDistance,
            float attackEnterDistance,
            float attackExitDistance)
        {
            float enterDistance = Mathf.Max(0f, attackEnterDistance);
            float exitDistance = Mathf.Max(enterDistance, attackExitDistance);
            return isEngaged
                ? horizontalDistance <= exitDistance
                : horizontalDistance <= enterDistance;
        }

        public static BossCombatMoveMode ResolveMoveMode(
            float horizontalDistance,
            float preferredMinDistance,
            float preferredMaxDistance)
        {
            float minDistance = Mathf.Max(0f, preferredMinDistance);
            float maxDistance = Mathf.Max(minDistance, preferredMaxDistance);
            if (horizontalDistance < minDistance)
                return BossCombatMoveMode.Retreat;
            if (horizontalDistance > maxDistance)
                return BossCombatMoveMode.Approach;
            return BossCombatMoveMode.Orbit;
        }

        /// <summary>
        /// 理想距离内使用切线环绕；过远或过近时混入径向修正。
        /// orbitSign 只取正负号，使运行时切换方向时不会改变速度大小。
        /// </summary>
        public static Vector3 ResolveCombatDirection(
            Vector3 ownerPosition,
            Vector3 targetPosition,
            float preferredMinDistance,
            float preferredMaxDistance,
            int orbitSign)
        {
            Vector3 toTarget = targetPosition - ownerPosition;
            toTarget.y = 0f;
            float distance = toTarget.magnitude;
            Vector3 radial = distance > DirectionEpsilon
                ? toTarget / distance
                : Vector3.forward;
            float side = orbitSign < 0 ? -1f : 1f;
            Vector3 tangent = new Vector3(radial.z, 0f, -radial.x) * side;

            BossCombatMoveMode mode = ResolveMoveMode(
                distance,
                preferredMinDistance,
                preferredMaxDistance);
            if (mode == BossCombatMoveMode.Orbit)
                return tangent;

            Vector3 correction = mode == BossCombatMoveMode.Approach
                ? radial
                : -radial;
            Vector3 combined = correction + tangent * OrbitWeightDuringCorrection;
            return combined.normalized;
        }
    }

    /// <summary>
    /// Enemy 寻路中的纯数学规则。这里不查询 Scene/NavMesh，因此阈值和候选方向可以在 EditMode 中稳定验证。
    /// 调用方提供并复用输出数组，避免远程敌人每次后撤时产生 GC Alloc。
    /// </summary>
    public static class EnemyNavigationMath
    {
        private const int RetreatCandidateCount = 3;
        private const int TargetSampleQueryCount = 3;
        private const float DirectionEpsilon = 1e-6f;
        private const float CosSixtyDegrees = 0.5f;
        private const float SinSixtyDegrees = 0.8660254f;

        public static bool ShouldRefreshPath(
            float elapsed,
            Vector3 previousTarget,
            Vector3 currentTarget,
            float interval,
            float moveThreshold)
        {
            if (elapsed >= Mathf.Max(0f, interval))
                return true;

            Vector3 delta = currentTarget - previousTarget;
            delta.y = 0f;
            float threshold = Mathf.Max(0f, moveThreshold);
            return delta.sqrMagnitude >= threshold * threshold;
        }

        /// <summary>
        /// 后撤候选即使首次选择失败，也必须等待刷新间隔或威胁明显移动后再采样。
        /// 不能把“当前没有候选”当成“每帧都要重试”，否则狭小高台会持续执行多组 NavMesh 查询。
        /// </summary>
        public static bool ShouldRefreshRetreatSelection(
            bool hasSelectionAttempt,
            float elapsed,
            Vector3 previousThreat,
            Vector3 currentThreat,
            float interval,
            float moveThreshold)
        {
            return !hasSelectionAttempt || ShouldRefreshPath(
                elapsed,
                previousThreat,
                currentThreat,
                interval,
                moveThreshold);
        }

        /// <summary>
        /// Moving/Pending 表示后撤仍有实际执行机会；其余结果不能继续独占 FSM 决策，
        /// 状态层应降级检查原地攻击或普通接近，避免理想行为失败后角色停止战斗。
        /// </summary>
        public static bool ShouldContinueRetreat(EnemyNavigationResult result)
        {
            return result == EnemyNavigationResult.Moving ||
                   result == EnemyNavigationResult.Pending;
        }

        /// <summary>
        /// 把目标位置投影到导航查询参考平面。
        /// CharacterController 的根节点可能处于角色中心、跳跃高度或视觉锚点高度，
        /// 但 NavMesh.SamplePosition 需要在地面附近查询；只替换 Y，保留 XZ，
        /// 可以继续追踪目标的水平位置，又不会因为目标暂时离地导致路径请求失败。
        /// </summary>
        public static Vector3 ProjectToNavigationPlane(Vector3 targetPosition, float navigationY)
        {
            targetPosition.y = navigationY;
            return targetPosition;
        }

        /// <summary>
        /// 生成零分配的目标采样顺序：先查 Agent 当前导航平面，再查目标原始高度，最后扩大地面查询。
        /// 障碍顶部或断开的高层 NavMesh 可能离角色 Transform 更近，因此不能把原始高度放在首位；
        /// 优先保持目标 XZ、只替换 Y，才能让同层 Enemy 稳定选择绕障碍的地面路径。
        /// </summary>
        public static int FillTargetSampleQueries(
            Vector3 worldTarget,
            float navigationY,
            float queryRadius,
            float expandedRadius,
            Vector3[] positions,
            float[] radii)
        {
            if (positions == null || positions.Length < TargetSampleQueryCount ||
                radii == null || radii.Length < TargetSampleQueryCount)
                return 0;

            float radius = Mathf.Max(0.05f, queryRadius);
            float fallbackRadius = Mathf.Max(radius, expandedRadius);
            Vector3 navigationPlaneTarget = ProjectToNavigationPlane(worldTarget, navigationY);

            positions[0] = navigationPlaneTarget;
            radii[0] = radius;
            positions[1] = worldTarget;
            radii[1] = radius;
            positions[2] = navigationPlaneTarget;
            radii[2] = fallbackRadius;
            return TargetSampleQueryCount;
        }

        /// <summary>
        /// 目标附近可能同时采到可达地面和障碍顶部。完整路径优先于更近但断开的 Partial Path；
        /// 路径质量相同时，再选水平上更接近玩家真实 XZ 的端点。
        /// </summary>
        public static bool ShouldPreferTargetCandidate(
            bool hasCurrentCandidate,
            bool currentPathComplete,
            float currentTargetErrorSqr,
            bool candidatePathComplete,
            float candidateTargetErrorSqr)
        {
            if (!hasCurrentCandidate)
                return true;
            if (candidatePathComplete != currentPathComplete)
                return candidatePathComplete;
            return candidateTargetErrorSqr < currentTargetErrorSqr;
        }

        public static int FillRetreatDirections(Vector3 awayDirection, Vector3[] output)
        {
            if (output == null || output.Length < RetreatCandidateCount)
                return 0;

            awayDirection.y = 0f;
            if (awayDirection.sqrMagnitude <= DirectionEpsilon)
                awayDirection = Vector3.back;
            else
                awayDirection *= 1f / (float)Math.Sqrt(awayDirection.sqrMagnitude);

            output[0] = awayDirection;
            // 在 XZ 平面显式应用 ±60° 旋转，避免把可测试纯逻辑绑到 Unity Native 的 Quaternion 实现。
            output[1] = new Vector3(
                CosSixtyDegrees * awayDirection.x + SinSixtyDegrees * awayDirection.z,
                0f,
                -SinSixtyDegrees * awayDirection.x + CosSixtyDegrees * awayDirection.z);
            output[2] = new Vector3(
                CosSixtyDegrees * awayDirection.x - SinSixtyDegrees * awayDirection.z,
                0f,
                SinSixtyDegrees * awayDirection.x + CosSixtyDegrees * awayDirection.z);
            return RetreatCandidateCount;
        }

        public static bool HasProgress(Vector3 previous, Vector3 current, float minimumDistance)
        {
            Vector3 delta = current - previous;
            delta.y = 0f;
            float threshold = Mathf.Max(0f, minimumDistance);
            // 小容差让恰好落在边界的手算向量不会因 float 舍入被误判为“没有进展”。
            return delta.sqrMagnitude + DirectionEpsilon >= threshold * threshold;
        }

        /// <summary>
        /// 移动中朝实际 Steering 方向转身；没有移动意图时才回退为面向目标。
        /// 这样绕障碍时不会横着走，同时攻击前仍能自然看向玩家。
        /// </summary>
        public static Vector3 SelectFacingDirection(Vector3 navigationVelocity, Vector3 targetDirection)
        {
            navigationVelocity.y = 0f;
            if (navigationVelocity.sqrMagnitude > DirectionEpsilon)
                return NormalizeHorizontal(navigationVelocity);

            targetDirection.y = 0f;
            return targetDirection.sqrMagnitude > DirectionEpsilon
                ? NormalizeHorizontal(targetDirection)
                : Vector3.zero;
        }

        /// <summary>
        /// NavMesh 重算期间可能短暂返回零 Steering。已有旧路径时保留上一帧速度，
        /// 避免每次 Path Pending 都产生肉眼可见的刹停。
        /// </summary>
        public static Vector3 ResolveSteeringVelocity(
            Vector3 currentVelocity,
            Vector3 previousVelocity,
            bool preservePreviousWhenCurrentStops)
        {
            currentVelocity.y = 0f;
            if (currentVelocity.sqrMagnitude > DirectionEpsilon)
                return currentVelocity;

            previousVelocity.y = 0f;
            return preservePreviousWhenCurrentStops ? previousVelocity : Vector3.zero;
        }

        /// <summary>
        /// 判断本帧是否仍应沿路径移动。
        /// NavMeshAgent.remainingDistance 在路径刚更新、位置同步或拐角重算期间可能暂时为 Infinity；
        /// 这表示“距离未知”，不表示“已经到达”。只要 Agent 仍提供有效水平 Steering，就继续推进。
        /// </summary>
        public static bool ShouldMoveAlongPath(
            float remainingDistance,
            float stoppingDistance,
            float reachedTolerance,
            Vector3 steeringVelocity)
        {
            if (float.IsNaN(remainingDistance))
                return false;

            if (float.IsPositiveInfinity(remainingDistance))
            {
                // 路径完整但距离尚未可用时，保留“仍需移动”的意图；即使 Local Avoidance
                // 本帧把速度压成零，也要让 FailureTracker 继续观察并触发拐点恢复，而非返回 Reached。
                return true;
            }
            if (float.IsNegativeInfinity(remainingDistance))
                return false;

            steeringVelocity.y = 0f;
            float threshold = Mathf.Max(0f, stoppingDistance) + Mathf.Max(0f, reachedTolerance);
            return remainingDistance > threshold;
        }

        /// <summary>
        /// Local Avoidance 可能让多个同优先级 Agent 在障碍入口互相礼让，导致 desiredVelocity 长时间为零。
        /// 卡住恢复期间改用 NavMesh 当前路径拐点（steeringTarget）生成速度；它仍沿已烘焙路径前进，
        /// 只暂时绕过会产生对称死锁的局部避障结果，不会改回朝玩家直线撞墙。
        /// </summary>
        public static Vector3 ResolveStuckRecoveryVelocity(
            Vector3 currentPosition,
            Vector3 steeringTarget,
            float moveSpeed)
        {
            Vector3 direction = steeringTarget - currentPosition;
            direction.y = 0f;
            float speed = Mathf.Max(0f, moveSpeed);
            if (direction.sqrMagnitude <= DirectionEpsilon || speed <= 0f)
                return Vector3.zero;

            return NormalizeHorizontal(direction) * speed;
        }

        /// <summary>
        /// NavMeshAgent 的 avoidancePriority 数值越小优先级越高。
        /// Prefab 默认全部为 50 会放大对称让行；按稳定 InstanceId 分散到 20..79，
        /// 让相邻 Enemy 能确定谁先通过，同时避免每帧随机抖动。
        /// </summary>
        public static int ResolveAvoidancePriority(int instanceId)
        {
            const int minimumPriority = 20;
            const int priorityCount = 60;
            int nonNegativeId = instanceId & int.MaxValue;
            return minimumPriority + nonNegativeId % priorityCount;
        }

        /// <summary>
        /// 攻击条件使用 NavMesh 路径几何，而不是“视野”。即使直线距离已进入攻击范围，
        /// 只要路径仍明显绕行，就继续追到最终直达段，防止 Enemy 隔着障碍站定。
        /// </summary>
        public static bool IsAttackPathReady(
            float remainingDistance,
            float directDistance,
            float attackRange,
            float detourTolerance)
        {
            if (float.IsNaN(remainingDistance) || float.IsInfinity(remainingDistance))
                return false;

            float range = Mathf.Max(0f, attackRange);
            float direct = Mathf.Max(0f, directDistance);
            float tolerance = Mathf.Max(0f, detourTolerance);
            return remainingDistance <= range &&
                   direct <= range &&
                   remainingDistance <= direct + tolerance;
        }

        private static Vector3 NormalizeHorizontal(Vector3 value)
        {
            return value * (1f / (float)Math.Sqrt(value.sqrMagnitude));
        }
    }
}
