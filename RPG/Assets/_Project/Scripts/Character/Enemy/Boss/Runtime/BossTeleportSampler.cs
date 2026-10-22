using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 按固定顺序把数学候选过滤成一个真正可站立的目标点：
    /// NavMesh -> Ground -> Slope -> Capsule Clearance。任一步失败就检查下一个候选。
    /// </summary>
    public sealed class BossTeleportSampler
    {
        private readonly IBossTeleportWorldQuery _worldQuery;
        private readonly Vector3[] _candidateBuffer;

        public BossTeleportSampler(
            IBossTeleportWorldQuery worldQuery,
            Vector3[] candidateBuffer)
        {
            _worldQuery = worldQuery;
            _candidateBuffer = candidateBuffer;
        }

        public bool TryFindDestination(
            in BossTeleportSamplingRequest request,
            BossTeleportCapsuleShape capsuleShape,
            Transform ignoredRoot,
            out Vector3 destination)
        {
            destination = default;
            if (_worldQuery == null ||
                _candidateBuffer == null ||
                _candidateBuffer.Length == 0)
            {
                return false;
            }

            int count = BossTeleportCandidateGenerator.Generate(
                request.PlayerPosition,
                request.MinRadius,
                request.MaxRadius,
                request.AngleOffsetDegrees,
                _candidateBuffer);

            for (int i = 0; i < count; i++)
            {
                if (!_worldQuery.TrySampleNavMesh(
                        _candidateBuffer[i],
                        request.NavMeshSampleDistance,
                        request.NavMeshAreaMask,
                        out Vector3 navMeshPoint))
                {
                    continue;
                }

                if (!_worldQuery.TryProjectToGround(
                        navMeshPoint,
                        request.GroundMask,
                        out Vector3 groundPoint,
                        out Vector3 groundNormal))
                {
                    continue;
                }

                float slopeDegrees = Vector3.Angle(
                    Vector3.up,
                    groundNormal);
                if (slopeDegrees >
                    Mathf.Clamp(request.MaxGroundSlopeDegrees, 0f, 89f))
                {
                    continue;
                }

                capsuleShape.BuildWorldCapsule(
                    groundPoint,
                    out Vector3 bottom,
                    out Vector3 top,
                    out float radius);
                if (_worldQuery.IsCapsuleBlocked(
                        bottom,
                        top,
                        radius,
                        request.BlockingMask,
                        ignoredRoot))
                {
                    continue;
                }

                destination = groundPoint;
                return true;
            }

            return false;
        }
    }
}
