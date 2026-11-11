using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using MySqlConnector;
using Norn.Business.Content;
using Norn.Business.Features.Inventory;
using Norn.Business.Features.Rooms;
using Norn.Business.Features.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>真实注册账号及其自建隔离物品经过原HTTP移动入口；不把本地草稿当成已提交账号资产。</summary>
public sealed class AccountInventoryMoveTests
{
    private static readonly (string Name,Type Type)[] Fields=
    [
        ("OperationKind",typeof(int)),("TargetLocationKind",typeof(int)),("TargetParentInstanceId",typeof(string)),
        ("GridX",typeof(int)),("GridY",typeof(int)),("QuarterTurns",typeof(int)),("WholeStack",typeof(bool))
    ];
    private static void Contracts()
    {
        foreach(var (name,type) in Fields)
        {
            var request=typeof(InventoryMoveRequest).GetField(name);Assert.NotNull(request);Assert.Equal(type,request.FieldType);
            var reply=typeof(InventoryMoveReply).GetField(name);Assert.NotNull(reply);Assert.Equal(type,reply.FieldType);
        }
    }
    [Fact]
    public void StructuredMoveHasExplicitFieldsAndLegacyDefaultOperation()
    {
        Contracts();Assert.Equal(0,Get<int>(new InventoryMoveRequest(),"OperationKind"));
        Assert.Equal("",Get<string>(new InventoryMoveRequest(),"TargetParentInstanceId"));
    }

    [Theory,InlineData(1),InlineData(7),InlineData(0)]
    [Trait("Category","DatabaseIntegration")]
    public async Task RealInstalledCardCanMoveToBagSpellStorageOrWarehouseAndBack(int destination)
    {
        Contracts();await using var f=await Fixture.Create();var card=f.Profile.Items.Single(i=>i.LocationKind==2);
        var move=f.Move(card.InstanceId,destination,destination==1?f.Bag:"",destination==7?11:-1);
        var committed=await f.Post(move);Assert.Equal(HttpStatusCode.OK,committed.StatusCode);
        var receipt=await committed.Content.ReadFromJsonAsync<InventoryMoveReply>(OnlineFactory.Json);Assert.NotNull(receipt);
        Assert.Equal(1,Get<int>(receipt,"OperationKind"));Assert.Equal(destination,Get<int>(receipt,"TargetLocationKind"));
        var actual=f.Profile.Items.Single(i=>i.InstanceId==card.InstanceId);
        Assert.Equal(card.ContentId,actual.ContentId);Assert.Equal(card.RemainingCharges,actual.RemainingCharges);
        Assert.Equal(card.HasUnlimitedCharges,actual.HasUnlimitedCharges);Assert.Equal(destination,actual.LocationKind);
        Assert.Equal(card.InstanceId,receipt.InstanceId);Assert.Equal(f.Profile.Version,receipt.CommittedVersion);
        if(destination==0)Assert.InRange(actual.Slot,0,15);
        var back=f.Move(card.InstanceId,2,card.ParentInstanceId,card.Slot);
        Assert.Equal(HttpStatusCode.OK,(await f.Post(back)).StatusCode);
        Assert.Equal(card.ParentInstanceId,f.Profile.Items.Single(i=>i.InstanceId==card.InstanceId).ParentInstanceId);
        Assert.Equal(4,f.Profile.Items.Length);Assert.Equal(f.InitialGold,f.Profile.Gold);
    }

    [Fact,Trait("Category","DatabaseIntegration")]
    public async Task EquippedWandAndItsProgramCanBeStoredWithExplicitEmptySelection()
    {
        Contracts();await using var f=await Fixture.Create();var wand=f.Profile.Items.Single(i=>i.LocationKind==3);
        var card=f.Profile.Items.Single(i=>i.ParentInstanceId==wand.InstanceId);
        Assert.Equal(HttpStatusCode.OK,(await f.Post(f.Move(wand.InstanceId,0))).StatusCode);
        Assert.Equal("",f.Profile.CombatAssets.SelectedWandInstanceId);
        Assert.Contains(f.Profile.Items,i=>i.InstanceId==card.InstanceId&&i.ParentInstanceId==wand.InstanceId&&i.LocationKind==2);
        Assert.DoesNotContain(f.Profile.CombatAssets.Items,i=>i.InstanceId==wand.InstanceId||i.InstanceId==card.InstanceId);
        Assert.Equal(HttpStatusCode.OK,(await f.Post(f.Move(wand.InstanceId,3,"",0))).StatusCode);
        Assert.Equal(wand.InstanceId,f.Profile.CombatAssets.SelectedWandInstanceId);
        Assert.Contains(f.Profile.CombatAssets.Items,i=>i.InstanceId==card.InstanceId);
    }

