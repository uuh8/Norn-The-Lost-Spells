using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 隔离 NavMesh 与 Physics 静态 API 的低频查询边界。
    /// Sampler 只编排检查顺序，真实 Scene 查询由实现类承担，测试则可注入确定性结果。
    /// </summary>
    public interface IBossTeleportWorldQuery
    {
        bool TrySampleNavMesh(
            Vector3 candidate,
            float maxDistance,
            int areaMask,
            out Vector3 sampledPosition);

        bool TryProjectToGround(
            Vector3 sampledPosition,
            int groundMask,
            out Vector3 groundPoint,
            out Vector3 groundNormal);

        bool IsCapsuleBlocked(
            Vector3 bottom,
            Vector3 top,
            float radius,
            int blockingMask,
            Transform ignoredRoot);
    }
}
