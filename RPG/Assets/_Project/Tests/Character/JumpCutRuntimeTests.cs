using NUnit.Framework;

namespace Game.Character.Tests
{
    public sealed class JumpCutRuntimeTests
    {
        [Test]
        public void Evaluate_ArmedAndReleasedWhileRising_CutsVelocityOnce()
        {
            JumpCutRuntime runtime = new JumpCutRuntime(0.5f);
            runtime.Arm();

            float firstFrameVelocity = runtime.Evaluate(6f, false);
            float secondFrameVelocity = runtime.Evaluate(firstFrameVelocity, false);

            Assert.That(firstFrameVelocity, Is.EqualTo(3f));
            Assert.That(secondFrameVelocity, Is.EqualTo(3f));
        }

        [Test]
        public void Evaluate_HeldThenReleased_PreservesFullVelocityUntilRelease()
        {
            JumpCutRuntime runtime = new JumpCutRuntime(0.5f);
            runtime.Arm();

            float heldVelocity = runtime.Evaluate(6f, true);
            float releasedVelocity = runtime.Evaluate(heldVelocity, false);

            Assert.That(heldVelocity, Is.EqualTo(6f));
            Assert.That(releasedVelocity, Is.EqualTo(3f));
        }

        [Test]
        public void Evaluate_ApexReachedBeforeRelease_DoesNotCutLaterVelocity()
        {
            JumpCutRuntime runtime = new JumpCutRuntime(0.5f);
            runtime.Arm();

            float fallingVelocity = runtime.Evaluate(-1f, true);
            float laterPositiveVelocity = runtime.Evaluate(4f, false);

            Assert.That(fallingVelocity, Is.EqualTo(-1f));
            Assert.That(laterPositiveVelocity, Is.EqualTo(4f));
        }

        [TestCase(-0.25f, 0f)]
        [TestCase(1.25f, 6f)]
        public void Constructor_OutOfRangeMultiplier_ClampsToSafeRange(float multiplier, float expectedVelocity)
        {
            JumpCutRuntime runtime = new JumpCutRuntime(multiplier);
            runtime.Arm();

            float velocity = runtime.Evaluate(6f, false);

            Assert.That(velocity, Is.EqualTo(expectedVelocity));
        }
    }

}