    [Fact,Trait("Category","DatabaseIntegration")]
    public async Task WandEquipAtomicallySwapsRealParentProgramsWithTheSourceWarehouseSlot()
    {
        Contracts();await using var f=await Fixture.Create();var original=f.Profile.Items.Single(i=>i.LocationKind==3);
        var oldCard=f.Profile.Items.Single(i=>i.LocationKind==2);var other=await f.AddWand(0);
        Assert.Equal(HttpStatusCode.OK,(await f.Post(f.Move(other.Wand,3,"",0))).StatusCode);
        Assert.Equal(other.Wand,f.Profile.CombatAssets.SelectedWandInstanceId);
        Assert.Contains(f.Profile.Items,i=>i.InstanceId==original.InstanceId&&i.LocationKind==0&&i.Slot==0);
        Assert.Contains(f.Profile.Items,i=>i.InstanceId==oldCard.InstanceId&&i.ParentInstanceId==original.InstanceId);
        Assert.Contains(f.Profile.CombatAssets.Items,i=>i.InstanceId==other.Card&&i.ParentInstanceId==other.Wand);
        Assert.DoesNotContain(f.Profile.CombatAssets.Items,i=>i.InstanceId==oldCard.InstanceId);
        Assert.Equal(6,f.Profile.Items.Length);Assert.Equal(f.InitialGold,f.Profile.Gold);
    }

    [Fact,Trait("Category","DatabaseIntegration")]
    public async Task CardPlacementSwapsExistingProgramSlotsWithoutReplacingInstancesOrCharges()
    {
        Contracts();await using var f=await Fixture.Create();var card=f.Profile.Items.Single(i=>i.LocationKind==2);
        var other=await f.AddWand(0);
        Assert.Equal(HttpStatusCode.OK,(await f.Post(f.Move(card.InstanceId,2,other.Wand,0))).StatusCode);
        Assert.Contains(f.Profile.Items,i=>i.InstanceId==card.InstanceId&&i.ParentInstanceId==other.Wand&&i.Slot==0&&i.RemainingCharges==card.RemainingCharges);
        Assert.Contains(f.Profile.Items,i=>i.InstanceId==other.Card&&i.ParentInstanceId==card.ParentInstanceId&&i.Slot==card.Slot&&i.RemainingCharges==5);
    }

    [Fact,Trait("Category","DatabaseIntegration")]
    public async Task ContainerToolBagAndWarehouseMovesKeepEveryMaterialComponent()
    {
        Contracts();await using var f=await Fixture.Create();var bottle=f.Profile.Items.Single(i=>i.InstanceId==f.Bottle);
        string original=JsonSerializer.Serialize(bottle.ContainerState,OnlineFactory.Json);
        foreach(int destination in new[]{1,0,8})
        {
            Assert.Equal(HttpStatusCode.OK,(await f.Post(f.Move(f.Bottle,destination,destination==1?f.Bag:"",destination==8?2:-1))).StatusCode);
            Assert.Equal(original,JsonSerializer.Serialize(f.Profile.Items.Single(i=>i.InstanceId==f.Bottle).ContainerState,OnlineFactory.Json));
        }
        Assert.Equal(4,f.Profile.Items.Length);Assert.Equal(f.InitialGold,f.Profile.Gold);
    }

    [Fact,Trait("Category","DatabaseIntegration")]
    public async Task BackpackReplacementMovesAllChildCoordinatesAndStoresTheOldBagInTheVacatedSlot()
    {
        Contracts();await using var f=await Fixture.Create();string other=await f.AddBag(0);
        var card=f.Profile.Items.Single(i=>i.LocationKind==2);
        Assert.Equal(HttpStatusCode.OK,(await f.Post(f.Move(card.InstanceId,1,f.Bag,-1,1,2,1))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await f.Post(f.Move(other,4,"",0))).StatusCode);
        Assert.Contains(f.Profile.Items,i=>i.InstanceId==other&&i.LocationKind==4);
        Assert.Contains(f.Profile.Items,i=>i.InstanceId==f.Bag&&i.LocationKind==0&&i.Slot==0);
        Assert.Contains(f.Profile.Items,i=>i.InstanceId==card.InstanceId&&i.ParentInstanceId==other&&i.X==1&&i.Y==2&&i.QuarterTurns==1);
        Assert.Equal(5,f.Profile.Items.Length);
    }

    [Fact,Trait("Category","DatabaseIntegration")]
    public async Task WholeStackMovesOnlyServerDerivedOriginalMembersAndSingleMoveSplitsNoNewAsset()
    {
        Contracts();await using var f=await Fixture.Create(syntheticStack:true);var ids=await f.AddStack();
        Console.WriteLine("SyntheticStackFactory: only this isolated HTTP case uses a cloned test catalog with MaxStack=3; official content is unchanged.");
        using var first=await f.Post(f.Move(ids[0],1,f.Bag,-1,3,0,0,true));
        if(!first.IsSuccessStatusCode)
        {
            var rules=f.Factory.Manifest.Items.Single(i=>i.ContentId==f.Profile.Items.Single(i=>i.InstanceId==ids[0]).ContentId);
            Console.WriteLine("WholeStack diagnostic: "+await first.Content.ReadAsStringAsync()
                +" Catalog="+JsonSerializer.Serialize(new{rules.ContentId,rules.MaxStack,rules.ShapeWidth,rules.ShapeHeight},OnlineFactory.Json));
        }
        Assert.Equal(HttpStatusCode.OK,first.StatusCode);
        Assert.All(ids,id=>Assert.Contains(f.Profile.Items,i=>i.InstanceId==id&&i.X==3&&i.Y==0&&i.ParentInstanceId==f.Bag));
        Assert.Equal(HttpStatusCode.OK,(await f.Post(f.Move(ids[0],1,f.Bag,-1,0,3))).StatusCode);
        Assert.Contains(f.Profile.Items,i=>i.InstanceId==ids[0]&&i.X==0&&i.Y==3);
        Assert.All(ids.Skip(1),id=>Assert.Contains(f.Profile.Items,i=>i.InstanceId==id&&i.X==3&&i.Y==0));
        Assert.Equal(7,f.Profile.Items.Length);
    }

