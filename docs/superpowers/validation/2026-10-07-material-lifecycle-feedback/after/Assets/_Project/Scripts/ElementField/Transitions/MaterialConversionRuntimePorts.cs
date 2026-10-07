using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    internal readonly struct LiquidConversionReceipt
    {
        internal readonly uint Dynamic, Stable;
        internal LiquidConversionReceipt(uint dynamic, uint stable) { Dynamic=dynamic; Stable=stable; }
    }
    public sealed partial class LiquidWorldRuntime
    {
        private Action<Vector3Int> _conversionObserver;
        private Action<MaterialId,ulong> _conversionExternal, _conversionConsumed;
        internal void SetConversionAccounting(Action<MaterialId,ulong> injected,Action<MaterialId,ulong> consumed)
        { _conversionExternal=injected; _conversionConsumed=consumed; }
        internal ulong MaterialQuantity(MaterialId material) => (_store?.MaterialQuantity(material) ?? 0)+(_stable?.MaterialQuantity(material) ?? 0);
        internal ulong PendingMaterialQuantity(MaterialId material)
        { ulong total=0; for(int i=0;i<_count;i++) { var pending=_pending[(_head+i)%_pending.Length]; if(pending.Request.MaterialKind==material) total+=pending.Remaining; } return total; }
        internal int ConversionSlotCount => _store?.ConversionSlotCount ?? 0;
        internal bool TryGetConversionCell(int cursor,out Vector3Int cell,out MaterialId material) => _store.TryGetConversionCell(cursor,out cell,out material);
        internal void SetConversionObserver(Action<Vector3Int> observer)
        { _conversionObserver=observer; _store.SetBeforeCellChanged(CaptureBeforeConversionAware); }
        private void CaptureBeforeConversionAware(Vector3Int cell)
        { _history.CaptureBefore(cell); _conversionObserver?.Invoke(cell); }
        internal bool PrepareConversionContact(Vector3Int cell, MaterialId target) => _stable.TryPrepareMaterialContact(cell,target);
        internal bool StageConversionRemove(Vector3Int cell, MaterialId material, uint amount, out LiquidConversionReceipt receipt)
        {
            receipt=default;
            if (!TryGetQuantity(cell,material,out uint total) || total<amount || amount==0) return false;
            _store.TryGetQuantity(cell,material,out uint dynamic);
            uint takeDynamic=Math.Min(dynamic,amount), takeStable=amount-takeDynamic;
            if (takeStable>0 && !_stable.TryConsume(cell,material,takeStable)) return false;
            if (takeDynamic>0 && !_store.TryRemove(cell,material,takeDynamic))
            { if(takeStable>0) _stable.TryAddBack(cell,material,takeStable); return false; }
            receipt=new LiquidConversionReceipt(takeDynamic,takeStable); return true;
        }
        internal void UndoConversionRemove(Vector3Int cell, MaterialId material, in LiquidConversionReceipt receipt)
        {
            if (receipt.Stable>0 && !_stable.TryAddBack(cell,material,receipt.Stable)
                || receipt.Dynamic>0 && !_store.TryAdd(cell,material,receipt.Dynamic))
                throw new InvalidOperationException("转换回滚液体失败；事务期间不能交错其他 Owner 写入。");
        }
        internal bool StageConversionAdd(Vector3Int cell, MaterialId material, uint amount)
        {
            // Ice 尚未发布 Collider 时，阻挡真值已经来自 Solid Store；局部缓存必须重查。
            _store.InvalidateStaticCell(cell); return _store.TryAdd(cell,material,amount);
        }
        internal void InvalidateConversionCell(Vector3Int worldCell)
        { var cell=WorldCellToLiquidCell(worldCell); _store.InvalidateStaticCell(cell); _residueOccupancy?.Remove(cell); }
        internal bool ValidateConversionVolume(Vector3Int worldCell)
        {
            // Stage 后刷新残留派生量；Collider 可与剩余湿粘液共存，但总占据不能超过真实容量。
            var cell=WorldCellToLiquidCell(worldCell); _residueOccupancy?.Remove(cell);
            return _store.OccupancyAt(cell)<=_store.CapacityGmu;
        }
        internal void UndoConversionAdd(Vector3Int cell, MaterialId material, uint amount)
        { if (!_store.TryRemove(cell,material,amount)) throw new InvalidOperationException("转换回滚产物失败。"); }
        internal void PublishConversionChange(Vector3Int cell)
        { _activity.WakeNeighbors(cell); MarkPresentationDeposit(_origin+((Vector3)cell+Vector3.one*.5f)*_cellSize); _conversionObserver?.Invoke(cell); }
        internal uint ConversionOccupancy(Vector3Int cell)
        {
            uint total=0;
            for(int i=0;i<_profile.MaterialCount;i++) { TryGetQuantity(cell,_profile.GetMaterial(i).Material,out uint q); total+=q; }
            return total;
        }
        internal uint ConversionFreeCapacity(Vector3Int cell) => (uint)_store.FreeCapacity(cell);
        internal bool IsVolatile(MaterialId material)
        { for(int i=0;i<_profile.MaterialCount;i++) if(_profile.GetMaterial(i).Material==material) return _profile.GetMaterial(i).IsVolatile; return false; }
    }
    public sealed partial class GasWorldRuntime
    {
        internal ulong MaterialQuantity(MaterialId material) => _store?.MaterialQuantity(material) ?? 0;
        internal ulong PendingMaterialQuantity(MaterialId material) => _pending?.MaterialQuantity(material) ?? 0;
        internal ulong VentedMaterialQuantity(MaterialId material) => _solver?.VentedMaterialQuantity(material) ?? 0;
        internal void SetConversionObserver(Action<Vector3Int> observer) => _store.SetChangeObserver(observer);
        internal bool StageConversionAdd(Vector3Int cell, MaterialId material, uint amount)
        {
            // 独立、有限的转换预算查询 Terrain，不借用已被 Gas Solver 用完的 Terrain 预算。
            if (_world?.FixedSolids?.BlocksGasCell(cell)==true || _baker!=null && _baker.SampleEnvironmentSolidWorldCell(cell,_origin,_size)) return false;
            return _store.TryAdd(cell,material,amount);
        }
        internal bool StageConversionRemove(Vector3Int cell, MaterialId material, uint amount) => _store.TryRemove(cell,material,amount);
        internal void UndoConversionRemove(Vector3Int cell, MaterialId material, uint amount)
        { if(!_store.TryAdd(cell,material,amount)) throw new InvalidOperationException("气体源量回滚失败。"); }
        internal void UndoConversionAdd(Vector3Int cell, MaterialId material, uint amount)
        { if(!_store.TryRemove(cell,material,amount)) throw new InvalidOperationException("气体产物回滚失败。"); }
        internal void PublishConversionChange(Vector3Int cell) => _activity.WakeNeighbors(cell);
        internal void FinishConversion() { _store.ReclaimEmptyChunks(); }
    }
    public sealed partial class FixedSolidWorldRuntime
    {
        internal ulong MaterialQuantity(MaterialId material) => _store?.MaterialQuantity(material) ?? 0;
        internal ulong PendingMaterialQuantity(MaterialId material) => _pending?.MaterialQuantity(material) ?? 0;
        private ElementChunkKey[] _conversionDirty;
        private int _conversionDirtyCount;
        internal void SetConversionObserver(Action<Vector3Int> observer) => _store.SetChangeObserver(observer);
        internal void BeginConversionGeometry() => _conversionDirtyCount=0;
        internal bool StageConversionAdd(Vector3Int cell, MaterialId material, uint amount)
        {
            if (_baker!=null && _baker.SampleEnvironmentSolidWorldCell(cell,_origin,_size)
                || BlocksMaterial(material) && IntersectsActor(cell,amount)
                || BlocksWholeCell(material) && _world!=null && _world.HasFluidOrGasAt(cell)) return false;
            // 完全填满细格前确认没有旧气体，避免先固化成墙再把气体永远封在墙体内。
            if(_world?.Gases!=null && (ulong)_store.OccupancyAt(cell)+amount>=CellCapacity)
                for(int i=0;i<_world.Gases.MaterialCount;i++)
                    if(_world.Gases.TryGetQuantity(cell,_world.Gases.GetMaterial(i),out _)) return false;
            if (!_store.TryAdd(cell,material,amount)) return false;
            TrackConversionCell(cell); return true;
        }
        internal bool StageConversionRemove(Vector3Int cell, MaterialId material, uint amount)
        { if(!_store.TryRemove(cell,material,amount)) return false; TrackConversionCell(cell); return true; }
        internal void UndoConversionRemove(Vector3Int cell, MaterialId material, uint amount)
        { if(!_store.TryAdd(cell,material,amount)) throw new InvalidOperationException("固体源量回滚失败。"); }
        internal void UndoConversionAdd(Vector3Int cell, MaterialId material, uint amount)
        { if(!_store.TryRemove(cell,material,amount)) throw new InvalidOperationException("固体产物回滚失败。"); }
        private void TrackConversionCell(Vector3Int cell)
        {
            CollectDirty(cell);
            for(int i=0;i<_dirtyCount;i++)
            {
                bool found=false;
                for(int n=0;n<_conversionDirtyCount;n++) found|=_conversionDirty[n].Equals(_dirty[i]);
                if(!found) _conversionDirty[_conversionDirtyCount++]=_dirty[i];
            }
        }
        internal bool ValidateConversionGeometry()
        { for(int i=0;i<_conversionDirtyCount;i++) if(!_physics.CanPrepare(_conversionDirty[i])) return false; return true; }
        internal void PublishConversionGeometry()
        {
            // 一个粗液体格可能转换成多个细冰格；每个 Dirty Chunk 只 Cooking 一次。
            for(int i=0;i<_conversionDirtyCount;i++) _physics.Publish(_conversionDirty[i]);
            _store.ReclaimEmptyChunks(); _physics.ReleaseUnused(_store);
        }
        internal void CancelConversionGeometry() => _store.ReclaimEmptyChunks();
    }
}
