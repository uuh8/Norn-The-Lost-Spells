using System;
using UnityEngine;
using Game.Materials;

namespace Game.ElementField
{
    /// <summary>极稀薄尾雾的有界生命周期；只处理模拟域内真实库存，不参与 Shader 或距离卸载。</summary>
    internal sealed class GasDiluteDissipation
    {
        private readonly GasVolumeStore _store;
        private readonly MaterialCellActivityQueue _activity;
        private readonly GasWorldProfile _profile;
        private readonly Action<MaterialId,ulong> _consumed;
        private readonly float[] _thinSeconds;
        private readonly bool[] _eligible;
        private readonly uint[] _generations;
        private int _cursor;
        internal int CheckedChunksLastTick { get; private set; }
        internal GasDiluteDissipation(GasVolumeStore store,MaterialCellActivityQueue activity,GasWorldProfile profile,Action<MaterialId,ulong> consumed)
        {
            _store=store;_activity=activity;_profile=profile;_consumed=consumed;
            _thinSeconds=new float[store.MaximumChunks];_eligible=new bool[store.MaximumChunks];_generations=new uint[store.MaximumChunks];
        }
        internal void Step(float deltaTime)
        {
            CheckedChunksLastTick=0;
            if(_profile.DiluteMaximumCellGmu==0 || _store.TotalQuantity==0) return;
            // O(MaximumChunks) 只更新小表的时钟/域状态；详细读取最多两个 Chunk，不能每 Tick 扫整个浓度场。
            // 每 Tick 发现离域就清掉尾雾计时，回来需重新验证，绝不补算离开时间。
            for(int i=0;i<_store.ResidentChunkCount;i++)
            {
                int slot=_store.GetChunkSlot(i);uint generation=_store.GetChunkAllocationVersion(i);
                if(_generations[slot]!=generation || !WholeChunkActive(_store.GetChunkKey(i)))
                {_generations[slot]=generation;_thinSeconds[slot]=0;_eligible[slot]=false;}
                else if(_eligible[slot]) _thinSeconds[slot]+=deltaTime;
            }
            ulong maximumTotal=(ulong)Math.Floor(_store.CellsPerChunk*(double)_store.CellCapacity*_profile.DiluteMaximumChunkConcentration);
            int work=Math.Min(_profile.DiluteChunksPerTick,_store.ResidentChunkCount);
            for(int scan=0;scan<work;scan++)
            {
                if(_cursor>=_store.ResidentChunkCount) _cursor=0;
                int index=_cursor++,slot=_store.GetChunkSlot(index);
                if(!WholeChunkActive(_store.GetChunkKey(index))) continue;
                CheckedChunksLastTick++;
                if(!_store.IsChunkDilute(index,_profile.DiluteMaximumCellGmu,maximumTotal))
                {_thinSeconds[slot]=0;_eligible[slot]=false;continue;}
                if(!_eligible[slot]) {_eligible[slot]=true;_thinSeconds[slot]=0;continue;}
                if(_thinSeconds[slot]<_profile.DiluteDelaySeconds) continue;
                // 最后一次重新验证浓度后才清理；版本通知会使 Renderer/Exposure 读取空量，账本同步记消散。
                _store.DissipateChunk(index,_consumed);_eligible[slot]=false;_thinSeconds[slot]=0;
            }
        }
        private bool WholeChunkActive(ElementChunkKey key)
            =>_activity.ContainsChunk(key,_store.ChunkSize);
    }
}