    [Theory,InlineData("foreign"),InlineData("parent"),InlineData("bounds"),InlineData("wrong-kind"),InlineData("world"),InlineData("warehouse-full")]
    [Trait("Category","DatabaseIntegration")]
    public async Task InvalidIntentFailsWithoutChangingAnyOriginalAssetsOrReceipt(string defect)
    {
        Contracts();await using var f=await Fixture.Create();var card=f.Profile.Items.Single(i=>i.LocationKind==2);
        var move=f.Move(card.InstanceId,1,f.Bag);
        switch(defect)
        {
            case "foreign":var other=await f.Factory.NewPlayerAsync();move.InstanceId=other.Profile.Items[0].InstanceId;break;
            case "parent":Set(move,"TargetParentInstanceId",Guid.NewGuid().ToString("N"));break;
            case "bounds":Set(move,"GridX",999);break;
            case "wrong-kind":Set(move,"TargetLocationKind",8);move.Slot=1;Set(move,"TargetParentInstanceId","");break;
            case "world":Set(move,"TargetLocationKind",5);Set(move,"TargetParentInstanceId","");break;
            case "warehouse-full":await f.FillWarehouse();move=f.Move(card.InstanceId,0);break;
        }
        string before=await f.State();using var result=await f.Post(move);
        Assert.True(result.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound or HttpStatusCode.BadRequest);
        Assert.Equal(before,await f.State());
    }

    [Fact,Trait("Category","DatabaseIntegration")]
    public async Task FixedCommandReplaysItsOriginalReceiptAndRejectsChangedOrStaleWrites()
    {
        Contracts();await using var f=await Fixture.Create();var card=f.Profile.Items.Single(i=>i.LocationKind==2);
        var move=f.Move(card.InstanceId,7,"",11);using var first=await f.Post(move);
        Assert.Equal(HttpStatusCode.OK,first.StatusCode);string body=await first.Content.ReadAsStringAsync();
        using var repeated=await f.Post(move);Assert.Equal(body,await repeated.Content.ReadAsStringAsync());
        string before=await f.State();move.Slot=10;
        Assert.Equal(HttpStatusCode.Conflict,(await f.Post(move)).StatusCode);
        move.CommandId=Guid.NewGuid().ToString("N");Assert.Equal(HttpStatusCode.Conflict,(await f.Post(move)).StatusCode);
        Assert.Equal(before,await f.State());
    }

    [Fact,Trait("Category","DatabaseIntegration")]
    public async Task ExistingReceiptSurvivesRunLeaseButNewAccountMoveCannotWriteFrozenAssets()
    {
        Contracts();await using var f=await Fixture.Create();var card=f.Profile.Items.Single(i=>i.LocationKind==2);
        var move=f.Move(card.InstanceId,7,"",11);Assert.Equal(HttpStatusCode.OK,(await f.Post(move)).StatusCode);
        var second=await f.Factory.NewPlayerAsync();var room=await RoomAdmissionTests.Create(f.Client);
        using var join=await RoomAdmissionTests.Join(second.Client,room.RoomId);Assert.Equal(HttpStatusCode.OK,join.StatusCode);
        room=await RoomAdmissionTests.View(f.Client,room.RoomId);room=await RoomAdmissionTests.Ready(f.Client,room,f.Profile.Version);
        room=await RoomAdmissionTests.Ready(second.Client,room,second.Profile.Version);
        var run=await f.Factory.Services.GetRequiredService<RunEntryService>().PrepareAsync(f.Profile.PlayerId,room.RoomId,room.ReadyRevision,default);
        f.Run=run.RunId;f.Epoch=run.RoomEpoch;
        Assert.Equal(HttpStatusCode.OK,(await f.Post(move)).StatusCode);
        string before=await f.State();var fresh=f.Move(card.InstanceId,7,"",10);
        Assert.Equal(HttpStatusCode.Conflict,(await f.Post(fresh)).StatusCode);Assert.Equal(before,await f.State());
    }

