using System;
using Game.Combat;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementWorldRuntime
    {
        private struct CellularPoisonRemoval
        {
            public Vector3Int Cell;
            public uint Quantity;
            public bool Resting;
        }

        private bool _cellularCanIgnite;
        private IgniteGooTuning _cellularIgnition;
        private ToxicCombustionTuning _cellularToxicTuning;
        private MaterialReactionProcessTable _cellularToxicProcesses;
        private ToxicWorldReactionResolution[] _cellularToxicResults;
        private CellularPoisonRemoval[] _cellularPoisonRemovals;
        private Func<Vector3Int, int> _readCellularPoison;

        private void InitializeCellularCombustion()
        {
            _cellularCanIgnite = OwnsCellularMaterial(MaterialId.Sticky)
                && _materialReactions.TryResolve(MaterialId.Fire, MaterialId.Sticky, out var ignite) && ignite == ElementReactionId.IgniteGoo;
            if (_cellularCanIgnite) _cellularIgnition = _profile.IgniteGoo;
            if (!OwnsCellularMaterial(MaterialId.Poison) || !_materialReactions.TryResolve(MaterialId.Fire, MaterialId.Poison, out var toxic)
                || toxic != ElementReactionId.ToxicCombustion) return;

            var tuning = _profile.ToxicCombustion;
            _cellularToxicTuning = LiquidReactionAdapter.CreateWorldToxicTuning(in tuning);
            int radiusCells = Mathf.Max(1, Mathf.CeilToInt(_cellularToxicTuning.Radius / _profile.CellSize));
            int capacity = _profile.MaxPendingWrites;
            _cellularToxicProcesses = new MaterialReactionProcessTable(capacity, radiusCells, true);
            _cellularToxicResults = new ToxicWorldReactionResolution[capacity];
            _cellularPoisonRemovals = new CellularPoisonRemoval[9];
            _readCellularPoison = ReadCellularPoisonNeighborhood;
        }

        private void ClearCellularCombustion()
        {
            _cellularCanIgnite = false;
            _cellularToxicProcesses = null;
            _cellularToxicResults = null;
            _cellularPoisonRemovals = null;
            _readCellularPoison = null;
        }

        /// <summary>灭火完成后用同一份火源地址检查面接触；有效强度重新读 Current。没有新接触时仍推进既有预热与冷却。</summary>
        private void StepCellularCombustion(LiquidCellFireSample[] samples, int count)
        {
            if (!_cellularCanIgnite && _cellularToxicProcesses == null) return;
            for (int i = 0; i < count; i++)
            {
                Vector3Int source = samples[i].GlobalCell;
                byte fire = ReadCommittedCellularFire(source);
                if (fire == 0) continue;
                foreach (Vector3Int face in FireContacts)
                {
                    if (!ElementCellSimulation.Offset(source, face, out Vector3Int cell)) continue;
                    if (_cellularCanIgnite && TryIgniteCellularFuel(cell, fire) && cell == source)
                        fire = ReadCommittedCellularFire(source);
                    if (_cellularToxicProcesses != null && TryGetCellularQuantity(cell, MaterialId.Poison, out _)
                        && !_cellularToxicProcesses.HasToxicContact(source, cell))
                        _cellularToxicProcesses.TryStartToxic(source, cell, fire, ReadCellularPoisonNeighborhood(cell), in _cellularToxicTuning);
                }
            }
            if (_cellularToxicProcesses == null) return;
            int resolved = _cellularToxicProcesses.TickToxicCore(_tickInterval, this, in _cellularToxicTuning,
                _cellularToxicResults, _readCellularPoison);
            for (int i = 0; i < resolved; i++) ResolveCellularToxic(in _cellularToxicResults[i]);
        }

        private byte ReadCommittedCellularFire(Vector3Int cell) =>
            TryGetCell(cell, out var current) && current.MaterialKind == MaterialId.Fire ? current.Amount : (byte)0;

        private bool TryIgniteCellularFuel(Vector3Int cell, byte sourceFire)
        {
            if (!TryGetCellularQuantity(cell, MaterialId.Sticky, out uint fuel)) return false;
            // 公式用 0..255 的充填读数和 Fire 强度。任何非零燃料至少可表示为一份；实际扣款向上取整并限制到当前 GMU。
            uint capacity = CellularCellCapacity;
            byte occupied = (byte)Math.Max(1UL, fuel * 255UL / capacity);
            TryGetAmount(cell, MaterialId.Water, out byte water);
            if (!LiquidReactionAdapter.TryPlanIgnition(sourceFire, ReadCommittedCellularFire(cell), occupied, water, _tickInterval,
                    in _cellularIgnition, out int consumed, out int produced)) return false;
            uint used = (uint)Math.Min(fuel, ((ulong)consumed * capacity + 254) / 255);
            var add = new ElementCellAddRequest(cell, MaterialId.Fire, (byte)produced);
            // 精确加火先检查目标格、固体、页容量和 byte 上限。燃料扣除失败时仅撤掉这笔新增强度。
            if (!TryApplyGridFireAdd(in add)) return false;
            if (TryStageCellularFuel(cell, MaterialId.Sticky, used, out bool resting))
            {
                if (resting) RestingLiquids.CommitStaged();
                return true;
            }
            var rollback = new ElementCellDeltaRequest(cell, MaterialId.Fire, (byte)produced);
            if (!TryApplyGridFireDelta(in rollback)) throw new InvalidOperationException("元胞黏液点燃失败后无法恢复原火强度。");
            return false;
        }

        private bool TryStageCellularFuel(Vector3Int cell, MaterialId material, uint amount, out bool resting)
        {
            resting = RestingLiquids != null && RestingLiquids.TryGetQuantity(cell, material, out _);
            return resting ? RestingLiquids.TryStageRemove(cell, material, amount) : CellularSimulation.TryRemove(cell, material, amount);
        }

        private int ReadCellularPoisonNeighborhood(Vector3Int center)
        {
            // 毒爆的阈值、最多消耗与每份伤害使用实际 GMU。高容量格不能被截为 255 GMU；九格仍允许预热期间毒液横移。
            ulong total = 0;
            for (int z = -1; z <= 1; z++)
            for (int x = -1; x <= 1; x++)
                if (ElementCellSimulation.Offset(center, new Vector3Int(x, 0, z), out var cell)
                    && TryGetCellularQuantity(cell, MaterialId.Poison, out uint quantity)) total += quantity;
            // 共用过程以 float 缓存计划，结算转回 int。此界是 Int32.MaxValue 以下能用 float 精确表示的最大整数。
            return (int)Math.Min(total, 2147483520UL);
        }

        private void ResolveCellularToxic(in ToxicWorldReactionResolution reaction)
        {
            if (reaction.ConsumedGmu <= 0) return;
            int requested = reaction.ConsumedGmu, remaining = requested, count = 0;
            for (int z = -1; z <= 1 && remaining > 0; z++)
            for (int x = -1; x <= 1 && remaining > 0; x++)
            {
                if (!ElementCellSimulation.Offset(reaction.LiquidCell, new Vector3Int(x, 0, z), out var cell)
                    || !TryGetCellularQuantity(cell, MaterialId.Poison, out uint current)) continue;
                uint take = Math.Min(current, (uint)remaining);
                _cellularPoisonRemovals[count++] = new CellularPoisonRemoval { Cell = cell, Quantity = take };
                remaining -= (int)take;
            }
            int consumed = requested - remaining;
            if (consumed < Mathf.Max(1f, _cellularToxicTuning.MinEffectiveConsume)) return;

            bool hasResting = false;
            for (int i = 0; i < count; i++)
            {
                ref CellularPoisonRemoval fuel = ref _cellularPoisonRemovals[i];
                if (!TryStageCellularFuel(fuel.Cell, MaterialId.Poison, fuel.Quantity, out fuel.Resting))
                {
                    if (hasResting) RestingLiquids.CancelStaged();
                    // 动态扣除只动本格，倒序恢复同种同量。暂存静水一次取消，避免把未爆炸的原液池改成活动格。
                    for (int j = i - 1; j >= 0; j--)
                    {
                        var previous = _cellularPoisonRemovals[j];
                        if (!previous.Resting && !CellularSimulation.TryDeposit(previous.Cell, MaterialId.Poison, previous.Quantity))
                            throw new InvalidOperationException("元胞毒爆无法撤销原格燃料扣除。");
                    }
                    return;
                }
                hasResting |= fuel.Resting;
            }
            if (hasResting) RestingLiquids.CommitStaged();
            // 同 Tick 另一个反应可能先取走重叠九格的燃料。只按本笔已经提交的数量发一次原有 Damage / VFX 事实。
            Vector3 position = _origin + ((Vector3)reaction.LiquidCell + Vector3.one * .5f) * _profile.CellSize;
            var damage = new WorldReactionDamageCommand(ElementReactionId.ToxicCombustion, position,
                Mathf.Max(0f, _cellularToxicTuning.BaseDamage) + consumed * Mathf.Max(0f, _cellularToxicTuning.DamagePerPoison), reaction.Radius);
            ApplyWorldReactionDamage(in damage);
        }
    }
}
