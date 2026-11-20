using System;
using UnityEngine;

namespace Game.Core
{
    /// <summary>只读表现端点；Quantity 是数量证据而非第二个数量 Owner。姿态只允许 Renderer 使用。</summary>
    public readonly struct WorldLiquidMotionSample
    {
        public readonly Vector3Int Cell;
        public readonly byte MaterialId;
        public readonly uint PreviousQuantity, CurrentQuantity;
        public readonly Vector3 PreviousOffset, PreviousVelocity, CurrentOffset, CurrentVelocity;
        public readonly int AnchorIndex;

        public WorldLiquidMotionSample(Vector3Int cell, byte materialId, uint previousQuantity,
            uint currentQuantity, Vector3 previousOffset, Vector3 previousVelocity,
            Vector3 currentOffset, Vector3 currentVelocity, int anchorIndex = -1)
        {
            Cell = cell; MaterialId = materialId; PreviousQuantity = previousQuantity;
            CurrentQuantity = currentQuantity; PreviousOffset = previousOffset;
            PreviousVelocity = previousVelocity; CurrentOffset = currentOffset;
            CurrentVelocity = currentVelocity; AnchorIndex = anchorIndex;
        }
    }

    /// <summary>表现帧引用的已提交数量身份；订阅代次只由认证连接 Coordinator 填写。</summary>
    public readonly struct WorldLiquidMotionAnchor
    {
        public readonly WorldChunkKey Key;
        public readonly ulong Generation, Revision, SubscriptionEpoch;
        public readonly bool IsResident;
        public WorldLiquidMotionAnchor(WorldChunkKey key, ulong generation, ulong revision,
            ulong subscriptionEpoch, bool isResident)
        { Key = key; Generation = generation; Revision = revision; SubscriptionEpoch = subscriptionEpoch; IsResident = isResident; }
    }

    /// <summary>
    /// 固定容量的单份完整表现帧。数组不扩容；有效前缀只由 Count 指定。
    /// 页头身份与本文数据独立，不能用 Sequence 推进材料 Revision 或持久化 checkpoint。
    /// </summary>
    public sealed class WorldLiquidMotionFrame
    {
        public const int MaxAnchors = 32, MaxRows = 128;
        public readonly WorldLiquidMotionAnchor[] Anchors = new WorldLiquidMotionAnchor[MaxAnchors];
        public readonly WorldLiquidMotionSample[] Rows = new WorldLiquidMotionSample[MaxRows];
        public ulong WorldEpoch, Sequence;
        public uint ConnectionEpoch, MaterialTick;
        public double SampleTime;
        public float Interval;
        public int ChunkSize, AnchorCount, RowCount;

        public void CopyFrom(WorldLiquidMotionFrame source)
        {
            if (source == null || source.AnchorCount < 0 || source.AnchorCount > MaxAnchors
                || source.RowCount < 0 || source.RowCount > MaxRows)
                throw new ArgumentException("表现帧的有效前缀无效。", nameof(source));
            Array.Copy(source.Anchors, Anchors, source.AnchorCount);
            Array.Copy(source.Rows, Rows, source.RowCount);
            WorldEpoch = source.WorldEpoch; ConnectionEpoch = source.ConnectionEpoch;
            Sequence = source.Sequence; MaterialTick = source.MaterialTick;
            SampleTime = source.SampleTime; Interval = source.Interval; ChunkSize = source.ChunkSize;
            AnchorCount = source.AnchorCount; RowCount = source.RowCount;
        }
    }

    /// <summary>全世界每材料 Tick 捕获一次；GroupIds 是完整搬运组的 union root，不是网络数量身份。</summary>
    public sealed class WorldLiquidMotionCaptureBuffer
    {
        public const int Capacity = 8192;
        public readonly WorldLiquidMotionSample[] Rows = new WorldLiquidMotionSample[Capacity];
        public readonly int[] GroupIds = new int[Capacity];
        public int Count, ChunkSize;
        public uint Epoch;
        public float Interval;
    }

    public interface IWorldLiquidMotionSource
    {
        bool TryCapture(WorldLiquidMotionCaptureBuffer destination, WorldReplicaBuffer quantities);
    }

    public interface IWorldLiquidMotionSink
    {
        bool TryPublish(WorldLiquidMotionFrame frame, double serverTime);
        void AdvancePresentation(double serverTime, bool frozen);
        void ResetConnection(uint epoch);
    }

    /// <summary>
    /// 可选的只读衔接能力：完整、认证且订阅有效的sidecar可先到，而其多个Chunk数量尚未全部安装。
    /// 候选仅允许短暂续播上一份已验证的显示曲线，不授予数量、版本、ACK或未来姿态。
    /// </summary>
    public interface IWorldLiquidMotionCandidateSink
    {
        void StagePresentationCandidate(WorldLiquidMotionFrame frame, double serverTime);
        void CancelPresentationCandidate();
    }

    /// <summary>
    /// active与最多两份future的只读订阅依赖。旧active可能包含最新accepted已省略的tombstone
    /// 源Chunk，外层必须继续核资格；不能只验证最新_published而漏掉旧壳的Leave。
    /// 返回Anchor值而非内部可变Frame，调用方不能修改显示缓冲；数量/ACK不由此接口授予。
    /// </summary>
    public interface IWorldLiquidMotionRetainedAnchors
    {
        int RetainedMotionWindowCount { get; }
        int GetRetainedMotionAnchorCount(int window);
        bool TryGetRetainedMotionAnchor(int window,int index,out WorldLiquidMotionAnchor anchor);
    }

    /// <summary>可选场景能力；Multiplayer 通过 Core 访问，不能反向依赖 ElementField 或 Rendering。</summary>
    public interface IOnlineLiquidMotionWorld
    {
        IWorldLiquidMotionSource Source { get; }
        IWorldLiquidMotionSink Sink { get; }
    }
}
