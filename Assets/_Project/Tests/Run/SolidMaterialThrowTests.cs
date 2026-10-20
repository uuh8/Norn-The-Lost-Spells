using Game.Core;
using NUnit.Framework;
using UnityEngine;
namespace Game.Run.Tests
{
    public sealed class SolidMaterialThrowTests : InventoryTestFixture
    {
        private sealed class Proxy : IContainerThrowProxy { public void Launch(){} public void Cancel(){} }
        private SinglePlayerProfile Build(byte key)
        {
            var bag=Define("bag",ItemKind.Backpack);var material=Define("solid",ItemKind.Material);
            JsonUtility.FromJsonOverwrite("{\"_useKind\":2,\"_materialKey\":"+key+"}",material);
            var store=Store(bag,material);Add(store,"bag","bag");
            Assert.That(store.TryMove("bag",new ItemLocation(ItemLocationKind.EquippedBackpack,"player",0,0,0,0)).Success,Is.True);
            Assert.That(store.TryAdd(new RuntimeItemState("solid","solid",0,material:new MaterialItemState(key,900)),Bag()),Is.True);
            return new SinglePlayerProfile("player",store);
        }
        private static ContainerUseRequestedEvent Request(SinglePlayerProfile p)=>new ContainerUseRequestedEvent
        {InventoryId="inventory",WorldId="world",SourceId="user",ItemId="solid",RequestId=1,ExpectedVersion=p.Items.Version,Kind=ContainerUseKind.Throw,Radius=.3f,ThrowVelocity=Vector3.forward*16};
        [TearDown]public void Clear(){EventBus<ContainerThrownEvent>.Clear();EventBus<MaterialWorldDepositRequestedEvent>.Clear();EventBus<ContainerTransitResolvedEvent>.Clear();}
        [TestCase((byte)5)][TestCase((byte)6)] public void ToolbeltSolidReturnsSameMaterialAndExactQuantityToWorld(byte key)
        {
            var p=Build(key);var tools=new ToolbeltService(p,"inventory");Assert.That(tools.TryBind(0,"solid",tools.ConfigurationVersion),Is.True);
            EventBus<ContainerThrownEvent>.Subscribe(e=>e.Exchange.Proxy=new Proxy());ulong deposited=0;
            EventBus<MaterialWorldDepositRequestedEvent>.Subscribe(e=>
            {
                var x=e.Exchange;Assert.That(x.Amounts[0].MaterialKey,Is.EqualTo(key));
                Assert.That(x.Source.TryReserve(x.Amounts,x.Count,out _),Is.True);x.Source.Commit();
                x.Handled=true;x.Failure=MaterialTransferFailure.None;x.Transferred=x.Amounts[0].Quantity;deposited+=x.Transferred;
            });
            var uses=new ContainerUseService(p,"inventory");var thrown=uses.Execute(Request(p));Assert.That(thrown.Success,Is.True);
            var hit=new ContainerImpactEvent{InventoryId="inventory",WorldId="world",FlightId=thrown.FlightId,Position=Vector3.forward};
            Assert.That(uses.Impact(hit).Success,Is.True);Assert.That(deposited,Is.EqualTo(900));
            Assert.That(p.Items.TryGet("solid",out _),Is.False);Assert.That(uses.Impact(hit).Success,Is.False);Assert.That(uses.PendingFlights,Is.Zero);
        }
        [Test]public void RejectedWorldKeepsSolidInTransitWithoutCreatingPickupOrLosingGmu()
        {
            var p=Build(6);EventBus<ContainerThrownEvent>.Subscribe(e=>e.Exchange.Proxy=new Proxy());
            var uses=new ContainerUseService(p,"inventory");var thrown=uses.Execute(Request(p));uint version=p.Items.Version;
            var hit=new ContainerImpactEvent{InventoryId="inventory",WorldId="world",FlightId=thrown.FlightId,Position=Vector3.forward};
            Assert.That(uses.Impact(hit).Success,Is.False);Assert.That(p.Items.Version,Is.EqualTo(version));
            Assert.That(p.Items.TryGet("solid",out var item),Is.True);Assert.That(item.Material.Quantity,Is.EqualTo(900));
            p.Items.TryGetLocation("solid",out var location);Assert.That(location.Kind,Is.EqualTo(ItemLocationKind.ThrownContainer));
            uses.End(new ContainerTransitEndedEvent{InventoryId="inventory",WorldId="world",FlightId=thrown.FlightId,Reason=ContainerTransitEndReason.SceneEnded});
            Assert.That(uses.LostTransitQuantity(6),Is.EqualTo(900));
        }
    }
}
