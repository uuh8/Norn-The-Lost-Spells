using UnityEngine;
using Game.Core;
using Game.Combat;

namespace Game.Character
{
    /// <summary>
    /// 感知（Sense）：每帧判断玩家是否位于有限高度圆柱内；用滞回(进入使用 DetectRadius/DetectHeight、
    /// 已锁定使用更大的 LoseRadius/LoseHeight)防止边界抖动。感知不受墙体或朝向影响。
    /// 玩家来源 = PlayerControllerBase.Current（零查找）。
    /// </summary>
    public class EnemyPerception
    {
        private readonly EnemyControllerBase _enemy;

        public bool HasTarget { get; private set; }
        public Transform Target { get; private set; }
        public float DistanceToTarget { get; private set; }
        public float VerticalDistanceToTarget { get; private set; }

        public EnemyPerception(EnemyControllerBase enemy) { _enemy = enemy; }

        public void Tick()
        {
            EnemyDefinition def = _enemy.Definition;
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
                HasTarget = true;
                Target = player.transform;
                Vector3 delta = playerPosition - enemyPosition;
                VerticalDistanceToTarget = Mathf.Abs(delta.y);
                delta.y = 0f;
                DistanceToTarget = delta.magnitude;
            }
            else
            {
                if (HasTarget) GameLog.Info("玩家脱离 → 脱战", "Enemy");
                ClearTarget();
            }
        }

        private void ClearTarget()
        {
            HasTarget = false;
            Target = null;
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
