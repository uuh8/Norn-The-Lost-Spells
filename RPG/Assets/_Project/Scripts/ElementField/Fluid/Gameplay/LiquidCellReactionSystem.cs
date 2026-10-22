using Game.Materials;
using System;
using Game.Combat;
using UnityEngine;

namespace Game.ElementField
{
    public readonly struct LiquidCellFireSample
    {
        public LiquidCellFireSample(Vector3Int globalCell, byte amount)
        {
            GlobalCell = globalCell;
            Amount = amount;
        }

        public Vector3Int GlobalCell { get; }
        public byte Amount { get; }
    }

    /// <summary>
    /// 只做确定性反应规划，不直接修改 CPU/GPU World。Water reservation 防止同一份延迟 Snapshot
    /// 被多个 Fire Cell 重复消费；成功提交后才记住 SnapshotVersion。
    /// </summary>
    public sealed class LiquidCellReactionSystem
    {
        private static readonly Vector3Int[] ContactOffsets =
        {
            Vector3Int.zero,
            Vector3Int.left,
            Vector3Int.right,
            Vector3Int.down,
            Vector3Int.up,
            new Vector3Int(0, 0, -1),
            new Vector3Int(0, 0, 1),
        };

        private readonly ElementCellDeltaRequest[] _fireDeltas;
        private readonly FluidConsumeCommand[] _consumeCommands;
        private readonly MaterialReactionCatalogSnapshot _reactionCatalog;
        private readonly Vector3Int[] _reservedWaterCells;
        private readonly byte[] _reservedWaterAmounts;
        private readonly MaterialReactionProcessTable _processTable;
        private readonly ToxicWorldReactionResolution[] _toxicResolutions;
        private readonly WorldReactionDamageCommand[] _damageCommands;
        private readonly ElementCellAddRequest[] _elementAdds;
        private readonly FluidConvertCommand[] _convertCommands;
        private readonly LiquidMaterialCellSample[] _stickySamples;
        private readonly Vector3Int[] _reservedStickyCells;
        private readonly byte[] _reservedStickyAmounts;
        private int _reservedWaterCount;
        private int _reservedStickyCount;
        private uint _lastCommittedSnapshotVersion;
        private bool _hasCommittedSnapshot;

        public LiquidCellReactionSystem(
            int maximumCommands,
            MaterialReactionCatalogSnapshot reactionCatalog)
        {
            if (maximumCommands <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumCommands));
            _reactionCatalog = reactionCatalog ?? throw new ArgumentNullException(nameof(reactionCatalog));
            _fireDeltas = new ElementCellDeltaRequest[maximumCommands];
            _consumeCommands = new FluidConsumeCommand[maximumCommands];
            _reservedWaterCells = new Vector3Int[maximumCommands];
            _reservedWaterAmounts = new byte[maximumCommands];
            _processTable = new MaterialReactionProcessTable(maximumCommands);
            _toxicResolutions = new ToxicWorldReactionResolution[maximumCommands];
            _damageCommands = new WorldReactionDamageCommand[maximumCommands];
            _elementAdds = new ElementCellAddRequest[maximumCommands];
            _convertCommands = new FluidConvertCommand[maximumCommands];
            _stickySamples = new LiquidMaterialCellSample[maximumCommands];
            _reservedStickyCells = new Vector3Int[maximumCommands];
            _reservedStickyAmounts = new byte[maximumCommands];
        }

        public bool TryPlanAndCommit(
            ILiquidOccupancyReadOnly occupancy,
            LiquidCellFireSample[] fireCells,
            int fireCellCount,
            float deltaTime,
            in ExtinguishTuning tuning,
            FluidReactionCommandQueue queue)
        {
            ToxicCombustionTuning disabled = default;
            return TryPlanAndCommit(occupancy, fireCells, fireCellCount, deltaTime, in tuning,
                in disabled, default, default, Vector3.zero, 1f, queue);
        }

        public bool TryPlanAndCommit(
            ILiquidOccupancyReadOnly occupancy,
            LiquidCellFireSample[] fireCells,
            int fireCellCount,
            float deltaTime,
            in ExtinguishTuning tuning,
            in ToxicCombustionTuning toxicTuning,
            Vector3 worldOrigin,
            float cellSize,
            FluidReactionCommandQueue queue)
        {
            return TryPlanAndCommit(occupancy, fireCells, fireCellCount, deltaTime, in tuning,
                in toxicTuning, default, default, worldOrigin, cellSize, queue);
        }

