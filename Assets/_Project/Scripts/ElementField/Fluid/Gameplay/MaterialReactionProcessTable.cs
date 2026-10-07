using System;
using Game.Combat;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>固定容量 World Reaction 状态表；旧 Carrier 按接触对去重，Grid 可按爆炸范围合并，不在 Tick 中分配。</summary>
    public sealed class MaterialReactionProcessTable
    {
        private struct Entry
        {
            public bool Used;
            public ElementReactionId Reaction;
            public Vector3Int FireCell;
            public Vector3Int LiquidCell;
            public ToxicCombustionProcess Toxic;
            public float CooldownRemaining;
        }

        private readonly Entry[] _entries;
        private readonly int _coalesceRadiusCells;
        private readonly bool _aggregatePoison;
        private int _usedEnd;
        public MaterialReactionProcessTable(int capacity, int coalesceRadiusCells = 0,
            bool aggregatePoison = false)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _entries = new Entry[capacity];
            _coalesceRadiusCells = Mathf.Max(0, coalesceRadiusCells);
            _aggregatePoison = aggregatePoison;
        }

        public bool TryStartToxic(
            Vector3Int fireCell,
            Vector3Int poisonCell,
            int fireGmu,
            int poisonGmu,
            in ToxicCombustionTuning tuning)
        {
            if (HasToxicContact(fireCell,poisonCell)) return false;
            // 保留原来“最低空槽优先”的顺序，只不扫描从未使用的预留尾部。
            // Grid预留65536槽，但常见只有少量毒爆；容量不能成为每次接触去重的成本。
            for (int i = 0; i < _entries.Length && i <= _usedEnd; i++)
            {
                if (_entries[i].Used) continue;
                var process = new ToxicCombustionProcess();
                if (!process.TryStart(fireGmu, poisonGmu, in tuning)) return false;
                _entries[i] = new Entry { Used = true, Reaction = ElementReactionId.ToxicCombustion,
                    FireCell = fireCell, LiquidCell = poisonCell, Toxic = process };
                if (i == _usedEnd) _usedEnd++;
                return true;
            }
            return false;
        }

        internal bool HasToxicContact(Vector3Int fireCell,Vector3Int poisonCell)
        {
            for (int i = 0; i < _usedEnd; i++)
            {
                if (_entries[i].Used && _entries[i].Reaction == ElementReactionId.ToxicCombustion
                    && ((_coalesceRadiusCells > 0
                        && (_entries[i].LiquidCell-poisonCell).sqrMagnitude
                            <= _coalesceRadiusCells*_coalesceRadiusCells)
                        || _entries[i].FireCell == fireCell && _entries[i].LiquidCell == poisonCell))
                    return true;
            }
            return false;
        }

        public int TickToxic(
            float deltaTime,
            IMaterialAmountReadOnly occupancy,
            in ToxicCombustionTuning tuning,
            ToxicWorldReactionResolution[] destination)
        {
            int count = 0;
            for (int i = 0; i < _usedEnd && count < destination.Length; i++)
            {
                if (!_entries[i].Used) continue;
                Entry entry = _entries[i];
                if (entry.CooldownRemaining > 0f)
                {
                    entry.CooldownRemaining = Mathf.Max(0f, entry.CooldownRemaining - deltaTime);
                    if (entry.CooldownRemaining <= 0f)
                        entry.Used = false;
                    _entries[i] = entry;
                    continue;
                }
                // 浅滩在预热期间仍会流动；Grid 在局部九格重新读数量，不能绑定一格旧位置。
                int current = ReadPoison(occupancy, entry.LiquidCell, _aggregatePoison);
                if (!entry.Toxic.Tick(deltaTime, current, in tuning, out int consumed, out float damage, out float radius))
                {
                    _entries[i] = entry;
                    continue;
                }
                destination[count++] = new ToxicWorldReactionResolution(entry.LiquidCell, consumed, damage, radius);
                // 反应完成后保留同一 Contact Key 到 Cooldown 结束，防止持续接触每 Tick 重复毒爆。
                entry.Toxic = default;
                entry.CooldownRemaining = Mathf.Max(0f, tuning.CooldownSeconds);
                entry.Used = entry.CooldownRemaining > 0f;
                _entries[i] = entry;
            }
            // 仅回收尾部空洞，不压缩/交换中间槽位，避免改变同Tick的输出/消费顺序。
            // destination满时仍按原语义停止；未处理的Used条目也会阻止尾部回收。
            while (_usedEnd > 0 && !_entries[_usedEnd - 1].Used) _usedEnd--;
            return count;
        }

        internal static int ReadPoison(IMaterialAmountReadOnly occupancy, Vector3Int center,
            bool aggregate)
        {
            int total = 0, extent = aggregate ? 1 : 0;
            for (int z=-extent; z<=extent; z++) for (int x=-extent; x<=extent; x++)
                if (occupancy.TryGetAmount(center+new Vector3Int(x,0,z),
                    Game.Materials.MaterialId.Poison,out byte amount)) total += amount;
            return total;
        }
    }

    public readonly struct ToxicWorldReactionResolution
    {
        public readonly Vector3Int LiquidCell;
        public readonly int ConsumedGmu;
        public readonly float Damage;
        public readonly float Radius;
        public ToxicWorldReactionResolution(Vector3Int liquidCell, int consumedGmu, float damage, float radius)
        { LiquidCell = liquidCell; ConsumedGmu = consumedGmu; Damage = damage; Radius = radius; }
    }
}
