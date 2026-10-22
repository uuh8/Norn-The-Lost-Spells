using NUnit.Framework;
using UnityEngine;

namespace Game.Character.Tests
{
    public sealed class BossTeleportSamplerTests
    {
        [Test]
        public void CapsuleShape_BuildWorldCapsule_UsesScaledControllerGeometry()
        {
            var shape = new BossTeleportCapsuleShape(
                new Vector3(0f, 2f, 0f),
                0.8f,
                4f,
                0.08f,
                new Vector3(2f, 2f, 2f));

            shape.BuildWorldCapsule(
                new Vector3(10f, 3f, -5f),
                out Vector3 bottom,
                out Vector3 top,
                out float radius);

            Assert.That(radius, Is.EqualTo(1.44f).Within(0.0001f));
            Assert.That(bottom, Is.EqualTo(new Vector3(10f, 4.6f, -5f)));
            Assert.That(top, Is.EqualTo(new Vector3(10f, 9.4f, -5f)));
            Assert.That(bottom.y - radius, Is.EqualTo(3.16f).Within(0.0001f));
        }

        [Test]
        public void TryFindDestination_SkipsRejectedCandidatesAndReturnsFirstSafePoint()
        {
            var query = new FakeWorldQuery
            {
                NavMeshFailuresRemaining = 1,
                BlockedResultsRemaining = 1,
            };
            var sampler = new BossTeleportSampler(query, new Vector3[6]);
            var request = CreateRequest();

            bool found = sampler.TryFindDestination(
                in request,
                CreateCapsuleShape(),
                null,
                out Vector3 destination);

            Assert.That(found, Is.True);
            Assert.That(destination.y, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(query.NavMeshCalls, Is.EqualTo(3));
            Assert.That(query.GroundCalls, Is.EqualTo(2));
            Assert.That(query.CapsuleCalls, Is.EqualTo(2));
        }

        [Test]
        public void TryFindDestination_WhenEveryGroundIsTooSteep_ReturnsFalse()
        {
            var query = new FakeWorldQuery
            {
                GroundNormal = Vector3.right,
            };
            var sampler = new BossTeleportSampler(query, new Vector3[4]);
            var request = CreateRequest();

            bool found = sampler.TryFindDestination(
                in request,
                CreateCapsuleShape(),
                null,
                out _);

            Assert.That(found, Is.False);
            Assert.That(query.CapsuleCalls, Is.Zero);
        }

        [Test]
        public void TryFindDestination_WhenEveryCapsuleIsBlocked_ReturnsFalse()
        {
            var query = new FakeWorldQuery
            {
                BlockEveryCapsule = true,
            };
            var sampler = new BossTeleportSampler(query, new Vector3[4]);
            var request = CreateRequest();

            bool found = sampler.TryFindDestination(
                in request,
                CreateCapsuleShape(),
                null,
                out _);

            Assert.That(found, Is.False);
            Assert.That(query.CapsuleCalls, Is.EqualTo(4));
        }

        private static BossTeleportSamplingRequest CreateRequest()
        {
            return new BossTeleportSamplingRequest(
                Vector3.zero,
                6f,
                12f,
                0f,
                2f,
                45f,
                -1,
                1 << 8,
                1 << 0);
        }

        private static BossTeleportCapsuleShape CreateCapsuleShape()
        {
            return new BossTeleportCapsuleShape(
                new Vector3(0f, 2f, 0f),
                0.8f,
                4f,
                0.08f,
                Vector3.one);
        }

        private sealed class FakeWorldQuery : IBossTeleportWorldQuery
        {
            public int NavMeshFailuresRemaining;
            public int BlockedResultsRemaining;
            public bool BlockEveryCapsule;
            public Vector3 GroundNormal = Vector3.up;
            public int NavMeshCalls;
            public int GroundCalls;
            public int CapsuleCalls;

            public bool TrySampleNavMesh(
                Vector3 candidate,
                float maxDistance,
                int areaMask,
                out Vector3 sampledPosition)
            {
                NavMeshCalls++;
                sampledPosition = candidate + Vector3.up * 2f;
                if (NavMeshFailuresRemaining <= 0)
                {
                    return true;
                }

                NavMeshFailuresRemaining--;
                return false;
            }

            public bool TryProjectToGround(
                Vector3 sampledPosition,
                int groundMask,
                out Vector3 groundPoint,
                out Vector3 groundNormal)
            {
                GroundCalls++;
                groundPoint = new Vector3(
                    sampledPosition.x,
                    0f,
                    sampledPosition.z);
                groundNormal = GroundNormal;
                return true;
            }

            public bool IsCapsuleBlocked(
                Vector3 bottom,
                Vector3 top,
                float radius,
                int blockingMask,
                Transform ignoredRoot)
            {
                CapsuleCalls++;
                if (BlockEveryCapsule)
                {
                    return true;
                }

                if (BlockedResultsRemaining <= 0)
                {
                    return false;
                }

                BlockedResultsRemaining--;
                return true;
            }
        }
    }
}
