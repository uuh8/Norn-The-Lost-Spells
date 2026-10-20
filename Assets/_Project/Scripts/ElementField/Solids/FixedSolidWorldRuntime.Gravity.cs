using System;
using System.Collections.Generic;
using Game.Materials;
using Unity.Profiling;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class FixedSolidWorldRuntime
    {
        private struct IslandMotion { public float Velocity,Distance; }
        private MaterialVolumeCell[] _fallCells;
        private int[] _fallGroups,_fallQueue;
        private Dictionary<Vector3Int,int> _fallIndex;
        private Dictionary<Vector3Int,IslandMotion> _fallMotions,_fallNextMotions;
        private Vector3Int[] _fallGasFrom,_fallGasTo;
        private uint[] _fallGasReceipts;
        private uint _fallSleepVersion;
        private float _fallSleepTime;
        private bool _fallSleeping;
        public uint GravityMoves {get;private set;}
        public uint GravityDeferredMoves {get;private set;}
        private static readonly ProfilerMarker GravityMarker=new ProfilerMarker("FixedSolid.IslandGravity");
        private void InitializeGravity()
        {
            int capacity=checked(_store.MaximumChunks*_store.CellsPerChunk);
            _fallCells=new MaterialVolumeCell[capacity];_fallGroups=new int[capacity];_fallQueue=new int[capacity];
            _fallIndex=new Dictionary<Vector3Int,int>(capacity);
            _fallMotions=new Dictionary<Vector3Int,IslandMotion>(capacity);_fallNextMotions=new Dictionary<Vector3Int,IslandMotion>(capacity);
            _fallGasFrom=new Vector3Int[capacity];_fallGasTo=new Vector3Int[capacity];_fallGasReceipts=new uint[checked(capacity*8)];
        }
        public void StepGravity(float dt)
        {
            using var measure=GravityMarker.Auto();
            if(_store==null||!_profile.EnableGravity||dt<=0||!float.IsFinite(dt)||_store.TotalQuantity==0)return;
            _fallSleepTime+=dt;
            if(_fallSleeping&&_fallSleepVersion==_store.Version&&_fallSleepTime<_profile.SupportRecheckSeconds)return;
            _fallSleepTime=0;_fallSleeping=true;_fallIndex.Clear();_fallNextMotions.Clear();int count=0;
            // 只扫描Resident页的连续数组，不创建Physics对象、List或临时委托。
            for(int i=0;i<_store.ResidentCellCount;i++)if(_store.TryGetCell(i,out var cell))
            {_fallCells[count]=cell;_fallGroups[count]=0;_fallIndex.Add(cell.Cell,count++);}
            int group=0;
            for(int seed=0;seed<count;seed++)
            {
                if(_fallGroups[seed]!=0)continue;group++;int head=0,length=1;_fallQueue[0]=seed;_fallGroups[seed]=group;
                Vector3Int root=_fallCells[seed].Cell;
                while(head<length)
                {
                    var current=_fallCells[_fallQueue[head++]].Cell;if(Less(current,root))root=current;
                    for(int side=0;side<6;side++)if(_fallIndex.TryGetValue(current+Side(side),out int index)&&_fallGroups[index]==0)
                    {_fallGroups[index]=group;_fallQueue[length++]=index;}
                }
                if(_world!=null&&!_world.IsConversionAddressActive(new MaterialConversionAddress(root,_fallCells[seed].Material)))continue;
                _fallMotions.TryGetValue(root,out var motion);motion.Velocity+=Mathf.Max(0,_profile.GravityAcceleration)*dt;
                motion.Distance+=motion.Velocity*dt;
                int steps=Math.Min(_profile.MaximumFallCellsPerTick,Mathf.FloorToInt(motion.Distance/_size));
                if(steps==0){_fallSleeping=false;_fallNextMotions[root]=motion;continue;}
                bool stopped=false;
                for(int step=0;step<steps;step++)
                {
                    if(!CanFall(group,length)){stopped=true;motion=default;break;}
                    if(!MoveIsland(group,length)){GravityDeferredMoves++;_fallSleeping=false;break;}
                    root+=Vector3Int.down;motion.Distance-=_size;GravityMoves++;_fallSleeping=false;
                }
                if(!stopped)_fallSleeping=false;
                _fallNextMotions[root]=motion;
            }
            var swap=_fallMotions;_fallMotions=_fallNextMotions;_fallNextMotions=swap;_fallSleepVersion=_store.Version;
        }
        private bool ContainsIsland(Vector3Int cell,int group)=>_fallIndex.TryGetValue(cell,out int index)&&_fallGroups[index]==group;
        private bool CanFall(int group,int length)
        {
            for(int i=0;i<length;i++)
            {
                var source=_fallCells[_fallQueue[i]];var target=source.Cell+Vector3Int.down;
                if(ContainsIsland(target,group))continue;
                if(_store.OccupancyAt(target)>0||_baker!=null&&_baker.SampleEnvironmentSolidWorldCell(target,_origin,_size)
                    ||IntersectsActor(target,source.Quantity))return false;
            }
            return true;
        }
        private bool MoveIsland(int group,int length)
        {
            // 先用真实Store排演几何/页容量，再完整撤回。Geometry未通过时不搬气体、不发布半个块。
            if(!StageFall(length))return false;
            bool geometry=_conversionDirtyCount<=_profile.MaximumColliderChunksPerTick&&ValidateConversionGeometry();
            UndoFall(length);
            if(!geometry)return false;
            int pairs=0;
            for(int i=0;i<length;i++)
            {
                var source=_fallCells[_fallQueue[i]];var target=source.Cell+Vector3Int.down;
                if(_world!=null&&BlocksWholeCell(source.Material)&&!ContainsIsland(source.Cell+Vector3Int.up,group)
                    &&!_world.TryPrepareMaterialTopologyChange(source.Cell,false))return false;
                if(ContainsIsland(target,group))continue;
                if(_world!=null&&BlocksWholeCell(source.Material)&&!_world.TryPrepareMaterialTopologyChange(target,true))return false;
                // 每段竖列的底部进入格与顶部腾空格一一对应，把被挤气体移到腾空体积，GMU不变。
                var top=source.Cell;while(ContainsIsland(top+Vector3Int.up,group))top+=Vector3Int.up;
                _fallGasFrom[pairs]=target;_fallGasTo[pairs]=top;pairs++;
            }
            int moved=0;
            if(_world?.Gases!=null)
            {
                for(;moved<pairs;moved++)if(!_world.Gases.TryRelocateForFallingSolid(_fallGasFrom[moved],_fallGasTo[moved],_fallGasReceipts,moved*8))break;
                if(moved<pairs){for(int i=moved-1;i>=0;i--)_world.Gases.UndoRelocateForFallingSolid(_fallGasFrom[i],_fallGasTo[i],_fallGasReceipts,i*8);return false;}
            }
            if(!StageFall(length))
            {
                for(int i=moved-1;i>=0;i--)_world.Gases.UndoRelocateForFallingSolid(_fallGasFrom[i],_fallGasTo[i],_fallGasReceipts,i*8);
                return false;
            }
            PublishConversionGeometry();
            // 更新快照索引，后面的Island看到本Tick已落下的实体，不能互相穿过。
            for(int i=0;i<length;i++)_fallIndex.Remove(_fallCells[_fallQueue[i]].Cell);
            for(int i=0;i<length;i++)
            {
                int index=_fallQueue[i];var old=_fallCells[index];var target=old.Cell+Vector3Int.down;
                _fallCells[index]=new MaterialVolumeCell(target,old.Material,old.Quantity);_fallIndex.Add(target,index);
                _world?.NotifyMaterialTopologyChanged(old.Cell);_world?.NotifyMaterialTopologyChanged(target);
            }
            for(int i=0;i<pairs;i++){_world?.Gases?.PublishConversionChange(_fallGasFrom[i]);_world?.Gases?.PublishConversionChange(_fallGasTo[i]);}
            return true;
        }
        private bool StageFall(int length)
        {
            BeginConversionGeometry();
            for(int i=0;i<length;i++){var cell=_fallCells[_fallQueue[i]];TrackConversionCell(cell.Cell);if(!_store.TryRemove(cell.Cell,cell.Material,cell.Quantity))throw new InvalidOperationException("重力源快照失效");}
            _store.ReclaimEmptyChunks();int added=0;
            for(;added<length;added++)
            {
                var cell=_fallCells[_fallQueue[added]];var target=cell.Cell+Vector3Int.down;
                if(!_store.TryAdd(target,cell.Material,cell.Quantity))break;TrackConversionCell(target);
            }
            if(added==length)return true;
            for(int i=added-1;i>=0;i--){var cell=_fallCells[_fallQueue[i]];_store.TryRemove(cell.Cell+Vector3Int.down,cell.Material,cell.Quantity);}
            _store.ReclaimEmptyChunks();RestoreOriginal(length);return false;
        }
        private void UndoFall(int length)
        {
            for(int i=0;i<length;i++){var cell=_fallCells[_fallQueue[i]];if(!_store.TryRemove(cell.Cell+Vector3Int.down,cell.Material,cell.Quantity))throw new InvalidOperationException("重力排演回滚失败");}
            _store.ReclaimEmptyChunks();RestoreOriginal(length);
        }
        private void RestoreOriginal(int length)
        {for(int i=0;i<length;i++){var cell=_fallCells[_fallQueue[i]];if(!_store.TryAdd(cell.Cell,cell.Material,cell.Quantity))throw new InvalidOperationException("重力原位置恢复失败");}}
        private static bool Less(Vector3Int a,Vector3Int b)=>a.x<b.x||a.x==b.x&&(a.z<b.z||a.z==b.z&&a.y<b.y);
        private static Vector3Int Side(int side)=>side==0?Vector3Int.down:side==1?Vector3Int.up:side==2?Vector3Int.left:side==3?Vector3Int.right:side==4?Vector3Int.forward:Vector3Int.back;
    }
}