        public bool TryPlanAndCommit(
            ILiquidOccupancyReadOnly occupancy,
            LiquidCellFireSample[] fireCells,
            int fireCellCount,
            float deltaTime,
            in ExtinguishTuning tuning,
            in ToxicCombustionTuning toxicTuning,
            in IgniteGooTuning igniteGooTuning,
            in AbsorbWaterTuning absorbWaterTuning,
            Vector3 worldOrigin,
            float cellSize,
            FluidReactionCommandQueue queue)
        {
            if (occupancy == null || fireCells == null || queue == null)
                return false;
            if (!occupancy.HasValidSnapshot || deltaTime <= 0f)
                return false;
            bool allowWaterPlanning = !_hasCommittedSnapshot
                || occupancy.SnapshotVersion != _lastCommittedSnapshotVersion;
            uint amountUnitsPerParticle = 0u;
            if (allowWaterPlanning
                && !occupancy.TryGetAmountUnitsPerParticle(MaterialId.Water, out amountUnitsPerParticle))
                allowWaterPlanning = false;

            fireCellCount = Mathf.Clamp(fireCellCount, 0, fireCells.Length);
            _reservedWaterCount = 0;
            _reservedStickyCount = 0;
            int fireDeltaCount = 0;
            int consumeCommandCount = 0;
            int elementAddCount = 0;
            int convertCommandCount = 0;
            for (int fireIndex = 0; fireIndex < fireCellCount; fireIndex++)
            {
                LiquidCellFireSample fire = fireCells[fireIndex];
                int remainingFireAmount = fire.Amount;
                if (remainingFireAmount <= 0)
                    continue;

                for (int contact = 0; contact < ContactOffsets.Length; contact++)
                {
                    if (remainingFireAmount <= 0 || fireDeltaCount >= _fireDeltas.Length)
                        break;

                    if (!allowWaterPlanning) break;
                    Vector3Int waterCell = fire.GlobalCell + ContactOffsets[contact];
                    if (!occupancy.TryGetAmount(waterCell, MaterialId.Water, out byte snapshotWater))
                        continue;
                    int availableWater = snapshotWater - GetReservedWater(waterCell);
                    if (availableWater <= 0)
                        continue;

                    if (!_reactionCatalog.TryResolve(
                            MaterialId.Fire,
                            MaterialId.Water,
                            out ElementReactionId reaction))
                        continue;
                    var contactSnapshot = new MaterialContactSnapshot(
                        MaterialId.Fire,
                        remainingFireAmount,
                        MaterialId.Water,
                        availableWater);
                    if (!ElementReactionEvaluator.TryEvaluateMaterialContact(
                        in contactSnapshot,
                        deltaTime,
                        reaction,
                        in tuning,
                        out MaterialReactionResult result))
                        continue;
                    int fireConsumedAmount = result.FirstConsumedGmu;
                    int waterConsumedAmount = result.SecondConsumedGmu;
                    if (fireConsumedAmount <= 0 || waterConsumedAmount <= 0)
                        continue;

                    _fireDeltas[fireDeltaCount++] = new ElementCellDeltaRequest(
                        fire.GlobalCell,
                        MaterialId.Fire,
                        (byte)fireConsumedAmount);
                    // GPU 粒子删除命令只预留实际投入的 Water，不能再用更大的 Fire 移除量代替。
                    ReserveWater(waterCell, waterConsumedAmount);
                    int reservationIndex = FindReservedWater(waterCell);
                    uint totalParticles = ((uint)_reservedWaterAmounts[reservationIndex]
                        + amountUnitsPerParticle - 1u) / amountUnitsPerParticle;
                    if (reservationIndex >= consumeCommandCount)
                        consumeCommandCount = reservationIndex + 1;
                    _consumeCommands[reservationIndex] = new FluidConsumeCommand(
                        waterCell,
                        waterCell,
                        (uint)MaterialId.Water,
                        totalParticles,
                        HashSeed(occupancy.SnapshotVersion, fire.GlobalCell, waterCell));
                    remainingFireAmount -= fireConsumedAmount;
                }
            }

            // Water + Sticky：Sticky 只作为催化接触条件，Water 粒子在原位置 Retag 为 Sticky。
            // 转换量仍进入 Water Reservation，避免同一延迟 Snapshot 被多个相邻 Sticky Cell 重复规划。
            int stickySampleCount = occupancy.CopyOccupiedCells(MaterialId.Sticky, _stickySamples);
            if (occupancy.TryGetAmountUnitsPerParticle(MaterialId.Sticky, out uint stickyScale))
            {
                var reactionTuning = new ElementReactionTuningSnapshot(
                    default, default, default, igniteGooTuning, absorbWaterTuning, 0f, 0f);
                for (int stickyIndex = 0;
                     allowWaterPlanning
                     && stickyIndex < stickySampleCount
                     && convertCommandCount < _convertCommands.Length;
                     stickyIndex++)
                {
                    LiquidMaterialCellSample sticky = _stickySamples[stickyIndex];
                    for (int contact = 0; contact < ContactOffsets.Length; contact++)
                    {
                        Vector3Int waterCell = sticky.GlobalCell + ContactOffsets[contact];
                        if (!occupancy.TryGetAmount(waterCell, MaterialId.Water, out byte snapshotWater)
                            || snapshotWater == 0
                            || !_reactionCatalog.TryResolve(MaterialId.Water, MaterialId.Sticky,
                                out ElementReactionId reaction)
                            || reaction != ElementReactionId.AbsorbWater)
                            continue;

                        int availableWater = snapshotWater - GetReservedWater(waterCell);
                        if (availableWater <= 0)
                            continue;

                        var snapshot = new MaterialContactSnapshot(
                            MaterialId.Water, availableWater, MaterialId.Sticky, sticky.Amount);
                        if (!ElementReactionEvaluator.TryEvaluateMaterialContact(
                                in snapshot, deltaTime, reaction, in reactionTuning,
                                out MaterialReactionResult result)
                            || result.FirstConsumedGmu <= 0
                            || result.SecondProducedGmu <= 0)
                            break;

                        ReserveWater(waterCell, result.FirstConsumedGmu);
                        uint particles = ((uint)result.FirstConsumedGmu
                            + amountUnitsPerParticle - 1u) / amountUnitsPerParticle;
                        _convertCommands[convertCommandCount++] = new FluidConvertCommand(
                            waterCell,
                            waterCell,
                            (uint)MaterialId.Water,
                            (uint)MaterialId.Sticky,
                            particles,
                            HashSeed(occupancy.SnapshotVersion, waterCell, sticky.GlobalCell));
                        break;
                    }
                }

                // AbsorbWater 不消费既有 Sticky，因此 Fire 仍可在同一规划步点燃原有 Sticky；
                // 刚转换出的新 Sticky 要到下一版 Gameplay Snapshot 才能参与，避免同 Tick 连锁抖动。
                for (int fireIndex = 0; fireIndex < fireCellCount; fireIndex++)
                {
                    LiquidCellFireSample fire = fireCells[fireIndex];
                    int fireAmount = fire.Amount;
                    for (int contact = 0; contact < ContactOffsets.Length; contact++)
                    {
                        if (consumeCommandCount >= _consumeCommands.Length
                            || elementAddCount >= _elementAdds.Length)
                            break;
                        Vector3Int stickyCell = fire.GlobalCell + ContactOffsets[contact];
                        if (!occupancy.TryGetAmount(stickyCell, MaterialId.Sticky, out byte sticky) || sticky == 0)
                            continue;
                        int availableSticky = sticky - GetReservedSticky(stickyCell);
                        if (availableSticky <= 0
                            || !_reactionCatalog.TryResolve(MaterialId.Fire, MaterialId.Sticky,
                                out ElementReactionId reaction)
                            || reaction != ElementReactionId.IgniteGoo)
                            continue;

                        var snapshot = new MaterialContactSnapshot(
                            MaterialId.Fire, fireAmount, MaterialId.Sticky, availableSticky);
                        if (!ElementReactionEvaluator.TryEvaluateMaterialContact(
                                in snapshot, deltaTime, reaction, in reactionTuning,
                                out MaterialReactionResult result)
                            || result.SecondConsumedGmu <= 0 || result.FirstProducedGmu <= 0)
                            continue;

                        ReserveSticky(stickyCell, result.SecondConsumedGmu);
                        uint particles = ((uint)result.SecondConsumedGmu + stickyScale - 1u) / stickyScale;
                        _consumeCommands[consumeCommandCount++] = new FluidConsumeCommand(
                            stickyCell, stickyCell, (uint)MaterialId.Sticky, particles,
                            HashSeed(occupancy.SnapshotVersion, fire.GlobalCell, stickyCell));
                        _elementAdds[elementAddCount++] = new ElementCellAddRequest(
                            stickyCell, MaterialId.Fire, (byte)result.FirstProducedGmu);
                        fireAmount = Mathf.Min(byte.MaxValue, fireAmount + result.FirstProducedGmu);
                    }
                }
            }

            // Poison+Fire 证明第二条规则共用同一 Contact/Occupancy/Consume 主链；Fire 只作触发条件。
            for (int fireIndex = 0; fireIndex < fireCellCount; fireIndex++)
            {
                LiquidCellFireSample fire = fireCells[fireIndex];
                for (int contact = 0; contact < ContactOffsets.Length; contact++)
                {
                    Vector3Int poisonCell = fire.GlobalCell + ContactOffsets[contact];
                    if (!occupancy.TryGetAmount(poisonCell, MaterialId.Poison, out byte poison) || poison == 0)
                        continue;
                    if (!_reactionCatalog.TryResolve(MaterialId.Fire, MaterialId.Poison, out ElementReactionId reaction)
                        || reaction != ElementReactionId.ToxicCombustion)
                        continue;
                    _processTable.TryStartToxic(
                        fire.GlobalCell, poisonCell, fire.Amount, poison, in toxicTuning);
                }
            }

            int damageCommandCount = 0;
            int toxicCount = _processTable.TickToxic(
                deltaTime, occupancy, in toxicTuning, _toxicResolutions);
            for (int i = 0; i < toxicCount && consumeCommandCount < _consumeCommands.Length; i++)
            {
                ToxicWorldReactionResolution resolved = _toxicResolutions[i];
                if (resolved.ConsumedGmu <= 0
                    || !occupancy.TryGetAmountUnitsPerParticle(MaterialId.Poison, out uint poisonScale))
                    continue;
                uint particles = ((uint)resolved.ConsumedGmu + poisonScale - 1u) / poisonScale;
                _consumeCommands[consumeCommandCount++] = new FluidConsumeCommand(
                    resolved.LiquidCell, resolved.LiquidCell, (uint)MaterialId.Poison, particles,
                    HashSeed(occupancy.SnapshotVersion, resolved.LiquidCell, resolved.LiquidCell));
                Vector3 center = worldOrigin + ((Vector3)resolved.LiquidCell + Vector3.one * 0.5f) * cellSize;
                _damageCommands[damageCommandCount++] = new WorldReactionDamageCommand(
                    ElementReactionId.ToxicCombustion,
                    center,
                    resolved.Damage,
                    resolved.Radius);
            }

            if (!queue.TryEnqueueBatch(
                    _fireDeltas,
                    fireDeltaCount,
                    _consumeCommands,
                    consumeCommandCount,
                    _damageCommands,
                    damageCommandCount,
                    _elementAdds,
                    elementAddCount,
                    _convertCommands,
                    convertCommandCount))
                return false;

            if (fireDeltaCount > 0 || convertCommandCount > 0)
            {
                _lastCommittedSnapshotVersion = occupancy.SnapshotVersion;
                _hasCommittedSnapshot = true;
            }
            return true;
        }

