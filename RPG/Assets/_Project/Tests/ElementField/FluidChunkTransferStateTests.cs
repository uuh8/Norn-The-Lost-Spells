using NUnit.Framework;

namespace Game.ElementField.Tests
{
    public sealed class FluidChunkTransferStateTests
    {
        [Test]
        public void Completion_IsConsumedAndFinishedExactlyOnce()
        {
            var state = new FluidChunkTransferState();
            Assert.That(state.TryBegin(7u), Is.True);
            Assert.That(state.TryBegin(8u), Is.False);
            Assert.That(state.RecordCompletion(8u, false), Is.False);
            Assert.That(state.RecordCompletion(7u, true), Is.True);
            Assert.That(state.RecordCompletion(7u, false), Is.False);
            Assert.That(state.TryConsumeCompletion(out uint id, out bool error), Is.True);
            Assert.That(id, Is.EqualTo(7u));
            Assert.That(error, Is.True);
            Assert.That(state.Finish(7u), Is.True);
            Assert.That(state.Finish(7u), Is.False);
            Assert.That(state.IsInFlight, Is.False);
        }
    }
}
