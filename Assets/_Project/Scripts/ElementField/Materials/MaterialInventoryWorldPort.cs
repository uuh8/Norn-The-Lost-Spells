using System;
using Game.Core;
using Game.Materials;
using UnityEngine;
using Unity.Profiling;

namespace Game.ElementField
{
    /// <summary>有界批量扣量 Journal；主线程同步预约期间不发布观察事件，失败逆序恢复真实 Owner。</summary>
    internal sealed class MaterialInventoryWorldPort
    {
        private const int MaximumEntries = 512, MaximumVisitedCells = 8192;
        private readonly ElementWorldRuntime _world;
        private readonly MaterialConversionAddress[] _addresses = new MaterialConversionAddress[MaximumEntries];
        private readonly uint[] _available = new uint[MaximumEntries], _takes = new uint[MaximumEntries];
        private readonly LiquidConversionReceipt[] _receipts = new LiquidConversionReceipt[MaximumEntries];
        private readonly MaterialAmount[] _sample = new MaterialAmount[8], _amounts = new MaterialAmount[8];
        private readonly ulong[] _totals = new ulong[256], _remaining = new ulong[256];
        private readonly ulong[] _remainders = new ulong[8];
        private int _count, _components;
        private static readonly ProfilerMarker TransferMarker = new ProfilerMarker("Norn.MaterialTransfer.World");
        private static readonly ProfilerMarker SampleMarker = new ProfilerMarker("Norn.MaterialTransfer.Sample");
        internal MaterialInventoryWorldPort(ElementWorldRuntime world) { _world = world; }
        internal MaterialTransferFailure Transfer(in MaterialTransferRequestedEvent request, IMaterialTransferDestination destination, out ulong transferred)
        {
            transferred = 0;
            using var measurement = TransferMarker.Auto();
            MaterialTransferFailure failure;
            using (SampleMarker.Auto()) failure = Sample(request.SampleCenter, request.Radius, destination.AllowedMaterials, !request.SolidItem);
            if (failure != MaterialTransferFailure.None) return failure;
            ulong budget = Math.Min(request.Budget, destination.FreeCapacity);
            if (budget == 0) return MaterialTransferFailure.Capacity;
            ulong total = MaterialAmountOps.SplitBudget(_sample, _components, budget, _amounts, _remainders, (int)(request.RequestId % 8));
            uint liquidVersion = _world.InventoryLiquid?.SurfaceVersion ?? 0, solidVersion = _world.FixedSolids?.Version ?? 0,
                gasVersion = _world.Gases?.Version ?? 0;
            if (!destination.TryReserve(_amounts, _components, out failure)) return failure;
            if (liquidVersion != (_world.InventoryLiquid?.SurfaceVersion ?? 0) || solidVersion != (_world.FixedSolids?.Version ?? 0)
                || gasVersion != (_world.Gases?.Version ?? 0)) { destination.Cancel(); return MaterialTransferFailure.StaleVersion; }
            Array.Clear(_remaining, 0, _remaining.Length);
            for (int i = 0; i < _components; i++) _remaining[_amounts[i].MaterialKey] = _amounts[i].Quantity;
            for (int i = 0; i < _count; i++)
            {
                byte key = (byte)_addresses[i].Material;
                _takes[i] = (uint)Math.Min(_available[i], _remaining[key]); _remaining[key] -= _takes[i];
                if (_takes[i] > 0 && (!Read(_addresses[i], out uint current) || current != _available[i] || !_world.IsConversionAddressActive(_addresses[i])))
                { destination.Cancel(); return MaterialTransferFailure.StaleVersion; }
            }
            _world.FixedSolids?.BeginConversionGeometry();
            int staged = 0;
            for (; staged < _count; staged++) if (_takes[staged] > 0 && !Remove(staged)) break;
            bool valid = staged == _count && (_world.FixedSolids == null || _world.FixedSolids.ValidateConversionGeometry());
            // 改变固体支撑必须先通过现有 Stable 拓扑交接；不是拆 Collider 后让湖泊继续借旧池底。
            for (int i = 0; i < staged && valid; i++)
                if (_takes[i] > 0 && Backend(_addresses[i]) == MaterialSimulationBackendKind.FixedSolid
                    && _world.FixedSolids.BlocksWholeCell(_addresses[i].Material))
                    valid = _world.TryPrepareMaterialTopologyChange(_addresses[i].Cell, _world.FixedSolids.BlocksCell(_addresses[i].Cell));
            if (!valid)
            { Undo(staged); destination.Cancel(); return MaterialTransferFailure.GeometryRejected; }
            // Commit 的目标操作仅写已验证的固定数组/预留库存；这段调用不能发布外部事件或交错写入。
            destination.Commit();
            for (int i = 0; i < _components; i++) _world.RecordInventoryExport((MaterialId)_amounts[i].MaterialKey, _amounts[i].Quantity);
            _world.FixedSolids?.PublishConversionGeometry();
            for (int i = 0; i < _count; i++) if (_takes[i] > 0) Publish(_addresses[i]);
            _world.Gases?.FinishConversion(); transferred = total; return MaterialTransferFailure.None;
        }
        private MaterialTransferFailure Sample(Vector3 center, float radius, ushort allowed, bool strictMix)
        {
            _count = _components = 0; Array.Clear(_totals, 0, _totals.Length); int visited = 0;
            for (byte key = 1; key <= 8; key++)
            {
                bool compatible = (allowed & 1 << key) != 0;
                // 容器检查整个真实样本；跳过不兼容组分会把混合物变成免费提纯。
                // 直接拾取固体仍按所选材料身份采样，不把周围液体变成该固体物品。
                if (!compatible && !strictMix) continue;
                if (!_world.MaterialRoutes.TryResolve((MaterialId)key, out var backend)) continue;
                if (backend != MaterialSimulationBackendKind.GridLiquid && backend != MaterialSimulationBackendKind.FixedSolid
                    && backend != MaterialSimulationBackendKind.GridGas)
                {
                    if (compatible) return MaterialTransferFailure.UnsupportedBackend;
                    continue;
                }
                Vector3 origin = backend == MaterialSimulationBackendKind.GridLiquid ? _world.InventoryLiquid.Origin : _world.ConversionOrigin;
                float size = backend == MaterialSimulationBackendKind.GridLiquid ? _world.InventoryLiquid.CellSize : _world.ConversionCellSize;
                var minimum = Vector3Int.FloorToInt((center - Vector3.one * radius - origin) / size);
                var maximum = Vector3Int.FloorToInt((center + Vector3.one * radius - origin) / size);
                for (int y = minimum.y; y <= maximum.y; y++) for (int z = minimum.z; z <= maximum.z; z++) for (int x = minimum.x; x <= maximum.x; x++)
                {
                    if (++visited > MaximumVisitedCells) return MaterialTransferFailure.SamplingLimit;
                    var cell = new Vector3Int(x, y, z); Vector3 lower = origin + (Vector3)cell * size;
                    Vector3 closest = new Vector3(Mathf.Clamp(center.x, lower.x, lower.x + size),
                        Mathf.Clamp(center.y, lower.y, lower.y + size), Mathf.Clamp(center.z, lower.z, lower.z + size));
                    if ((closest - center).sqrMagnitude > radius * radius) continue;
                    var address = new MaterialConversionAddress(cell, (MaterialId)key);
                    if (!_world.IsConversionAddressActive(address) || !Read(address, out uint quantity) || quantity == 0) continue;
                    if (!compatible) return MaterialTransferFailure.IncompatibleMaterial;
                    if (_count == MaximumEntries) return MaterialTransferFailure.SamplingLimit;
                    _addresses[_count] = address; _available[_count] = quantity; _count++;
                    _totals[key] += quantity;
                }
                if (_totals[key] > 0) _sample[_components++] = new MaterialAmount(key, _totals[key]);
            }
            return _components == 0 ? MaterialTransferFailure.NoMaterial : MaterialTransferFailure.None;
        }
        private MaterialSimulationBackendKind Backend(MaterialConversionAddress address) => _world.ResolveConversionBackend(address.Material);
        private bool Read(MaterialConversionAddress address, out uint quantity)
        {
            quantity = 0;
            switch (Backend(address))
            {
                case MaterialSimulationBackendKind.GridLiquid: return _world.InventoryLiquid.TryGetQuantity(address.Cell, address.Material, out quantity);
                case MaterialSimulationBackendKind.FixedSolid: return _world.FixedSolids.TryGetQuantity(address.Cell, address.Material, out quantity);
                case MaterialSimulationBackendKind.GridGas: return _world.Gases.TryGetQuantity(address.Cell, address.Material, out quantity);
                default: return false;
            }
        }
        private bool Remove(int i)
        {
            var address = _addresses[i];
            switch (Backend(address))
            {
                case MaterialSimulationBackendKind.GridLiquid: return _world.InventoryLiquid.StageConversionRemove(address.Cell, address.Material, _takes[i], out _receipts[i]);
                case MaterialSimulationBackendKind.FixedSolid: return _world.FixedSolids.StageConversionRemove(address.Cell, address.Material, _takes[i]);
                case MaterialSimulationBackendKind.GridGas: return _world.Gases.StageConversionRemove(address.Cell, address.Material, _takes[i]);
                default: return false;
            }
        }
        private void Undo(int staged)
        {
            for (int i = staged - 1; i >= 0; i--) if (_takes[i] > 0)
            {
                var address = _addresses[i];
                switch (Backend(address))
                {
                    case MaterialSimulationBackendKind.GridLiquid: _world.InventoryLiquid.UndoConversionRemove(address.Cell, address.Material, _receipts[i]); break;
                    case MaterialSimulationBackendKind.FixedSolid:
                        _world.FixedSolids.UndoConversionRemove(address.Cell, address.Material, _takes[i]);
                        _world.InventoryLiquid?.InvalidateConversionCell(address.Cell); break;
                    case MaterialSimulationBackendKind.GridGas: _world.Gases.UndoConversionRemove(address.Cell, address.Material, _takes[i]); break;
                }
            }
            _world.FixedSolids?.CancelConversionGeometry(); _world.Gases?.FinishConversion();
        }
        private void Publish(MaterialConversionAddress address)
        {
            switch (Backend(address))
            {
                case MaterialSimulationBackendKind.GridLiquid: _world.InventoryLiquid.PublishConversionChange(address.Cell); break;
                case MaterialSimulationBackendKind.FixedSolid: _world.NotifyMaterialTopologyChanged(address.Cell); break;
                case MaterialSimulationBackendKind.GridGas: _world.Gases.PublishConversionChange(address.Cell); break;
            }
        }
    }
}
