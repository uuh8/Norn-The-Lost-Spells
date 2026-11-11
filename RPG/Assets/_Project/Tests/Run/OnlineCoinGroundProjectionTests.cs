using System;
using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    /// <summary>验证地面Coin与Owner余额同批原子安装，显示不能领取金币或借旧版本复活掉落。</summary>
    public sealed class OnlineCoinGroundProjectionTests : InventoryTestFixture
    {
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private ItemCatalogIndex _catalog;
        [SetUp] public void SetupCatalog() { _catalog = Store(Define("coin.projection.placeholder", ItemKind.Consumable)).Catalog; }

        [Test]
        public void CopyUsesValueFactsWithoutExposingIncomingOrInternalMutableArrays()
        {
            var snapshot=Snapshot(); using var source=Create(snapshot); var buffer=Buffer();
            Assert.That(Copy(source,buffer),Is.EqualTo(1)); Assert.That(Get(buffer.GetValue(0),"Amount"),Is.EqualTo(23L));
            Change(snapshot,"Amount",99L); Assert.That(Copy(source,buffer),Is.EqualTo(1)); Assert.That(Get(buffer.GetValue(0),"Amount"),Is.EqualTo(23L));
            object copy=buffer.GetValue(0);Set(copy,"Amount",888L);buffer.SetValue(copy,0);Copy(source,buffer);
            Assert.That(Get(buffer.GetValue(0),"Amount"),Is.EqualTo(23L)); Assert.That(source.Currency,Is.EqualTo(977L));
        }

        [TestCase("Amount")] [TestCase("Position")] [TestCase("Id")]
        public void SameGroundVersionCannotChangeAnyCoinFact(string field)
        {
            using var source=Create(Snapshot()); var next=Snapshot(2);
            Change(next,field,field=="Amount"?(object)24L:field=="Id"?6ul:(object)new Vector3(.00000001f,2,3));
            Assert.That(source.TryInstall(next,out _),Is.False); Assert.That(source.SnapshotSequence,Is.EqualTo(1ul));
            Assert.That(source.Currency,Is.EqualTo(977L));
        }

        [Test]
        public void NewerEmptyRemovesCoinAndOlderGroundOrEpochCannotResurrectIt()
        {
            using var source=Create(Snapshot());var empty=Snapshot(2,2,0);
            Assert.That(source.TryInstall(empty,out _),Is.True);Assert.That(Copy(source,Buffer()),Is.Zero);
            Assert.That(source.TryInstall(Snapshot(3),out _),Is.False);
            var oldEpoch=Snapshot(3,3);oldEpoch.ConnectionEpoch=1;Assert.That(source.TryInstall(oldEpoch,out _),Is.False);
            Assert.That(Copy(source,Buffer()),Is.Zero);
        }

        [Test]
        public void DuplicateOverflowAndMissingCoinListsRejectEntireCandidate()
        {
            using var source=Create(Snapshot());
            var duplicate=Snapshot(2,2,2);Change(duplicate,"Id",6ul); Assert.That(source.TryInstall(duplicate,out _),Is.False);
            Assert.That(source.TryInstall(Snapshot(2,2,13),out _),Is.False);
            var missing=Snapshot(2,2);Set(missing.Ground,"Coins",null);Assert.That(source.TryInstall(missing,out _),Is.False);
            Assert.That(source.SnapshotSequence,Is.EqualTo(1ul));
        }

        [Test]
        public void InvalidCoinCannotPublishNewOwnerBalance()
        {
            using var source=Create(Snapshot()); var invalid=Snapshot(2,2);invalid.Assets.Gold=1000;invalid.Assets.CurrencyVersion=2;
            Change(invalid,"Position",new Vector3(float.PositiveInfinity,0,0));
            Assert.That(source.TryInstall(invalid,out _),Is.False);Assert.That(source.Currency,Is.EqualTo(977L));
        }

        [Test]
        public void DisposeClearsCoinReadCapabilityAndRejectsLateSnapshot()
        {
            var source=Create(Snapshot());source.Dispose();Assert.That(Copy(source,Buffer()),Is.Zero);
            Assert.That(source.TryInstall(Snapshot(2),out _),Is.False);
        }

        private OnlineInventoryPresentationSource Create(OnlineInventoryPresentationSnapshot snapshot)
        {
            var identity=new OnlineInventoryPresentationIdentity("run","player",1,"inventory",Hash,2,"DungeonWorld");
            Assert.That(OnlineInventoryPresentationSource.TryCreate(_catalog,identity,snapshot,out var source,out var error),Is.True,error);return source;
        }
        private static OnlineInventoryPresentationSnapshot Snapshot(ulong sequence=1,uint groundVersion=1,int count=1)
        {
            var result=new OnlineInventoryPresentationSnapshot {SchemaVersion=1,ConnectionEpoch=2,SnapshotSequence=sequence,ResourceVersion=1,SelectionVersion=1,SpellUseVersion=1,
                Assets=new OnlineOwnedAssetSnapshot {SchemaVersion=3,RunId="run",PlayerId="player",ActorId=1,InventoryId="inventory",GameplayConfigHash=Hash,InventoryVersion=1,SelectedWandInstanceId="",Gold=977,CurrencyVersion=1},
                Ground=new OnlineGroundInventorySnapshot {Version=groundVersion}};
            Array coins=Array.CreateInstance(CoinType(),count);for(int i=0;i<count;i++){object coin=Activator.CreateInstance(CoinType());Set(coin,"Id",(ulong)(5+i));Set(coin,"Amount",23L);Set(coin,"Position",new Vector3(i,2,3));coins.SetValue(coin,i);}Set(result.Ground,"Coins",coins);return result;
        }
        private static Type CoinType() {var type=typeof(GameLog).Assembly.GetType("Game.Core.OnlineCoinSnapshot");Assert.That(type,Is.Not.Null,"缺少Core Coin值合同");return type;}
        private static Array Buffer()=>Array.CreateInstance(CoinType(),12);
        private static int Copy(OnlineInventoryPresentationSource source,Array target) {var method=source.GetType().GetMethod("CopyGroundCoinsTo");Assert.That(method,Is.Not.Null,"表现读口须返回值副本，不泄露源数组");return (int)method.Invoke(source,new object[]{target});}
        private static object Get(object value,string name) {var f=value.GetType().GetField(name);Assert.That(f,Is.Not.Null);return f.GetValue(value);}
        private static void Set(object value,string name,object content) {var f=value.GetType().GetField(name);Assert.That(f,Is.Not.Null,"缺少Coin字段:"+name);f.SetValue(value,content);}
        private static void Change(OnlineInventoryPresentationSnapshot value,string field,object content) {Array coins=(Array)Get(value.Ground,"Coins");object coin=coins.GetValue(0);Set(coin,field,content);coins.SetValue(coin,0);}
    }
}
