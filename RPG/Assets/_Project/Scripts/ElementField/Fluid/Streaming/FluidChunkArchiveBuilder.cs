using System;
using Game.Materials;
using Unity.Collections;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 将一次粒子级 Readback 聚合成 (Chunk, LocalCell, Material) 记录。
    /// 所有数组在构造期创建；Transfer 完成热路径只清标记和覆写值，不创建 Dictionary/List。
    /// </summary>
    public sealed class FluidChunkArchiveBuilder
    {
        private readonly FluidArchiveCellRecord[] _records;
        private readonly uint[] _particleCounts;
        private readonly Vector3[] _velocitySums;
        private readonly byte[] _states;
        private readonly int _mask;

        public int RecordCount { get; private set; }
        public int SlotCapacity => _records.Length;

        public FluidChunkArchiveBuilder(int particleCapacity)
        {
            if (particleCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(particleCapacity));
            int capacity = 1;
            int required = checked(particleCapacity * 2);
            while (capacity < required) capacity = checked(capacity << 1);
            _records = new FluidArchiveCellRecord[capacity];
            _particleCounts = new uint[capacity];
            _velocitySums = new Vector3[capacity];
            _states = new byte[capacity];
            _mask = capacity - 1;
        }

        public bool TryRebuild(NativeArray<FluidGpuArchiveSample> samples, in FluidArchiveBuildContext context)
        {
            Reset();
            if (!ValidateContext(in context)) return false;

            for (int i = 0; i < samples.Length; i++)
            {
                FluidGpuArchiveSample sample = samples[i];
                if ((sample.Flags & (FluidGpuLayout.AliveFlag | FluidActivityFlags.ArchiveLocked))
                    != (FluidGpuLayout.AliveFlag | FluidActivityFlags.ArchiveLocked))
                    continue;
                if (!IsFinite(sample.Position) || !IsFinite(sample.Velocity)) return Fail();
                if (sample.MaterialId == 0u || sample.MaterialId > byte.MaxValue) return Fail();

                MaterialId material = (MaterialId)(byte)sample.MaterialId;
                if (!context.AmountScales.TryGet(material, out uint scale)) return Fail();
                Vector3Int globalCell;
                try
                {
                    globalCell = ElementWorldCoordinates.WorldToGlobalCell(
                        sample.Position, context.WorldOrigin, context.CellSize);
                    ElementWorldCoordinates.GlobalCellToChunkAndLocal(
                        globalCell, context.ChunkSize, out ElementChunkKey chunk, out Vector3Int local);
                    int localIndex = checked(local.x + context.ChunkSize *
                        (local.y + context.ChunkSize * local.z));
                    if (!TryAccumulate(chunk, localIndex, material, scale, sample.Velocity)) return Fail();
                }
                catch (Exception exception) when (exception is ArgumentException || exception is OverflowException)
                {
                    return Fail();
                }
            }

            // 只在完整扫描成功后发布 Amount 与平均速度；失败时不会暴露半份归档。
            for (int slot = 0; slot < _states.Length; slot++)
            {
                if (_states[slot] != 1) continue;
                FluidArchiveCellRecord key = _records[slot];
                uint count = _particleCounts[slot];
                if (!context.AmountScales.TryGet(key.Material, out uint scale)) return Fail();
                uint amount;
                try { amount = checked(count * scale); }
                catch (OverflowException) { return Fail(); }
                _records[slot] = new FluidArchiveCellRecord(
                    key.Chunk, key.LocalCellIndex, key.Material, amount, _velocitySums[slot] / count);
            }
            return true;
        }

        public bool TryGet(
            ElementChunkKey chunk,
            int localCellIndex,
            MaterialId material,
            out FluidArchiveCellRecord record)
        {
            int slot = FindSlot(chunk, localCellIndex, material);
            if (slot >= 0) { record = _records[slot]; return true; }
            record = default;
            return false;
        }

        internal bool TryGetRecordAtSlot(int slot, out FluidArchiveCellRecord record)
        {
            if ((uint)slot < (uint)_states.Length && _states[slot] == 1)
            {
                record = _records[slot];
                return true;
            }
            record = default;
            return false;
        }

        private bool TryAccumulate(
            ElementChunkKey chunk,
            int localCellIndex,
            MaterialId material,
            uint amountScale,
            Vector3 velocity)
        {
            int slot = Hash(chunk, localCellIndex, material) & _mask;
            for (int probe = 0; probe < _states.Length; probe++)
            {
                if (_states[slot] == 0)
                {
                    _states[slot] = 1;
                    _records[slot] = new FluidArchiveCellRecord(chunk, localCellIndex, material, amountScale, velocity);
                    _particleCounts[slot] = 1u;
                    _velocitySums[slot] = velocity;
                    RecordCount++;
                    return true;
                }
                FluidArchiveCellRecord existing = _records[slot];
                if (existing.Chunk == chunk && existing.LocalCellIndex == localCellIndex && existing.Material == material)
                {
                    if (_particleCounts[slot] == uint.MaxValue) return false;
                    _particleCounts[slot]++;
                    _velocitySums[slot] += velocity;
                    if (!IsFinite(_velocitySums[slot])) return false;
                    return true;
                }
                slot = (slot + 1) & _mask;
            }
            return false;
        }

        private int FindSlot(ElementChunkKey chunk, int localCellIndex, MaterialId material)
        {
            int slot = Hash(chunk, localCellIndex, material) & _mask;
            for (int probe = 0; probe < _states.Length; probe++)
            {
                if (_states[slot] == 0) return -1;
                FluidArchiveCellRecord candidate = _records[slot];
                if (candidate.Chunk == chunk && candidate.LocalCellIndex == localCellIndex && candidate.Material == material)
                    return slot;
                slot = (slot + 1) & _mask;
            }
            return -1;
        }

        private void Reset()
        {
            Array.Clear(_states, 0, _states.Length);
            RecordCount = 0;
        }

        private bool Fail() { Reset(); return false; }

        private static bool ValidateContext(in FluidArchiveBuildContext context) =>
            context.AmountScales != null && context.ChunkSize > 0 && context.CellSize > 0f
            && !float.IsNaN(context.CellSize) && !float.IsInfinity(context.CellSize);

        private static int Hash(ElementChunkKey chunk, int localCellIndex, MaterialId material)
        {
            unchecked
            {
                int hash = chunk.GetHashCode();
                hash = hash * 397 ^ localCellIndex;
                hash = hash * 397 ^ (byte)material;
                hash ^= hash >> 16;
                return hash;
            }
        }

        private static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }
}
