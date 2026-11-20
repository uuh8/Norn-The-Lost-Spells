using System;
using System.Collections.Generic;
using Game.Core;
using Game.NetworkProtocol;

namespace Game.Multiplayer
{
    public sealed partial class WorldReplicationCoordinator
    {
        // HTTP读回期间Gameplay可继续变化，因此连续性只对比“确实保存且完整读回”的上一份Head，
        // 不能对比此刻live Owner，也不能把仅捕获/POST过的候选当成已确认事实。
        // 两个对象都只由本类持有；对外DTO另做深拷贝，调用方排序/后续修改不污染确认锚点。
        private OnlineRunAssetCheckpointWorld _lastOfferedCheckpointWorld, _currentConfirmedWorld;
        // 只追踪最近已确认与已提供的两个 Head。字典在实际捕获时增长，Replica 不为权威上限预付内存。
        private Dictionary<WorldChunkKey,WorldChunkPayloadHeader> _checkpointWatch = new Dictionary<WorldChunkKey,WorldChunkPayloadHeader>();
        private Dictionary<WorldChunkKey,WorldChunkPayloadHeader> _nextCheckpointWatch = new Dictionary<WorldChunkKey,WorldChunkPayloadHeader>();
        private readonly Dictionary<WorldChunkKey,int> _confirmationRows = new Dictionary<WorldChunkKey,int>();
        internal int CheckpointRetirementWatchCount => _checkpointWatch.Count;

