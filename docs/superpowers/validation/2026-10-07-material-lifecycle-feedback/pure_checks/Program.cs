using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Game.ElementField;
using Game.Materials;
using UnityEngine;

// 直接加载本轮编译的生产程序集，只运行 managed 数据逻辑；没有 Unity lifecycle、Collider 或 Shader。
// Profile 在外部 CLR 中绕过 native 构造，仅填本检查所需数据字段，不能把它当作 Editor 验收。
internal static class Program
{
    private static int _passed;
    private static void Check(bool value,string message)
    {if(!value)throw new Exception(message);_passed++;Console.WriteLine("PASS "+message);}
    private static GasWorldProfile Profile()
    {
        var p=(GasWorldProfile)RuntimeHelpers.GetUninitializedObject(typeof(GasWorldProfile));
        Set(p,"_diluteMaximumCellGmu",1);Set(p,"_diluteMaximumChunkConcentration",.005f);
        Set(p,"_diluteDelaySeconds",3f);Set(p,"_diluteChunksPerTick",2);return p;
    }
    private static void Set(object target,string field,object value)=>target.GetType().GetField(field,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(target,value);
    private sealed class Port:IMaterialConversionPort
    {
        internal uint Source=16,Water,Version;internal bool Full,Prepared;
        public bool TryPrepare(MaterialConversionAddress s,uint q,MaterialConversionPlacement[] t,int n,out MaterialConversionStamp stamp)
        {stamp=new MaterialConversionStamp(0,0,Version,Source);if(Full || Prepared || Source<q)return false;Prepared=true;return true;}
        public bool TryCommit(MaterialConversionAddress s,uint q,MaterialConversionPlacement[] t,int n,in MaterialConversionStamp stamp)
        {if(!Prepared || Version!=stamp.Gas)return false;Source-=q;for(int i=0;i<n;i++)Water+=t[i].Quantity;Prepared=false;return true;}
        public void Cancel()=>Prepared=false;
    }
    private static void Main()
    {
        Console.WriteLine("Evidence: managed production data logic in .NET 8 against installed Unity types; no Editor/Play/Shader.");
        var port=new Port();var ledger=new MaterialWorldQuantityLedger();var tx=new MaterialConversionTransaction(port,ledger);
        var source=new MaterialConversionAddress(Vector3Int.zero,MaterialId.Steam);
        var products=new[]{new MaterialConversionPlacement(Vector3Int.zero,MaterialId.Water,1)};
        Check(!tx.TryPrepare(source,8,products,1),"undeclared mass gap rejected");
        port.Full=true;Check(!tx.TryPrepare(source,8,products,1,7) && ledger.ExplicitlyConsumed(MaterialId.Steam)==0,"full target preserves source and ledger");
        port.Full=false;Check(tx.TryPrepare(source,8,products,1,7),"declared low yield can prepare");tx.Cancel();
        Check(port.Source==16 && ledger.ExplicitlyConsumed(MaterialId.Steam)==0,"cancel does not dissipate gas");
        tx.TryPrepare(source,8,products,1,7);port.Version++;
        Check(!tx.Commit() && port.Source==16,"version invalidation rolls back");
        tx.TryPrepare(source,8,products,1,7);Check(tx.Commit(),"low yield commits");
        Check(port.Source==8 && port.Water==1 && ledger.ExplicitlyConsumed(MaterialId.Steam)==7,"input equals water plus background dissipation");
        Check(!tx.Commit(),"duplicate commit rejected");
        var melt=new MaterialConversionRule{Source=MaterialId.Ice,FirstProduct=MaterialId.Water,InputUnits=1,FirstUnits=1,InputGmuPerSecond=1,Condition=MaterialConversionCondition.Melting,Threshold=5,Disabled=true};
        var heat=new MaterialEnvironmentSample(100,.4f);
        Check(melt.IsValid && !MaterialConversionEvaluator.Matches(in melt,in heat),"disabled solid melting is valid and cannot run");
        var store=new GasVolumeStore(8,8,32,new[]{MaterialId.Steam,MaterialId.ToxicVapor});
        var activity=new MaterialCellActivityQueue(128,store);var gasLedger=new MaterialWorldQuantityLedger();
        var cleanup=new GasDiluteDissipation(store,activity,Profile(),gasLedger.RecordExplicitConsumption);
        store.TryAdd(Vector3Int.zero,MaterialId.Steam,1);store.TryAdd(Vector3Int.right*8,MaterialId.Steam,2);
        store.TryAdd(Vector3Int.right*16,MaterialId.Steam,1);store.TryAdd(Vector3Int.right*16,MaterialId.ToxicVapor,1);
        for(int n=0;n<128;n++)store.TryAdd(new Vector3Int(24+n%8,n/8%8,n/64),MaterialId.ToxicVapor,1);
        int maximum=0;for(int i=0;i<60;i++){cleanup.Step(.1f);maximum=Math.Max(maximum,cleanup.CheckedChunksLastTick);}
        Check(store.TotalQuantity==132 && gasLedger.ExplicitlyConsumed(MaterialId.Steam)==1,"only truly dilute single-material tail dissipates");
        Check(maximum<=2,"chunk inspection stays within budget");store.ReclaimEmptyChunks();
        Check(store.ResidentChunkCount==3,"empty chunk reclaimed without dense loss");
        store=new GasVolumeStore(8,2,32,new[]{MaterialId.Steam});activity=new MaterialCellActivityQueue(32,store);
        cleanup=new GasDiluteDissipation(store,activity,Profile(),null);store.TryAdd(Vector3Int.zero,MaterialId.Steam,1);
        for(int i=0;i<20;i++)cleanup.Step(.1f);activity.SetDomain(new ElementWorldActivityRegions(),Vector3.zero,1);
        for(int i=0;i<100;i++)cleanup.Step(.1f);Check(store.TotalQuantity==1,"off-domain tail remains frozen");
        activity.SetDomain(null,Vector3.zero,1);cleanup.Step(.1f);Check(store.TotalQuantity==1,"return does not catch up old grace time");
        for(int i=0;i<31;i++)cleanup.Step(.1f);Check(store.TotalQuantity==0,"new active grace eventually clears thin tail");
        store.ReclaimEmptyChunks();store.TryAdd(Vector3Int.zero,MaterialId.Steam,1);cleanup.Step(.1f);
        Check(store.TotalQuantity==1,"reused physical chunk slot resets dilute age");
        store=new GasVolumeStore(8,64,32,new[]{MaterialId.Steam});activity=new MaterialCellActivityQueue(128,store);
        cleanup=new GasDiluteDissipation(store,activity,Profile(),null);
        for(int i=0;i<64;i++)store.TryAdd(Vector3Int.right*(i*8),MaterialId.Steam,i<32?1u:2u);
        for(int i=0;i<10;i++)cleanup.Step(.1f);long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<150;i++)cleanup.Step(.1f);long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Check(allocated==0 && store.TotalQuantity==64,"bounded cleanup actually removes 32 chunks with zero managed allocation");
        Console.WriteLine("Passed="+_passed+" Failed=0");
    }
}
