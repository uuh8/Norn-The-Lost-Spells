using NUnit.Framework;

namespace Game.Networking.Tests
{
    /// <summary>网络可能把合理流量聚合到同一回调帧；验证预算而非假定每包严格间隔。</summary>
    public sealed class ConnectionSessionTests
    {
        [Test]
        public void CoalescedBurstIsAcceptedButCapacityIsBounded()
        {
            var session = new ConnectionSession();
            session.Attach(1);
            for (ulong sequence = 1; sequence <= 4; sequence++)
                Assert.That(session.TryAccept(sequence, 1), Is.True, "同帧四包在 Burst 容量内。");
            Assert.That(session.TryAccept(5, 1), Is.False);
            Assert.That(session.TryAccept(5, 1.051), Is.True, "20/s 预算约 50ms 补回一个 Token。");
        }

        [Test]
        public void DuplicateDoesNotSpendAnotherToken()
        {
            var session = new ConnectionSession();
            session.Attach(1);
            Assert.That(session.TryAccept(1, 1), Is.True);
            Assert.That(session.TryAccept(1, 1), Is.False);
            Assert.That(session.TryAccept(2, 1), Is.True);
            Assert.That(session.AcceptedMessages, Is.EqualTo(2));
        }

        [Test]
        public void NonFiniteOrBackwardClockCannotChangeAcceptedSequence()
        {
            var session = new ConnectionSession();
            session.Attach(1);
            Assert.That(session.TryAccept(1, 1), Is.True);
            Assert.That(session.TryAccept(2, double.NaN), Is.False);
            Assert.That(session.TryAccept(2, double.PositiveInfinity), Is.False);
            Assert.That(session.TryAccept(2, .9), Is.False);
            Assert.That(session.LastSequence, Is.EqualTo(1));
            Assert.That(session.TryAccept(2, 1.1), Is.True);
        }

        [Test]
        public void BudgetBelongsToEachConnectionAndClearDetaches()
        {
            var a = new ConnectionSession(); var b = new ConnectionSession();
            a.Attach(1); b.Attach(2);
            for (ulong sequence = 1; sequence <= 4; sequence++) Assert.That(a.TryAccept(sequence, 1), Is.True);
            Assert.That(a.TryAccept(5, 1), Is.False);
            Assert.That(b.TryAccept(1, 1), Is.True);
            a.Clear();
            Assert.That(a.TryAccept(1, 2), Is.False);
            a.Attach(3);
            Assert.That(a.TryAccept(1, 2), Is.True);
            Assert.That(a.AcceptedMessages, Is.EqualTo(1));
        }
    }
}
