using UnityEngine;

namespace Game.Combat
{
    public readonly struct ProjectileDebugSnapshot
    {
        public readonly Vector3 Position;
        public readonly Vector3 Velocity;
        public readonly bool UsesGravity;
        public readonly bool HomingEnabled;
        public readonly float HomingRadius;
        public readonly bool HasHomingTarget;
        public readonly Vector3 HomingTargetPosition;
        public readonly bool OrbitEnabled;
        public readonly Vector3 OrbitCenter;
        public readonly Vector3 OrbitForward;
        public readonly Vector3 OrbitRight;
        public readonly Vector3 OrbitUp;
        public readonly float OrbitRadius;
        public readonly bool HasRecentReflection;
        public readonly Vector3 LastCollisionPoint;
        public readonly Vector3 LastCollisionNormal;
        public readonly Vector3 LastReflectedDirection;
        public readonly float LastCollisionTime;

        public ProjectileDebugSnapshot(
            Vector3 position, Vector3 velocity, bool usesGravity,
            bool homingEnabled, float homingRadius,
            bool hasHomingTarget, Vector3 homingTargetPosition,
            bool orbitEnabled, Vector3 orbitCenter, Vector3 orbitForward,
            Vector3 orbitRight, Vector3 orbitUp, float orbitRadius,
            bool hasRecentReflection, Vector3 lastCollisionPoint,
            Vector3 lastCollisionNormal, Vector3 lastReflectedDirection,
            float lastCollisionTime)
        {
            Position = position;
            Velocity = velocity;
            UsesGravity = usesGravity;
            HomingEnabled = homingEnabled;
            HomingRadius = homingRadius;
            HasHomingTarget = hasHomingTarget;
            HomingTargetPosition = homingTargetPosition;
            OrbitEnabled = orbitEnabled;
            OrbitCenter = orbitCenter;
            OrbitForward = orbitForward;
            OrbitRight = orbitRight;
            OrbitUp = orbitUp;
            OrbitRadius = orbitRadius;
            HasRecentReflection = hasRecentReflection;
            LastCollisionPoint = lastCollisionPoint;
            LastCollisionNormal = lastCollisionNormal;
            LastReflectedDirection = lastReflectedDirection;
            LastCollisionTime = lastCollisionTime;
        }
    }
}
