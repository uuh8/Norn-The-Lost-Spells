using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public enum FluidArchiveStoreRejectionReason : byte
    {
        None,
        ChunkKindConflict,
        CommitScratchCapacity,
        AmountOverflow,
        ChunkCapacity,
        RecordCapacity,
    }

    public enum FluidArchivedChunkKind : byte
    {
        Cold = 0,
        Dormant = 1,
    }

    /// <summary>
    /// 固定容量 RAM Cold Store。Open Addressing 使用 Empty/Occupied/Tombstone，
    /// 删除 Chunk 后探测链仍连续；归档前先做完整容量检查，失败不会污染旧权威数据。
    /// </summary>
    public sealed class FluidChunkArchiveStore
    {
        private readonly FluidArchiveCellRecord[] _records;
        private readonly uint[] _recordVersions;
        private readonly byte[] _recordStates;
        private readonly ElementChunkKey[] _chunks;
        private readonly uint[] _chunkVersions;
        private readonly FluidArchivedChunkKind[] _chunkKinds;
        private readonly byte[] _chunkStates;
        private readonly ElementChunkKey[] _commitChunkKeys;
        private readonly byte[] _commitChunkStates;
        private readonly int _recordMask;
        private readonly int _chunkMask;
        private readonly int _chunkSize;

        public int RecordCount { get; private set; }
        public int ChunkCount { get; private set; }
        public int DormantChunkCount { get; private set; }
        public int DormantRecordCount { get; private set; }
        public ulong DormantAmount { get; private set; }
        public uint Version { get; private set; }
        public FluidArchiveStoreRejectionReason LastRejectionReason { get; private set; }

        public FluidChunkArchiveStore(int maximumChunks, int maximumCellRecords, int chunkSize = 8)
        {
            if (maximumChunks <= 0) throw new ArgumentOutOfRangeException(nameof(maximumChunks));
            if (maximumCellRecords <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCellRecords));
            if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
            int chunkCapacity = TableCapacity(maximumChunks);
            int recordCapacity = TableCapacity(maximumCellRecords);
            _chunks = new ElementChunkKey[chunkCapacity];
            _chunkVersions = new uint[chunkCapacity];
            _chunkKinds = new FluidArchivedChunkKind[chunkCapacity];
            _chunkStates = new byte[chunkCapacity];
            _commitChunkKeys = new ElementChunkKey[chunkCapacity];
            _commitChunkStates = new byte[chunkCapacity];
            _records = new FluidArchiveCellRecord[recordCapacity];
            _recordVersions = new uint[recordCapacity];
            _recordStates = new byte[recordCapacity];
            _chunkMask = chunkCapacity - 1;
            _recordMask = recordCapacity - 1;
            _chunkSize = chunkSize;
        }

        public bool TryCommit(FluidChunkArchiveBuilder builder, uint version)
        {
            return TryCommitCore(builder, version, false, default);
        }

        public bool TryCommit(
            FluidChunkArchiveBuilder builder,
            uint version,
            in FluidChunkRegion retainedRegion)
        {
            return TryCommitCore(builder, version, true, in retainedRegion);
        }

        private bool TryCommitCore(
            FluidChunkArchiveBuilder builder,
            uint version,
            bool classifyRetained,
            in FluidChunkRegion retainedRegion,
            ElementWorldActivityRegions regions = null,
            Vector3 origin = default,
            float chunkWorldSize = 1f)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            LastRejectionReason = FluidArchiveStoreRejectionReason.None;
            if (builder.RecordCount == 0) return true;

            int newChunks = 0;
            int newRecords = 0;
            Array.Clear(_commitChunkStates, 0, _commitChunkStates.Length);
            for (int slot = 0; slot < builder.SlotCapacity; slot++)
            {
                if (!builder.TryGetRecordAtSlot(slot, out FluidArchiveCellRecord record)) continue;
                FluidArchivedChunkKind targetKind = classifyRetained
                    && ContainsRegion(record.Chunk, retainedRegion, regions, origin, chunkWorldSize)
                    ? FluidArchivedChunkKind.Dormant
                    : FluidArchivedChunkKind.Cold;
                int existingChunk = FindChunk(record.Chunk);
                if (existingChunk >= 0 && _chunkKinds[existingChunk] != targetKind)
                {
                    // 活动区移走后，原 Dormant RAM 权威可以原子降级为 Cold，并与本次
                    // 新归档量合并；反方向必须先走 Restore，不能把未恢复的 Cold 数据
                    // 直接宣布为活动区可查询数据。
                    if (_chunkKinds[existingChunk] != FluidArchivedChunkKind.Dormant
                        || targetKind != FluidArchivedChunkKind.Cold)
                    {
                        LastRejectionReason = FluidArchiveStoreRejectionReason.ChunkKindConflict;
                        return false;
                    }
                    if (!TryMarkCommitChunk(record.Chunk, 2))
                    {
                        LastRejectionReason = FluidArchiveStoreRejectionReason.CommitScratchCapacity;
                        return false;
                    }
                }
                if (existingChunk < 0)
                {
                    // 原预检对每个 Record 回扫所有前序 Record，是 O(N²)。固定 Hash scratch
                    // 只记录本次新增 Chunk 身份，失败仍不修改 Store，保留原子提交语义。
                    int seenSlot = HashChunk(record.Chunk) & _chunkMask;
                    int probes = 0;
                    while (_commitChunkStates[seenSlot] != 0 && _commitChunkKeys[seenSlot] != record.Chunk
                        && probes++ < _commitChunkStates.Length)
                        seenSlot = (seenSlot + 1) & _chunkMask;
                    if (probes >= _commitChunkStates.Length)
                    {
                        LastRejectionReason = FluidArchiveStoreRejectionReason.CommitScratchCapacity;
                        return false;
                    }
                    if (_commitChunkStates[seenSlot] == 0)
                    {
                        _commitChunkStates[seenSlot] = 1;
                        _commitChunkKeys[seenSlot] = record.Chunk;
                        newChunks++;
                    }
                }
                int existingRecord = FindRecord(
                    record.Chunk, record.LocalCellIndex, record.Material);
                if (existingRecord < 0) newRecords++;
                else if (!TryCombine(_records[existingRecord], record, out _))
                {
                    LastRejectionReason = FluidArchiveStoreRejectionReason.AmountOverflow;
                    return false;
                }
            }
            if (ChunkCount + newChunks > MaximumOccupied(_chunks.Length))
            {
                LastRejectionReason = FluidArchiveStoreRejectionReason.ChunkCapacity;
                return false;
            }
            if (RecordCount + newRecords > MaximumOccupied(_records.Length))
            {
                LastRejectionReason = FluidArchiveStoreRejectionReason.RecordCapacity;
                return false;
            }

            ReclassifyMarkedDormantChunksAsCold();

            // 预检后所有 key 都一定能放入固定表；查询和 Commit 都在 ElementWorld 主线程串行执行。
            for (int slot = 0; slot < builder.SlotCapacity; slot++)
            {
                if (!builder.TryGetRecordAtSlot(slot, out FluidArchiveCellRecord record)) continue;
                FluidArchivedChunkKind kind = classifyRetained && ContainsRegion(record.Chunk, retainedRegion, regions, origin, chunkWorldSize)
                    ? FluidArchivedChunkKind.Dormant
                    : FluidArchivedChunkKind.Cold;
                InsertChunk(record.Chunk, version, kind);
                UpsertRecord(record, version);
            }
            IncrementVersion();
            return true;
        }

        private bool TryMarkCommitChunk(ElementChunkKey chunk, byte state)
        {
            int slot = HashChunk(chunk) & _chunkMask;
            for (int probes = 0; probes < _commitChunkStates.Length; probes++)
            {
                if (_commitChunkStates[slot] == 0)
                {
                    _commitChunkStates[slot] = state;
                    _commitChunkKeys[slot] = chunk;
                    return true;
                }
                if (_commitChunkKeys[slot] == chunk)
                {
                    _commitChunkStates[slot] = state;
                    return true;
                }
                slot = (slot + 1) & _chunkMask;
            }
            return false;
        }

        private bool IsCommitChunkMarked(ElementChunkKey chunk, byte state)
        {
            int slot = HashChunk(chunk) & _chunkMask;
            for (int probes = 0; probes < _commitChunkStates.Length; probes++)
            {
                if (_commitChunkStates[slot] == 0) return false;
                if (_commitChunkKeys[slot] == chunk)
                    return _commitChunkStates[slot] == state;
                slot = (slot + 1) & _chunkMask;
            }
            return false;
        }

        private void ReclassifyMarkedDormantChunksAsCold()
        {
            bool changed = false;
            for (int i = 0; i < _commitChunkStates.Length; i++)
            {
                if (_commitChunkStates[i] != 2) continue;
                int chunkSlot = FindChunk(_commitChunkKeys[i]);
                if (chunkSlot < 0 || _chunkKinds[chunkSlot] != FluidArchivedChunkKind.Dormant)
                    continue;
                _chunkKinds[chunkSlot] = FluidArchivedChunkKind.Cold;
                DormantChunkCount--;
                changed = true;
            }
            if (!changed) return;
            // 每次 Commit 只扫一次固定 Record 表，避免“每个 Chunk 再扫全表”的 O(C*R)。
            for (int i = 0; i < _records.Length; i++)
            {
                if (_recordStates[i] != 1
                    || !IsCommitChunkMarked(_records[i].Chunk, 2)) continue;
                DormantRecordCount--;
                DormantAmount -= _records[i].Amount;
            }
        }

        public bool ContainsChunk(ElementChunkKey chunk) => FindChunk(chunk) >= 0;

        internal bool TryCommit(FluidChunkArchiveBuilder builder, uint version,
            ElementWorldActivityRegions frozenRegions, Vector3 origin, float chunkWorldSize)
            => TryCommitCore(builder, version, true, default, frozenRegions, origin, chunkWorldSize);

        internal static Bounds ChunkBounds(ElementChunkKey key, Vector3 origin, float size)
            => new Bounds(origin + new Vector3(key.X + .5f, key.Y + .5f, key.Z + .5f) * size, Vector3.one * size);

        private static bool ContainsRegion(ElementChunkKey key, FluidChunkRegion fallback,
            ElementWorldActivityRegions regions, Vector3 origin, float size)
            => regions != null ? regions.Intersects(ChunkBounds(key, origin, size)) : fallback.Contains(key);

        internal bool TryFindColdChunk(ElementWorldActivityRegions regions, Vector3 origin,
            float size, out ElementChunkKey chunk)
        {
            chunk = default;
            float best = float.MaxValue;
            bool found = false;
            for (int i = 0; i < _chunks.Length; i++)
            {
                if (_chunkStates[i] != 1 || _chunkKinds[i] != FluidArchivedChunkKind.Cold) continue;
                Bounds bounds = ChunkBounds(_chunks[i], origin, size);
                if (!regions.Intersects(bounds)) continue;
                float distance = float.MaxValue;
                for (int r = 0; r < regions.Count; r++)
                    distance = Mathf.Min(distance, (bounds.center - regions.GetBounds(r).center).sqrMagnitude);
                if (!found || distance < best || (distance == best && Compare(_chunks[i], chunk) < 0))
                { chunk = _chunks[i]; best = distance; found = true; }
            }
            return found;
        }

        internal int CopyRegionCells(ElementWorldActivityRegions regions, Bounds visible,
            bool useVisible, Vector3 origin, float size, MaterialId material, LiquidMaterialCellSample[] destination)
        {
            int count = 0;
            for (int slot = 0; slot < _records.Length && count < destination.Length; slot++)
            {
                if (_recordStates[slot] != 1 || _records[slot].Material != material) continue;
                FluidArchiveCellRecord record = _records[slot];
                Bounds bounds = ChunkBounds(record.Chunk, origin, size);
                if (useVisible ? !visible.Intersects(bounds) : !regions.Intersects(bounds)) continue;
                destination[count++] = new LiquidMaterialCellSample(ToGlobalCell(in record),
                    (byte)Math.Min(byte.MaxValue, record.Amount));
            }
            return count;
        }

        public bool ContainsMaterialOtherThanInRange(
            ElementChunkKey chunk,
            Vector3Int minimum,
            Vector3Int maximum,
            MaterialId material)
        {
            for (int slot = 0; slot < _records.Length; slot++)
            {
                if (_recordStates[slot] != 1) continue;
                FluidArchiveCellRecord record = _records[slot];
                if (record.Chunk != chunk || record.Material == material) continue;
                Vector3Int cell = ToGlobalCell(in record);
                if (cell.x >= minimum.x && cell.x <= maximum.x
                    && cell.y >= minimum.y && cell.y <= maximum.y
                    && cell.z >= minimum.z && cell.z <= maximum.z)
                    return true;
            }
            return false;
        }

        public bool TryFindNearestChunk(
            ElementChunkKey interestChunk, in FluidChunkRegion region, out ElementChunkKey chunk)
        {
            return TryFindNearestChunk(
                interestChunk, in region, FluidArchivedChunkKind.Cold, false, out chunk);
        }

        public bool TryFindNearestChunk(
            ElementChunkKey interestChunk,
            in FluidChunkRegion region,
            FluidArchivedChunkKind requiredKind,
            bool includeOtherKinds,
            out ElementChunkKey chunk)
        {
            long bestDistance = long.MaxValue;
            bool found = false;
            chunk = default;
            for (int i = 0; i < _chunks.Length; i++)
            {
                if (_chunkStates[i] != 1 || !region.Contains(_chunks[i])
                    || (!includeOtherKinds && _chunkKinds[i] != requiredKind)) continue;
                ElementChunkKey candidate = _chunks[i];
                long distance = Math.Abs((long)candidate.X - interestChunk.X)
                    + Math.Abs((long)candidate.Y - interestChunk.Y)
                    + Math.Abs((long)candidate.Z - interestChunk.Z);
                if (!found || distance < bestDistance
                    || (distance == bestDistance && Compare(candidate, chunk) < 0))
                {
                    found = true;
                    bestDistance = distance;
                    chunk = candidate;
                }
            }
            return found;
        }

        public bool TryGetChunkVersion(ElementChunkKey chunk, out uint version)
        {
            int slot = FindChunk(chunk);
            version = slot >= 0 ? _chunkVersions[slot] : 0u;
            return slot >= 0;
        }

        public bool TryGetChunkKind(ElementChunkKey chunk, out FluidArchivedChunkKind kind)
        {
            int slot = FindChunk(chunk);
            kind = slot >= 0 ? _chunkKinds[slot] : FluidArchivedChunkKind.Cold;
            return slot >= 0;
        }

        public bool TryGetAmount(Vector3Int globalCell, MaterialId material, out uint amount)
        {
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(
                globalCell, _chunkSize, out ElementChunkKey chunk, out Vector3Int local);
            int localIndex = local.x + _chunkSize * (local.y + _chunkSize * local.z);
            int slot = FindRecord(chunk, localIndex, material);
            amount = slot >= 0 ? _records[slot].Amount : 0u;
            return slot >= 0;
        }

        public int CopyChunkRecords(ElementChunkKey chunk, FluidArchiveCellRecord[] destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            int count = 0;
            for (int slot = 0; slot < _records.Length; slot++)
            {
                if (_recordStates[slot] != 1 || _records[slot].Chunk != chunk) continue;
                if (count >= destination.Length) return -1;
                destination[count++] = _records[slot];
            }
            return count;
        }

        /// <summary>
        /// 把 RAM Store 当前持有的每条记录加入同一审计 Epoch。遍历固定数组，不创建临时集合；
        /// Restore 的镜像期由调用方禁止采样，以免已激活 GPU 粒子与尚未移除的 RAM Chunk 重复计算。
        /// </summary>
        public bool TryAccumulateMaterialAudit(FluidMaterialAudit audit)
        {
            if (audit == null) throw new ArgumentNullException(nameof(audit));
            for (int slot = 0; slot < _records.Length; slot++)
            {
                if (_recordStates[slot] != 1) continue;
                FluidArchiveCellRecord record = _records[slot];
                if (!audit.TryAddRam(record.Material, record.Amount)) return false;
            }
            return true;
        }

        public int CopyCells(
            in FluidChunkRegion region,
            FluidArchivedChunkKind kind,
            MaterialId material,
            LiquidMaterialCellSample[] destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            int count = 0;
            for (int slot = 0; slot < _records.Length && count < destination.Length; slot++)
            {
                if (_recordStates[slot] != 1) continue;
                FluidArchiveCellRecord record = _records[slot];
                if (record.Material != material || !region.Contains(record.Chunk)
                    || !TryGetChunkKind(record.Chunk, out FluidArchivedChunkKind recordKind)
                    || recordKind != kind)
                    continue;
                destination[count++] = new LiquidMaterialCellSample(
                    ToGlobalCell(in record), (byte)Math.Min(byte.MaxValue, record.Amount));
            }
            return count;
        }

        public int CopyCells(
            in FluidChunkRegion region,
            MaterialId material,
            LiquidMaterialCellSample[] destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            int count = 0;
            for (int slot = 0; slot < _records.Length && count < destination.Length; slot++)
            {
                if (_recordStates[slot] != 1) continue;
                FluidArchiveCellRecord record = _records[slot];
                if (record.Material != material || !region.Contains(record.Chunk)) continue;
                destination[count++] = new LiquidMaterialCellSample(
                    ToGlobalCell(in record), (byte)Math.Min(byte.MaxValue, record.Amount));
            }
            return count;
        }

        public void RemoveChunk(ElementChunkKey chunk)
        {
            int chunkSlot = FindChunk(chunk);
            if (chunkSlot < 0) return;
            _chunkStates[chunkSlot] = 2;
            ChunkCount--;
            bool wasDormant = _chunkKinds[chunkSlot] == FluidArchivedChunkKind.Dormant;
            if (wasDormant) DormantChunkCount--;
            for (int slot = 0; slot < _records.Length; slot++)
            {
                if (_recordStates[slot] == 1 && _records[slot].Chunk == chunk)
                {
                    _recordStates[slot] = 2;
                    RecordCount--;
                    if (wasDormant) DormantRecordCount--;
                    if (wasDormant) DormantAmount -= _records[slot].Amount;
                }
            }
            IncrementVersion();
        }

        private void InsertChunk(
            ElementChunkKey chunk,
            uint version,
            FluidArchivedChunkKind kind)
        {
            int existing = FindChunk(chunk);
            if (existing >= 0)
            {
                _chunkVersions[existing] = version;
                return;
            }
            int slot = FindInsertChunkSlot(chunk);
            _chunks[slot] = chunk;
            _chunkVersions[slot] = version;
            _chunkKinds[slot] = kind;
            _chunkStates[slot] = 1;
            ChunkCount++;
            if (kind == FluidArchivedChunkKind.Dormant) DormantChunkCount++;
        }

        private void IncrementVersion()
        {
            unchecked { Version++; }
            if (Version == 0u) Version = 1u;
        }

        private Vector3Int ToGlobalCell(in FluidArchiveCellRecord record)
        {
            int layer = _chunkSize * _chunkSize;
            int localZ = record.LocalCellIndex / layer;
            int remainder = record.LocalCellIndex - localZ * layer;
            int localY = remainder / _chunkSize;
            int localX = remainder - localY * _chunkSize;
            return new Vector3Int(
                checked(record.Chunk.X * _chunkSize + localX),
                checked(record.Chunk.Y * _chunkSize + localY),
                checked(record.Chunk.Z * _chunkSize + localZ));
        }

        private void UpsertRecord(FluidArchiveCellRecord record, uint version)
        {
            int existing = FindRecord(record.Chunk, record.LocalCellIndex, record.Material);
            if (existing >= 0)
            {
                TryCombine(_records[existing], record, out FluidArchiveCellRecord combined);
                _records[existing] = combined;
                _recordVersions[existing] = version;
                if (TryGetChunkKind(record.Chunk, out FluidArchivedChunkKind existingKind)
                    && existingKind == FluidArchivedChunkKind.Dormant)
                    DormantAmount += record.Amount;
                return;
            }
            int slot = FindInsertRecordSlot(record.Chunk, record.LocalCellIndex, record.Material);
            _records[slot] = record; _recordVersions[slot] = version; _recordStates[slot] = 1; RecordCount++;
            if (TryGetChunkKind(record.Chunk, out FluidArchivedChunkKind kind)
                && kind == FluidArchivedChunkKind.Dormant)
            {
                DormantRecordCount++;
                DormantAmount += record.Amount;
            }
        }

        private static bool TryCombine(
            in FluidArchiveCellRecord existing,
            in FluidArchiveCellRecord incoming,
            out FluidArchiveCellRecord combined)
        {
            combined = default;
            uint amount;
            try { amount = checked(existing.Amount + incoming.Amount); }
            catch (OverflowException) { return false; }
            double inverse = 1d / amount;
            var velocity = new Vector3(
                (float)((existing.AverageVelocity.x * existing.Amount
                    + incoming.AverageVelocity.x * incoming.Amount) * inverse),
                (float)((existing.AverageVelocity.y * existing.Amount
                    + incoming.AverageVelocity.y * incoming.Amount) * inverse),
                (float)((existing.AverageVelocity.z * existing.Amount
                    + incoming.AverageVelocity.z * incoming.Amount) * inverse));
            if (!float.IsFinite(velocity.x) || !float.IsFinite(velocity.y)
                || !float.IsFinite(velocity.z)) return false;
            combined = new FluidArchiveCellRecord(
                existing.Chunk, existing.LocalCellIndex, existing.Material, amount, velocity);
            return true;
        }

        private int FindChunk(ElementChunkKey chunk)
        {
            int slot = HashChunk(chunk) & _chunkMask;
            for (int i = 0; i < _chunks.Length; i++)
            {
                if (_chunkStates[slot] == 0) return -1;
                if (_chunkStates[slot] == 1 && _chunks[slot] == chunk) return slot;
                slot = (slot + 1) & _chunkMask;
            }
            return -1;
        }

        private int FindRecord(ElementChunkKey chunk, int local, MaterialId material)
        {
            int slot = HashRecord(chunk, local, material) & _recordMask;
            for (int i = 0; i < _records.Length; i++)
            {
                if (_recordStates[slot] == 0) return -1;
                FluidArchiveCellRecord record = _records[slot];
                if (_recordStates[slot] == 1 && record.Chunk == chunk
                    && record.LocalCellIndex == local && record.Material == material) return slot;
                slot = (slot + 1) & _recordMask;
            }
            return -1;
        }

        private int FindInsertChunkSlot(ElementChunkKey chunk)
        {
            int slot = HashChunk(chunk) & _chunkMask; int tombstone = -1;
            for (int i = 0; i < _chunks.Length; i++)
            {
                if (_chunkStates[slot] == 0) return tombstone >= 0 ? tombstone : slot;
                if (_chunkStates[slot] == 2 && tombstone < 0) tombstone = slot;
                slot = (slot + 1) & _chunkMask;
            }
            return tombstone;
        }

        private int FindInsertRecordSlot(ElementChunkKey chunk, int local, MaterialId material)
        {
            int slot = HashRecord(chunk, local, material) & _recordMask; int tombstone = -1;
            for (int i = 0; i < _records.Length; i++)
            {
                if (_recordStates[slot] == 0) return tombstone >= 0 ? tombstone : slot;
                if (_recordStates[slot] == 2 && tombstone < 0) tombstone = slot;
                slot = (slot + 1) & _recordMask;
            }
            return tombstone;
        }

        private static int MaximumOccupied(int capacity) => capacity / 2;
        private static int TableCapacity(int maximum)
        {
            int result = 1; int required = checked(maximum * 2);
            while (result < required) result = checked(result << 1);
            return result;
        }
        private static int HashChunk(ElementChunkKey chunk) => chunk.GetHashCode() & int.MaxValue;
        private static int Compare(ElementChunkKey left, ElementChunkKey right)
        {
            int z = left.Z.CompareTo(right.Z);
            if (z != 0) return z;
            int y = left.Y.CompareTo(right.Y);
            return y != 0 ? y : left.X.CompareTo(right.X);
        }
        private static int HashRecord(ElementChunkKey chunk, int local, MaterialId material)
        {
            unchecked { int hash = chunk.GetHashCode(); hash = hash * 397 ^ local; hash = hash * 397 ^ (byte)material; return hash & int.MaxValue; }
        }
    }
}
