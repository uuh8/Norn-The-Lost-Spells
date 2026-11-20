using System;
using Game.Core;
using Game.Materials;
using Unity.Profiling;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>真实三相接收的有界Journal；库存仍拥有全部量，直到几何和全部组分写入均验证。</summary>
    internal sealed class MaterialInventoryDepositPort
    {
        private const int MaximumEntries = 512, MaximumVisited = 8192;
        private readonly MaterialConversionPlacement[] _journal = new MaterialConversionPlacement[MaximumEntries];
        private readonly bool[] _restingEntries = new bool[MaximumEntries];
        private bool _hasStagedResting;
        // 与原Placement同索引的固定scratch；只在成功Source.Commit后交给History。
        // 失败/Undo从不注册出生，不能让未扣瓶的一次尝试获得特殊连续播放资格。
        private readonly LiquidCellPresentationSample[] _birthPrevious=new LiquidCellPresentationSample[MaximumEntries];
        private readonly ElementWorldRuntime _world;
        private int _count, _visited;
        private static readonly ProfilerMarker Marker = new ProfilerMarker("Norn.MaterialTransfer.Deposit");
        internal MaterialInventoryDepositPort(ElementWorldRuntime world) { _world = world; }
        internal MaterialTransferFailure Deposit(MaterialDepositExchange exchange, out ulong transferred)
        {
            transferred = 0;
            if (_world == null) return MaterialTransferFailure.WorldUnavailable;
            // 先取得世界锁再进入 Core：同一个 Port 重入时连 _count 也不能清，否则会丢掉外层 Undo Journal。
            MaterialTransferFailure ownership = _world.TryBeginMaterialTransaction();
            if (ownership != MaterialTransferFailure.None) return ownership;
            Exception primary = null;
            try { return DepositCore(exchange, out transferred); }
            catch (Exception exception)
            {
                primary = exception;
                _world.MarkMaterialTransactionFaulted();
                throw;
            }
            finally
            {
                try
                {
                    // 所有成功/普通失败/异常路径统一清理一次，且仍持有 World 锁。
                    // 成功 Commit 后 Cancel 只释放已消耗的预留，不是 Undo，不会撤销已提交的库存和世界数量。
                    exchange?.Source?.Cancel();
                }
                catch (Exception cleanup)
                {
                    _world.MarkMaterialTransactionFaulted();
                    if (primary == null) throw;
                    // primary 正在向调用者传播，finally 不能用第二个异常覆盖它。
                    // Exception.Data 的稀有异常路径允许分配；正常交易不访问 Data，也不增加热路径分配。
                    primary.Data["Norn.MaterialCleanupException"] = cleanup;
                }
                finally { _world.EndMaterialTransaction(); }
            }
        }
        private MaterialTransferFailure DepositCore(MaterialDepositExchange exchange, out ulong transferred)
        {
            using var marker = Marker.Auto(); transferred = 0; _count = _visited = 0;
            _hasStagedResting = false;
            // Port 也可以被同 Assembly 的代码直接调用，不能只依赖 EventBus Adapter 做生命周期检查。
            // 先拒绝故障世界，确保 Source 预约和任何 Stage 都尚未发生；保留原 Undo/Cancel 路径完成已有事务。
            if (_world == null || !_world.CanAcceptWorldWrites) return MaterialTransferFailure.WorldUnavailable;
            if (exchange.Source == null || exchange.Amounts == null || exchange.Count <= 0 || exchange.Count > 8
                || exchange.Count > exchange.Amounts.Length || !Finite(exchange.Position) || !float.IsFinite(exchange.Radius)
                || exchange.HasInitialMotion && (!Finite(exchange.InitialVelocity) || exchange.InitialVelocity.sqrMagnitude>4096f)
                || exchange.Radius <= 0 || exchange.Radius > 2) return MaterialTransferFailure.InvalidRequest;
            ushort seen = 0; ulong total = 0;
            for (int i = 0; i < exchange.Count; i++)
            {
                var amount = exchange.Amounts[i];
                if (!MaterialAmount.IsLocalInventoryMaterial(amount.MaterialKey) || (seen & 1 << amount.MaterialKey) != 0
                    || amount.Quantity > ulong.MaxValue - total) return MaterialTransferFailure.InvalidRequest;
                seen |= (ushort)(1 << amount.MaterialKey); total += amount.Quantity;
                if (amount.Quantity == 0) continue;
                var backend = Backend((MaterialId)amount.MaterialKey);
                if (backend == MaterialSimulationBackendKind.Cellular)
                {
                    if (_world.CellularSimulation == null) return MaterialTransferFailure.WorldUnavailable;
                    continue;
                }
                if (backend != MaterialSimulationBackendKind.GridLiquid && backend != MaterialSimulationBackendKind.FixedSolid
                    && backend != MaterialSimulationBackendKind.GridGas) return MaterialTransferFailure.UnsupportedBackend;
                if (backend == MaterialSimulationBackendKind.GridLiquid ? _world.InventoryLiquid == null || !_world.InventoryLiquid.IsFluidInitialized
                    : backend == MaterialSimulationBackendKind.FixedSolid ? _world.InventorySolids == null || !_world.InventorySolids.IsMaterialInitialized
                    : _world.InventoryGases == null || !_world.InventoryGases.IsMaterialInitialized) return MaterialTransferFailure.WorldUnavailable;
            }
            if (total == 0) return MaterialTransferFailure.NoMaterial;
            if (exchange.HasInitialMotion && _world.CellularSimulation != null)
            {
                // 喷口命中整格墙/满格不能绕到其另一侧。旧 GridLiquid 的初速度合同继续由原 Owner 保有。
                var nozzle = Vector3Int.FloorToInt((exchange.Position - _world.ConversionOrigin) / _world.ConversionCellSize);
                for (int i = 0; i < exchange.Count; i++)
                {
                    var component = exchange.Amounts[i];
                    var material = (MaterialId)component.MaterialKey;
                    if (component.Quantity > 0 && Backend(material) == MaterialSimulationBackendKind.Cellular
                        && CellularDepositCapacity(nozzle, material, out _) == 0) return MaterialTransferFailure.Capacity;
                }
            }
            if(exchange.HasInitialMotion && _world.InventoryLiquid!=null)
                for(int i=0;i<exchange.Count;i++)
                    if(exchange.Amounts[i].Quantity>0 && Backend((MaterialId)exchange.Amounts[i].MaterialKey)==MaterialSimulationBackendKind.GridLiquid)
                    {
                        // 喷口中心若已在墙或满格中，不能用邻格搜索绕过障碍；失败时材料仍完整留在瓶中。
                        if(!_world.InventoryLiquid.CanPrepareInjectionOrigin(exchange.Position))return MaterialTransferFailure.Capacity;
                        break;
                    }
            if (!exchange.Source.TryReserve(exchange.Amounts, exchange.Count, out var failure)) return failure;
            _world.RetainInventoryDepositActivity(exchange.Position,exchange.Radius);
            _world.InventorySolids?.BeginConversionGeometry();
            // 元胞气体先取得局部剩余容积，后续主材只用无需挤气的空间。
            // 这使同一瓶的水与蒸汽可以落入一格，无需在已经暂存冷页扣增时再展开它。
            int passes = _world.CellularSimulation == null || exchange.Count == 1 ? 1 : 2;
            for (int pass = 0; pass < passes; pass++)
            for (int i = 0; i < exchange.Count; i++)
            {
                var amount = exchange.Amounts[i]; if (amount.Quantity == 0) continue;
                if (passes == 2 && (ElementCellMaterialRules.Motion((MaterialId)amount.MaterialKey) == ElementCellMotion.Gas) != (pass == 0)) continue;
                failure = StageComponent((MaterialId)amount.MaterialKey, amount.Quantity, exchange.Position, exchange.Radius);
                if (failure != MaterialTransferFailure.None) { Undo(); return failure; }
            }
            bool valid = _world.InventorySolids == null || _world.InventorySolids.ValidateConversionGeometry();
            for (int i = 0; i < _count && valid; i++)
            {
                var address = _journal[i].Address;
                if (Backend(address.Material) != MaterialSimulationBackendKind.FixedSolid) continue;
                valid = _world.InventoryLiquid == null || _world.InventoryLiquid.ValidateConversionVolume(address.Cell);
                if (valid && _world.InventorySolids.BlocksWholeCell(address.Material))
                    valid = _world.TryPrepareMaterialTopologyChange(address.Cell, true);
            }
            if (!valid) { Undo(); return MaterialTransferFailure.GeometryRejected; }
            if(exchange.HasInitialMotion)
            {
                // 先证明全部新运动行都能进入固定容量字典，再扣库存；世界锁排除了期间另一笔注入。
                // Undo只撤暂存数量，不曾改变旧速度。Commit后不会再走一个“失败就丢速度”的分支。
                int needed=0;
                for(int i=0;i<_count;i++)
                {
                    var address=_journal[i].Address;
                    if(Backend(address.Material)!=MaterialSimulationBackendKind.GridLiquid)continue;
                    if(!_world.InventoryLiquid.CanPrepareInjectionMotion(address.Cell,address.Material,out bool newSlot))
                    {Undo();return MaterialTransferFailure.Capacity;}
                    if(newSlot)needed++;
                }
                if(needed>0 && (_world.InventoryLiquid==null || needed>_world.InventoryLiquid.AvailableInjectionMotionSlots))
                {Undo();return MaterialTransferFailure.Capacity;}
            }
            exchange.Source.Commit();
            if (_hasStagedResting) _world.RestingLiquids.CommitStaged();
            if(exchange.HasInitialMotion)
                for(int i=0;i<_count;i++)
                {
                    var placement=_journal[i];var address=placement.Address;
                    if(Backend(address.Material)==MaterialSimulationBackendKind.GridLiquid)
                        _world.InventoryLiquid.PublishInjectionMotion(address.Cell,address.Material,exchange.InitialVelocity,placement.Quantity);
                    else if (Backend(address.Material) == MaterialSimulationBackendKind.Cellular)
                        _world.PublishCellularImpulse(address.Cell, address.Material, placement.Quantity, exchange.InitialVelocity);
                }
            for(int i=0;i<_count;i++)
            {
                var placement=_journal[i];var address=placement.Address;
                if(Backend(address.Material)==MaterialSimulationBackendKind.GridLiquid)
                    _world.InventoryLiquid.PublishCommittedBirth(address.Cell,address.Material,_birthPrevious[i],placement.Quantity);
            }
            for (int i = 0; i < exchange.Count; i++)
                _world.RecordInventoryImport((MaterialId)exchange.Amounts[i].MaterialKey, exchange.Amounts[i].Quantity);
            _world.InventorySolids?.PublishConversionGeometry();
            for (int i = 0; i < _count; i++)
            {
                var address = _journal[i].Address;
                switch (Backend(address.Material))
                {
                    case MaterialSimulationBackendKind.GridLiquid: _world.InventoryLiquid.PublishConversionChange(address.Cell); break;
                    case MaterialSimulationBackendKind.FixedSolid: _world.NotifyMaterialTopologyChanged(address.Cell); break;
                    case MaterialSimulationBackendKind.GridGas: _world.InventoryGases.PublishConversionChange(address.Cell); break;
                }
            }
            _world.InventoryGases?.FinishConversion();
            if (_world.CellularSimulation != null) _world.PublishCellularChanges();
            transferred = total; return MaterialTransferFailure.None;
        }
        private MaterialTransferFailure StageComponent(MaterialId material, ulong remaining, Vector3 position, float radius)
        {
            bool liquid = Backend(material) == MaterialSimulationBackendKind.GridLiquid;
            Vector3 origin = liquid ? _world.InventoryLiquid.Origin : _world.ConversionOrigin;
            float size = liquid ? _world.InventoryLiquid.CellSize : _world.ConversionCellSize;
            Vector3Int center = Vector3Int.FloorToInt((position - origin) / size);
            var minimum = Vector3Int.FloorToInt((position - Vector3.one * radius - origin) / size);
            var maximum = Vector3Int.FloorToInt((position + Vector3.one * radius - origin) / size);
            long candidates = (long)(maximum.x - minimum.x + 1) * (maximum.y - minimum.y + 1) * (maximum.z - minimum.z + 1);
            if (candidates <= 0 || candidates > MaximumVisited - _visited) return MaterialTransferFailure.SamplingLimit;
            _visited += (int)candidates;
            // 首选准星/前方所在格，随后仅检查与物理投放球相交的邻格；不是无限寻找空位。
            if (TryStage(center, material, remaining, out uint placed)) remaining -= placed;
            for (int y = minimum.y; y <= maximum.y && remaining > 0; y++)
            for (int z = minimum.z; z <= maximum.z && remaining > 0; z++)
            for (int x = minimum.x; x <= maximum.x && remaining > 0; x++)
            {
                var cell = new Vector3Int(x, y, z); if (cell == center) continue;
                Vector3 lower = origin + (Vector3)cell * size;
                Vector3 closest = new Vector3(Mathf.Clamp(position.x, lower.x, lower.x + size),
                    Mathf.Clamp(position.y, lower.y, lower.y + size), Mathf.Clamp(position.z, lower.z, lower.z + size));
                if ((closest - position).sqrMagnitude > radius * radius) continue;
                if (TryStage(cell, material, remaining, out placed)) remaining -= placed;
            }
            return remaining == 0 ? MaterialTransferFailure.None : MaterialTransferFailure.Capacity;
        }
        private bool TryStage(Vector3Int cell, MaterialId material, ulong remaining, out uint placed)
        {
            placed = 0; if (_count == MaximumEntries) return false;
            var address = new MaterialConversionAddress(cell, material);
            if (!_world.IsConversionAddressActive(address)) return false;
            uint free;
            bool resting = false;
            switch (Backend(material))
            {
                case MaterialSimulationBackendKind.Cellular:
                    // 这里是多组分暂存。若暂存时先把旧气体推入邻格，扣回本次量也恢复不了原来的气体位置。
                    // 只使用无需挤走邻居的本格空间，其余量由同一个有界投放球继续寻找落点。
                    free = CellularDepositCapacity(cell, material, out resting); break;
                case MaterialSimulationBackendKind.GridLiquid:
                    if (!_world.InventoryLiquid.PrepareConversionContact(cell, material)) return false;
                    free = _world.InventoryLiquid.ConversionFreeCapacity(cell); break;
                case MaterialSimulationBackendKind.FixedSolid:
                    ulong occupiedSolid = 0;
                    var solids = _world.InventorySolids.ConfigurationProfile;
                    for (int i = 0; i < solids.MaterialCount; i++)
                        if (_world.InventorySolids.TryGetQuantity(cell, solids.GetMaterial(i).Material, out uint q)) occupiedSolid += q;
                    free = occupiedSolid >= _world.InventorySolids.CellCapacity ? 0 : _world.InventorySolids.CellCapacity - (uint)occupiedSolid; break;
                case MaterialSimulationBackendKind.GridGas:
                    ulong occupiedGas = 0;
                    for (int i = 0; i < _world.InventoryGases.MaterialCount; i++) if (_world.InventoryGases.TryGetQuantity(cell, _world.InventoryGases.GetMaterial(i), out uint q)) occupiedGas += q;
                    free = occupiedGas >= _world.InventoryGases.CellCapacity ? 0 : _world.InventoryGases.CellCapacity - (uint)occupiedGas; break;
                default: return false;
            }
            uint amount = (uint)Math.Min(free, remaining); if (amount == 0) return false;
            bool success;LiquidCellPresentationSample before=default;
            switch (Backend(material))
            {
                case MaterialSimulationBackendKind.Cellular:
                    success = resting ? _world.RestingLiquids.TryStageAdd(cell, material, amount)
                        : _world.TryDepositIntoWorkingCell(cell, material, amount);
                    break;
                case MaterialSimulationBackendKind.GridLiquid: success = _world.InventoryLiquid.StageInventoryBirth(cell, material, amount,out before); break;
                case MaterialSimulationBackendKind.FixedSolid: success = _world.InventorySolids.StageConversionAdd(cell, material, amount); break;
                case MaterialSimulationBackendKind.GridGas: success = _world.InventoryGases.StageConversionAdd(cell, material, amount); break;
                default: return false;
            }
            if (!success) return false;
            _restingEntries[_count] = resting;
            _hasStagedResting |= resting;
            _birthPrevious[_count]=before;_journal[_count++] = new MaterialConversionPlacement(cell, material, amount); placed = amount; return true;
        }
        private uint CellularDepositCapacity(Vector3Int cell, MaterialId material, out bool resting)
        {
            // 静水部分满格仍由原区域持有，不能在同一基础格新建一份 CA 库存。已交接的空格归动态 Owner。
            return _world.AvailableForDeposit(cell, material, false, out resting);
        }
        private void Undo()
        {
            if (_hasStagedResting) _world.RestingLiquids.CancelStaged();
            for (int i = _count - 1; i >= 0; i--)
            {
                if (_restingEntries[i]) continue;
                var target = _journal[i];
                switch (Backend(target.Address.Material))
                {
                    case MaterialSimulationBackendKind.Cellular:
                        if (!_world.CellularSimulation.TryRemove(target.Address.Cell, target.Address.Material, target.Quantity))
                            throw new InvalidOperationException("Cellular 投放事务无法撤销原格的暂存数量。");
                        break;
                    case MaterialSimulationBackendKind.GridLiquid: _world.InventoryLiquid.UndoConversionAdd(target.Address.Cell, target.Address.Material, target.Quantity); break;
                    case MaterialSimulationBackendKind.FixedSolid:
                        _world.InventorySolids.UndoConversionAdd(target.Address.Cell, target.Address.Material, target.Quantity);
                        _world.InventoryLiquid?.InvalidateConversionCell(target.Address.Cell); break;
                    case MaterialSimulationBackendKind.GridGas: _world.InventoryGases.UndoConversionAdd(target.Address.Cell, target.Address.Material, target.Quantity); break;
                }
            }
            _world.InventorySolids?.CancelConversionGeometry(); _world.InventoryGases?.FinishConversion();
        }
        private MaterialSimulationBackendKind Backend(MaterialId material) => _world.ResolveConversionBackend(material);
        private static bool Finite(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
    }
}
