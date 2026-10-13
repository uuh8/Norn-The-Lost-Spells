using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class InventoryPlacementPreviewTests : InventoryTestFixture
    {
        private ItemOwnershipStore _store;
        private InventoryReadModel _read;
        private void Build()
        {
            _store=Store(Define("bag",ItemKind.Backpack,bagWidth:4,bagHeight:4),
                Define("shape",ItemKind.Container,2,2,cells:new[]{Vector2Int.zero,Vector2Int.right,Vector2Int.up}),
                Define("spell",ItemKind.Spell,maxStack:2));
            Add(_store,"bag","bag");Add(_store,"a","shape");Add(_store,"s","spell",8);Add(_store,"t","spell",8);
            _store.TryMove("bag",new ItemLocation(ItemLocationKind.EquippedBackpack,"player",0,0,0,0));
            _store.TryMove("a",Bag());_store.TryMove("s",Bag(1,1));
            _read=new InventoryReadModel(new SinglePlayerProfile("player",_store),"preview");
        }
        private InventoryFailureReason Preview(string id,int x,int y,int turns=0,bool whole=false)
        {
            var method=typeof(InventoryReadModel).GetMethod("PreviewPlacement",BindingFlags.Instance|BindingFlags.Public);
            Assert.That(method,Is.Not.Null,"拖拽预览必须读真实形状、堆叠相容性和容量，不写归属表。");
            return (InventoryFailureReason)method.Invoke(_read,new object[]{id,"bag",new GridPlacement(x,y,turns),whole,_read.Version});
        }
        [Test] public void HolesAndRotationUseActualCellsWithoutChangingVersion()
        {
            Build();uint version=_store.Version;
            Assert.That(Preview("a",0,0),Is.EqualTo(InventoryFailureReason.None));
            Assert.That(Preview("a",0,0,1),Is.EqualTo(InventoryFailureReason.Occupied));
            Assert.That(Preview("a",-1,0),Is.EqualTo(InventoryFailureReason.OutOfBounds));
            Assert.That(Preview("a",3,3),Is.EqualTo(InventoryFailureReason.OutOfBounds));
            Assert.That(_store.Version,Is.EqualTo(version));At(_store,"a",Bag());
        }
        [Test] public void CompatibleStacksRespectLimitAndWholeStackIgnoresItsOldCells()
        {
            Build();Assert.That(Preview("t",1,1),Is.EqualTo(InventoryFailureReason.None));
            _store.TryMove("t",Bag(1,1));
            Assert.That(Preview("s",2,2,whole:true),Is.EqualTo(InventoryFailureReason.None));
            Add(_store,"third","spell",8);
            Assert.That(Preview("third",1,1),Is.EqualTo(InventoryFailureReason.Capacity));
        }
    }
}
