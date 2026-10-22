using NUnit.Framework;
using UnityEngine;

namespace Game.Character.Tests
{
    public sealed class JumpAnimationPhaseResolverTests
    {
        [TestCase(true, -1f, false)]
        [TestCase(true, 1f, false)]
        [TestCase(false, 1f, false)]
        [TestCase(false, 0f, true)]
        [TestCase(false, -1f, true)]
        public void IsFalling_RequiresAirborneNonPositiveVelocity(
            bool isGrounded,
            float verticalVelocity,
            bool expected)
        {
            bool result = JumpAnimationPhaseResolver.IsFalling(isGrounded, verticalVelocity);

            Assert.That(result, Is.EqualTo(expected));
        }

        [TestCase(JumpAnimationPhase.JumpStart, 0.4f, false, JumpAnimationPhase.JumpStart)]
        [TestCase(JumpAnimationPhase.JumpStart, 0.625f, false, JumpAnimationPhase.JumpAir)]
        [TestCase(JumpAnimationPhase.JumpStart, 0.1f, true, JumpAnimationPhase.JumpAir)]
        [TestCase(JumpAnimationPhase.Locomotion, 0f, false, JumpAnimationPhase.JumpAir)]
        public void ResolveAirbornePhase_UsesPoseFallbackAndPhysicalFalling(
            JumpAnimationPhase currentPhase,
            float normalizedTime,
            bool isFalling,
            JumpAnimationPhase expected)
        {
            JumpAnimationPhase result = JumpAnimationPhaseResolver.ResolveAirbornePhase(
                currentPhase,
                normalizedTime,
                isFalling);

            Assert.That(result, Is.EqualTo(expected));
        }

        [TestCase(JumpAnimationPhase.JumpStart, 0f, JumpAnimationPhase.JumpEnd)]
        [TestCase(JumpAnimationPhase.JumpAir, 0f, JumpAnimationPhase.JumpEnd)]
        [TestCase(JumpAnimationPhase.JumpEnd, 0.1f, JumpAnimationPhase.JumpEnd)]
        [TestCase(JumpAnimationPhase.JumpEnd, 0.15f, JumpAnimationPhase.Locomotion)]
        public void ResolveGroundedPhase_PreservesBriefLandingReadability(
            JumpAnimationPhase currentPhase,
            float normalizedTime,
            JumpAnimationPhase expected)
        {
            JumpAnimationPhase result = JumpAnimationPhaseResolver.ResolveGroundedPhase(
                currentPhase,
                normalizedTime);

            Assert.That(result, Is.EqualTo(expected));
        }

        [Test]
        public void JumpVfxPlacement_UsesCharacterFeetAndVerticalOffset()
        {
            Bounds characterBounds = new Bounds(
                new Vector3(2f, 3f, 4f),
                new Vector3(2f, 6f, 2f));

            Vector3 result = JumpVfxPlacement.ResolveFootPosition(characterBounds, 0.2f);

            Assert.That(result, Is.EqualTo(new Vector3(2f, 0.2f, 4f)));
        }
    }
}
