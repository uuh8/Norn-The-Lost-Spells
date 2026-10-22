using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class GasWorldRuntime
    {
        internal bool TryRelocateForFallingSolid(Vector3Int from,Vector3Int to,uint[] receipt,int offset)
        {
            for(int i=0;i<8;i++)receipt[offset+i]=0;
            if(_store==null)return true;
            ulong total=0;for(byte key=1;key<=8;key++)if(_store.TryGetQuantity(from,(MaterialId)key,out uint q)){receipt[offset+key-1]=q;total+=q;}
            if(total==0)return true;
            if(total+_store.OccupancyAt(to)>_store.CellCapacity)return false;
            for(byte key=1;key<=8;key++){uint q=receipt[offset+key-1];if(q>0)_store.TryRemove(from,(MaterialId)key,q);}
            _store.ReclaimEmptyChunks();int added=0;
            for(byte key=1;key<=8;key++)
            {
                uint q=receipt[offset+key-1];if(q>0&&!_store.TryAdd(to,(MaterialId)key,q))
                {
                    for(byte undo=1;undo<=added;undo++){uint amount=receipt[offset+undo-1];if(amount>0)_store.TryRemove(to,(MaterialId)undo,amount);}
                    _store.ReclaimEmptyChunks();for(byte undo=1;undo<=8;undo++){uint amount=receipt[offset+undo-1];if(amount>0&&!_store.TryAdd(from,(MaterialId)undo,amount))throw new InvalidOperationException("气体挤出回滚失败");}
                    return false;
                }
                added=key;
            }
            return true;
        }
        internal void UndoRelocateForFallingSolid(Vector3Int from,Vector3Int to,uint[] receipt,int offset)
        {
            if(_store==null)return;
            for(byte key=1;key<=8;key++){uint q=receipt[offset+key-1];if(q>0&&!_store.TryRemove(to,(MaterialId)key,q))throw new InvalidOperationException("气体挤出目标回滚失败");}
            _store.ReclaimEmptyChunks();for(byte key=1;key<=8;key++){uint q=receipt[offset+key-1];if(q>0&&!_store.TryAdd(from,(MaterialId)key,q))throw new InvalidOperationException("气体挤出源回滚失败");}
        }
    }
}
