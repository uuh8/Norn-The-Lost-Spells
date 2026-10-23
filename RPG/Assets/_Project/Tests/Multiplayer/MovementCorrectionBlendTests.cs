using NUnit.Framework;
using UnityEngine;

namespace Game.Multiplayer.Tests
{
    public sealed class MovementCorrectionBlendTests
    {
        [Test] public void CorrectionKeepsPreviousVisualThenFinishesInFiniteTime()
        {
            var blend = new MovementCorrectionBlend(); blend.Begin(Vector3.right);
            Assert.That(blend.Offset, Is.EqualTo(Vector3.right));
            blend.Advance(.05f); Assert.That(blend.Offset.x, Is.EqualTo(.5f).Within(.001));
            blend.Advance(.05f); Assert.That(blend.Offset, Is.EqualTo(Vector3.zero));
            blend.Advance(10); Assert.That(blend.Offset, Is.EqualTo(Vector3.zero));
        }
        [Test] public void NewCorrectionStartsFromCurrentVisualError_NotAnUnboundedOldOffset()
        {
            var blend = new MovementCorrectionBlend(); blend.Begin(Vector3.right); blend.Advance(.05f);
            blend.Begin(Vector3.forward); Assert.That(blend.Offset, Is.EqualTo(Vector3.forward));
            blend.Advance(.2f); Assert.That(blend.Offset, Is.EqualTo(Vector3.zero));
        }
        [Test] public void TeleportOrNonfiniteErrorCannotLeaveVisualOutsideAuthorityWorld()
        {
            var blend = new MovementCorrectionBlend(); blend.Begin(Vector3.right * 3);
            Assert.That(blend.Offset, Is.EqualTo(Vector3.zero));
            blend.Begin(new Vector3(float.NaN, 0, 0)); Assert.That(blend.Offset, Is.EqualTo(Vector3.zero));
        }
    }
}
