using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    /// <summary>同一实例从包/道具位到在途再落地；普通物体不能伪造容器组分或复制。</summary>
    public sealed class ItemThrowServiceTests : InventoryTestFixture
    {
        private sealed class Proxy:IContainerThrowProxy
        { public bool Launched;public void Launch()=>Launched=true;public void Cancel(){} }
        private SinglePlayerProfile Build()
        {
            var bag=Define("bag",ItemKind.Backpack);var food=Define("food",ItemKind.Consumable);
            JsonUtility.FromJsonOverwrite("{\"_useKind\":2}",food);
            var store=Store(bag,food);Add(store,"bag","bag");
            Assert.That(store.TryMove("bag",new ItemLocation(ItemLocationKind.EquippedBackpack,"player",0,0,0,0)).Success,Is.True);
            Assert.That(store.TryAdd(new RuntimeItemState("food","food",0),new ItemLocation(ItemLocationKind.Backpack,"bag",0,0,0,-1)),Is.True);
            return new SinglePlayerProfile("player",store);
        }
        private static ContainerUseRequestedEvent Request(SinglePlayerProfile p)=>new ContainerUseRequestedEvent
        { InventoryId="inventory",WorldId="world",SourceId="user",ItemId="food",RequestId=1,ExpectedVersion=p.Items.Version,
            Kind=ContainerUseKind.Throw,Radius=.1f,ThrowVelocity=Vector3.forward*16 };
        [TearDown] public void ClearEvents(){EventBus<ContainerThrownEvent>.Clear();EventBus<ContainerTransitResolvedEvent>.Clear();}
        [Test] public void OrdinaryItemEquipsThrowsAndLandsAsTheSamePickup()
        {
            var p=Build();var tools=new ToolbeltService(p,"inventory");Assert.That(tools.TryBind(0,"food",tools.ConfigurationVersion),Is.True);
            var uses=new ContainerUseService(p,"inventory");var proxy=new Proxy();
            EventBus<ContainerThrownEvent>.Subscribe(e=>e.Exchange.Proxy=proxy);
            var result=uses.Execute(Request(p));Assert.That(result.Success,Is.True);Assert.That(proxy.Launched,Is.True);
            p.Items.TryGetLocation("food",out var flight);Assert.That(flight.Kind,Is.EqualTo(ItemLocationKind.ThrownContainer));
            tools.Synchronize();Assert.That(tools.TryGetBinding(0,out _),Is.False);
            var impact=new ContainerImpactEvent{InventoryId="inventory",WorldId="world",FlightId=result.FlightId,Position=Vector3.forward*8};
            Assert.That(uses.Impact(impact).Success,Is.True);p.Items.TryGetLocation("food",out var ground);
            Assert.That(ground.Kind,Is.EqualTo(ItemLocationKind.WorldItem));Assert.That(p.Items.Count,Is.EqualTo(2));
            Assert.That(uses.Impact(impact).Success,Is.False);Assert.That(uses.PendingFlights,Is.Zero);
        }
        [Test] public void MissingThrowProxyPreservesEquippedItemAndInventoryVersion()
        {
            var p=Build();var tools=new ToolbeltService(p,"inventory");Assert.That(tools.TryBind(0,"food",tools.ConfigurationVersion),Is.True);
            uint version=p.Items.Version;var uses=new ContainerUseService(p,"inventory");
            Assert.That(uses.Execute(Request(p)).Success,Is.False);Assert.That(p.Items.Version,Is.EqualTo(version));
            Assert.That(tools.TryGetBinding(0,out var id),Is.True);Assert.That(id,Is.EqualTo("food"));
        }
        [Test] public void OrdinaryTransitTerminationIsIdempotentAndNeverRefundsIntoBackpack()
        {
            var p=Build();var uses=new ContainerUseService(p,"inventory");EventBus<ContainerThrownEvent>.Subscribe(e=>e.Exchange.Proxy=new Proxy());
            var result=uses.Execute(Request(p));Assert.That(result.Success,Is.True);
            var ended=new ContainerTransitEndedEvent{InventoryId="inventory",WorldId="world",FlightId=result.FlightId,Reason=ContainerTransitEndReason.ProxyDestroyed};
            uses.End(ended);uint version=p.Items.Version;uses.End(ended);
            Assert.That(p.Items.TryGet("food",out _),Is.False);Assert.That(p.Items.TryGet("bag",out _),Is.True);
            Assert.That(p.Items.Version,Is.EqualTo(version));Assert.That(uses.PendingFlights,Is.Zero);
        }
        [Test] public void SpellCannotBorrowOrdinaryThrowCapability()
        {
            var spell=Define("spell",ItemKind.Spell);JsonUtility.FromJsonOverwrite("{\"_useKind\":2}",spell);
            var catalog=Track(ScriptableObject.CreateInstance<ItemCatalog>());var data=new UnityEditor.SerializedObject(catalog);
            data.FindProperty("_definitions").arraySize=1;data.FindProperty("_definitions").GetArrayElementAtIndex(0).objectReferenceValue=spell;data.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(catalog.TryCreateIndex(out _,out _),Is.False);
        }
    }
}
