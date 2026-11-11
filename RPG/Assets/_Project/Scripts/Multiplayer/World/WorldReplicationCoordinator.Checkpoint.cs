using System;
using Game.Core;
using Game.NetworkProtocol;

namespace Game.Multiplayer
{
    public sealed partial class WorldReplicationCoordinator
    {
        /// <summary>
        /// 同一Authority安全边界的离散深拷贝。逐原生Key读完整已发布payload，包含已退役Key，
        /// 不按任何玩家Interest筛选，不推进模拟/提交序列/ACK。调用方必须同步捕获其它资产分区后再跨yield。
        /// </summary>
        public bool TryCaptureCheckpoint(string worldId, out OnlineRunAssetCheckpointWorld snapshot)
        {
            snapshot = null;
            if (_stopped || Faulted || _owner == null || string.IsNullOrWhiteSpace(worldId) || worldId.Length > 256
                || _owner.LastCapturedWorldTick < 0 || _owner.ChunkCount > OnlineRunAssetCheckpoint.MaximumWorldKeys)
                return false;
            long tick = _owner.LastCapturedWorldTick;
            ulong sequence = _owner.CommitSequence;
            int count = _owner.ChunkCount;
            var chunks = new OnlineRunAssetCheckpointChunk[count];
            for (int i = 0; i < count; i++)
            {
                var current = _owner.GetChunkHeader(i);
                if (current.WordCount < 0 || current.IsResident && current.WordCount < WorldChunkPayloadCodec.PrefixWords
                    || !current.IsResident && current.WordCount != 0 || current.WordCount > _maxWords
                    || current.WordCount > OnlineRunAssetCheckpoint.MaximumWordsPerChunk) return false;
                // 数组归本候选所有；后续材料变化或发送分页都不能改写已经准备提交的正文。
                var words = new uint[current.WordCount];
                if (!_owner.TryCopyChunk(current.Key, words, out var header) || header.WorldEpoch != WorldEpoch
                    || header.SnapshotId != sequence || header.ServerTick != tick || header.BaseRevision != 0
                    || header.WordCount != words.Length
                    || header.IsResident && !WorldChunkPayloadCodec.Validate(words, words.Length, header.Key)
                    || WorldChunkPayloadCodec.ComputeChecksum(words, words.Length) != header.Checksum) return false;
                chunks[i] = new OnlineRunAssetCheckpointChunk
                {
                    Domain = (uint)header.Key.Domain, X = header.Key.X, Y = header.Key.Y, Z = header.Key.Z,
                    WorldEpoch = header.WorldEpoch, SnapshotId = header.SnapshotId, Generation = header.Generation,
                    BaseRevision = header.BaseRevision, Revision = header.Revision, ServerTick = header.ServerTick,
                    WordCount = header.WordCount, Checksum = header.Checksum, IsResident = header.IsResident, Words = words
                };
            }
            if (_owner.LastCapturedWorldTick != tick || _owner.CommitSequence != sequence || _owner.ChunkCount != count)
                return false;
            snapshot = new OnlineRunAssetCheckpointWorld
            {
                WorldId = worldId, WorldEpoch = WorldEpoch, CommitSequence = sequence, WorldTick = tick,
                // 原payload只有空间数量/等待队列，不包含全部Solver或战斗状态，绝不升级恢复承诺。
                CanRestoreAuthorityRules = false, Chunks = chunks
            };
            return true;
        }
    }
}
