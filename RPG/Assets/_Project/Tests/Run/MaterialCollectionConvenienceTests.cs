using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
namespace Game.Run.Tests
{
    public sealed class MaterialCollectionConvenienceTests:InventoryTestFixture
    {
        private SinglePlayerProfile Build(int width=2)
        {
            var bag=Define("bag",ItemKind.Backpack,bagWidth:width,bagHeight:2);var ice=Define("ice",ItemKind.Material);var food=Define("food",ItemKind.Consumable);var big=Define("big",ItemKind.Alchemy,width:2);
            JsonUtility.FromJsonOverwrite("{\"_materialItemCapacityGmu\":1000,\"_useKind\":2}",ice);
            var store=Store(bag,ice,food,big);Add(store,"bag","bag");Assert.That(store.TryMove("bag",new ItemLocation(ItemLocationKind.EquippedBackpack,"player",0,0,0,0)).Success,Is.True);
            Assert.That(store.TryAdd(new RuntimeItemState("a","ice",0,material:new MaterialItemState(5,100)),Bag(0,0)),Is.True);
            return new SinglePlayerProfile("player",store);
        }
        private static MaterialTransferResultEvent Collect(SinglePlayerProfile p,InventoryMaterialTransferService service,ulong id,ulong amount)
            =>service.Execute(new MaterialTransferRequestedEvent{InventoryId="inventory",WorldId="world",SourceId="user",RequestId=id,ExpectedVersion=p.Items.Version,SolidItem=true,MaterialContentId="ice",BackpackId="bag",Budget=amount});
        private static void Accept(MaterialWorldTransferRequestedEvent e)
        {
            var x=e.Exchange;ulong amount=System.Math.Min(x.Request.Budget,x.Destination.FreeCapacity);var amounts=new[]{new MaterialAmount(5,amount)};
            x.Handled=true;var result=x.Result;
            if(x.Destination.TryReserve(amounts,1,out var failure)){x.Destination.Commit();result.Transferred=amount;result.Failure=MaterialTransferFailure.None;}
            else result.Failure=failure;x.Result=result;
        }
        [TearDown] public void Clear()=>EventBus<MaterialWorldTransferRequestedEvent>.Clear();
        [Test]public void RepeatedSolidCollectionFillsSameInstanceInsteadOfCreatingFragments()
        {
            var p=Build();EventBus<MaterialWorldTransferRequestedEvent>.Subscribe(Accept);var service=new InventoryMaterialTransferService(p,"inventory");
            for(ulong i=1;i<=5;i++)Assert.That(Collect(p,service,i,100).Success,Is.True);
            Assert.That(p.Items.Count,Is.EqualTo(2));p.Items.TryGet("a",out var item);Assert.That(item.Material.Quantity,Is.EqualTo(600));
        }
        [Test]public void FullBagCanStillTopUpExistingSolidAndCancellationPreservesIt()
        {
            var p=Build(1);Assert.That(p.Items.TryAdd(new RuntimeItemState("food","food",0),Bag(0,1)),Is.True);
            EventBus<MaterialWorldTransferRequestedEvent>.Subscribe(Accept);var service=new InventoryMaterialTransferService(p,"inventory");
            Assert.That(Collect(p,service,1,250).Success,Is.True);Assert.That(p.Items.Count,Is.EqualTo(3));p.Items.TryGet("a",out var item);Assert.That(item.Material.Quantity,Is.EqualTo(350));
            EventBus<MaterialWorldTransferRequestedEvent>.Unsubscribe(Accept);
            EventBus<MaterialWorldTransferRequestedEvent>.Subscribe(Cancel);
            uint version=p.Items.Version;Assert.That(Collect(p,service,2,250).Success,Is.False);
            Assert.That(item.Material.Quantity,Is.EqualTo(350));Assert.That(p.Items.Version,Is.EqualTo(version));
        }
        private static void Cancel(MaterialWorldTransferRequestedEvent e)
        {var x=e.Exchange;x.Handled=true;Assert.That(x.Destination.TryReserve(new[]{new MaterialAmount(5,100)},1,out _),Is.True);x.Destination.Cancel();var result=x.Result;result.Failure=MaterialTransferFailure.GeometryRejected;x.Result=result;}
        [Test]public void StaleOrganizeAndSharedMaterialStateCannotMutateInventory()
        {
            var p=Build();p.Items.TryGet("a",out var first);uint before=p.Items.Version;
            Assert.That(p.Items.TryAdd(new RuntimeItemState("b","ice",0,material:first.Material),Bag(1,0)),Is.False);
            Assert.That(BackpackOrganizeOps.TryOrganize(p.Items,"bag",before-1).Success,Is.False);
            Assert.That(p.Items.Version,Is.EqualTo(before));Assert.That(first.Material.Quantity,Is.EqualTo(100));
        }
        [Test]public void MaterialDragMergeConservesGmuAndRetainsOverflow()
        {
            var p=Build();Assert.That(p.Items.TryAdd(new RuntimeItemState("b","ice",0,material:new MaterialItemState(5,950)),Bag(1,0)),Is.True);
            Assert.That(ItemStackOps.TryMerge(p.Items,"a","b",p.Items.Version).Success,Is.True);
            p.Items.TryGet("a",out var source);p.Items.TryGet("b",out var target);Assert.That(source.Material.Quantity,Is.EqualTo(50));Assert.That(target.Material.Quantity,Is.EqualTo(1000));
        }
        [Test]public void MaterialMergePreviewIsGreenWhenTargetHasRoomAndRedWhenFull()
        {
            var p=Build();Assert.That(p.Items.TryAdd(new RuntimeItemState("b","ice",0,material:new MaterialItemState(5,950)),Bag(1,0)),Is.True);
            var read=new InventoryReadModel(p,"inventory");Assert.That(read.PreviewPlacement("a","bag",new GridPlacement(1,0,0),true,p.Items.Version),Is.EqualTo(InventoryFailureReason.None));
            Assert.That(p.Items.TryMove("a",Bag(1,0),p.Items.Version).Success,Is.True);
            Assert.That(read.PreviewPlacement("a","bag",new GridPlacement(1,0,0),true,p.Items.Version),Is.EqualTo(InventoryFailureReason.Capacity));
        }
        private WorldItemPickup Ground(SinglePlayerProfile p,SinglePlayerInventorySession session,string id,string content,ulong quantity,float distance)
        {
            string owner="ground-"+id;Assert.That(p.Items.TryAdd(new RuntimeItemState(id,content,0,material:quantity>0?new MaterialItemState(5,quantity):null),new ItemLocation(ItemLocationKind.WorldItem,owner,0,0,0,-1)),Is.True);
            var go=Track(new GameObject(id));go.transform.position=Vector3.right*distance;var proxy=go.AddComponent<WorldItemPickup>();Assert.That(proxy.TryBind(p.Items,id,owner,session.ReadOnly.InventoryId),Is.True);return proxy;
        }
        private NearbyPickupController Nearby(SinglePlayerProfile p,out SinglePlayerInventorySession session)
        {
            var go=Track(new GameObject("Batch pickup test"));session=go.AddComponent<SinglePlayerInventorySession>();
            Assert.That(session.Initialize(p,new ProfileSaveRepository("Temp/SinglePlayer/MaterialConvenience",p.Items.Catalog)),Is.True);
            var nearby=go.AddComponent<NearbyPickupController>();nearby.Configure(session,go.transform);return nearby;
        }
        [Test]public void PickupAllSkipsLargeUnfittableItemAndTakesSmallerItemOnce()
        {
            var p=Build(1);var nearby=Nearby(p,out var session);Ground(p,session,"large","big",0,.1f);Ground(p,session,"small","food",0,.2f);
            Assert.That(nearby.CollectAll(out int remaining),Is.EqualTo(1));Assert.That(remaining,Is.EqualTo(1));At(p.Items,"small",Bag(0,1));
            uint version=p.Items.Version;Assert.That(nearby.CollectAll(out remaining),Is.Zero);Assert.That(p.Items.Version,Is.EqualTo(version));
        }
        [Test]public void FullBagPickupPartiallyMergesSolidAndKeepsWorldRemainderAndProxy()
        {
            var p=Build(1);EventBus<MaterialWorldTransferRequestedEvent>.Subscribe(Accept);var service=new InventoryMaterialTransferService(p,"inventory");Assert.That(Collect(p,service,1,850).Success,Is.True);
            Assert.That(p.Items.TryAdd(new RuntimeItemState("food","food",0),Bag(0,1)),Is.True);
            var nearby=Nearby(p,out var session);var proxy=Ground(p,session,"ground","ice",200,.2f);
            Assert.That(nearby.CollectAll(out int remaining),Is.EqualTo(1));Assert.That(remaining,Is.EqualTo(1));Assert.That(proxy.isActiveAndEnabled,Is.True);
            p.Items.TryGet("a",out var target);p.Items.TryGet("ground",out var source);Assert.That(target.Material.Quantity,Is.EqualTo(1000));Assert.That(source.Material.Quantity,Is.EqualTo(150));
            uint version=p.Items.Version;Assert.That(nearby.CollectAll(out remaining),Is.Zero);Assert.That(p.Items.Version,Is.EqualTo(version));
        }
        [Test]public void OrganizeCompactsAndConsolidatesOldMaterialFragmentsWithoutTouchingEquipment()
        {
            var p=Build(3);Assert.That(p.Items.TryAdd(new RuntimeItemState("b","ice",0,material:new MaterialItemState(5,200)),Bag(2,1)),Is.True);
            Assert.That(p.Items.TryAdd(new RuntimeItemState("food","food",0),Bag(1,1)),Is.True);
            var type=typeof(ItemOwnershipStore).Assembly.GetType("Game.Run.BackpackOrganizeOps");Assert.That(type,Is.Not.Null);
            var result=(InventoryOperationResult)type.GetMethod("TryOrganize").Invoke(null,new object[]{p.Items,"bag",p.Items.Version});Assert.That(result.Success,Is.True);
            Assert.That(p.Items.Count,Is.EqualTo(3));p.Items.TryGet("a",out var item);Assert.That(item.Material.Quantity,Is.EqualTo(300));
            At(p.Items,"a",Bag(0,0));At(p.Items,"food",Bag(1,0));At(p.Items,"bag",new ItemLocation(ItemLocationKind.EquippedBackpack,"player",0,0,0,0));
        }
    }
}
