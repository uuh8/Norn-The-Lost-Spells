using System;
using Game.Materials;

namespace Game.ElementField
{
    public enum MaterialQuantityFamily : byte { None, WaterBased, Toxic }
    public sealed class MaterialWorldQuantityLedger
    {
        private readonly ulong[] _consumed = new ulong[256], _produced = new ulong[256];
        private readonly ulong[] _external=new ulong[256], _ruleConsumed=new ulong[256];
        internal void RecordExternal(MaterialId material,ulong quantity) { if(Family(material)!=MaterialQuantityFamily.None) _external[(byte)material]+=quantity; }
        internal void RecordExplicitConsumption(MaterialId material,ulong quantity) { if(Family(material)!=MaterialQuantityFamily.None) _ruleConsumed[(byte)material]+=quantity; }
        public ulong ExternallyInjected(MaterialId material) => _external[(byte)material];
        public ulong ExplicitlyConsumed(MaterialId material) => _ruleConsumed[(byte)material];
        public ulong CommittedTransactions { get; private set; }
        public ulong CancelledTransactions { get; internal set; }
        public ulong Consumed(MaterialId material) => _consumed[(byte)material];
        public ulong Produced(MaterialId material) => _produced[(byte)material];
        public static MaterialQuantityFamily Family(MaterialId id) => id == MaterialId.Water || id == MaterialId.Steam
            || id == MaterialId.Ice || id == MaterialId.Sticky || id == MaterialId.DriedSlime ? MaterialQuantityFamily.WaterBased
            : id == MaterialId.Poison || id == MaterialId.ToxicVapor ? MaterialQuantityFamily.Toxic : MaterialQuantityFamily.None;
        internal bool Allows(MaterialId source, uint consume, MaterialConversionPlacement[] targets, int count)
        {
            MaterialQuantityFamily family=Family(source); if (family==MaterialQuantityFamily.None || consume==0) return false;
            ulong total=0;
            for(int i=0;i<count;i++)
            { if (targets[i].Quantity==0 || Family(targets[i].Address.Material)!=family) return false; total+=targets[i].Quantity; }
            return total==consume;
        }
        internal void Record(MaterialId source, uint consume, MaterialConversionPlacement[] targets, int count)
        {
            _consumed[(byte)source]+=consume;
            for(int i=0;i<count;i++) _produced[(byte)targets[i].Address.Material]+=targets[i].Quantity;
            CommittedTransactions++;
        }
    }
    public readonly struct MaterialQuantityFamilySnapshot
    {
        public readonly ulong Injected, Resident, Pending, Vented, RuleConsumed;
        public bool IsBalanced => Injected==Resident+Pending+Vented+RuleConsumed;
        public MaterialQuantityFamilySnapshot(ulong injected,ulong resident,ulong pending,ulong vented,ulong consumed)
        { Injected=injected; Resident=resident; Pending=pending; Vented=vented; RuleConsumed=consumed; }
    }
}
