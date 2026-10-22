using UnityEngine;
using UnityEngine.AI;

namespace Game.Character
{
    /// <summary>
    /// Boss Teleport 的真实 Unity 世界查询 Adapter。
    /// OverlapCapsule 使用预分配数组；若数组被填满则保守判定为阻塞，宁可取消传送也不冒险穿模。
    /// </summary>
    public sealed class UnityBossTeleportWorldQuery :
        IBossTeleportWorldQuery
    {
        private const float GroundRayStartHeight = 2f;
        private const float GroundRayDistance = 6f;

        private readonly Collider[] _overlapBuffer;

        public UnityBossTeleportWorldQuery(Collider[] overlapBuffer)
        {
            _overlapBuffer = overlapBuffer;
        }

        public bool TrySampleNavMesh(
            Vector3 candidate,
            float maxDistance,
            int areaMask,
            out Vector3 sampledPosition)
        {
            bool found = NavMesh.SamplePosition(
                candidate,
                out NavMeshHit hit,
                Mathf.Max(0f, maxDistance),
                areaMask);
            sampledPosition = found ? hit.position : default;
            return found;
        }

        public bool TryProjectToGround(
            Vector3 sampledPosition,
            int groundMask,
            out Vector3 groundPoint,
            out Vector3 groundNormal)
        {
            Vector3 origin =
                sampledPosition + Vector3.up * GroundRayStartHeight;
            bool found = Physics.Raycast(
                origin,
                Vector3.down,
                out RaycastHit hit,
                GroundRayDistance,
                groundMask,
                QueryTriggerInteraction.Ignore);
            groundPoint = found ? hit.point : default;
            groundNormal = found ? hit.normal : Vector3.up;
            return found;
        }

        public bool IsCapsuleBlocked(
            Vector3 bottom,
            Vector3 top,
            float radius,
            int blockingMask,
            Transform ignoredRoot)
        {
            if (_overlapBuffer == null || _overlapBuffer.Length == 0)
            {
                return true;
            }

            int hitCount = Physics.OverlapCapsuleNonAlloc(
                bottom,
                top,
                radius,
                _overlapBuffer,
                blockingMask,
                QueryTriggerInteraction.Ignore);
            if (hitCount >= _overlapBuffer.Length)
            {
                return true;
            }

            for (int i = 0; i < hitCount; i++)
            {
                Collider hit = _overlapBuffer[i];
                _overlapBuffer[i] = null;
                if (hit == null)
                {
                    continue;
                }

                Transform hitTransform = hit.transform;
                if (ignoredRoot != null &&
                    (hitTransform == ignoredRoot ||
                     hitTransform.IsChildOf(ignoredRoot)))
                {
                    continue;
                }

                return true;
            }

            return false;
        }
    }
}
