using Game.Combat;
using NUnit.Framework;

namespace Game.UI.Tests
{
    public sealed class StatusIconAcquisitionOrderTests
    {
        [Test]
        public void RefreshingAnActiveStatusDoesNotChangeItsPosition()
        {
            var order = new StatusIconAcquisitionOrder();

            Assert.That(order.Activate(StatusKind.Wet), Is.True);
            Assert.That(order.Activate(StatusKind.Burning), Is.True);
            Assert.That(order.Activate(StatusKind.Burning), Is.False,
                "强度刷新不是重新获得状态，不能触发 UI 重排。");

            Assert.That(order.GetActiveKindAt(0), Is.EqualTo(StatusKind.Wet));
            Assert.That(order.GetActiveKindAt(1), Is.EqualTo(StatusKind.Burning));
        }

        [Test]
        public void ReactivatedStatusBecomesTheNewestRightmostStatus()
        {
            var order = new StatusIconAcquisitionOrder();
            order.Activate(StatusKind.Wet);
            order.Activate(StatusKind.Burning);

            Assert.That(order.Deactivate(StatusKind.Burning), Is.True);
            Assert.That(order.Activate(StatusKind.Burning), Is.True);

            Assert.That(order.ActiveCount, Is.EqualTo(2));
            Assert.That(order.GetActiveKindAt(0), Is.EqualTo(StatusKind.Wet));
            Assert.That(order.GetActiveKindAt(1), Is.EqualTo(StatusKind.Burning));
        }

        [Test]
        public void RemovingAnOlderStatusCompactsTheRemainingOrderWithoutReorderingIt()
        {
            var order = new StatusIconAcquisitionOrder();
            order.Activate(StatusKind.Wet);
            order.Activate(StatusKind.Poisoned);
            order.Activate(StatusKind.Burning);

            Assert.That(order.Deactivate(StatusKind.Wet), Is.True);

            Assert.That(order.ActiveCount, Is.EqualTo(2));
            Assert.That(order.GetActiveKindAt(0), Is.EqualTo(StatusKind.Poisoned));
            Assert.That(order.GetActiveKindAt(1), Is.EqualTo(StatusKind.Burning));
        }
    }
}
