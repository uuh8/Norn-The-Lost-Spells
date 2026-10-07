using System;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>长寿命GPU Buffer，按实际Stride统一计费；Destroy/Domain Reload均可释放。</summary>
    public sealed class GridLiquidFieldResources : IDisposable
    {
        public readonly GraphicsBuffer Previous,Current,Terrain,Lookup,OwnerLookup,Owners,OwnerBounds,Field,Vertices,PositionsA,PositionsB,Indices,Counters,Arguments,Drops;
        public long AllocatedBytes { get; private set; }
        private GraphicsBuffer Make(GraphicsBuffer.Target target,int count,int stride)
        { var buffer=new GraphicsBuffer(target,Mathf.Max(1,count),stride);AllocatedBytes+=(long)Mathf.Max(1,count)*stride;return buffer; }
        public GridLiquidFieldResources(GridLiquidFieldBudget budget)
        {
            try
            {
                int cells=budget.InputChunks*budget.ChunkSize*budget.ChunkSize*budget.ChunkSize;
                Previous=Make(GraphicsBuffer.Target.Structured,cells*budget.MaterialCount,16);
                Current=Make(GraphicsBuffer.Target.Structured,cells*budget.MaterialCount,16);
                Terrain=Make(GraphicsBuffer.Target.Structured,cells,16);
                Lookup=Make(GraphicsBuffer.Target.Structured,budget.LookupSlots,16);
                OwnerLookup=Make(GraphicsBuffer.Target.Structured,budget.LookupSlots,16);
                Owners=Make(GraphicsBuffer.Target.Structured,budget.Views,16);
                OwnerBounds=Make(GraphicsBuffer.Target.Structured,budget.Views*2,16);
                Field=Make(GraphicsBuffer.Target.Structured,budget.MaximumNodeSlots,4);
                Vertices=Make(GraphicsBuffer.Target.Structured,budget.MaximumCubeSlots,48);
                PositionsA=Make(GraphicsBuffer.Target.Structured,budget.MaximumCubeSlots,12);
                PositionsB=Make(GraphicsBuffer.Target.Structured,budget.MaximumCubeSlots,12);
                Indices=Make(GraphicsBuffer.Target.Structured|GraphicsBuffer.Target.Index,budget.MaximumIndices,4);
                Counters=Make(GraphicsBuffer.Target.Structured,budget.Views,4);
                Arguments=Make(GraphicsBuffer.Target.Structured|GraphicsBuffer.Target.IndirectArguments,budget.Views*5,4);
                Drops=Make(GraphicsBuffer.Target.Structured,65536,32);
                if(AllocatedBytes>budget.Bytes)throw new InvalidOperationException("液体实际GPU资源超过统一预算估计。");
            }
            catch{Dispose();throw;}
        }
        public void Dispose()
        {
            Previous?.Dispose();Current?.Dispose();Terrain?.Dispose();Lookup?.Dispose();OwnerLookup?.Dispose();Owners?.Dispose();OwnerBounds?.Dispose();
            Field?.Dispose();Vertices?.Dispose();PositionsA?.Dispose();PositionsB?.Dispose();Indices?.Dispose();Counters?.Dispose();Arguments?.Dispose();Drops?.Dispose();
        }
        public void Bind(ComputeShader compute,int kernel)
        {
            compute.SetBuffer(kernel,"_PreviousCells",Previous);compute.SetBuffer(kernel,"_CurrentCells",Current);
            compute.SetBuffer(kernel,"_TerrainCells",Terrain);compute.SetBuffer(kernel,"_ChunkLookup",Lookup);
            compute.SetBuffer(kernel,"_OwnerKeys",Owners);compute.SetBuffer(kernel,"_Field",Field);compute.SetBuffer(kernel,"_Vertices",Vertices);
            compute.SetBuffer(kernel,"_OwnerBounds",OwnerBounds);
            compute.SetBuffer(kernel,"_PositionsA",PositionsA);compute.SetBuffer(kernel,"_PositionsB",PositionsB);
            compute.SetBuffer(kernel,"_Indices",Indices);compute.SetBuffer(kernel,"_Counters",Counters);compute.SetBuffer(kernel,"_Arguments",Arguments);
        }
    }
}
