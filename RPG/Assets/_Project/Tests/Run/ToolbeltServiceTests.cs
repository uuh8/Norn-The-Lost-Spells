using System;
using System.Reflection;
using Game.Core;
using NUnit.Framework;

namespace Game.Run.Tests
{
    /// <summary>装备释放背包空间、交换/满包失败与旧引用迁移，始终保留唯一实例。</summary>
    public sealed class ToolbeltServiceTests : InventoryTestFixture
    {
        private ItemOwnershipStore _store;
        private SinglePlayerProfile _profile;
        private Type _type;
        private object Create()
        {
            _store = Store(Define("bag", ItemKind.Backpack, bagWidth:8, bagHeight:8),
                Define("bottle", ItemKind.Container), Define("spell", ItemKind.Spell));
            Add(_store,"bag","bag"); Add(_store,"a","bottle"); Add(_store,"b","bottle"); Add(_store,"s","spell",8);
            Assert.That(_store.TryMove("bag",new ItemLocation(ItemLocationKind.EquippedBackpack,"player",0,0,0,0)).Success,Is.True);
            Assert.That(_store.TryMove("a",Bag()).Success,Is.True);
            Assert.That(_store.TryMove("b",Bag(1)).Success,Is.True);
            Assert.That(_store.TryMove("s",Bag(2)).Success,Is.True);
            _profile = new SinglePlayerProfile("player",_store, currency:17);
            _type = typeof(SinglePlayerProfile).Assembly.GetType("Game.Run.ToolbeltService");
            Assert.That(_type,Is.Not.Null,"快捷位需要业务所有者，不能只存在于UI图标中。");
            return Activator.CreateInstance(_type,_profile,"inventory.test");
        }
        private uint Version(object service)=>(uint)_type.GetProperty("ConfigurationVersion").GetValue(service);
        private bool Bind(object service,int slot,string id,uint? version=null)=>
            (bool)_type.GetMethod("TryBind").Invoke(service,new object[]{slot,id,version ?? Version(service)});
        private string Binding(object service,int slot)
        {
            var args = new object[]{slot,null};
            return (bool)_type.GetMethod("TryGetBinding").Invoke(service,args) ? (string)args[1] : null;
        }
        [Test] public void EquippingReleasesFootprintAndCommitsInventoryVersion()
        {
            var service=Create(); uint before=_store.Version;
            Assert.That(Bind(service,0,"a"),Is.True); At(_store,"a",new ItemLocation(ItemLocationKind.EquippedTool,"player",0,0,0,0));
            Assert.That(_store.Version,Is.GreaterThan(before)); Assert.That(Binding(service,0),Is.EqualTo("a"));
            Add(_store,"new","bottle"); Assert.That(_store.TryMove("new",Bag()).Success,Is.True,"装备必须释放原位置");
            Assert.That(_profile.Items.Count,Is.EqualTo(5));
        }
        [Test] public void MovingExistingBindingSwapsWithoutDuplicatingInstances()
        {
            var service=Create(); Assert.That(Bind(service,0,"a"),Is.True); Assert.That(Bind(service,1,"b"),Is.True);
            Assert.That(Bind(service,1,"a"),Is.True);
            Assert.That(Binding(service,0),Is.EqualTo("b")); Assert.That(Binding(service,1),Is.EqualTo("a"));
        }
        [Test] public void RejectsUncarriedAndNonUsableItemsAndStaleCommands()
        {
            var service=Create(); uint old=Version(service);
            Assert.That(Bind(service,0,"s"),Is.False);
            Assert.That(Bind(service,7,"a"),Is.False);
            Assert.That(Bind(service,0,"a"),Is.True);
            Assert.That(Bind(service,1,"b",old),Is.False);
            Assert.That(_store.TryMove("b",ItemLocation.Warehouse).Success,Is.True);
            Assert.That(Bind(service,1,"b"),Is.False);
        }
        [Test] public void ClearingBindingDoesNotDropTheItem()
        {
            var service=Create(); Bind(service,0,"a");
            Assert.That(Bind(service,0,null),Is.True); At(_store,"a",Bag()); Assert.That(Binding(service,0),Is.Null);
        }
        [Test] public void MovingEquippedItemBackToBagInvalidatesEquipmentBinding()
        {
            var service=Create(); Bind(service,0,"a");
            Assert.That(_store.TryMove("a",Bag(7,7)).Success,Is.True);
            _type.GetMethod("Synchronize").Invoke(service,null); Assert.That(Binding(service,0),Is.Null);
            Assert.That(_store.TryMove("a",ItemLocation.Warehouse).Success,Is.True);
            _type.GetMethod("Synchronize").Invoke(service,null); Assert.That(Binding(service,0),Is.Null);
        }
        [Test] public void EmptySlotDoesNotReplaceCurrentHeldItem()
        {
            var service=Create(); Bind(service,0,"a");
            Assert.That((bool)_type.GetMethod("TrySelect").Invoke(service,new object[]{3}),Is.True);
            Assert.That((bool)_type.GetMethod("TrySelect").Invoke(service,new object[]{4}),Is.False);
            Assert.That(_type.GetProperty("SelectedItemId").GetValue(service),Is.EqualTo("a"));
        }
        [Test] public void SaveRestoreAndRollbackSnapshotKeepBindingsWithRealCoins()
        {
            var service=Create(); Bind(service,2,"a");
            var dto=ProfileSaveMapper.ToDto(_profile,0);
            Assert.That(ProfileSaveMapper.TryRestore(dto,_store.Catalog,out var restored).Success,Is.True);
            var again=Activator.CreateInstance(_type,restored,"restored");
            Assert.That(Binding(again,2),Is.EqualTo("a")); Assert.That(restored.Currency,Is.EqualTo(17));
            var rollback=ProfileAssetSnapshot.From(dto).ToDto();
            Assert.That(ProfileSaveMapper.TryRestore(rollback,_store.Catalog,out var hub).Success,Is.True);
            Assert.That(Binding(Activator.CreateInstance(_type,hub,"hub"),2),Is.EqualTo("a"));
        }
        [Test] public void CloneDoesNotShareWritableBindingArray()
        {
            var service=Create(); Bind(service,0,"a"); var clone=RaidProfileOps.Clone(_profile);
            var candidate=Activator.CreateInstance(_type,clone,"candidate"); Bind(candidate,0,null);
            Assert.That(Binding(service,0),Is.EqualTo("a"));
        }
        [Test] public void FullBackpackRejectsUnequipButReplacingUsesSpaceFreedByIncomingTool()
        {
            var service = Create(); Assert.That(Bind(service, 0, "a"), Is.True);
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
            {
                if (y == 0 && (x == 1 || x == 2)) continue;
                string id = "filler." + x + "." + y; Add(_store, id, "bottle");
                Assert.That(_store.TryMove(id, Bag(x, y)).Success, Is.True);
            }
            uint version = _store.Version;
            Assert.That(Bind(service, 0, null), Is.False); Assert.That(_store.Version, Is.EqualTo(version));
            Assert.That(Binding(service, 0), Is.EqualTo("a"));
            Assert.That(Bind(service, 0, "b"), Is.True, "入装备释放的位置可以原子安置旧道具");
            At(_store, "a", Bag(1)); Assert.That(Binding(service, 0), Is.EqualTo("b"));
        }
        [TestCase(1)][TestCase(2)][TestCase(3)][TestCase(4)][TestCase(5)]
        public void OldSchemasKeepAssetsAndCoinsWithEmptyShortcuts(int schema)
        {
            Create();var dto=ProfileSaveMapper.ToDto(_profile,0);dto.SchemaVersion=schema;
            Assert.That(ProfileSaveMapper.TryRestore(dto,_store.Catalog,out var restored).Success,Is.True);
            var read=new ToolbeltService(restored,"old");for(int i=0;i<6;i++)Assert.That(read.TryGetBinding(i,out _),Is.False);
            Assert.That(restored.Items.Count,Is.EqualTo(4));Assert.That(restored.Currency,Is.EqualTo(17));At(restored.Items,"a",Bag());
        }
        [Test] public void BrokenAndDuplicateReferencesAreCleanedWithoutDeletingAssets()
        {
            Create();var dto=ProfileSaveMapper.ToDto(_profile,0);dto.ToolBindings=new[]{"a","a","s","missing",null,"b","a"};
            Assert.That(ProfileSaveMapper.TryRestore(dto,_store.Catalog,out var restored).Success,Is.True);
            var read=new ToolbeltService(restored,"clean");Assert.That(read.TryGetBinding(0,out var a),Is.True);Assert.That(a,Is.EqualTo("a"));
            for(int i=1;i<5;i++)Assert.That(read.TryGetBinding(i,out _),Is.False);
            Assert.That(read.TryGetBinding(5,out var b),Is.True);Assert.That(b,Is.EqualTo("b"));Assert.That(restored.Items.Count,Is.EqualTo(4));Assert.That(restored.Currency,Is.EqualTo(17));
        }
    }
}