        private int GetReservedSticky(Vector3Int cell)
        {
            for (int index = 0; index < _reservedStickyCount; index++)
                if (_reservedStickyCells[index] == cell) return _reservedStickyAmounts[index];
            return 0;
        }

        private void ReserveSticky(Vector3Int cell, int amount)
        {
            for (int index = 0; index < _reservedStickyCount; index++)
            {
                if (_reservedStickyCells[index] != cell) continue;
                _reservedStickyAmounts[index] = (byte)Mathf.Min(
                    byte.MaxValue, _reservedStickyAmounts[index] + amount);
                return;
            }
            if (_reservedStickyCount >= _reservedStickyCells.Length) return;
            _reservedStickyCells[_reservedStickyCount] = cell;
            _reservedStickyAmounts[_reservedStickyCount] = (byte)Mathf.Min(byte.MaxValue, amount);
            _reservedStickyCount++;
        }

        private int GetReservedWater(Vector3Int cell)
        {
            for (int index = 0; index < _reservedWaterCount; index++)
            {
                if (_reservedWaterCells[index] == cell)
                    return _reservedWaterAmounts[index];
            }
            return 0;
        }

        private int FindReservedWater(Vector3Int cell)
        {
            for (int index = 0; index < _reservedWaterCount; index++)
            {
                if (_reservedWaterCells[index] == cell)
                    return index;
            }
            return -1;
        }

        private void ReserveWater(Vector3Int cell, int amount)
        {
            for (int index = 0; index < _reservedWaterCount; index++)
            {
                if (_reservedWaterCells[index] != cell)
                    continue;
                _reservedWaterAmounts[index] = (byte)Mathf.Min(
                    byte.MaxValue,
                    _reservedWaterAmounts[index] + amount);
                return;
            }
            if (_reservedWaterCount >= _reservedWaterCells.Length)
                return;
            _reservedWaterCells[_reservedWaterCount] = cell;
            _reservedWaterAmounts[_reservedWaterCount] = (byte)amount;
            _reservedWaterCount++;
        }

        private static uint HashSeed(uint version, Vector3Int fire, Vector3Int water)
        {
            unchecked
            {
                uint hash = version * 747796405u + 2891336453u;
                hash ^= (uint)fire.x * 73856093u;
                hash ^= (uint)fire.y * 19349663u;
                hash ^= (uint)fire.z * 83492791u;
                hash ^= (uint)water.GetHashCode();
                return hash;
            }
        }
    }
}
