using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class LiquidWorldRuntime
    {
        /// <summary>
        /// 库存事务的运动预检与发布只访问原Solver，不创建新的粒子/资源Owner。
        /// 世界事务锁保证预检与Commit间不交错另一笔写入；失败时没有动量需要Undo。
        /// </summary>
        internal int AvailableInjectionMotionSlots=>_solver!=null?_solver.AvailableInitialMotionSlots:0;
        internal bool CanPrepareInjectionOrigin(Vector3 position)
        {
            if(_store==null)return false;
            Vector3Int cell=Vector3Int.FloorToInt((position-_origin)/_cellSize);
            // 固体可能刚在另一事务中生成；喷口检查必须重采当前逻辑拓扑，不能沿用旧空格缓存。
            _store.InvalidateStaticCell(cell);return _store.FreeCapacity(cell)>0;
        }
        internal bool CanPrepareInjectionMotion(Vector3Int cell,MaterialId material,out bool requiresSlot)
        {requiresSlot=true;return _solver!=null && _solver.CanPrepareInitialMotion(cell,material,out requiresSlot);}
        internal void PublishInjectionMotion(Vector3Int cell,MaterialId material,Vector3 velocity,uint addedQuantity)
            =>_solver.CommitPreparedInitialMotion(cell,material,velocity,addedQuantity);

        /// <summary>
        /// 只读表示的出生前姿态必须早于Stage数量和动量合并；事后用total-added只能恢复数量，
        /// 不能恢复旧水的offset/velocity。本方法不登记出生，Port失败仍沿原Journal完整Undo。
        /// known scope只覆盖这一次确定的TryAdd，既不包住外部Source.Commit也不包住Stable交接。
        /// </summary>
        internal bool StageInventoryBirth(Vector3Int cell,MaterialId material,uint quantity,out LiquidCellPresentationSample before)
            =>TryStageKnownBirth(cell,material,quantity,true,out before);
        private bool TryStageKnownBirth(Vector3Int cell,MaterialId material,uint quantity,bool inventory,
            out LiquidCellPresentationSample before)
        {
            before=ReadCurrentPresentation(cell,material);
            _history.BeginKnownBirthWrite(cell);
            try{return inventory?StageConversionAdd(cell,material,quantity):_store.TryAdd(cell,material,quantity);}
            finally{_history.EndKnownBirthWrite();}
        }
        // 必须由原数量Owner成功Commit后调用。只新增有界表示证据，不再次加GMU或扣库存。
        internal void PublishCommittedBirth(Vector3Int cell,MaterialId material,LiquidCellPresentationSample before,uint quantity)
            =>_history.RecordCommittedBirth(cell,material,before,quantity);
    }
}
