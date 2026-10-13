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
        private readonly ElementWorldRuntime _world;
        private int _count, _visited;
        private static readonly ProfilerMarker Marker = new ProfilerMarker("Norn.MaterialTransfer.Deposit");
        internal MaterialInventoryDepositPort(ElementWorldRuntime world) { _world = world; }
        internal MaterialTransferFailure Deposit(MaterialDepositExchange exchange, out ulong transferred)
        {
            using var marker = Marker.Auto(); transferred = 0; _count = _visited = 0;
            if (exchange.Source == null || exchange.Amounts == null || exchange.Count <= 0 || exchange.Count > 8
                || exchange.Count > exchange.Amounts.Length || !Finite(exchange.Position) || !float.IsFinite(exchange.Radius)
                || exchange.Radius <= 0 || exchange.Radius > 1) return MaterialTransferFailure.InvalidRequest;
            ushort seen = 0; ulong total = 0;
            for (int i = 0; i < exchange.Count; i++)
            {
                var amount = exchange.Amounts[i];
                if (!MaterialAmount.IsKnown(amount.MaterialKey) || (seen & 1 << amount.MaterialKey) != 0
                    || amount.Quantity > ulong.MaxValue - total) return MaterialTransferFailure.InvalidRequest;
                seen |= (ushort)(1 << amount.MaterialKey); total += amount.Quantity;
                if (amount.Quantity == 0) continue;
                var backend = Backend((MaterialId)amount.MaterialKey);
                if (backend != MaterialSimulationBackendKind.GridLiquid && backend != MaterialSimulationBackendKind.FixedSolid
                    && backend != MaterialSimulationBackendKind.GridGas) return MaterialTransferFailure.UnsupportedBackend;
                if (backend == MaterialSimulationBackendKind.GridLiquid ? _world.InventoryLiquid == null || !_world.InventoryLiquid.IsFluidInitialized
                    : backend == MaterialSimulationBackendKind.FixedSolid ? _world.FixedSolids == null || !_world.FixedSolids.IsMaterialInitialized
                    : _world.Gases == null || !_world.Gases.IsMaterialInitialized) return MaterialTransferFailure.WorldUnavailable;
            }
            if (total == 0) return MaterialTransferFailure.NoMaterial;
            if (!exchange.Source.TryReserve(exchange.Amounts, exchange.Count, out var failure)) return failure;
            _world.FixedSolids?.BeginConversionGeometry();
            for (int i = 0; i < exchange.Count; i++)
            {
                var amount = exchange.Amounts[i]; if (amount.Quantity == 0) continue;
                failure = StageComponent((MaterialId)amount.MaterialKey, amount.Quantity, exchange.Position, exchange.Radius);
                if (failure != MaterialTransferFailure.None) { Undo(); exchange.Source.Cancel(); return failure; }
            }
            bool valid = _world.FixedSolids == null || _world.FixedSolids.ValidateConversionGeometry();
            for (int i = 0; i < _count && valid; i++)
            {
                var address = _journal[i].Address;
                if (Backend(address.Material) != MaterialSimulationBackendKind.FixedSolid) continue;
                valid = _world.InventoryLiquid == null || _world.InventoryLiquid.ValidateConversionVolume(address.Cell);
                if (valid && _world.FixedSolids.BlocksWholeCell(address.Material))
                    valid = _world.TryPrepareMaterialTopologyChange(address.Cell, true);
            }
            if (!valid) { Undo(); exchange.Source.Cancel(); return MaterialTransferFailure.GeometryRejected; }
            exchange.Source.Commit();
            for (int i = 0; i < exchange.Count; i++)
                _world.RecordInventoryImport((MaterialId)exchange.Amounts[i].MaterialKey, exchange.Amounts[i].Quantity);
            _world.FixedSolids?.PublishConversionGeometry();
            for (int i = 0; i < _count; i++)
            {
                var address = _journal[i].Address;
                switch (Backend(address.Material))
                {
                    case MaterialSimulationBackendKind.GridLiquid: _world.InventoryLiquid.PublishConversionChange(address.Cell); break;
                    case MaterialSimulationBackendKind.FixedSolid: _world.NotifyMaterialTopologyChanged(address.Cell); break;
                    case MaterialSimulationBackendKind.GridGas: _world.Gases.PublishConversionChange(address.Cell); break;
                }
            }
            _world.Gases?.FinishConversion(); transferred = total; return MaterialTransferFailure.None;
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
            switch (Backend(material))
            {
                case MaterialSimulationBackendKind.GridLiquid:
                    if (!_world.InventoryLiquid.PrepareConversionContact(cell, material)) return false;
                    free = _world.InventoryLiquid.ConversionFreeCapacity(cell); break;
                case MaterialSimulationBackendKind.FixedSolid:
                    ulong occupiedSolid = 0;
                    for (byte key = 5; key <= 6; key++) if (_world.FixedSolids.TryGetQuantity(cell, (MaterialId)key, out uint q)) occupiedSolid += q;
                    free = occupiedSolid >= _world.FixedSolids.CellCapacity ? 0 : _world.FixedSolids.CellCapacity - (uint)occupiedSolid; break;
                case MaterialSimulationBackendKind.GridGas:
                    ulong occupiedGas = 0;
                    for (int i = 0; i < _world.Gases.MaterialCount; i++) if (_world.Gases.TryGetQuantity(cell, _world.Gases.GetMaterial(i), out uint q)) occupiedGas += q;
                    free = occupiedGas >= _world.Gases.CellCapacity ? 0 : _world.Gases.CellCapacity - (uint)occupiedGas; break;
                default: return false;
            }
            uint amount = (uint)Math.Min(free, remaining); if (amount == 0) return false;
            bool success;
            switch (Backend(material))
            {
                case MaterialSimulationBackendKind.GridLiquid: success = _world.InventoryLiquid.StageConversionAdd(cell, material, amount); break;
                case MaterialSimulationBackendKind.FixedSolid: success = _world.FixedSolids.StageConversionAdd(cell, material, amount); break;
                case MaterialSimulationBackendKind.GridGas: success = _world.Gases.StageConversionAdd(cell, material, amount); break;
                default: return false;
            }
            if (!success) return false;
            _journal[_count++] = new MaterialConversionPlacement(cell, material, amount); placed = amount; return true;
        }
        private void Undo()
        {
            for (int i = _count - 1; i >= 0; i--)
            {
                var target = _journal[i];
                switch (Backend(target.Address.Material))
                {
                    case MaterialSimulationBackendKind.GridLiquid: _world.InventoryLiquid.UndoConversionAdd(target.Address.Cell, target.Address.Material, target.Quantity); break;
                    case MaterialSimulationBackendKind.FixedSolid:
                        _world.FixedSolids.UndoConversionAdd(target.Address.Cell, target.Address.Material, target.Quantity);
                        _world.InventoryLiquid?.InvalidateConversionCell(target.Address.Cell); break;
                    case MaterialSimulationBackendKind.GridGas: _world.Gases.UndoConversionAdd(target.Address.Cell, target.Address.Material, target.Quantity); break;
                }
            }
            _world.FixedSolids?.CancelConversionGeometry(); _world.Gases?.FinishConversion();
        }
        private MaterialSimulationBackendKind Backend(MaterialId material) => _world.ResolveConversionBackend(material);
        private static bool Finite(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
    }
}
