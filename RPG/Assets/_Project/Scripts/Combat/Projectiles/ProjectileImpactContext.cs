using System;
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 一次真实表面命中的只读值快照。这里刻意保留 Physics 给出的原始向量，
    /// 不在 Combat 层归一化，避免上层丢失接触信息或重复猜测表面朝向。
    /// </summary>
    public readonly struct ProjectileImpactContext
    {
        public readonly Vector3 Point;
        public readonly Vector3 IncomingDirection;
        public readonly Vector3 SurfaceNormal;
        public readonly IDamageable Target;
        public readonly int AttackerId;
        public readonly byte AttackerTeam;

        public ProjectileImpactContext(
            Vector3 point,
            Vector3 incomingDirection,
            Vector3 surfaceNormal)
            : this(point, incomingDirection, surfaceNormal, null, 0, 0)
        {
        }

        /// <summary>
        /// Combat 在一次 Physics 命中中已经解析过目标与攻击方归因，因此把它们随几何信息一起快照。
        /// 上层命中效果无需再次查询 Collision，也不会因投射物稍后销毁而丢失来源。
        /// </summary>
        public ProjectileImpactContext(
            Vector3 point,
            Vector3 incomingDirection,
            Vector3 surfaceNormal,
            IDamageable target,
            int attackerId,
            byte attackerTeam)
        {
            if (!IsFinite(point)
                || !IsFinite(incomingDirection)
                || !IsFinite(surfaceNormal)
                || surfaceNormal.sqrMagnitude <= 1e-12f)
            {
                throw new ArgumentException("Projectile impact context requires finite values and a non-zero surface normal.");
            }

            Point = point;
            IncomingDirection = incomingDirection;
            SurfaceNormal = surfaceNormal;
            Target = target;
            AttackerId = attackerId;
            AttackerTeam = attackerTeam;
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