        /// <summary>
        /// 同一Authority安全边界的离散深拷贝。逐当前原生Key读完整已发布payload；
        /// 已退休且在上一确认Head仍为resident的Key，仅补一次有退休证明的连续性tombstone。
        /// 不按任何玩家Interest筛选，不推进模拟/提交序列/ACK。调用方必须同步捕获其它资产分区后再跨yield。
        /// </summary>
        public bool TryCaptureCheckpoint(string worldId, out OnlineRunAssetCheckpointWorld snapshot)
        {
            snapshot = null;
            if (_stopped || Faulted || _owner == null || string.IsNullOrWhiteSpace(worldId) || worldId.Length > 128
                || _owner.LastCapturedWorldTick < 0 || _owner.ChunkCount > OnlineRunAssetCheckpoint.MaximumWorldKeys
                || _lastOfferedCheckpointWorld != null && _lastOfferedCheckpointWorld.WorldId != worldId)
                return false;
            long tick = _owner.LastCapturedWorldTick;
            ulong sequence = _owner.CommitSequence;
            int count = _owner.ChunkCount;
            if (_currentConfirmedWorld != null && (sequence < _currentConfirmedWorld.CommitSequence
                || tick < _currentConfirmedWorld.WorldTick)) return false;
            int proofs = 0;
            if (_currentConfirmedWorld != null)
            {
                foreach (var old in _currentConfirmedWorld.Chunks)
                {
                    if (!old.IsResident || _owner.TryGetCurrentHeader(CheckpointKey(old), out _)) continue;
                    // 缺席本身不是删除证据。只有Owner确实已完成退休、且本次提交晚于已确认快照，
                    // 才能把旧resident闭合为墓碑；证据不足必须拒绝整份候选，不能静默漏掉旧材料。
                    if (!TryGetCheckpointRetirement(old, sequence, tick, out _) || ++proofs > OnlineRunAssetCheckpoint.MaximumCurrentWorldKeys)
                        return false;
                }
            }
            if (count + proofs > OnlineRunAssetCheckpoint.MaximumWorldKeys) return false;
            int resident=0;
            long minimumBytes=0;
            for(int i=0;i<count;i++)
            {
                var header=_owner.GetChunkHeader(i);
                if(header.IsResident && ++resident>OnlineRunAssetCheckpoint.MaximumCurrentWorldKeys)return false;
                // 每个十进制 uint 至少一个数字及相邻逗号或边界。先检查全体表头，
                // 明显超 64 MiB 的世界在分配任何深拷贝数组之前整笔拒绝。
                minimumBytes+=(long)header.WordCount*2;
                if(minimumBytes>OnlineRunAssetCheckpoint.MaximumPayloadBytes)return false;
            }
            var chunks = new OnlineRunAssetCheckpointChunk[count + proofs];
            long wordTextBytes=0;
            for (int i = 0; i < count; i++)
            {
                var current = _owner.GetChunkHeader(i);
                if (current.WordCount < 0 || current.IsResident && current.WordCount < WorldChunkPayloadCodec.PrefixWords
                    || !current.IsResident && current.WordCount != 0 || current.WordCount > _maxWords
                    || current.WordCount > OnlineRunAssetCheckpoint.MaximumWordsPerChunk) return false;
                // 数组归本候选所有；后续材料变化或发送分页都不能改写已经准备提交的正文。
                var words = current.WordCount==0?Array.Empty<uint>():new uint[current.WordCount];
                if (!_owner.TryCopyChunk(current.Key, words, out var header) || header.WorldEpoch != WorldEpoch
                    || header.SnapshotId != sequence || header.ServerTick != tick || header.BaseRevision != 0
                    || header.WordCount != words.Length
                    || header.IsResident && !WorldChunkPayloadCodec.Validate(words, words.Length, header.Key)
                    || WorldChunkPayloadCodec.ComputeChecksum(words, words.Length) != header.Checksum) return false;
                // 在还未深拷贝完整快照前核对实际 Words 数字长度。完整 DTO 包括其它资产和头部，
                // 最终仍由 InstanceCheckpointJson 与业务端逐字节验证整个 UTF-8 正文。
                if (!OnlineRunAssetCheckpoint.TryBudgetWorldWords(words, ref wordTextBytes)) return false;
                chunks[i] = new OnlineRunAssetCheckpointChunk
                {
                    Domain = (uint)header.Key.Domain, X = header.Key.X, Y = header.Key.Y, Z = header.Key.Z,
                    WorldEpoch = header.WorldEpoch, SnapshotId = header.SnapshotId, Generation = header.Generation,
                    BaseRevision = header.BaseRevision, Revision = header.Revision, ServerTick = header.ServerTick,
                    WordCount = header.WordCount, Checksum = header.Checksum, IsResident = header.IsResident, Words = words
                };
            }
            int next = count;
            if (_currentConfirmedWorld != null)
            {
                foreach (var old in _currentConfirmedWorld.Chunks)
                {
                    // 当前Owner同址已重建时，以真实新Generation/数量为准，不能再附一个同Key的旧墓碑。
                    // 已确认墓碑允许在下次Head缺席，避免证明本身变成另一份永久ever-seen表。
                    if (!old.IsResident || _owner.TryGetCurrentHeader(CheckpointKey(old), out _)) continue;
                    if (next == chunks.Length || !TryGetCheckpointRetirement(old, sequence, tick, out var terminal)) return false;
                    chunks[next++] = new OnlineRunAssetCheckpointChunk
                    {
                        Domain = old.Domain, X = old.X, Y = old.Y, Z = old.Z,
                        WorldEpoch = WorldEpoch, SnapshotId = sequence, Generation = terminal.Generation,
                        BaseRevision = 0, Revision = terminal.Revision, ServerTick = tick,
                        WordCount = 0, Checksum = 2166136261u, IsResident = false, Words = Array.Empty<uint>()
                    };
                }
            }
            if (_owner.LastCapturedWorldTick != tick || _owner.CommitSequence != sequence || _owner.ChunkCount != count)
                return false;
            if (next != chunks.Length) return false;
            snapshot = new OnlineRunAssetCheckpointWorld
            {
                WorldId = worldId, WorldEpoch = WorldEpoch, CommitSequence = sequence, WorldTick = tick,
                // 原payload只有空间数量/等待队列，不包含全部Solver或战斗状态，绝不升级恢复承诺。
                CanRestoreAuthorityRules = false, Chunks = chunks
            };
            _lastOfferedCheckpointWorld = CopyCheckpointWorld(snapshot);
            RebuildCheckpointRetirementWatch();
            return true;
        }

