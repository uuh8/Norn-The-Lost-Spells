using System;
using Game.Core;
using NUnit.Framework;
using Shared = Game.NetworkProtocol;

namespace Game.Multiplayer.Tests
{
    /// <summary>个人已退出不能抹去已发生的材料损耗，也不能让其Entry库存重新进入活动成员。</summary>
    public sealed class RunExitedLossCheckpointCaptureTests
    {
        [Test]
        public void ExitedActorsActualFinalLossesRemainWhileOnlyTheOtherActiveMemberIsCaptured()
        {
            var input=Input(out var a,out var b);Array cache=(Array)Read(input,"ExitedLosses");
            Assert.That(RunAssetCheckpointCapture.TryBuild(input,out var captured,out var error),Is.True,error);
            Assert.That(captured.Value.Members.Length,Is.EqualTo(1));var member=captured.Value.Members[0];
            Assert.That(member.PlayerId,Is.EqualTo(b));Assert.That(member.ActorId,Is.EqualTo(2ul));Assert.That(member.ConnectionEpoch,Is.EqualTo(3u));
            Assert.That(member.Assets.Gold,Is.EqualTo(input.Owners[1].Assets.Gold));
            var losses=captured.Value.LostMaterials;Assert.That(losses.Length,Is.EqualTo(2));
            foreach(var loss in losses){Assert.That(loss.PlayerId,Is.EqualTo(a));Assert.That(loss.ActorId,Is.EqualTo(1ul));}
            Assert.That(Array.Find(losses,l=>l.Source==Shared.OnlineRunAssetCheckpointLostMaterial.ContainerTransitSource).Quantity,Is.EqualTo(7ul));
            Assert.That(Array.Find(losses,l=>l.Source==Shared.OnlineRunAssetCheckpointLostMaterial.SpellOutputSource).Quantity,Is.EqualTo(11ul));
            // 候选只保留值，不借用Instance缓存里的可变组分对象。
            ((OnlineOwnedMaterialAmountSnapshot[])Read(cache.GetValue(0),"ContainerLost"))[0].Quantity=99;
            Assert.That(Array.Find(losses,l=>l.Source==Shared.OnlineRunAssetCheckpointLostMaterial.ContainerTransitSource).Quantity,Is.EqualTo(7ul));
            Assert.That(input.Owners[0],Is.Null,"不能为保留损耗读回或重建已退出者的Owned工作区。");
        }

        [Test]
        public void MissingForeignOrNonExitedLossCacheRejectsTheWholeCandidate()
        {
            foreach(string defect in new[]{"missing","run","player","actor","active"})
            {
                var input=Input(out _,out var b);Array cache=(Array)Read(input,"ExitedLosses");object row=cache.GetValue(0);
                if(defect=="missing")Set(input,"ExitedLosses",Array.CreateInstance(cache.GetType().GetElementType(),0));
                if(defect=="run")Set(row,"RunId",new string('f',32));
                if(defect=="player")Set(row,"PlayerId",new string('f',32));
                if(defect=="actor")Set(row,"ActorId",3ul);
                if(defect=="active"){Set(row,"ActorId",2ul);Set(row,"PlayerId",b);}
                Assert.That(RunAssetCheckpointCapture.TryBuild(input,out var captured,out _),Is.False,defect);
                Assert.That(captured,Is.Null,defect);
            }
        }

        private static RunCheckpointCaptureInput Input(out string a,out string b)
        {
            var input=RunAssetCheckpointCaptureTests.Input();
            Assert.That(input.EntryInventory.TryGetFrozenMember(0,out var first),Is.True);a=first.Profile.PlayerId;
            Assert.That(input.EntryInventory.TryGetFrozenMember(1,out var second),Is.True);b=second.Profile.PlayerId;
            var final=input.Owners[0];final.ContainerLost=new[]{new OnlineOwnedMaterialAmountSnapshot{MaterialKey=1,Quantity=7}};
            final.SpellMaterialLost=new[]{new OnlineOwnedMaterialAmountSnapshot{MaterialKey=2,Quantity=11}};
            var field=typeof(RunCheckpointCaptureInput).GetField("ExitedLosses");Assert.That(field,Is.Not.Null,"缺少真实Receipt后保留的Exited最终损耗输入。");
            Type type=field.FieldType.GetElementType();object loss=Activator.CreateInstance(type);
            Set(loss,"RunId",input.Launch.RunId);Set(loss,"PlayerId",a);Set(loss,"ActorId",1ul);
            Set(loss,"ContainerLost",final.ContainerLost);Set(loss,"SpellMaterialLost",final.SpellMaterialLost);
            Array values=Array.CreateInstance(type,1);values.SetValue(loss,0);field.SetValue(input,values);
            Assert.That(input.EntryInventory.MarkMemberExited(1,a),Is.True);input.Owners[0]=null;
            var assets=OnlineOwnedAssetMapping.ToCore(second.Profile.CombatAssets);
            input.Owners[1]=new OnlineOwnedCheckpointSnapshot {Assets=assets,ResourceVersion=2,SelectionVersion=1,SpellUseVersion=1,
                Wands=new[]{new OnlineInventoryWandPresentationSnapshot{InstanceId=assets.SelectedWandInstanceId,ProgramRevision=3,CurrentMana=4,CooldownRemaining=.2f}}};
            input.ConnectionEpochs[1]=3;return input;
        }
        private static object Read(object value,string name){var field=value.GetType().GetField(name);Assert.That(field,Is.Not.Null,name);return field.GetValue(value);}
        private static void Set(object value,string name,object content){var field=value.GetType().GetField(name);Assert.That(field,Is.Not.Null,name);field.SetValue(value,content);}
    }
}
