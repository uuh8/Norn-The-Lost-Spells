using System.Reflection;
using System.Text.Json;
using Game.NetworkProtocol;
using Norn.Business.Features.Runs;
using Norn.Business.Infrastructure;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>当前完整能力显式保存Coin墓碑；历史1/2/3正文不能因为新增字段改变收据Hash。</summary>
public sealed class RunCheckpointCoinFactsTests
{
    [Fact] public void CurrentCompleteCapabilityIsFourAndKeepsAllHistoricalWorldSchemas()
    {
        Assert.Equal(4u,OnlineRunAssetCheckpoint.CompleteSchemaVersion);
        foreach(uint schema in new uint[]{2,3,4})Assert.True(OnlineRunAssetCheckpoint.HasWorldFacts(schema));
        Assert.False(OnlineRunAssetCheckpoint.HasWorldFacts(1));Assert.False(OnlineRunAssetCheckpoint.HasWorldFacts(5));
    }
    [Fact] public void ActiveAndCollectedOriginalCoinAreBothExplicitCompleteFacts()
    { var value=Value(4);SetCoins(value,Coin(5,23,false,0),Coin(6,long.MaxValue,true,1));RunCheckpointCompleteRules.RequireShape(value); }
    [Theory,InlineData("missing"),InlineData("id"),InlineData("amount"),InlineData("position"),InlineData("winner"),InlineData("active-winner"),InlineData("duplicate"),InlineData("overflow")]
    public void MissingMalformedOrUnboundedCoinFactsAreRejected(string defect)
    {
        var value=Value(4);object coin=Coin(5,23,false,0);
        if(defect=="id")Set(coin,"Id",17ul);if(defect=="amount")Set(coin,"Amount",0L);
        if(defect=="position")Set(Read(coin,"Position"),"X",float.NaN);
        if(defect=="winner"){Set(coin,"Collected",true);Set(coin,"CollectorActorId",0ul);}
        if(defect=="active-winner")Set(coin,"CollectorActorId",1ul);
        if(defect=="missing")Set(value,"Coins",null);
        else if(defect=="duplicate")SetCoins(value,coin,Coin(5,23,false,0));
        else if(defect=="overflow")SetCoins(value,Enumerable.Range(0,13).Select(i=>Coin((ulong)(5+i),1,false,0)).ToArray());
        else SetCoins(value,coin);
        Assert.Throws<OnlineFailure>(()=>RunCheckpointCompleteRules.RequireShape(value));
    }
    [Theory,InlineData(1u),InlineData(2u),InlineData(3u)]
    public void HistoricalCanonicalBytesKeepOriginalFieldsAndOwnedCurrencySemantics(uint schema)
    {
        var value=Value(schema);string expected=HistoricalBody(value);string actual=Canonical(value);
        Assert.Equal(expected,actual);Assert.DoesNotContain("\"Coins\"",actual);
        if(schema==3){Assert.Contains("\"Gold\":977",actual);Assert.Contains("\"CurrencyVersion\":9",actual);}
        else Assert.DoesNotContain("\"Gold\"",actual);
    }
    [Theory,InlineData(1u),InlineData(2u),InlineData(3u)]
    public void HistoricalSchemaCannotSmuggleNewCoinFactsThatItsHashWouldOmit(uint schema)
    {var value=Value(schema);SetCoins(value,Coin(5,23,false,0));Assert.Throws<OnlineFailure>(()=>RunCheckpointCompleteRules.RequireShape(value));}

    [Theory,InlineData("missing"),InlineData("amount"),InlineData("position"),InlineData("revived"),InlineData("winner")]
    public void LaterCheckpointCannotForgetOrRewriteAnAlreadyCollectedCoin(string defect)
    {
        var previous=Value(4);SetCoins(previous,Coin(5,23,true,1));var next=Value(4);next.Sequence=2;
        object coin=Coin(5,23,true,1);
        if(defect=="amount")Set(coin,"Amount",24L);if(defect=="position")Set(Read(coin,"Position"),"X",2f);
        if(defect=="revived"){Set(coin,"Collected",false);Set(coin,"CollectorActorId",0ul);}if(defect=="winner")Set(coin,"CollectorActorId",2ul);
        SetCoins(next,defect=="missing"?Array.Empty<object>():new[]{coin});
        var type=typeof(RunAssetCheckpointService).Assembly.GetType("Norn.Business.Features.Runs.RunCheckpointCoinRules");Assert.NotNull(type);
        var method=type.GetMethod("Require",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public);Assert.NotNull(method);
        var error=Assert.Throws<TargetInvocationException>(()=>method.Invoke(null,[next,previous]));Assert.IsType<OnlineFailure>(error.InnerException);
    }