    [Fact,Trait("Category","DatabaseIntegration")]
    public async Task ProtectedBackpackCanBeAtomicallyReplacedAndReequippedWithoutGrantingAnother()
    {
        Contracts();await using var factory=new OnlineFactory();var a=await factory.NewPlayerAsync();var b=await factory.NewPlayerAsync();
        var runs=factory.Services.GetRequiredService<RunEntryService>();string? activeRun=null;uint epoch=0;
        try
        {
            var room=await RoomAdmissionTests.Create(a.Client);
            using var join=await RoomAdmissionTests.Join(b.Client,room.RoomId);Assert.Equal(HttpStatusCode.OK,join.StatusCode);
            room=await RoomAdmissionTests.View(a.Client,room.RoomId);room=await RoomAdmissionTests.Ready(a.Client,room,a.Profile.Version);
            room=await RoomAdmissionTests.Ready(b.Client,room,b.Profile.Version);
            var first=await runs.PrepareAsync(a.Profile.PlayerId,room.RoomId,room.ReadyRevision,default);activeRun=first.RunId;epoch=first.RoomEpoch;
            await runs.FailStartAsync(first.RunId,first.RoomEpoch,"Cancelled",default);activeRun=null;
            var profile=(await a.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile",OnlineFactory.Json))!;
            var original=profile.Items.Single(i=>i.EntryBoundBackpack);Assert.Equal(4,original.LocationKind);
            string replacement=Guid.NewGuid().ToString("N");
            await using(var setup=new MySqlConnection(factory.Connection))await setup.ExecuteAsync("""
                INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges) VALUES(@Bag,@Player,@Content,1,0,FALSE);
                INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind) VALUES(@Bag,@Player,'Warehouse',0,NULL)
                """,new{Bag=replacement,Player=profile.PlayerId,Content=original.ContentId});
            // 不能再裸卸最后一包；经原目标4操作换包仍允许原可信包入仓，且全程有实际装备包。
            var move=new InventoryMoveRequest{CommandId=Guid.NewGuid().ToString("N"),ExpectedVersion=profile.Version,InstanceId=replacement,Slot=0};
            Set(move,"OperationKind",1);Set(move,"TargetLocationKind",4);
            using var stored=await a.Client.PostAsJsonAsync("/api/online/inventory/move",move,OnlineFactory.Json);Assert.Equal(HttpStatusCode.OK,stored.StatusCode);
            profile=(await a.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile",OnlineFactory.Json))!;
            var warehouse=profile.Items.Single(i=>i.InstanceId==original.InstanceId);Assert.True(warehouse.EntryBoundBackpack);Assert.Equal(0,warehouse.LocationKind);
            Assert.Contains(profile.Items,i=>i.InstanceId==replacement&&i.LocationKind==4&&!i.EntryBoundBackpack);
            var restore=new InventoryMoveRequest{CommandId=Guid.NewGuid().ToString("N"),ExpectedVersion=profile.Version,InstanceId=original.InstanceId,Slot=0};
            Set(restore,"OperationKind",1);Set(restore,"TargetLocationKind",4);
            using var restored=await a.Client.PostAsJsonAsync("/api/online/inventory/move",restore,OnlineFactory.Json);Assert.Equal(HttpStatusCode.OK,restored.StatusCode);
            profile=(await a.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile",OnlineFactory.Json))!;
            var peer=(await b.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile",OnlineFactory.Json))!;
            room=await RoomAdmissionTests.View(a.Client,room.RoomId);room=await RoomAdmissionTests.Ready(a.Client,room,profile.Version);
            room=await RoomAdmissionTests.Ready(b.Client,room,peer.Version);
            var second=await runs.PrepareAsync(profile.PlayerId,room.RoomId,room.ReadyRevision,default);activeRun=second.RunId;epoch=second.RoomEpoch;
            var entered=(await a.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile",OnlineFactory.Json))!;
            Assert.Contains(entered.Items,i=>i.InstanceId==original.InstanceId&&i.LocationKind==4&&i.EntryBoundBackpack&&i.Revision==warehouse.Revision+1);
            Assert.Equal(a.Profile.Gold,entered.Gold);
            await using var db=new MySqlConnection(factory.Connection);
            Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM EntryBackpackGrants WHERE CharacterId=@Player",new{Player=profile.PlayerId}));
            Assert.Equal(original.InstanceId,await db.QuerySingleAsync<string>("SELECT InstanceId FROM EntryBackpackGrants WHERE CharacterId=@Player",new{Player=profile.PlayerId}));
            Assert.Equal(2,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Items WHERE OwnerCharacterId=@Player AND ContentId=@Content",new{Player=profile.PlayerId,Content=original.ContentId}));
        }
        finally{if(activeRun!=null)await runs.FailStartAsync(activeRun,epoch,"Cancelled",default);}
    }

    [Fact,Trait("Category","DatabaseIntegration")]
    public async Task AccountSelectWandChangesOnlyTheRealEquippedSelectionAndReplaysItsReceipt()
    {
        Contracts();await using var f=await Fixture.Create();var other=await f.AddWand(0);
        var illegal=f.Move(other.Wand,3,"",1);Set(illegal,"OperationKind",2);
        string before=await f.State();Assert.Equal(HttpStatusCode.Conflict,(await f.Post(illegal)).StatusCode);Assert.Equal(before,await f.State());
        Assert.Equal(HttpStatusCode.OK,(await f.Post(f.Move(other.Wand,3,"",1))).StatusCode);
        var item=f.Profile.Items.Single(i=>i.InstanceId==other.Wand);uint version=f.Profile.Version;
        var select=f.Move(other.Wand,3,"",1);Set(select,"OperationKind",2);
        using var first=await f.Post(select);Assert.Equal(HttpStatusCode.OK,first.StatusCode);
        string receipt=await first.Content.ReadAsStringAsync();
        Assert.Equal(other.Wand,f.Profile.CombatAssets.SelectedWandInstanceId);Assert.Equal(version+1,f.Profile.Version);
        Assert.Equal(item.Revision,f.Profile.Items.Single(i=>i.InstanceId==other.Wand).Revision);
        using var repeated=await f.Post(select);Assert.Equal(receipt,await repeated.Content.ReadAsStringAsync());
        var unchanged=f.Move(other.Wand,3,"",1);Set(unchanged,"OperationKind",2);version=f.Profile.Version;
        Assert.Equal(HttpStatusCode.OK,(await f.Post(unchanged)).StatusCode);Assert.Equal(version,f.Profile.Version);
        Assert.Equal(6,f.Profile.Items.Length);Assert.Equal(f.InitialGold,f.Profile.Gold);
    }

    [Fact,Trait("Category","DatabaseIntegration")]
    public async Task AccountOrganizePreservesMaterialAndPermanentlyRetiresOnlyExhaustedOriginalIds()
    {
        await using var f=await Fixture.Create();
        string[] ids=await f.AddMaterialFragments();
        var organize=f.Move(f.Bag,4,"",0);Set(organize,"OperationKind",3);
        using var response=await f.Post(organize);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        string receipt=await response.Content.ReadAsStringAsync();
        var remaining=Assert.Single(f.Profile.Items.Where(i=>ids.Contains(i.InstanceId)));
        Assert.Equal(ids[0],remaining.InstanceId);Assert.Equal(5UL,remaining.MaterialState.Quantity);
        Assert.Equal(0,remaining.X);Assert.Equal(0,remaining.Y);Assert.Equal(f.InitialGold,f.Profile.Gold);
        await using(var db=new MySqlConnection(f.Factory.Connection))
        {
            Assert.Equal(2,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Items WHERE InstanceId IN @Ids",new{Ids=ids}));
            Assert.Equal(ids[1],await db.ExecuteScalarAsync<string>("SELECT InstanceId FROM AccountItemRetirements WHERE OwnerCharacterId=@P",new{P=f.Profile.PlayerId}));
        }
        using var replay=await f.Post(organize);Assert.Equal(receipt,await replay.Content.ReadAsStringAsync());
        string before=await f.State();Assert.Equal(HttpStatusCode.NotFound,(await f.Post(f.Move(ids[1],0))).StatusCode);
        Assert.Equal(before,await f.State());
    }

    [Fact,Trait("Category","DatabaseIntegration")]
    public async Task PrivateHubDropAndPickupPersistTheSameItemsAndFenceOldSpawnRequests()
    {
        await using var f=await Fixture.Create();
        var pose=new OnlineHubWorldItemPose{SceneKey="hub.map1",WorldSceneKey="hub.map1",PositionX=2,PositionY=1,PositionZ=3,PoseKind=2};
        var drop=f.Move(f.Bottle,5);Set(drop,"OperationKind",4);drop.WorldPose=pose;
        using var dropped=await f.Post(drop);Assert.Equal(HttpStatusCode.OK,dropped.StatusCode);
        string receipt=await dropped.Content.ReadAsStringAsync();
        var world=Assert.Single(f.Profile.WorldItems);Assert.Equal(f.Bottle,world.InstanceId);
        Assert.Equal(2,world.Pose.PositionX);Assert.Equal(27UL,f.Profile.Items.Single(i=>i.InstanceId==f.Bottle).ContainerState.Components.Aggregate(0UL,(sum,i)=>sum+i.Quantity));
        Assert.DoesNotContain(f.Profile.CombatAssets.Items,i=>i.InstanceId==f.Bottle);
        using(var replay=await f.Post(drop))Assert.Equal(receipt,await replay.Content.ReadAsStringAsync());
        var pickup=f.Move(f.Bottle,8,"",0);Set(pickup,"OperationKind",5);pickup.WorldOwnerId=world.WorldOwnerId;
        Assert.Equal(HttpStatusCode.OK,(await f.Post(pickup)).StatusCode);Assert.Empty(f.Profile.WorldItems);
        var dropAgain=f.Move(f.Bottle,5);Set(dropAgain,"OperationKind",4);dropAgain.WorldPose=pose;
        Assert.Equal(HttpStatusCode.OK,(await f.Post(dropAgain)).StatusCode);
        Assert.NotEqual(world.WorldOwnerId,Assert.Single(f.Profile.WorldItems).WorldOwnerId);
        var stale=f.Move(f.Bottle,8,"",0);Set(stale,"OperationKind",5);stale.WorldOwnerId=world.WorldOwnerId;
        string before=await f.State();Assert.Equal(HttpStatusCode.Conflict,(await f.Post(stale)).StatusCode);Assert.Equal(before,await f.State());
        var current=f.Move(f.Bottle,8,"",0);Set(current,"OperationKind",5);current.WorldOwnerId=f.Profile.WorldItems[0].WorldOwnerId;
        Assert.Equal(HttpStatusCode.OK,(await f.Post(current)).StatusCode);
        var wand=f.Profile.Items.Single(i=>i.LocationKind==3);var card=f.Profile.Items.Single(i=>i.ParentInstanceId==wand.InstanceId);
        var dropWand=f.Move(wand.InstanceId,5);Set(dropWand,"OperationKind",4);dropWand.WorldPose=pose;
        Assert.Equal(HttpStatusCode.OK,(await f.Post(dropWand)).StatusCode);
        Assert.DoesNotContain(f.Profile.CombatAssets.Items,i=>i.InstanceId==wand.InstanceId||i.InstanceId==card.InstanceId);
        Assert.Equal(card.ParentInstanceId,f.Profile.Items.Single(i=>i.InstanceId==card.InstanceId).ParentInstanceId);
        var takeWand=f.Move(wand.InstanceId,3,"",0);Set(takeWand,"OperationKind",5);takeWand.WorldOwnerId=f.Profile.WorldItems[0].WorldOwnerId;
        Assert.Equal(HttpStatusCode.OK,(await f.Post(takeWand)).StatusCode);
        Assert.Contains(f.Profile.CombatAssets.Items,i=>i.InstanceId==card.InstanceId&&i.RemainingCharges==card.RemainingCharges);
        Assert.Equal(4,f.Profile.Items.Length);Assert.Equal(f.InitialGold,f.Profile.Gold);
        // 原材料拾取：先部分并堆，原Spawn/余量保留；再全部并堆，源ID永久退休而非凭空移位。
        string[] materials=await f.AddMaterialFragments(true);
        ulong capacity=f.Factory.Manifest.Items.Single(i=>i.ContentId==f.Profile.Items.Single(x=>x.InstanceId==materials[0]).ContentId).MaterialItemCapacityGmu;
        await using(var db=new MySqlConnection(f.Factory.Connection))await db.ExecuteAsync(
            "UPDATE ItemMaterialStates SET Quantity=@Amount WHERE InstanceId=@Id",new{Id=materials[0],Amount=capacity-1});
        await f.Refresh();
        var materialDrop=f.Move(materials[2],5);Set(materialDrop,"OperationKind",4);materialDrop.WorldPose=pose;
        Assert.Equal(HttpStatusCode.OK,(await f.Post(materialDrop)).StatusCode);
        string spawn=f.Profile.WorldItems.Single().WorldOwnerId;
        var target=f.Profile.Items.Single(i=>i.InstanceId==materials[0]);
        var partial=f.Move(materials[2],1,f.Bag,-1,target.X,target.Y,target.QuarterTurns);Set(partial,"OperationKind",5);partial.WorldOwnerId=spawn;
        using var partialResponse=await f.Post(partial);Assert.Equal(HttpStatusCode.OK,partialResponse.StatusCode);
        Assert.Equal(1UL,(await partialResponse.Content.ReadFromJsonAsync<InventoryMoveReply>(OnlineFactory.Json))!.Transferred);
        Assert.Equal(spawn,Assert.Single(f.Profile.WorldItems).WorldOwnerId);
        Assert.Equal(3UL,f.Profile.Items.Single(i=>i.InstanceId==materials[2]).MaterialState.Quantity);
        target=f.Profile.Items.Single(i=>i.InstanceId==materials[1]);
        var finish=f.Move(materials[2],1,f.Bag,-1,target.X,target.Y,target.QuarterTurns);Set(finish,"OperationKind",5);finish.WorldOwnerId=spawn;
        using var finished=await f.Post(finish);Assert.Equal(HttpStatusCode.OK,finished.StatusCode);
        Assert.Equal(3UL,(await finished.Content.ReadFromJsonAsync<InventoryMoveReply>(OnlineFactory.Json))!.Transferred);
        Assert.Empty(f.Profile.WorldItems);Assert.DoesNotContain(f.Profile.Items,i=>i.InstanceId==materials[2]);
        Assert.Equal(6UL,f.Profile.Items.Single(i=>i.InstanceId==materials[1]).MaterialState.Quantity);
        Assert.Equal(f.InitialGold,f.Profile.Gold);
    }

    private static void Set(object value,string name,object field)=>value.GetType().GetField(name)!.SetValue(value,field);
    private static T Get<T>(object value,string name)=>(T)value.GetType().GetField(name)!.GetValue(value)!;

    private sealed class Fixture:IAsyncDisposable
    {
        public required OnlineFactory Factory;public required HttpClient Client;public required OnlineProfileReply Profile;
        public required string Bag,Bottle;public long InitialGold;
        public string? Run;public uint Epoch;
        public static async Task<Fixture> Create(bool syntheticStack=false)
        {
            OnlineFactory factory=syntheticStack?new SyntheticStackFactory():new OnlineFactory();var player=await factory.NewPlayerAsync();
            var content=factory.Services.GetRequiredService<ContentManifestValidator>();string bag=player.Profile.Items.Single(i=>i.LocationKind==4).InstanceId,bottle=Guid.NewGuid().ToString("N");
            byte key=Enumerable.Range(1,8).Select(i=>(byte)i).First(i=>(content.RequireEntryContainer().ContainerMaterialsMask&(1<<i))!=0);
            await using(var db=new MySqlConnection(factory.Connection))await db.ExecuteAsync("""
                INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges)
                  VALUES(@Bottle,@Player,@BottleContent,1,0,FALSE);
                INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind)
                  VALUES(@Bottle,@Player,NULL,0,8);
                INSERT INTO ItemContainerStates VALUES(@Bottle,1);
                INSERT INTO ItemContainerComponents VALUES(@Bottle,@Key,27)
                """,new{Bag=bag,Bottle=bottle,Player=player.Profile.PlayerId,BagContent=content.RequireEntryBackpack().ContentId,
                    BottleContent=content.RequireEntryContainer().ContentId,Key=key});
            var f=new Fixture{Factory=factory,Client=player.Client,Profile=player.Profile,Bag=bag,Bottle=bottle,InitialGold=player.Profile.Gold};
            await f.Refresh();return f;
        }
        public InventoryMoveRequest Move(string id,int target,string parent="",int slot=-1,int x=0,int y=0,int turns=0,bool whole=false)
        {
            var move=new InventoryMoveRequest{CommandId=Guid.NewGuid().ToString("N"),ExpectedVersion=Profile.Version,InstanceId=id,Slot=slot};
            Set(move,"OperationKind",1);Set(move,"TargetLocationKind",target);Set(move,"TargetParentInstanceId",parent);
            Set(move,"GridX",x);Set(move,"GridY",y);Set(move,"QuarterTurns",turns);Set(move,"WholeStack",whole);return move;
        }
        public async Task<HttpResponseMessage> Post(InventoryMoveRequest move)
        {var reply=await Client.PostAsJsonAsync("/api/online/inventory/move",move,OnlineFactory.Json);if(reply.IsSuccessStatusCode)await Refresh();return reply;}
        public async Task Refresh()=>Profile=(await Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile",OnlineFactory.Json))!;
        public async Task<(string Wand,string Card)> AddWand(int slot)
        {
            string wand=Guid.NewGuid().ToString("N"),card=Guid.NewGuid().ToString("N");
            await using var db=new MySqlConnection(Factory.Connection);
            await db.ExecuteAsync("""
                INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges)
                  VALUES(@Wand,@Player,@WandContent,1,0,FALSE),(@Card,@Player,@CardContent,1,5,FALSE);
                INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind,ParentInstanceId)
                  VALUES(@Wand,@Player,'Warehouse',@Slot,NULL,NULL),(@Card,@Player,NULL,0,2,@Wand)
                """,new{Wand=wand,Card=card,Player=Profile.PlayerId,Slot=slot,WandContent=Profile.Items.Single(i=>i.LocationKind==3).ContentId,
                    CardContent=Profile.Items.Single(i=>i.LocationKind==2).ContentId});
            await Refresh();return(wand,card);
        }
        public async Task<string> AddBag(int slot)
        {
            string id=Guid.NewGuid().ToString("N");await using var db=new MySqlConnection(Factory.Connection);
            await db.ExecuteAsync("""
                INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges)
                  VALUES(@Id,@Player,@Content,1,0,FALSE);
                INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot)VALUES(@Id,@Player,'Warehouse',@Slot)
                """,new{Id=id,Player=Profile.PlayerId,Content=Profile.Items.Single(i=>i.InstanceId==Bag).ContentId,Slot=slot});await Refresh();return id;
        }
        public async Task<string[]> AddMaterialFragments(bool extra=false)
        {
            string[] ids=Enumerable.Range(0,extra?3:2).Select(_=>Guid.NewGuid().ToString("N")).OrderBy(i=>i,StringComparer.Ordinal).ToArray();
            var definition=Factory.Manifest.Items.First(i=>i.Kind==4&&i.MaterialItemCapacityGmu>=8&&i.ShapeWidth<=2&&i.ShapeHeight<=2);
            await using var db=new MySqlConnection(Factory.Connection);
            for(int i=0;i<ids.Length;i++)await db.ExecuteAsync("""
                INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges)VALUES(@Id,@P,@Content,1,0,FALSE);
                INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind,ParentInstanceId,GridX,GridY)
                  VALUES(@Id,@P,NULL,-1,1,@Bag,@X,4);
                INSERT INTO ItemMaterialStates VALUES(@Id,1,@Key,@Amount)
                """,new{Id=ids[i],P=Profile.PlayerId,Content=definition.ContentId,Bag,X=i*3,Key=definition.MaterialKey,Amount=i+2});
            await Refresh();return ids;
        }
        public async Task<string[]> AddStack()
        {
            string[] ids=Enumerable.Range(0,3).Select(_=>Guid.NewGuid().ToString("N")).ToArray();await using var db=new MySqlConnection(Factory.Connection);
            // 真实火卡MaxStack=1；此case已明确使用局部合成测试目录，合法三叠输入不代表正式内容配置。
            var stackable=Factory.Manifest.Items.FirstOrDefault(i=>i.MaxStack>=3&&i.Kind is 0 or 5 or 6&&i.ShapeWidth<=5&&i.ShapeHeight<=5);
            Assert.NotNull(stackable);
            foreach(string id in ids)await db.ExecuteAsync("""
                INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges)VALUES(@Id,@Player,@Content,1,@Charges,FALSE);
                INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind,ParentInstanceId)VALUES(@Id,@Player,NULL,-1,1,@Bag)
                """,new{Id=id,Player=Profile.PlayerId,Content=stackable.ContentId,Charges=stackable.Kind==0?5:0,Bag});await Refresh();return ids;
        }
        public async Task FillWarehouse()
        {
            await using var db=new MySqlConnection(Factory.Connection);
            for(int slot=0;slot<16;slot++)await db.ExecuteAsync("""
                INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges)VALUES(@Id,@Player,@Content,1,5,FALSE);
                INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot)VALUES(@Id,@Player,'Warehouse',@Slot)
                """,new{Id=Guid.NewGuid().ToString("N"),Player=Profile.PlayerId,Content=Profile.Items.Single(i=>i.LocationKind==2).ContentId,Slot=slot});await Refresh();
        }
        public async Task<string> State()
        {
            await using var db=new MySqlConnection(Factory.Connection);
            var items=await db.QueryAsync("SELECT * FROM Items WHERE OwnerCharacterId=@Player ORDER BY InstanceId",new{Player=Profile.PlayerId});
            var places=await db.QueryAsync("SELECT * FROM ItemLocations WHERE OwnerCharacterId=@Player ORDER BY InstanceId",new{Player=Profile.PlayerId});
            var wallet=await db.QueryAsync("SELECT * FROM Wallets WHERE CharacterId=@Player",new{Player=Profile.PlayerId});
            var versions=await db.QueryAsync("SELECT Version FROM Characters WHERE CharacterId=@Player",new{Player=Profile.PlayerId});
            var receipts=await db.QueryAsync("SELECT * FROM InventoryCommands WHERE CharacterId=@Player ORDER BY CommandId",new{Player=Profile.PlayerId});
            return JsonSerializer.Serialize(new{items,places,wallet,versions,receipts},OnlineFactory.Json);
        }
        public async ValueTask DisposeAsync()
        {
            try{if(Run!=null)await Factory.Services.GetRequiredService<RunEntryService>().FailStartAsync(Run,Epoch,"Cancelled",default);}
            finally{await Factory.DisposeAsync();}
        }
    }

    /// <summary>
    /// 当前正式目录没有三叠普通物，不篡改正式参数来凑通过。仅本case用已有WebHost钩子
    /// 加载Temp深副本，显式提供合法MaxStack=3输入，检验通用原规则与真实HTTP/SQL事务。
    /// 不改变全局环境变量、官方文件或共享OnlineFactory；其他18case继续使用正式目录。
    /// </summary>
    private sealed class SyntheticStackFactory:OnlineFactory
    {
        private readonly string _path=Path.Combine(Path.GetTempPath(),"norn-account-stack-"+Guid.NewGuid().ToString("N")+".json");
        public SyntheticStackFactory()
        {
            var copy=JsonSerializer.Deserialize<ContentManifest>(JsonSerializer.Serialize(Manifest,OnlineFactory.Json),OnlineFactory.Json)!;
            copy.Items.First(i=>i.Kind==0&&i.ShapeWidth<=5&&i.ShapeHeight<=5).MaxStack=3;
            copy.Hash=ContentManifestRules.CalculateHash(copy);
            // Manifest是此fixture从官方JSON独立读出的DTO；同步其私有测试视图以保登录头与Host哈希一致。
            Manifest.Items=copy.Items;Manifest.Hash=copy.Hash;
            File.WriteAllText(_path,JsonSerializer.Serialize(copy,OnlineFactory.Json));
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_,configuration)=>configuration.AddInMemoryCollection(
                new Dictionary<string,string?>{{"Online:ManifestPath",_path}}));
        }
        public override async ValueTask DisposeAsync()
        {
            try{await base.DisposeAsync();}
            finally{if(File.Exists(_path))File.Delete(_path);}
        }
    }
}
