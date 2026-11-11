using System;
using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using Shared = Game.NetworkProtocol;

namespace Game.Multiplayer.Tests
{
    /// <summary>实际捕获器和Pending固定正文的Coin闭环；反射只兼容RED阶段缺字段，不模拟生产保存成功。</summary>
    public sealed class RunCoinCheckpointTests
    {
        [Test] public void CompleteFourIncludesCollectedTombstoneAndDeepCopiesOriginalFacts()
        {
            var input=Input(); Assert.That(RunAssetCheckpointCapture.TryBuild(input,out var captured,out var error),Is.True,error);
            Assert.That(captured.Value.SchemaVersion,Is.EqualTo(4u)); Array coins=Coins(captured.Value);
            Assert.That(coins.Length,Is.EqualTo(2)); Assert.That(Read(coins.GetValue(1),"Collected"),Is.True);
            Assert.That(Read(coins.GetValue(1),"CollectorActorId"),Is.EqualTo(1ul));
            Assert.That(Read(coins.GetValue(1),"Amount"),Is.EqualTo(long.MaxValue));
            Array source=(Array)Read(input,"Coins");object first=source.GetValue(0);Set(first,"Amount",999L);source.SetValue(first,0);
            Assert.That(Read(coins.GetValue(0),"Amount"),Is.EqualTo(23L));
        }
        [Test] public void MissingCoinPartitionCannotMasqueradeAsKnownEmptyWorld()
        { var input=Input();Set(input,"Coins",null);Assert.That(RunAssetCheckpointCapture.TryBuild(input,out var captured,out _),Is.False);Assert.That(captured,Is.Null); }
        [TestCase("duplicate")] [TestCase("id")] [TestCase("amount")] [TestCase("position")] [TestCase("collector")] [TestCase("active-view")]
        public void InvalidCoinPartitionRejectsTheWholeCapturedCandidate(string defect)
        {
            var input=Input();Array coins=(Array)Read(input,"Coins");object coin=coins.GetValue(0);
            if(defect=="duplicate")Set(coin,"Id",6ul);if(defect=="id")Set(coin,"Id",17ul);if(defect=="amount")Set(coin,"Amount",0L);
            if(defect=="position")Set(coin,"Position",new Vector3(float.NaN,0,0));if(defect=="collector")Set(coin,"CollectorActorId",1ul);
            coins.SetValue(coin,0);if(defect=="active-view")input.Ground.Coins=Array.Empty<OnlineCoinSnapshot>();
            Assert.That(RunAssetCheckpointCapture.TryBuild(input,out var captured,out _),Is.False);Assert.That(captured,Is.Null);
        }
        [Test] public void FixedBodyRetainsCoinsAndAReorderedEquivalentHeadCanConfirm()
        {
            var captured=Capture();var pending=new RunCheckpointPending();Assert.That(pending.TryPrepare(captured,out var error),Is.True,error);
            string fixedBody=pending.Body;StringAssert.Contains("\"Coins\"",fixedBody);Array original=Coins(captured.Value);
            Set(original.GetValue(0),"Amount",888L);Assert.That(pending.Body,Is.EqualTo(fixedBody));
            var correct=Capture();Assert.That(pending.ObserveReceipt(Receipt(correct)),Is.True);
            var head=Head(correct);Array.Reverse(Coins(head.Checkpoint));Assert.That(pending.ConfirmReadback(head),Is.True);
            Assert.That(pending.LastConfirmedSequence,Is.EqualTo(1ul));
        }
        [TestCase("amount")] [TestCase("position")] [TestCase("winner")] [TestCase("collected")] [TestCase("missing")]
        public void ChangedOrMissingCoinCannotAdvanceReceiptAndHeadWatermarks(string defect)
        {
            var captured=Capture();var pending=new RunCheckpointPending();Assert.That(pending.TryPrepare(captured,out _),Is.True);
            Assert.That(pending.ObserveReceipt(Receipt(captured)),Is.True);var bad=Head(captured);Array coins=Coins(bad.Checkpoint);
            object coin=coins.GetValue(1);
            if(defect=="amount")Set(coin,"Amount",99L);if(defect=="winner")Set(coin,"CollectorActorId",2ul);
            if(defect=="collected"){Set(coin,"Collected",false);Set(coin,"CollectorActorId",0ul);}
            if(defect=="position")Set(Read(coin,"Position"),"X",8f);if(defect=="missing")Set(bad.Checkpoint,"Coins",null);
            Assert.That(pending.ConfirmReadback(bad),Is.False);Assert.That(pending.HasPending,Is.True);
            Assert.That(pending.LastConfirmedSequence,Is.Zero);Assert.That(pending.LastAcknowledgedRetirementSequence,Is.Zero);
            Assert.That(pending.ConfirmReadback(Head(captured)),Is.True);
        }
        private static RunCheckpointCaptureInput Input()
        {
            var input=RunAssetCheckpointCaptureTests.Input();var values=new[] {
                new OnlineCoinCheckpointRecord {Id=5,Amount=23,Position=new Vector3(1,2,3)},
                new OnlineCoinCheckpointRecord {Id=6,Amount=long.MaxValue,Position=new Vector3(-4,0,2),Collected=true,CollectorActorId=1}};
            Set(input,"Coins",values); input.Ground.Coins=new[] {new OnlineCoinSnapshot {Id=5,Amount=23,Position=new Vector3(1,2,3)}};return input;
        }
        private static CapturedRunCheckpoint Capture(){Assert.That(RunAssetCheckpointCapture.TryBuild(Input(),out var value,out var error),Is.True,error);return value;}
        private static Shared.OnlineRunAssetCheckpointReceipt Receipt(CapturedRunCheckpoint value)=>new Shared.OnlineRunAssetCheckpointReceipt {
            CheckpointId=value.Value.CheckpointId,InstanceId=value.Value.InstanceId,RunId=value.Value.RunId,RoomEpoch=value.Value.RoomEpoch,Sequence=value.Value.Sequence,PayloadHash=new string('f',64)};
        private static Shared.OnlineRunAssetCheckpointHeadReply Head(CapturedRunCheckpoint value)=>new Shared.OnlineRunAssetCheckpointHeadReply {
            Receipt=Receipt(value),IsComplete=true,Checkpoint=JsonUtility.FromJson<Shared.OnlineRunAssetCheckpoint>(JsonUtility.ToJson(value.Value))};
        private static Array Coins(object value)=>(Array)Read(value,"Coins");
        private static object Read(object value,string name){var f=value.GetType().GetField(name);Assert.That(f,Is.Not.Null,"缺少显式Coin事实字段:"+name);return f.GetValue(value);}
        private static void Set(object value,string name,object content){var f=value.GetType().GetField(name);Assert.That(f,Is.Not.Null,"缺少显式Coin事实字段:"+name);f.SetValue(value,content);}
    }
}
