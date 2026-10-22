using NUnit.Framework;
using UnityEngine;

namespace Game.Character.Tests
{
    public sealed class BallisticTrajectoryTests
    {
        private const float MinimumFlightTime = 0.1f;

        [TestCase(8f, 0f, 0f)]
        [TestCase(8f, 3f, 0f)]
        [TestCase(8f, -2f, 5f)]
        public void TrySolveVelocity_ValidTarget_ReachesRequestedPoint(
            float targetX,
            float targetY,
            float targetZ)
        {
            Vector3 origin = new Vector3(-2f, 1f, 3f);
            Vector3 target = new Vector3(targetX, targetY, targetZ);
            Vector3 gravity = new Vector3(0f, -9.81f, 0f);

            bool solved = BallisticTrajectory.TrySolveVelocity(
                origin,
                target,
                16f,
                gravity,
                MinimumFlightTime,
                out Vector3 velocity,
                out float flightTime);

            Assert.That(solved, Is.True);
            Assert.That(flightTime, Is.GreaterThanOrEqualTo(MinimumFlightTime));

            // 用连续运动学方程回代：若初速度正确，理想轨迹会在 flightTime 时经过目标点。
            Vector3 reached = origin + velocity * flightTime +
                              0.5f * gravity * flightTime * flightTime;
            Assert.That(Vector3.Distance(reached, target), Is.LessThan(0.001f));
        }

        [Test]
        public void TrySolveVelocity_CustomGravity_UsesProvidedGravityInsteadOfGlobalState()
        {
            Vector3 origin = new Vector3(1f, 5f, -2f);
            Vector3 target = new Vector3(7f, 2f, 10f);
            Vector3 customGravity = new Vector3(0f, -18f, 0f);

            bool solved = BallisticTrajectory.TrySolveVelocity(
                origin,
                target,
                18f,
                customGravity,
                MinimumFlightTime,
                out Vector3 velocity,
                out float flightTime);

            Assert.That(solved, Is.True);
            Vector3 reached = origin + velocity * flightTime +
                              0.5f * customGravity * flightTime * flightTime;
            Assert.That(Vector3.Distance(reached, target), Is.LessThan(0.001f));
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        public void TrySolveVelocity_NonPositiveNominalSpeed_ReturnsFalse(float nominalSpeed)
        {
            bool solved = BallisticTrajectory.TrySolveVelocity(
                Vector3.zero,
                Vector3.forward,
                nominalSpeed,
                Physics.gravity,
                MinimumFlightTime,
                out Vector3 velocity,
                out float flightTime);

            Assert.That(solved, Is.False);
            Assert.That(velocity, Is.EqualTo(Vector3.zero));
            Assert.That(flightTime, Is.Zero);
        }

        [Test]
        public void TrySolveVelocity_CoincidentPoints_ReturnsFalse()
        {
            bool solved = BallisticTrajectory.TrySolveVelocity(
                Vector3.one,
                Vector3.one,
                16f,
                Physics.gravity,
                MinimumFlightTime,
                out Vector3 velocity,
                out float flightTime);

            Assert.That(solved, Is.False);
            Assert.That(velocity, Is.EqualTo(Vector3.zero));
            Assert.That(flightTime, Is.Zero);
        }

        [Test]
        public void TrySolveVelocity_NonFiniteInput_ReturnsFalse()
        {
            bool solved = BallisticTrajectory.TrySolveVelocity(
                Vector3.zero,
                new Vector3(float.NaN, 1f, 0f),
                16f,
                Physics.gravity,
                MinimumFlightTime,
                out Vector3 velocity,
                out float flightTime);

            Assert.That(solved, Is.False);
            Assert.That(velocity, Is.EqualTo(Vector3.zero));
            Assert.That(flightTime, Is.Zero);
        }
    }
}
