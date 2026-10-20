using System;
using System.Collections.Generic;
using Game.Skills;

namespace Game.Run
{
    public enum WandLayerResourceStatus : byte { Unattempted, Prepared, Committed, Cancelled }
    public enum WandWorldExecutionStatus : byte { NotObserved, Released, Cancelled }
    public readonly struct WandUseReceipt
    {
        public readonly string InstanceId;
        public readonly int Count;
        internal WandUseReceipt(string id, int count) { InstanceId = id; Count = count; }
    }
    /// <summary>资源提交证据与世界发射证据分开；服务Sandbox成功不意味着已经生成投射物。</summary>
    public sealed class WandExecutionReplay
    {
        public string TicketId { get; }
        public string SourceWandId { get; }
        public string CallerId { get; }
        public ShotExecutionGraph Graph { get; }
        public SpellExecutionTrace Trace { get; }
        public WandDrawSnapshot Before { get; }
        public WandDrawSnapshot After { get; }
        public SpellExecutionInputs Inputs { get; }
        public SpellBuildSummary Summary { get; }
        public uint InventoryVersionBefore { get; }
        public uint InventoryVersionAfter { get; }
        public IReadOnlyList<WandUseReceipt> UsesPaid { get; }
        public WandWorldExecutionStatus WorldStatus { get; internal set; }
        private readonly WandLayerResourceStatus[] _layers;
        private readonly bool[] _released;
        public IReadOnlyList<bool> ReleasedOutputs { get; }
        private double _lastWorldTime = -1;
        internal WandExecutionReplay(string ticket, string wand, string caller, SpellBuildBuffer buffer,
            WandDrawSnapshot before, WandDrawSnapshot after, in SpellExecutionInputs inputs, in SpellBuildSummary summary,
            uint inventoryBefore, uint inventoryAfter, IReadOnlyDictionary<string, int> uses, WandLayerResourceStatus[] layers)
        {
            TicketId = ticket; SourceWandId = wand; CallerId = caller; Graph = buffer.Graph; Trace = buffer.Trace;
            Before = before; After = after; Inputs = inputs; Summary = summary;
            InventoryVersionBefore = inventoryBefore; InventoryVersionAfter = inventoryAfter; _layers = layers;
            _released=new bool[Graph.Outputs.Count];ReleasedOutputs=Array.AsReadOnly(_released);
            var receipts = new WandUseReceipt[uses.Count]; int i = 0;
            foreach (var paid in uses) receipts[i++] = new WandUseReceipt(paid.Key, paid.Value);
            UsesPaid = Array.AsReadOnly(receipts);
        }
        public WandLayerResourceStatus GetLayerStatus(int layer) => _layers[layer];
        internal bool Observe(int layer,int output,double time)
        {
            if(layer<0||layer>=_layers.Length||_layers[layer]!=WandLayerResourceStatus.Committed
                ||output<0||output>=Graph.Outputs.Count||Graph.Outputs[output].ScopeId!=layer||_released[output]
                ||!double.IsFinite(time)||time<_lastWorldTime)return false;
            _lastWorldTime=time;_released[output]=true;if(layer==0)WorldStatus=WandWorldExecutionStatus.Released;return true;
        }
    }
}
