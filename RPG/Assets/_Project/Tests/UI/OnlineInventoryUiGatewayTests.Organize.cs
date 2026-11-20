using Game.Core;
using NUnit.Framework;

namespace Game.UI.Tests
{
    public sealed partial class OnlineInventoryUiGatewayTests
    {
        [Test]
        public void DungeonOrganizeButtonSendsOneOwnedBackpackIntentWithoutLocalLayoutWrites()
        {
            UseContainerBindingSource(); CaptureRequests();
            _ui.OrganizeBackpack();
            Assert.That(_requests, Is.EqualTo(1), "原整理按钮必须把线上请求送到DS，而非被本地命令门吞掉。");
            var request = (OnlineInventoryCommandRequestedEvent)_request;
            Assert.That((byte)request.Intent.Kind, Is.EqualTo(18));
            Assert.That(request.Intent.ItemId, Is.EqualTo("bag"));
            Assert.That(request.ActorId, Is.EqualTo(1ul));
            Assert.That(request.CallerId, Is.EqualTo(_ui.GetInstanceID()));
            Assert.That(request.Intent.ConnectionEpoch, Is.EqualTo(7u));
            Assert.That(request.Intent.ExpectedInventoryVersion, Is.EqualTo(9u));
            Assert.That(request.Intent.TargetLocationKind, Is.Zero);
            Assert.That(request.Intent.WorldOwnerId ?? "", Is.Empty);
            Assert.That(request.Intent.TargetBackpackId ?? "", Is.Empty);
            Assert.That(request.Intent.TransferBudget, Is.Zero);
            Assert.That(InventoryVersion(), Is.EqualTo(9u), "收据/完整快照到来前不得在Client先排列物品。");
            Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void DungeonOrganizeButtonDoesNotDuplicateAnOutstandingRequest()
        {
            UseContainerBindingSource(); CaptureRequests();
            _ui.OrganizeBackpack(); _ui.OrganizeBackpack();
            Assert.That(_requests, Is.EqualTo(1));
            Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void DungeonOrganizeButtonKeepsAnActiveDragAndItsOriginalItemLocation()
        {
            UseContainerBindingSource(); CaptureRequests(); Begin("bag-bottle");
            Assert.That(_ui.IsDragging, Is.True);
            _ui.OrganizeBackpack();
            Assert.That(_requests, Is.Zero); Assert.That(_localWrites, Is.Zero);
            Assert.That(_ui.IsDragging, Is.True);
        }
    }
}
