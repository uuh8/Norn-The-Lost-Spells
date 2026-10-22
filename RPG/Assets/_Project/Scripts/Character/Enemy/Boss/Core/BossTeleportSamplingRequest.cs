using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 一次 Teleport 采样所需的只读值快照。
    /// 使用 struct 传递可以让 Sampler 脱离 ScriptableObject 与 Scene 查询，测试时也能精确构造边界条件。
    /// </summary>
    public readonly struct BossTeleportSamplingRequest
    {
        public readonly Vector3 PlayerPosition;
        public readonly float MinRadius;
        public readonly float MaxRadius;
        public readonly float AngleOffsetDegrees;
        public readonly float NavMeshSampleDistance;
        public readonly float MaxGroundSlopeDegrees;
        public readonly int NavMeshAreaMask;
        public readonly int GroundMask;
        public readonly int BlockingMask;

        public BossTeleportSamplingRequest(
            Vector3 playerPosition,
            float minRadius,
            float maxRadius,
            float angleOffsetDegrees,
            float navMeshSampleDistance,
            float maxGroundSlopeDegrees,
            int navMeshAreaMask,
            int groundMask,
            int blockingMask)
        {
            PlayerPosition = playerPosition;
            MinRadius = minRadius;
            MaxRadius = maxRadius;
            AngleOffsetDegrees = angleOffsetDegrees;
            NavMeshSampleDistance = navMeshSampleDistance;
            MaxGroundSlopeDegrees = maxGroundSlopeDegrees;
            NavMeshAreaMask = navMeshAreaMask;
            GroundMask = groundMask;
            BlockingMask = blockingMask;
        }
    }
}
