using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// CPU 到 GPU 的纯值 Spawn Command。它快照写入位置、初速度与确定性 Seed，
    /// 因此 Projectile/ElementWorld 之后即使销毁或改变，也不会反向影响已经排队的粒子生成。
    /// </summary>
    public readonly struct FluidSpawnRequest
    {
        public readonly Vector3 WorldPosition;
        public readonly Vector3 InitialVelocity;
        public readonly float Radius;
        public readonly uint ParticleCount;
        public readonly uint MaterialId;
        public readonly uint Seed;
        public readonly uint Flags;
        public readonly float RestSpacing;

        public FluidSpawnRequest(
            Vector3 worldPosition,
            Vector3 initialVelocity,
            float radius,
            uint particleCount,
            uint materialId,
            uint seed,
            uint flags,
            float restSpacing = 0f)
        {
            WorldPosition = worldPosition;
            InitialVelocity = initialVelocity;
            Radius = radius;
            ParticleCount = particleCount;
            MaterialId = materialId;
            Seed = seed;
            Flags = flags;
            RestSpacing = restSpacing;
        }
    }
}
