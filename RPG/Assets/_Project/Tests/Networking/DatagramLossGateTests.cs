using NUnit.Framework;

namespace Game.Networking.Tests
{
    public sealed class DatagramLossGateTests
    {
        [Test]
        public void DisabledDoesNotDropAndEnabledDropsExactlyEveryTwentiethDatagram()
        {
            var gate = new DatagramLossGate();
            for (int i = 0; i < 40; i++) Assert.That(gate.ShouldDrop(), Is.False);
            gate.Enabled = true;
            for (int i = 1; i <= 100; i++) Assert.That(gate.ShouldDrop(), Is.EqualTo(i % 20 == 0));
            Assert.That(gate.Dropped, Is.EqualTo(5));
        }
    }
}
