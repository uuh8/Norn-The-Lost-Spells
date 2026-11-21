using System;
using System.Collections.Generic;
using Game.Combat;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementWorldRuntime
    {
        private CellularAbsorptionQueue _absorbQueue;
        private HashSet<Vector3Int> _sealedAbsorption;
        private readonly Vector3Int[] _absorptionReceivers = new Vector3Int[6];
        private MaterialConversionPlacement[] _absorbPlacements;
        private ElementReactionTuningSnapshot _absorbTuning;
        private static readonly MaterialConversionRule AbsorbRecipe = new MaterialConversionRule
        {
            Source = MaterialId.Water, FirstProduct = MaterialId.Sticky,
            InputUnits = 1, FirstUnits = 1, InputGmuPerSecond = 1
        };

        private void InitializeCellularAbsorption()
        {
            if (!_materialReactions.TryResolve(MaterialId.Water, MaterialId.Sticky, out var id)
                || id != ElementReactionId.AbsorbWater || _profile.AbsorbWater.WaterConvertPerSecond <= 0f) return;
            _absorbQueue = new CellularAbsorptionQueue();
            _sealedAbsorption = new HashSet<Vector3Int>();
            _absorbPlacements = new MaterialConversionPlacement[7];
            _absorbTuning = new ElementReactionTuningSnapshot(default, default, default, default, _profile.AbsorbWater, 0f, 0f);
        }

        // 只在水或黏液发生变化时登记接触面。用水格作为唯一候选，多个相邻黏液格不能重复结算同一次接触。
        private void DiscoverCellularAbsorption(Vector3Int cell)
        {
            WakeSealedAbsorption(cell);
            if (TryGetCellularQuantity(cell, MaterialId.Water, out _))
            {
                if (FindTouchingSticky(cell, out _)) _absorbQueue.Enqueue(cell);
            }
            else if (TryGetCellularQuantity(cell, MaterialId.Sticky, out _))
                foreach (var face in ElementCellSimulation.Faces)
                    if (ElementCellSimulation.Offset(cell, face, out var water) && TryGetCellularQuantity(water, MaterialId.Water, out _))
                        _absorbQueue.Enqueue(water);
        }

        private void WakeSealedAbsorption(Vector3Int changed)
        {
            if (_sealedAbsorption == null || _sealedAbsorption.Count == 0) return;
            if (_sealedAbsorption.Remove(changed)) _absorbQueue.Enqueue(changed);
            foreach (var face in ElementCellSimulation.Faces)
                if (ElementCellSimulation.Offset(changed, face, out var cell) && _sealedAbsorption.Remove(cell))
                    _absorbQueue.Enqueue(cell);
        }

        private bool FindTouchingSticky(Vector3Int cell, out uint sticky)
        {
            foreach (var face in ElementCellSimulation.Faces)
                if (ElementCellSimulation.Offset(cell, face, out var neighbor) && TryGetCellularQuantity(neighbor, MaterialId.Sticky, out sticky))
                    return true;
            sticky = 0;
            return false;
        }

        private bool PrepareAbsorptionOutlets(Vector3Int cell, uint used)
        {
            uint missing = CellularSimulation.AbsorptionOutletDeficit(cell, used);
            if (missing == 0) return true;
            int count = 0;
            foreach (var face in ElementCellSimulation.Faces)
            {
                if (!ElementCellSimulation.Offset(cell, face, out var neighbor) ||
                    !RestingLiquids.TryGetQuantity(neighbor, MaterialId.Sticky, out uint quantity) || quantity >= CellularCellCapacity) continue;
                _absorptionReceivers[count++] = neighbor;
                missing -= Math.Min(missing, CellularCellCapacity - quantity);
                if (missing == 0) break;
            }
            if (missing != 0)
            {
                _sealedAbsorption.Add(cell);
                return false;
            }
            // 静止的部分黏液也有真实余量。先证明所有产物放得下，再按需转交接收格。
            // 槽满只改变调度；源水还未扣除，已经展开的邻格仍保有原数量。
            for (int i = 0; i < count; i++)
                if (!RestingLiquids.TryMaterialize(_absorptionReceivers[i], CellularSimulation))
                {
                    _absorbQueue.Enqueue(cell);
                    return false;
                }
            return true;
        }

        private void ProcessCellularAbsorption()
        {
            if (_absorbQueue == null) return;
            int work = Math.Min(32, _absorbQueue.Count);
            for (int i = 0; i < work; i++)
            {
                var cell = _absorbQueue.Take();
                if (!TryGetCellularQuantity(cell, MaterialId.Water, out var water) || !FindTouchingSticky(cell, out var sticky)) continue;
                var contact = new MaterialContactSnapshot(MaterialId.Water, ToCellIntensity(water), MaterialId.Sticky, ToCellIntensity(sticky));
                if (!ElementReactionEvaluator.TryEvaluateMaterialContact(in contact, _tickInterval, ElementReactionId.AbsorbWater,
                    in _absorbTuning, out var result) || result.FirstConsumedGmu <= 0) continue;
                uint used = (uint)Math.Min(water, ((ulong)result.FirstConsumedGmu * CellularCellCapacity + 254) / 255);
                if (used < water && !PrepareAbsorptionOutlets(cell, used)) continue;
                bool ready = !RestingLiquids.TryGetCell(cell, out _, out _) || RestingLiquids.TryMaterialize(cell, CellularSimulation);
                if (!ready || !CellularSimulation.TryReact(new MaterialConversionAddress(cell, MaterialId.Water), in AbsorbRecipe,
                    used, _absorbPlacements, CellularLedger, out _))
                    _absorbQueue.Enqueue(cell); // 临时资源不足后再试；成功提交则由该格变化自行唤醒下一批。
            }
        }

        private byte ToCellIntensity(uint amount) => (byte)Math.Max(1UL, (ulong)amount * 255 / CellularCellCapacity);
    }

    // 活动页与页内位置分别轮转，反应暂缓或高频改写一个位置时，其余接触仍能轮到。
    internal sealed class CellularAbsorptionQueue
    {
        private struct Page
        {
            internal ulong Pending;
            internal int Next;
        }

        private readonly Queue<ElementChunkKey> _ready = new Queue<ElementChunkKey>(32);
        private readonly Dictionary<ElementChunkKey, Page> _pages = new Dictionary<ElementChunkKey, Page>(32);
        internal int Count { get; private set; }

        internal void Enqueue(Vector3Int cell)
        {
            // 一页 4³ 个位置共用一个掩码和一个 FIFO 位置。工作预算只限制消费速度，
            // 洪峰过后，冷页里的接触仍在队列中，无需回扫全部驻留格来猜哪些事件丢了。
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell, 4, out var key, out var local);
            ulong bit = 1UL << (local.x + 4 * local.y + 16 * local.z);
            if (!_pages.TryGetValue(key, out var page)) _ready.Enqueue(key);
            if ((page.Pending & bit) != 0) return;
            page.Pending |= bit;
            _pages[key] = page;
            Count++;
        }

        internal Vector3Int Take()
        {
            var key = _ready.Dequeue();
            var page = _pages[key];
            int index = page.Next;
            while ((page.Pending & (1UL << index)) == 0) index = (index + 1) & 63;
            page.Pending &= ~(1UL << index);
            if (page.Pending == 0) _pages.Remove(key);
            else
            {
                page.Next = (index + 1) & 63;
                _pages[key] = page;
                _ready.Enqueue(key);
            }
            Count--;
            return ElementWorldCoordinates.ComposeGlobalCell(key, new Vector3Int(index & 3, (index >> 2) & 3, index >> 4), 4);
        }
    }
}