    private static OnlineRunAssetCheckpoint Value(uint schema)=>new() {
        SchemaVersion=schema,CheckpointId=new('e',32),InstanceId=new('d',32),RunId=new('c',32),GameplayConfigHash=new('a',64),RoomEpoch=7,Sequence=1,ServerTick=12,
        Members=[new(){PlayerId=new('b',32),ActorId=1,ConnectionEpoch=2,Assets=new(){SchemaVersion=schema==3||schema==4?3u:2u,
            RunId=new('c',32),PlayerId=new('b',32),ActorId=1,InventoryId="inventory",GameplayConfigHash=new('a',64),InventoryVersion=1,
            Gold=schema==3||schema==4?977:0,CurrencyVersion=schema==3||schema==4?9u:0u},
            Wands=schema==1?null!:[],SpellUses=schema==1?null!:[]}],
        World=schema==1?null!:new(){WorldId="material-world",WorldEpoch=42,WorldTick=1,Chunks=[]},
        Flights=schema==1?null!:[],MaterialOutputs=schema==1?null!:[],LostMaterials=schema==1?null!:[]};
    private static object Coin(ulong id,long amount,bool collected,ulong winner)
    {
        var field=typeof(OnlineRunAssetCheckpoint).GetField("Coins");Assert.NotNull(field);var type=field.FieldType.GetElementType()!;
        object value=Activator.CreateInstance(type)!;Set(value,"Id",id);Set(value,"Amount",amount);Set(value,"Collected",collected);Set(value,"CollectorActorId",winner);
        Set(value,"Position",new OnlineRunAssetCheckpointVector3 {X=1,Y=-2,Z=3});return value;
    }
    private static void SetCoins(OnlineRunAssetCheckpoint value,params object[] coins)
    {var field=value.GetType().GetField("Coins");Assert.NotNull(field);var array=Array.CreateInstance(field.FieldType.GetElementType()!,coins.Length);for(int i=0;i<coins.Length;i++)array.SetValue(coins[i],i);field.SetValue(value,array);}
    private static object Read(object value,string name){var field=value.GetType().GetField(name);Assert.NotNull(field);return field.GetValue(value)!;}
    private static void Set(object value,string name,object content){var field=value.GetType().GetField(name);Assert.NotNull(field);field.SetValue(value,content);}
    private static string Canonical(OnlineRunAssetCheckpoint value)=>(string)typeof(RunAssetCheckpointService).GetMethod("CanonicalJson",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[value])!;
    private static string HistoricalBody(OnlineRunAssetCheckpoint value)
    {
        // 独立重现新增Coin前的字段顺序，禁止用当前生产canonicalizer给自己计算期望值。
        object shape=value.SchemaVersion==1?(object)new {value.SchemaVersion,value.CheckpointId,value.InstanceId,value.RunId,value.GameplayConfigHash,value.RoomEpoch,value.ServerTick,value.Sequence,
            Members=value.Members.Select(m=>new{m.PlayerId,m.ActorId,m.ConnectionEpoch,m.Assets}).ToArray(),
            GroundItems=value.GroundItems.Select(g=>new{g.WorldOwnerId,g.Item,g.Children}).ToArray(),value.RetiredItems}
            :new {value.SchemaVersion,value.CheckpointId,value.InstanceId,value.RunId,value.GameplayConfigHash,value.RoomEpoch,value.ServerTick,value.Sequence,
                value.Members,value.GroundItems,value.RetiredItems,value.World,value.Flights,value.MaterialOutputs,value.LostMaterials};
        var node=JsonSerializer.SerializeToNode(shape,OnlineFactory.Json)!;
        if(value.SchemaVersion<3)foreach(var member in node["Members"]!.AsArray()){member!["Assets"]!.AsObject().Remove("Gold");member["Assets"]!.AsObject().Remove("CurrencyVersion");}
        return node.ToJsonString(new JsonSerializerOptions{WriteIndented=false});
    }
}
