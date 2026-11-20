namespace Game.Core
{
    /// <summary>只适用于协调器独占、上次已完整提交的同一缓冲。无内容变化时只更新时间，不重新编码。</summary>
    public interface IWorldUnchangedReplicationSource
    {
        bool TryAdvanceUnchangedCapture(WorldReplicaBuffer committedBuffer);
    }

    /// <summary>每个点名 Chunk 是整体 replacement，空块也点名。false 且 failure=None 表示必须重新全量捕获。</summary>
    public interface IWorldCellularIncrementalSource
    {
        bool TryCaptureChangedChunks(WorldReplicaBuffer destination, out int changedChunkCount, out WorldCaptureFailure failure);
    }
}
