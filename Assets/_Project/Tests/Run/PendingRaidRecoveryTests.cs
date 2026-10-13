using Game.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
namespace Game.Run.Tests
{
    public sealed class PendingRaidRecoveryTests : RaidTestFixture
    {
        [Test] public void LegacyThreeCellWandOverflowSurvivesSettlementAndNestedRollback()
        {
            Build();Add(Profile.Items,"spare","wand");
            Assert.That(Profile.Items.TryMove("spare",Bag(3,0)).Success,Is.True);
            for(int y=1;y<3;y++)for(int x=0;x<4;x++)AddAt(x,y);
            Depart();var dto=ProfileSaveMapper.ToDto(Profile,Profile.SaveGeneration);dto.SchemaVersion=11;
            Profile.Items.Catalog.TryGet("wand",out var wand);
            var data=new SerializedObject(wand);data.FindProperty("_shapeWidth").intValue=3;
            var cells=data.FindProperty("_occupiedCells");cells.arraySize=3;
            for(int i=0;i<3;i++)cells.GetArrayElementAtIndex(i).vector2IntValue=new Vector2Int(i,0);
            data.ApplyModifiedPropertiesWithoutUndo();
            var definitions=new System.Collections.Generic.List<ItemDefinition>();
            foreach(var name in new[]{"bag","wand","spell","mod","bottle"}){Profile.Items.Catalog.TryGet(name,out var definition);definitions.Add(definition);}
            var catalog=Store(definitions.ToArray()).Catalog;
            var restored=ProfileSaveMapper.TryRestore(dto,catalog,out var migrated);
            Assert.That(restored.Success,Is.True,restored.Message);
            At(migrated.Items,"spare",ItemLocation.Warehouse);
            Assert.That(migrated.PendingRaid.IsHubAsset("spare"),Is.True,"旧随身杖自动转仓不能被结算当成丢失资产。");
            Assert.That(ProfileSaveMapper.TryRestore(ProfileSaveMapper.ToDto(migrated,migrated.SaveGeneration),catalog,out var reloaded).Success,Is.True);
            var repo=new ProfileSaveRepository("Temp/SinglePlayer/M4-Memory",catalog,Files);
            Files.Data[repo.MainPath]=JsonUtility.ToJson(dto);
            // 新Repository必须先读取磁盘提交代；否则会被并发覆盖保护拒绝，不能绕过该保护。
            Assert.That(repo.TryLoad(out _).Success,Is.True);
            var settled=new RaidSettlementService().TrySettle(reloaded,repo,new RaidSettlementRequest(dto.PendingRaid.RaidId,RaidOutcome.Success),out var hub,out _);
            Assert.That(settled.Success,Is.True,settled.Message);
            At(hub.Items,"spare",ItemLocation.Warehouse);
        }
        private void AddAt(int x,int y)
        {Assert.That(Profile.Items.TryAdd(new RuntimeItemState("fill."+x+"."+y,"spell",1),Bag(x,y)),Is.True);}
        [TestCase(false)][TestCase(true)] public void ReloadResolvesPendingWithoutResumingWorld(bool exploring)
        { Build(); Depart(exploring); var repo=new ProfileSaveRepository("Temp/SinglePlayer/M4-Memory",Profile.Items.Catalog,Files);
            Assert.That(repo.TryLoad(out var loaded).Success,Is.True); Assert.That(new ProfileTransactionJournal(repo).Recover(loaded,out var hub).Success,Is.True);
            Assert.That(hub.PendingRaid,Is.Null); Assert.That(hub.ActorId,Is.EqualTo("player")); Assert.That(hub.Items.Count,Is.EqualTo(exploring?3:7),"失败恢复也保留装备背包本体"); }
        [Test] public void LatestReliableProtectedCountIsUsedAfterCrash()
        { Build(); Depart(); SetCharges("protected",1); Repository.TrySave(Profile); SetCharges("protected",0);
            var repo=new ProfileSaveRepository("Temp/SinglePlayer/M4-Memory",Profile.Items.Catalog,Files); repo.TryLoad(out var loaded); new ProfileTransactionJournal(repo).Recover(loaded,out var hub);
            hub.Items.TryGet("protected",out var card); Assert.That(card.RemainingCharges,Is.EqualTo(1),"断电窗口只承诺最后可靠记录，不伪造每次施法都已落盘。"); }
        [Test] public void UnknownSchemaWithPendingIsPreserved()
        { Build(); Depart(); var dto=ProfileSaveMapper.ToDto(Profile,Profile.SaveGeneration); dto.SchemaVersion=999; string text=JsonUtility.ToJson(dto); Files.Data[Repository.MainPath]=text;
            Assert.That(Repository.TryLoad(out _).Status,Is.EqualTo(ProfilePersistenceStatus.UnsupportedSchema)); Assert.That(Files.Data[Repository.MainPath],Is.EqualTo(text)); }
        [Test] public void LegacyNestedRollbackSeparatesContainersBeforeSavingAsCurrentSchema()
        {
            Build();Assert.That(Profile.Items.TryAdd(new RuntimeItemState("extra.bottle","bottle",0),Bag(3,0)),Is.True);Depart(false);
            var dto=ProfileSaveMapper.ToDto(Profile,Profile.SaveGeneration);dto.SchemaVersion=9;
            MakeLegacyStack(dto.Items);MakeLegacyStack(dto.PendingRaid.Rollback.Items);
            Assert.That(ProfileSaveMapper.TryRestore(dto,Profile.Items.Catalog,out var migrated).Success,Is.True);
            var next=ProfileSaveMapper.ToDto(migrated,migrated.SaveGeneration);
            Assert.That(ProfileSaveMapper.TryRestore(next,Profile.Items.Catalog,out var reloaded).Success,Is.True,"新版保存不应残留无法恢复的旧回滚重叠");
            Assert.That(new RaidPreparationService().TryRollback(reloaded,Repository,out var hub).Success,Is.True);
            hub.Items.TryGetLocation("bottle",out var first);hub.Items.TryGetLocation("extra.bottle",out var second);
            Assert.That(first,Is.Not.EqualTo(second));Assert.That(hub.Items.Count,Is.EqualTo(8));
        }
        private static void MakeLegacyStack(SinglePlayerItemDto[] items)
        {
            SinglePlayerItemDto first=null,second=null;
            foreach(var item in items){if(item.InstanceId=="bottle")first=item;if(item.InstanceId=="extra.bottle")second=item;}
            second.LocationKind=first.LocationKind;second.OwnerId=first.OwnerId;second.X=first.X;second.Y=first.Y;second.QuarterTurns=first.QuarterTurns;
        }
        [Test] public void LegacyRaidOverflowRemainsInWarehouseAfterSuccessfulSettlement()
        {
            Build();Add(Profile.Items,"extra.bottle","bottle");
            for(int y=0;y<3;y++)for(int x=0;x<4;x++)Profile.Items.TryAdd(new RuntimeItemState("fill."+x+"."+y,"spell",1),Bag(x,y));
            Depart();var dto=ProfileSaveMapper.ToDto(Profile,Profile.SaveGeneration);dto.SchemaVersion=9;
            MakeLegacyStack(dto.Items);MakeLegacyStack(dto.PendingRaid.Rollback.Items);
            // 构造旧版真实“随身同格瓶”的名单，而不是把额外瓶误当成原有仓库资产。
            dto.PendingRaid.HubAssetIds=System.Array.FindAll(dto.PendingRaid.HubAssetIds,id=>id!="extra.bottle");
            Assert.That(ProfileSaveMapper.TryRestore(dto,Profile.Items.Catalog,out var migrated).Success,Is.True);
            Assert.That(migrated.PendingRaid.IsHubAsset("extra.bottle"),Is.True,"迁移转仓的旧资产不能在结算时当作掉落遗失");
            Assert.That(ProfileSaveMapper.TryRestore(ProfileSaveMapper.ToDto(migrated,migrated.SaveGeneration),Profile.Items.Catalog,out var reloaded).Success,Is.True);
            Assert.That(new RaidSettlementService().TrySettle(reloaded,Repository,new RaidSettlementRequest(dto.PendingRaid.RaidId,RaidOutcome.Success),out var hub,out _).Success,Is.True);
            Assert.That(hub.Items.TryGet("extra.bottle",out _),Is.True);hub.Items.TryGetLocation("extra.bottle",out var location);
            Assert.That(location,Is.EqualTo(ItemLocation.Warehouse));
        }
        [Test] public void LegacyOverflowWithoutDepartureIdentityRejectsCandidateRatherThanLosingAsset()
        {
            Build();Add(Profile.Items,"extra.bottle","bottle");
            for(int y=0;y<3;y++)for(int x=0;x<4;x++)Profile.Items.TryAdd(new RuntimeItemState("fill."+x+"."+y,"spell",1),Bag(x,y));
            Depart();var dto=ProfileSaveMapper.ToDto(Profile,Profile.SaveGeneration);dto.SchemaVersion=9;MakeLegacyStack(dto.Items);
            dto.PendingRaid.HubAssetIds=System.Array.FindAll(dto.PendingRaid.HubAssetIds,id=>id!="extra.bottle");
            dto.PendingRaid.Rollback.Items=System.Array.FindAll(dto.PendingRaid.Rollback.Items,item=>item.InstanceId!="extra.bottle");
            string before=JsonUtility.ToJson(dto);
            Assert.That(ProfileSaveMapper.TryRestore(dto,Profile.Items.Catalog,out var candidate).Success,Is.False);
            Assert.That(candidate,Is.Null);Assert.That(JsonUtility.ToJson(dto),Is.EqualTo(before),"失败不得改动输入旧档或造出出发前资产");
        }
    }
}
