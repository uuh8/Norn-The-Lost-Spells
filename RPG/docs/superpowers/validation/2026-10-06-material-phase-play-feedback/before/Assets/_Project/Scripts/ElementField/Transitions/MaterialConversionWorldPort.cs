using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 主线程内的有界写入 Journal。Prepare 暂存全部数量变更、验证几何后逆序还原；
    /// 在此同步调用内不发布 Collider/Renderer。Prepared 期间源仍拥有全部 GMU，Commit 重查版本。
    /// 这避免把不可撤销的 MeshCollider Cooking 当作容量预约，也不把 Pending 当成已生成材料。
    /// </summary>
    internal sealed class MaterialConversionWorldPort : IMaterialConversionPort
    {
        private readonly ElementWorldRuntime _world;
        private readonly LiquidWorldRuntime _liquid;
        private readonly FixedSolidWorldRuntime _solid;
        private readonly GasWorldRuntime _gas;
        private LiquidConversionReceipt _liquidReceipt;
        private bool _prepared;
        internal MaterialConversionWorldPort(ElementWorldRuntime world, LiquidWorldRuntime liquid, FixedSolidWorldRuntime solid, GasWorldRuntime gas)
        { _world=world; _liquid=liquid; _solid=solid; _gas=gas; }
        public bool TryPrepare(MaterialConversionAddress source, uint consume, MaterialConversionPlacement[] targets, int count, out MaterialConversionStamp stamp)
        {
            stamp=default; if(_prepared || !Read(source,out uint available) || available<consume) return false;
            for(int i=0;i<count;i++) if(!_world.IsConversionAddressActive(targets[i].Address)) return false;
            if(!_world.IsConversionAddressActive(source)) return false;
            // Stable 是另一种真实 Liquid Owner。生成固体前只展开涉及的局部分区，失败保留原池。
            if(Backend(source.Material)==MaterialSimulationBackendKind.GridLiquid)
                for(int i=0;i<count;i++) if(Backend(targets[i].Address.Material)==MaterialSimulationBackendKind.FixedSolid
                    && !_liquid.PrepareConversionContact(source.Cell,targets[i].Address.Material)) return false;
            for(int i=0;i<count;i++) if(Backend(targets[i].Address.Material)==MaterialSimulationBackendKind.GridLiquid
                && !_liquid.PrepareConversionContact(targets[i].Address.Cell,targets[i].Address.Material)) return false;
            if(!Stage(source,consume,targets,count)) return false;
            Undo(source,consume,targets,count);
            _solid.CancelConversionGeometry(); _gas.FinishConversion();
            if(!Read(source,out available)) return false;
            stamp=new MaterialConversionStamp(_liquid.SurfaceVersion,_solid.Version,_gas.Version,available);
            _prepared=true; return true;
        }
        public bool TryCommit(MaterialConversionAddress source, uint consume, MaterialConversionPlacement[] targets, int count, in MaterialConversionStamp stamp)
        {
            if(!_prepared || stamp.Liquid!=_liquid.SurfaceVersion || stamp.Solid!=_solid.Version || stamp.Gas!=_gas.Version
                || !Read(source,out uint available) || available!=stamp.SourceQuantity || available<consume
                || !_world.IsConversionAddressActive(source)) return false;
            for(int i=0;i<count;i++) if(!_world.IsConversionAddressActive(targets[i].Address)) return false;
            if(!Stage(source,consume,targets,count)) return false;
            _prepared=false;
            _solid.PublishConversionGeometry();
            Publish(source);
            for(int i=0;i<count;i++) Publish(targets[i].Address);
            _gas.FinishConversion(); return true;
        }
        public void Cancel() => _prepared=false;
        private bool Stage(MaterialConversionAddress source, uint consume, MaterialConversionPlacement[] targets, int count)
        {
            _solid.BeginConversionGeometry();
            if(!Remove(source,consume)) return false;
            int staged=0;
            for(;staged<count;staged++) if(!Add(targets[staged])) break;
            bool valid=staged==count && _solid.ValidateConversionGeometry();
            if(valid)
            {
                // 阻挡变化预检在暂存数量之后执行，冻结时粗液体格已经清空，避免旧拓扑路径搬走残液。
                if(Backend(source.Material)==MaterialSimulationBackendKind.FixedSolid && _solid.BlocksMaterial(source.Material))
                    valid=_world.TryPrepareMaterialTopologyChange(source.Cell,_solid.BlocksCell(source.Cell));
                for(int i=0;i<count && valid;i++) if(Backend(targets[i].Address.Material)==MaterialSimulationBackendKind.FixedSolid
                    && _solid.BlocksMaterial(targets[i].Address.Material)) valid=_world.TryPrepareMaterialTopologyChange(targets[i].Address.Cell,true);
            }
            if(valid) return true;
            Undo(source,consume,targets,staged); _solid.CancelConversionGeometry(); _gas.FinishConversion(); return false;
        }
        private void Undo(MaterialConversionAddress source, uint consume, MaterialConversionPlacement[] targets, int staged)
        {
            for(int i=staged-1;i>=0;i--)
            {
                var target=targets[i];
                switch(Backend(target.Address.Material))
                {
                    case MaterialSimulationBackendKind.GridLiquid: _liquid.UndoConversionAdd(target.Address.Cell,target.Address.Material,target.Quantity); break;
                    case MaterialSimulationBackendKind.FixedSolid: _solid.UndoConversionAdd(target.Address.Cell,target.Address.Material,target.Quantity); break;
                    case MaterialSimulationBackendKind.GridGas: _gas.UndoConversionAdd(target.Address.Cell,target.Address.Material,target.Quantity); break;
                }
            }
            // 先撤销产物派生占据，再归还源液体容量；否则残留预览缓存会使源的 Add 被误拒绝。
            for(int i=0;i<staged;i++) if(Backend(targets[i].Address.Material)==MaterialSimulationBackendKind.FixedSolid)
                _liquid.InvalidateConversionCell(targets[i].Address.Cell);
            switch(Backend(source.Material))
            {
                case MaterialSimulationBackendKind.GridLiquid: _liquid.UndoConversionRemove(source.Cell,source.Material,in _liquidReceipt); break;
                case MaterialSimulationBackendKind.FixedSolid: _solid.UndoConversionRemove(source.Cell,source.Material,consume); break;
                case MaterialSimulationBackendKind.GridGas: _gas.UndoConversionRemove(source.Cell,source.Material,consume); break;
            }
            // Preview 期间 Liquid 可能缓存了“冰已移除”。Undo 必须同步撤销这个派生缓存，
            // 否则 GMU/Collider 都正确，下一 Tick 液体仍会穿过保留的冰。
            if(Backend(source.Material)==MaterialSimulationBackendKind.FixedSolid) _liquid.InvalidateConversionCell(source.Cell);
        }
        private bool Remove(MaterialConversionAddress source,uint amount)
        {
            switch(Backend(source.Material))
            {
                case MaterialSimulationBackendKind.GridLiquid: return _liquid.StageConversionRemove(source.Cell,source.Material,amount,out _liquidReceipt);
                case MaterialSimulationBackendKind.FixedSolid: return _solid.StageConversionRemove(source.Cell,source.Material,amount);
                case MaterialSimulationBackendKind.GridGas: return _gas.StageConversionRemove(source.Cell,source.Material,amount);
                default: return false;
            }
        }
        private bool Add(MaterialConversionPlacement target)
        {
            switch(Backend(target.Address.Material))
            {
                case MaterialSimulationBackendKind.GridLiquid: return _liquid.StageConversionAdd(target.Address.Cell,target.Address.Material,target.Quantity);
                case MaterialSimulationBackendKind.FixedSolid: return _solid.StageConversionAdd(target.Address.Cell,target.Address.Material,target.Quantity);
                case MaterialSimulationBackendKind.GridGas: return _gas.StageConversionAdd(target.Address.Cell,target.Address.Material,target.Quantity);
                default: return false;
            }
        }
        private void Publish(MaterialConversionAddress address)
        {
            switch(Backend(address.Material))
            {
                case MaterialSimulationBackendKind.GridLiquid: _liquid.PublishConversionChange(address.Cell); break;
                case MaterialSimulationBackendKind.GridGas: _gas.PublishConversionChange(address.Cell); break;
                case MaterialSimulationBackendKind.FixedSolid: _world.NotifyMaterialTopologyChanged(address.Cell); break;
            }
        }
        internal bool Read(MaterialConversionAddress address,out uint quantity)
        {
            quantity=0;
            switch(Backend(address.Material))
            {
                case MaterialSimulationBackendKind.GridLiquid: return _liquid.TryGetQuantity(address.Cell,address.Material,out quantity);
                case MaterialSimulationBackendKind.FixedSolid: return _solid.TryGetQuantity(address.Cell,address.Material,out quantity);
                case MaterialSimulationBackendKind.GridGas: return _gas.TryGetQuantity(address.Cell,address.Material,out quantity);
                default: return false;
            }
        }
        internal MaterialSimulationBackendKind Backend(MaterialId material) => _world.ResolveConversionBackend(material);
    }
}
