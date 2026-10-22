using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 目标点弹道的纯数学工具。它不读取 Transform、Rigidbody 或全局 Physics 状态，
    /// 因而 SpellCaster 可以在释放帧把当时锁定的目标点转换为初速度，EditMode 也能独立验证。
    /// </summary>
    public static class BallisticTrajectory
    {
        private const float MinimumDistance = 0.0001f;

        /// <summary>
        /// 在给定飞行时间下反解初速度，使连续运动学轨迹经过 target：
        /// target = origin + velocity * time + 0.5 * gravity * time²。
        /// nominalSpeed 只决定飞行时间的基准，不强制最终速度模长；这是用可达性换取真实固定弓力的取舍。
        /// </summary>
        public static bool TrySolveVelocity(
            Vector3 origin,
            Vector3 target,
            float nominalSpeed,
            Vector3 gravity,
            float minimumFlightTime,
            out Vector3 velocity,
            out float flightTime)
        {
            velocity = Vector3.zero;
            flightTime = 0f;

            if (!IsFinite(origin) ||
                !IsFinite(target) ||
                !IsFinite(gravity) ||
                !IsFinite(nominalSpeed) ||
                !IsFinite(minimumFlightTime) ||
                nominalSpeed <= 0f ||
                minimumFlightTime <= 0f)
            {
                return false;
            }

            Vector3 displacement = target - origin;
            float distance = displacement.magnitude;
            if (!IsFinite(distance) || distance <= MinimumDistance)
                return false;

            float requestedFlightTime = distance / nominalSpeed;
            if (!IsFinite(requestedFlightTime))
                return false;

            flightTime = Mathf.Max(requestedFlightTime, minimumFlightTime);
            float halfTimeSquared = 0.5f * flightTime * flightTime;
            velocity = (displacement - gravity * halfTimeSquared) / flightTime;

            // 绝不能把 NaN/Infinity 交给 Rigidbody.velocity；PhysX 之后的碰撞结果将不可预测。
            if (!IsFinite(velocity))
            {
                velocity = Vector3.zero;
                flightTime = 0f;
                return false;
            }

            return true;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }
    }
}
