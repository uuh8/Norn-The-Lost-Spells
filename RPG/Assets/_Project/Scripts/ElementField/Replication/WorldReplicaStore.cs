using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 客户端只读数量缓存；不拥有Solver、反应、库存Port或时钟。
    /// 每Chunk预分配两个独立数据块，安装只检查/复制该Chunk，最后交换引用，不扫描或复制全世界。
    /// 同Epoch内保留tombstone元数据。总Cell/Pending容量和每块word容量同时生效，超限保留旧值。
    /// 所有调用在Unity主线程串行完成；网络线程不能直接读写本对象。
    /// </summary>
    public sealed class WorldReplicaStore : IWorldChunkPayloadInstaller, IWorldChunkPayloadInterestSink
    {
        private sealed class Chunk
        {
            internal WorldMaterialCellSample[] Current, Candidate;
            internal WorldPendingDepositSample[] Pending, CandidatePending;
            internal int Count, PendingCount;
            internal readonly WorldReplicaRevisionState Version;
            internal WorldChunkPayloadHeader Header;
            internal uint PresentationVersion;
            internal bool Interested;
            internal Chunk(ulong epoch, int cells, int pending)
            {
                Current = new WorldMaterialCellSample[cells]; Candidate = new WorldMaterialCellSample[cells];
                Pending = new WorldPendingDepositSample[pending]; CandidatePending = new WorldPendingDepositSample[pending];
                Version = new WorldReplicaRevisionState(epoch);
            }
        }
        private readonly Chunk[] _chunks;
        private readonly WorldChunkKey[] _keys;
        private readonly Dictionary<WorldChunkKey, int> _indices;
        private readonly WorldReplicaBuffer _decode;
        private readonly WorldGridGeometry[] _geometry = new WorldGridGeometry[4];
        private readonly bool[] _hasGeometry = new bool[4];
        private readonly uint[] _versions = { 1, 1, 1, 1 };
        private readonly ulong[] _totals = new ulong[4], _pendingTotals = new ulong[4];
        private readonly int[] _domainCells = new int[4];
        private readonly uint[] _capacity = { uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue };
        private readonly bool[,] _supported = new bool[4, 256];
        private readonly bool[] _hasMaterialConfiguration = new bool[4], _singleMaterial = new bool[4];
        private readonly int[] _domainChunkLimits = { int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue };
        private readonly int[] _domainChunks = new int[4];
        private int _slots, _cells, _pending;
        private readonly int _cellCapacity, _pendingCapacity;
        public ulong WorldEpoch { get; }
        public uint PresentationVersion { get; private set; } = 1;
        public int MaximumChunks => _chunks.Length;
        public int MaximumCells => _cellCapacity;
        public int CellCount => _cells;
        public long WorldTick { get; private set; }
        public WorldReplicaStore(ulong epoch, int chunkCapacity, int cellCapacity, int pendingCapacity, int maxWords)
        {
            if (epoch == 0 || chunkCapacity < 1 || cellCapacity < 1 || pendingCapacity < 0 || maxWords < 11)
                throw new ArgumentOutOfRangeException(nameof(chunkCapacity));
            WorldEpoch = epoch; _cellCapacity = cellCapacity; _pendingCapacity = pendingCapacity;
            _chunks = new Chunk[chunkCapacity]; _keys = new WorldChunkKey[chunkCapacity];
            _indices = new Dictionary<WorldChunkKey, int>(chunkCapacity);
            int blockCells = Math.Min(cellCapacity, (maxWords - 11) / WorldChunkPayloadCodec.CellWords);
            int blockPending = Math.Min(pendingCapacity, (maxWords - 11) / WorldChunkPayloadCodec.PendingWords);
            for (int i = 0; i < chunkCapacity; i++) _chunks[i] = new Chunk(epoch, blockCells, blockPending);
            _decode = new WorldReplicaBuffer(blockCells, blockPending, maxWords);
        }
        /// <summary>激活Renderer前由可信场景固定Geometry；网络候选只能与它一致，不能重新解释旧Cell。</summary>
        public bool ConfigureGeometry(in WorldGridGeometry geometry)
        {
            if (!WorldChunkPayloadCodec.ValidGeometry(geometry)) return false;
            int d = (int)geometry.Domain;
            if (_hasGeometry[d]) return Same(_geometry[d], geometry);
            _geometry[d] = geometry; _hasGeometry[d] = true; return true;
        }
        public bool TryGetGeometry(WorldMaterialDomain domain, out WorldGridGeometry geometry)
        {
            geometry = default; int d = (int)domain;
            if (d < 0 || d > 3 || !_hasGeometry[d]) return false;
            geometry = _geometry[d]; return true;
        }
        /// <summary>只读视图也必须拒绝超出可信Profile的数量/身份，不能等到ushort上传或Mesh创建才截断/抛错。</summary>
        public bool ConfigureMaterials(WorldMaterialDomain domain, uint capacity, byte[] materials, bool singleMaterial = false, int maximumChunks = int.MaxValue)
        {
            int d = (int)domain;
            if (d < 0 || d > 3 || capacity == 0 || maximumChunks < 1 || materials == null || materials.Length == 0 || _hasMaterialConfiguration[d] || _domainCells[d] != 0) return false;
            for (int i = 0; i < materials.Length; i++) if (materials[i] == 0) return false;
            for (int i = 0; i < materials.Length; i++) _supported[d, materials[i]] = true;
            _capacity[d] = capacity; _singleMaterial[d] = singleMaterial; _hasMaterialConfiguration[d] = true; _domainChunkLimits[d] = maximumChunks; return true;
        }
        bool IWorldChunkPayloadInstaller.TryInstall(WorldChunkPayloadHeader header, uint[] words, int count)
            => TryInstall(header, words, count);
        public bool TryInstall(in WorldChunkPayloadHeader header, uint[] words, int count)
        {
            if (header.WorldEpoch != WorldEpoch || header.SnapshotId == 0 || header.Generation == 0 ||
                header.Revision == 0 || header.ServerTick < 0 || header.WordCount != count ||
                (byte)header.Key.Domain > 3 || PresentationVersion == uint.MaxValue) return false;
            int slot = Find(header.Key), d = (int)header.Key.Domain;
            if (slot < 0 && _slots == _chunks.Length) return false;
            int target = slot < 0 ? _slots : slot;
            Chunk chunk = _chunks[target];
            if (slot >= 0 && !chunk.Header.IsResident && header.IsResident
                && header.Generation <= chunk.Header.Generation) return false;
            if (slot >= 0 && chunk.Header.IsResident && !chunk.Interested && header.BaseRevision != 0) return false;
            int incomingCells = 0, incomingPending = 0;
            WorldGridGeometry geometry = default;
            if (header.IsResident)
            {
                if (words == null || count < 11 || count > words.Length || count > _decode.InspectionBudget ||
                    WorldChunkPayloadCodec.ComputeChecksum(words, count) != header.Checksum ||
                    !WorldChunkPayloadCodec.TryDecode(words, count, header.Key, header.ServerTick, _decode)) return false;
                geometry = _decode.Geometries[0];
                if (_hasGeometry[d] && !Same(_geometry[d], geometry)) return false;
                incomingCells = _decode.Header.CellCount; incomingPending = _decode.Header.PendingCount;
                ulong occupied = 0; Vector3Int previousCell = default;
                for (int i = 0; i < incomingCells; i++)
                {
                    var cell = _decode.Cells[i]; bool sameCell = i > 0 && cell.Cell == previousCell;
                    if (!sameCell) occupied = 0;
                    if (sameCell && (header.Key.Domain == WorldMaterialDomain.LegacyCell || _singleMaterial[d])) return false;
                    occupied += cell.Quantity;
                    if (occupied > _capacity[d] || _hasMaterialConfiguration[d] && !_supported[d, cell.MaterialId]) return false;
                    previousCell = cell.Cell;
                }
                for (int i = 0; i < incomingPending; i++)
                    if (_hasMaterialConfiguration[d] && !_supported[d, _decode.Pending[i].MaterialId]) return false;
            }
            else if (count != 0 || header.Checksum != 2166136261u) return false;
            if (slot >= 0 && chunk.Header.Generation == header.Generation && chunk.Header.Revision == header.Revision)
            {
                // Interest释放只清占据，不删除版本事实。同版本重入必须先与保留Header核对内容，
                // 不能因!Interested绕过一致性检查，把“有合法CRC的另一份数据”当成原版本。
                // 内容一致的resident重入仍继续原子重装；已可见或权威tombstone保持原幂等返回。
                if (chunk.Header.Checksum != header.Checksum || chunk.Header.WordCount != count || chunk.Header.IsResident != header.IsResident) return false;
                if (chunk.Interested || !chunk.Header.IsResident) return true;
            }
            if (incomingCells > _cellCapacity - (_cells - chunk.Count) ||
                incomingPending > _pendingCapacity - (_pending - chunk.PendingCount)) return false;
            int residentDelta = (header.IsResident ? 1 : 0) - (slot >= 0 && chunk.Header.IsResident && chunk.Interested ? 1 : 0);
            if (_domainChunks[d] + residentDelta > _domainChunkLimits[d]) return false;

            ulong oldTotal = 0, newTotal = 0, oldPending = 0, newPending = 0;
            for (int i = 0; i < chunk.Count; i++) oldTotal += chunk.Current[i].Quantity;
            for (int i = 0; i < chunk.PendingCount; i++) oldPending += chunk.Pending[i].Remaining;
            for (int i = 0; i < incomingCells; i++) { chunk.Candidate[i] = _decode.Cells[i]; newTotal += _decode.Cells[i].Quantity; }
            for (int i = 0; i < incomingPending; i++) { chunk.CandidatePending[i] = _decode.Pending[i]; newPending += _decode.Pending[i].Remaining; }
            bool accepted = header.BaseRevision == 0
                ? chunk.Version.TryAcceptBaselineVersion(header.WorldEpoch, header.Generation, header.Revision)
                : chunk.Version.TryAcceptDeltaVersion(header.WorldEpoch, header.Generation, header.BaseRevision, header.Revision);
            if (!accepted) return false;
            // 以下没有外部回调或可能失败的操作；一份Chunk值与版本在同一主线程提交边界发布。
            if (slot < 0) { _keys[target] = header.Key; _indices.Add(header.Key, target); _slots++; }
            if (header.IsResident) { _geometry[d] = geometry; _hasGeometry[d] = true; }
            var cells = chunk.Current; chunk.Current = chunk.Candidate; chunk.Candidate = cells;
            var pending = chunk.Pending; chunk.Pending = chunk.CandidatePending; chunk.CandidatePending = pending;
            _cells += incomingCells - chunk.Count; _domainCells[d] += incomingCells - chunk.Count;
            _pending += incomingPending - chunk.PendingCount;
            chunk.Count = incomingCells; chunk.PendingCount = incomingPending; chunk.Header = header;
            chunk.Interested = header.IsResident;
            chunk.PresentationVersion++; _domainChunks[d] += residentDelta;
            _totals[d] = _totals[d] - oldTotal + newTotal; _pendingTotals[d] = _pendingTotals[d] - oldPending + newPending;
            WorldTick = Math.Max(WorldTick, header.ServerTick); PresentationVersion++; _versions[d]++;
            // 显式诊断只观察已成功发布的Current，不能把收页/CRC通过或幂等重送伪记为安装。
            // 默认没有Active实例；启用时也只复制值到预分配前缀，不在此做JSON、文件IO或渲染操作。
            var timing = WorldReplicationTimingTrace.Active;
            if (timing != null) timing.Record(new WorldReplicationTimingEvent
            {
                Stage = WorldReplicationTimingStage.Installed, Header = header,
                PresentationVersion = chunk.PresentationVersion, Frame = Time.frameCount,
                Count0 = incomingCells, Count1 = incomingPending,
                InstalledPresent = header.IsResident, InstalledGeneration = header.Generation,
                InstalledRevision = header.Revision
            });
            return true;
        }
        /// <summary>
        /// 只读取得实际已发布且仍在Interest中的网络Header，供显式诊断关联安装与表现。
        /// Header与Current在TryInstall的同一主线程边界交换；CRC失败的候选绝不经过这里发布。
        /// 返回的presentationVersion仅标识本地渲染脏版本，不能代替Header中的Generation/Revision。
        /// Interest已退出的元数据仍可能保留版本栅栏，但不是当前可读的材料快照，故返回false。
        /// </summary>
        public bool TryGetInstalledChunkHeader(WorldChunkKey key, out WorldChunkPayloadHeader header,
            out uint presentationVersion)
        {
            header = default; presentationVersion = 0;
            if (!_indices.TryGetValue(key, out int index)) return false;
            var chunk = _chunks[index];
            if (!chunk.Interested || !chunk.Header.IsResident) return false;
            header = chunk.Header; presentationVersion = chunk.PresentationVersion;
            return true;
        }

        public bool TryGetQuantity(WorldMaterialDomain domain, Vector3Int cell, byte material, out uint quantity)
        {
            quantity = 0;
            if (!TryGetGeometry(domain, out var geometry)) return false;
            int slot = Find(WorldChunkPayloadCodec.CellKey(domain, cell, geometry.ChunkSize));
            if (slot < 0) return false;
            Chunk chunk = _chunks[slot]; int low = 0, high = chunk.Count - 1;
            // Codec已保证Cell按xyz/material严格排序；不分配Dictionary，也不扫描整个Chunk。
            while (low <= high)
            {
                int middle = low + (high - low) / 2; var candidate = chunk.Current[middle];
                int c = candidate.Cell.x.CompareTo(cell.x);
                if (c == 0) c = candidate.Cell.y.CompareTo(cell.y);
                if (c == 0) c = candidate.Cell.z.CompareTo(cell.z);
                if (c == 0) c = candidate.MaterialId.CompareTo(material);
                if (c == 0) { quantity = candidate.Quantity; return true; }
                if (c < 0) low = middle + 1; else high = middle - 1;
            }
            return false;
        }
        public uint DomainVersion(WorldMaterialDomain domain) => _versions[(int)domain];
        public bool HasMaterialInChunk(WorldMaterialDomain domain, ElementChunkKey key, byte material)
        {
            int slot = Find(new WorldChunkKey(domain, key.X, key.Y, key.Z));
            if (slot < 0) return false;
            var chunk = _chunks[slot];
            for (int i = 0; i < chunk.Count; i++) if (chunk.Current[i].MaterialId == material) return true;
            return false;
        }
        /// <summary>
        /// 离开Interest只释放缓存占据，不伪造权威删除。保留真实Header/Generation栅栏；
        /// 重入时必须接收当前Baseline，即使Revision相同也重新安装完整数量。
        /// 固定块归池而不销毁数组，缓存容量立即归还，不因探索反复分配。
        /// </summary>
        public bool TryEvictInterest(WorldChunkKey key)
        {
            int slot = Find(key);
            if (slot < 0 || PresentationVersion == uint.MaxValue) return false;
            var chunk = _chunks[slot];
            if (!chunk.Interested) return true;
            int d = (int)key.Domain;
            for (int i = 0; i < chunk.Count; i++) _totals[d] -= chunk.Current[i].Quantity;
            for (int i = 0; i < chunk.PendingCount; i++) _pendingTotals[d] -= chunk.Pending[i].Remaining;
            _cells -= chunk.Count; _domainCells[d] -= chunk.Count; _pending -= chunk.PendingCount; _domainChunks[d]--;
            chunk.Count = chunk.PendingCount = 0; chunk.Interested = false; chunk.PresentationVersion++;
            PresentationVersion++; _versions[d]++;
            return true;
        }
        public bool TryGetFirstCell(WorldMaterialDomain domain, Vector3Int cell, out WorldMaterialCellSample sample)
        {
            sample = default;
            if (!TryGetGeometry(domain, out var geometry)) return false;
            int slot = Find(WorldChunkPayloadCodec.CellKey(domain, cell, geometry.ChunkSize));
            if (slot < 0) return false;
            var chunk = _chunks[slot]; int low = 0, high = chunk.Count - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2; var candidate = chunk.Current[middle];
                int c = candidate.Cell.x.CompareTo(cell.x);
                if (c == 0) c = candidate.Cell.y.CompareTo(cell.y);
                if (c == 0) c = candidate.Cell.z.CompareTo(cell.z);
                if (c == 0) { sample = candidate; return true; }
                if (c < 0) low = middle + 1; else high = middle - 1;
            }
            return false;
        }
        public ulong TotalQuantity(WorldMaterialDomain domain) => _totals[(int)domain];
        public ulong PendingQuantity(WorldMaterialDomain domain) => _pendingTotals[(int)domain];
        public int ResidentCellCount(WorldMaterialDomain domain) => _domainCells[(int)domain];
        public WorldMaterialCellSample GetCellAt(int index)
        {
            if (index < 0 || index >= _cells) throw new ArgumentOutOfRangeException(nameof(index));
            for (int i = 0; i < _slots; i++) { if (index < _chunks[i].Count) return _chunks[i].Current[index]; index -= _chunks[i].Count; }
            throw new InvalidOperationException();
        }
        public bool TryGetCell(WorldMaterialDomain domain, int index, out WorldMaterialCellSample cell)
        {
            cell = default;
            if (index < 0) return false;
            for (int i = 0; i < _slots; i++)
                if (_keys[i].Domain == domain) { if (index < _chunks[i].Count) { cell = _chunks[i].Current[index]; return true; } index -= _chunks[i].Count; }
            return false;
        }
        public int CopyChunkKeys(WorldMaterialDomain domain, ElementChunkKey[] destination)
        {
            int count = 0;
            for (int i = 0; i < _slots && count < destination.Length; i++)
                if (_keys[i].Domain == domain && _chunks[i].Header.IsResident && _chunks[i].Interested)
                    destination[count++] = new ElementChunkKey(_keys[i].X, _keys[i].Y, _keys[i].Z);
            return count;
        }
        public uint GetChunkVersion(WorldMaterialDomain domain, ElementChunkKey key)
        { int slot = Find(new WorldChunkKey(domain, key.X, key.Y, key.Z)); return slot >= 0 && _chunks[slot].Header.IsResident && _chunks[slot].Interested ? _chunks[slot].PresentationVersion : 0; }
        private int Find(WorldChunkKey key)
            => _indices.TryGetValue(key, out int slot) ? slot : -1;
        private static bool Same(in WorldGridGeometry a, in WorldGridGeometry b) => a.Domain == b.Domain && a.GridId == b.GridId &&
            a.Origin.Equals(b.Origin) && a.CellSize.Equals(b.CellSize) && a.ChunkSize == b.ChunkSize;
    }
}