        /// <summary>
        /// 仅由同一Authority协调器在Owner.TryRetireInactiveChunk成功后同步调用，参数必须是退休前
        /// 从Owner取到的真实非resident Header，不能由Checkpoint版本推算或由Wire构造。
        /// 全局Generation floor只证明某次退休发生，不能证明某Key的最终G/R；这里保留精确终态头。
        /// 未被两份持久化Head观察的Key直接忽略，避免长期迁移把witness也变成无限ever-seen表。
        /// </summary>
        internal bool RecordCheckpointRetirement(WorldChunkPayloadHeader terminal)
        {
            if (_stopped || Faulted || _owner == null || terminal.WorldEpoch != WorldEpoch || terminal.IsResident
                || (byte)terminal.Key.Domain > (byte)WorldMaterialDomain.Cellular || terminal.Generation == 0
                || terminal.Revision == 0 || terminal.BaseRevision >= terminal.Revision || terminal.SnapshotId == 0
                || terminal.SnapshotId > _owner.CommitSequence || terminal.ServerTick < 0
                || terminal.ServerTick > _owner.LastCapturedWorldTick || terminal.WordCount != 0 || terminal.Checksum != 2166136261u
                || terminal.Generation >= _owner.RetiredGenerationFloor || _owner.TryGetCurrentHeader(terminal.Key, out _)) return false;
            if (!_checkpointWatch.TryGetValue(terminal.Key,out var recorded)) return true;
            if (recorded.Generation != 0)
            {
                if (terminal.Generation < recorded.Generation || terminal.Generation == recorded.Generation
                    && terminal.Revision < recorded.Revision) return true;
                if (terminal.Generation == recorded.Generation && terminal.Revision == recorded.Revision)
                    return terminal.Equals(recorded);
                if (terminal.SnapshotId <= recorded.SnapshotId || terminal.ServerTick < recorded.ServerTick) return false;
            }
            _checkpointWatch[terminal.Key] = terminal;
            return true;
        }

        private bool TryGetCheckpointRetirement(OnlineRunAssetCheckpointChunk old, ulong currentSequence,
            long currentTick, out WorldChunkPayloadHeader terminal)
        {
            terminal = default;
            if (!_checkpointWatch.TryGetValue(CheckpointKey(old), out var recorded)) return false;
            if (recorded.Generation == 0 || recorded.WorldEpoch != WorldEpoch || recorded.IsResident
                || recorded.Generation < old.Generation || recorded.Generation == old.Generation && recorded.Revision <= old.Revision
                || recorded.SnapshotId <= old.SnapshotId || recorded.SnapshotId > currentSequence
                || recorded.ServerTick < old.ServerTick || recorded.ServerTick > currentTick
                || recorded.Generation >= _owner.RetiredGenerationFloor) return false;
            terminal = recorded; return true;
        }

        private void RebuildCheckpointRetirementWatch()
        {
            _nextCheckpointWatch.Clear();
            AddCheckpointResidentWatch(_currentConfirmedWorld);
            AddCheckpointResidentWatch(_lastOfferedCheckpointWorld);
            var previous=_checkpointWatch;_checkpointWatch=_nextCheckpointWatch;_nextCheckpointWatch=previous;
        }

        private void AddCheckpointResidentWatch(OnlineRunAssetCheckpointWorld world)
        {
            if (world == null) return;
            foreach (var chunk in world.Chunks)
            {
                if (!chunk.IsResident) continue;
                var key = CheckpointKey(chunk);
                if (_nextCheckpointWatch.ContainsKey(key)) continue;
                _checkpointWatch.TryGetValue(key,out var witness);
                _nextCheckpointWatch.Add(key,witness);
            }
        }

        /// <summary>
        /// 只由可信RoomInstanceBootstrap在RunCheckpointPending.ConfirmReadback完整按值确认成功后调用。
        /// Receipt、POST成功或未知HTTP结果都不能调用这里；本方法不代替持久化认证或完整资产分区核对。
        /// 再核对本World最近一次offered的全部值，允许Canonical规范排序，但拒绝错World、旧候选、
        /// 回退或被外部修改的DTO。这里确认的是冻结候选，不重读后来变化的live Owner。
        /// 成功只切换私有锚点引用，不清数量/不退休运行时Key/不改版本，也不触碰Canonical正文或Hash。
        /// </summary>
        public bool ConfirmCheckpointWorld(OnlineRunAssetCheckpointWorld world)
        {
            var offered = _lastOfferedCheckpointWorld;
            if (_stopped || Faulted || _owner == null || offered == null || world == null
                || world.WorldEpoch != WorldEpoch || !SameCheckpointWorld(offered, world)
                || _currentConfirmedWorld != null && (world.CommitSequence < _currentConfirmedWorld.CommitSequence
                    || world.WorldTick < _currentConfirmedWorld.WorldTick)) return false;
            _currentConfirmedWorld = offered;
            RebuildCheckpointRetirementWatch();
            return true;
        }

