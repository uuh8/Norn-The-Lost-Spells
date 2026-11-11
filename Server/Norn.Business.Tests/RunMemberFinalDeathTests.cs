using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Norn.Business.Features.Inventory;
using Norn.Business.Features.Runs;
using Norn.Business.Infrastructure;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>仅最终死亡与旧ESC兼容的直接开发检查；独立账号/Run，不代替实际死亡玩法验收。</summary>
public sealed class RunMemberFinalDeathTests
{
    [Fact,Trait("Category","DatabaseIntegration")]
    public async Task FinalDeathRetiresTheExactCarriedIdsPreservesBagGoldAndContinuesConfirmedLossFacts()
    {
        await using var f=await RunAssetCheckpointServiceTests.Fixture.Create();
        var member=f.Ready.Members.Single(m=>m.ActorId==1);var assets=Clone(member.Profile.CombatAssets);
        var bottle=assets.Items.Single(i=>i.ContainerState!=null);
        var definition=f.Factory.Manifest.Items.Single(i=>i.ContentId==bottle.ContentId);
        byte key=Enumerable.Range(1,8).Select(i=>(byte)i).First(i=>(definition.ContainerMaterialsMask&(1<<i))!=0);
        bottle.ContainerState.Components=[new(){MaterialKey=key,Quantity=7}];bottle.Revision++;assets.InventoryVersion++;
        assets.Gold+=37;assets.CurrencyVersion++;
        var request=Request(f,assets);request.Reason=OnlineRunMemberExitReason.FinalDeath;
        request.FinalRetiredItems=assets.Items.Where(i=>i.LocationKind!=4).Select(i=>new OnlineRunAssetCheckpointRetired
        {InstanceId=i.InstanceId,ContentId=i.ContentId,Revision=i.Revision,Reason=OnlineRunAssetRetiredReason.Destroyed}).ToArray();
        request.FinalDeathLostMaterials=[new(){MaterialKey=key,Quantity=7}];
        var exits=f.Factory.Services.GetRequiredService<RunMemberExitService>();string before=await f.AssetState();
        var wrong=Clone(request);wrong.FinalDeathLostMaterials[0].Quantity=6;
        await Assert.ThrowsAsync<OnlineFailure>(()=>exits.CommitAsync(request.InstanceId,f.Grant.Secret!,wrong,default));
        var incomplete=Clone(request);incomplete.FinalRetiredItems=incomplete.FinalRetiredItems.Skip(1).ToArray();
        await Assert.ThrowsAsync<OnlineFailure>(()=>exits.CommitAsync(request.InstanceId,f.Grant.Secret!,incomplete,default));
        Assert.Equal(before,await f.AssetState());
        var receipt=await exits.CommitAsync(request.InstanceId,f.Grant.Secret!,request,default);
        Assert.Equal(OnlineRunMemberExitReason.FinalDeath,receipt.Reason);Assert.Equal(2u,receipt.SchemaVersion);
        Assert.Equal(7UL,Assert.Single(receipt.FinalDeathLostMaterials).Quantity);Assert.Equal(assets.Gold,receipt.Gold);
        var profile=await f.Factory.Services.GetRequiredService<InventoryService>().GetProfileAsync(request.PlayerId,default);
        Assert.Equal(assets.Gold,profile.Gold);Assert.Equal("",profile.CombatAssets.SelectedWandInstanceId);
        var retained=Assert.Single(profile.CombatAssets.Items);Assert.Equal(4,retained.LocationKind);
        Assert.Equal(assets.Items.Single(i=>i.LocationKind==4).InstanceId,retained.InstanceId);
        Assert.DoesNotContain(profile.Items,i=>request.FinalRetiredItems.Any(r=>r.InstanceId==i.InstanceId));
        await using var db=new MySqlConnection(f.Factory.Connection);
        Assert.Equal(request.FinalRetiredItems.Length,await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM RunItemCustody WHERE RunId=@Run AND State='Retired'",new{Run=request.RunId}));
        Assert.Equal(receipt.PayloadHash,(await exits.CommitAsync(request.InstanceId,f.Grant.Secret!,Clone(request),default)).PayloadHash);
        var next=RunAssetCheckpointServiceTests.Complete(f.Current());next.Coins=[];next.Members=next.Members.Where(m=>m.ActorId==2).ToArray();
        await Assert.ThrowsAsync<OnlineFailure>(()=>f.Save(next));
        next.LostMaterials=[new(){PlayerId=request.PlayerId,ActorId=1,MaterialKey=key,Quantity=7,Source=OnlineRunAssetCheckpointLostMaterial.FinalDeathSource}];
        await f.Save(next);
        var head=await f.Service.ReadHeadAsync(next.InstanceId,f.Grant.Secret!,next.RoomEpoch,default);
        Assert.Single(head.Checkpoint.Members);Assert.Equal(OnlineRunAssetCheckpointLostMaterial.FinalDeathSource,Assert.Single(head.Checkpoint.LostMaterials).Source);
    }

    [Fact,Trait("Category","DatabaseIntegration")]
    public async Task LegacyEscKeepsItsOriginalCanonicalHashAndAllCarriedAssets()
    {
        await using var f=await RunAssetCheckpointServiceTests.Fixture.Create();
        var assets=Clone(f.Ready.Members.Single(m=>m.ActorId==1).Profile.CombatAssets);
        Array.Sort(assets.Items,(a,b)=>StringComparer.Ordinal.Compare(a.InstanceId,b.InstanceId));
        foreach(var item in assets.Items)if(item.ContainerState!=null)Array.Sort(item.ContainerState.Components,(a,b)=>a.MaterialKey.CompareTo(b.MaterialKey));
        var request=Request(f,assets);request.SchemaVersion=OnlineRunMemberExitRequest.LegacySchemaVersion;
        request.Reason=OnlineRunMemberExitReason.None;request.FinalRetiredItems=null!;request.FinalDeathLostMaterials=null!;
        string oldJson=JsonSerializer.Serialize(new{request.SchemaVersion,request.SettlementId,request.InstanceId,request.RunId,request.PlayerId,
            request.RoomEpoch,request.ConnectionEpoch,request.ExpectedPersistentVersion,request.ActorId,request.Assets},OnlineFactory.Json);
        string expected=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(oldJson)));
        var exits=f.Factory.Services.GetRequiredService<RunMemberExitService>();
        var receipt=await exits.CommitAsync(request.InstanceId,f.Grant.Secret!,request,default);
        Assert.Equal(expected,receipt.PayloadHash);Assert.Equal(1u,receipt.SchemaVersion);
        var profile=await f.Factory.Services.GetRequiredService<InventoryService>().GetProfileAsync(request.PlayerId,default);
        Assert.Equal(assets.Items.Length,profile.CombatAssets.Items.Length);Assert.Equal(assets.Gold,profile.Gold);
        Assert.All(assets.Items,item=>Assert.Contains(profile.Items,p=>p.InstanceId==item.InstanceId&&p.RemainingCharges==item.RemainingCharges));
        Assert.Equal(expected,(await exits.CommitAsync(request.InstanceId,f.Grant.Secret!,Clone(request),default)).PayloadHash);
    }

    private static OnlineRunMemberExitRequest Request(RunAssetCheckpointServiceTests.Fixture f,OnlineOwnedAssetSnapshot assets)=>new()
    {
        // 保留真实Schema2最终死亡/ESC兼容覆盖，新Schema3由Home专属用例验证，不能随Current常量改掉旧基线。
        SchemaVersion=OnlineRunMemberExitRequest.LegacyFinalDeathSchemaVersion,SettlementId=Guid.NewGuid().ToString("N"),InstanceId=f.Grant.Descriptor.InstanceId,
        RunId=f.Grant.Descriptor.RunId,RoomEpoch=f.Grant.Descriptor.RoomEpoch,PlayerId=assets.PlayerId,ActorId=assets.ActorId,ConnectionEpoch=1,
        ExpectedPersistentVersion=f.Ready.Members.Single(m=>m.ActorId==assets.ActorId).Profile.Version,Assets=assets,
        Reason=OnlineRunMemberExitReason.VoluntaryExit,FinalRetiredItems=[],FinalDeathLostMaterials=[]
    };
    private static T Clone<T>(T value)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value,OnlineFactory.Json),OnlineFactory.Json)!;
}