        private static WorldChunkKey CheckpointKey(OnlineRunAssetCheckpointChunk chunk)
            => new WorldChunkKey((WorldMaterialDomain)chunk.Domain, chunk.X, chunk.Y, chunk.Z);

        private static OnlineRunAssetCheckpointWorld CopyCheckpointWorld(OnlineRunAssetCheckpointWorld source)
        {
            var chunks = new OnlineRunAssetCheckpointChunk[source.Chunks.Length];
            for (int i = 0; i < chunks.Length; i++)
            {
                var chunk = source.Chunks[i]; var words = chunk.Words.Length==0?Array.Empty<uint>():new uint[chunk.Words.Length];
                Array.Copy(chunk.Words, words, words.Length);
                chunks[i] = new OnlineRunAssetCheckpointChunk
                {
                    Domain = chunk.Domain, X = chunk.X, Y = chunk.Y, Z = chunk.Z,
                    WorldEpoch = chunk.WorldEpoch, SnapshotId = chunk.SnapshotId, Generation = chunk.Generation,
                    BaseRevision = chunk.BaseRevision, Revision = chunk.Revision, ServerTick = chunk.ServerTick,
                    WordCount = chunk.WordCount, Checksum = chunk.Checksum, IsResident = chunk.IsResident, Words = words
                };
            }
            return new OnlineRunAssetCheckpointWorld
            {
                WorldId = source.WorldId, WorldEpoch = source.WorldEpoch, CommitSequence = source.CommitSequence,
                WorldTick = source.WorldTick, CanRestoreAuthorityRules = source.CanRestoreAuthorityRules, Chunks = chunks
            };
        }

        private bool SameCheckpointWorld(OnlineRunAssetCheckpointWorld expected, OnlineRunAssetCheckpointWorld actual)
        {
            if (expected.WorldId != actual.WorldId || expected.WorldEpoch != actual.WorldEpoch
                || expected.CommitSequence != actual.CommitSequence || expected.WorldTick != actual.WorldTick
                || expected.CanRestoreAuthorityRules != actual.CanRestoreAuthorityRules || actual.Chunks == null
                || expected.Chunks.Length != actual.Chunks.Length || actual.Chunks.Length > OnlineRunAssetCheckpoint.MaximumWorldKeys)
                return false;
            // 全世界数千块的读回可重新排序。每个 Key 在索引中只能配对一次，保持线性匹配。
            _confirmationRows.Clear();
            for(int index=0;index<expected.Chunks.Length;index++) _confirmationRows.Add(CheckpointKey(expected.Chunks[index]),index);
            for (int i = 0; i < actual.Chunks.Length; i++)
            {
                var chunk = actual.Chunks[i];
                if (chunk == null || chunk.Domain > (uint)WorldMaterialDomain.Cellular || chunk.Words == null
                    || chunk.WordCount != chunk.Words.Length) return false;
                var key = CheckpointKey(chunk);
                if (!_confirmationRows.TryGetValue(key,out int position) || !_confirmationRows.Remove(key)) return false;
                var found=expected.Chunks[position];
                if (found.WorldEpoch != chunk.WorldEpoch || found.SnapshotId != chunk.SnapshotId
                    || found.Generation != chunk.Generation || found.BaseRevision != chunk.BaseRevision
                    || found.Revision != chunk.Revision || found.ServerTick != chunk.ServerTick
                    || found.WordCount != chunk.WordCount || found.Checksum != chunk.Checksum || found.IsResident != chunk.IsResident)
                    return false;
                for (int word = 0; word < chunk.Words.Length; word++)
                    if (found.Words[word] != chunk.Words[word]) return false;
            }
            return true;
        }
    }
}
