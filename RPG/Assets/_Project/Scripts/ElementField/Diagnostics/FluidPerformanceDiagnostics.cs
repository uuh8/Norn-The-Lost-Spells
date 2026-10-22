namespace Game.ElementField
{
    /// <summary>同一次 GPU Counter 回读得到的粒子池状态；时间字段用于判断样本是否过期。</summary>
    public readonly struct GpuFluidPoolDiagnostics
    {
        public readonly bool Valid;
        public readonly uint Version;
        public readonly uint Alive;
        public readonly uint Free;
        public readonly uint Dropped;
        public readonly uint MutationVersion;
        public readonly double RequestedAt;
        public readonly double CompletedAt;

        public GpuFluidPoolDiagnostics(bool valid, uint version, uint alive, uint free,
            uint dropped, double requestedAt, double completedAt)
            : this(valid, version, alive, free, dropped, requestedAt, completedAt, 0u)
        {
        }

        public GpuFluidPoolDiagnostics(bool valid, uint version, uint alive, uint free,
            uint dropped, double requestedAt, double completedAt, uint mutationVersion)
        {
            Valid = valid;
            Version = version;
            Alive = alive;
            Free = free;
            Dropped = dropped;
            MutationVersion = mutationVersion;
            RequestedAt = requestedAt;
            CompletedAt = completedAt;
        }

        /// <summary>Archive/Restore 会改写 Alive/Free；只有同一 Mutation Version 的样本才能参与总账。</summary>
        public bool IsFreshFor(uint mutationVersion) => Valid && MutationVersion == mutationVersion;

        public bool HasConsistentCapacity(uint capacity)
        {
            return Valid && capacity > 0u && Alive <= capacity && Free <= capacity
                && (ulong)Alive + Free == capacity;
        }
    }

    /// <summary>同一次 Activity Counter 回读得到的求解活动状态。</summary>
    public readonly struct GpuFluidActivityDiagnostics
    {
        public readonly bool Valid;
        public readonly uint Version;
        public readonly uint Awake;
        public readonly uint Sleeping;
        public readonly uint Interest;
        public readonly double RequestedAt;
        public readonly double CompletedAt;

        public GpuFluidActivityDiagnostics(bool valid, uint version, uint awake, uint sleeping,
            uint interest, double requestedAt, double completedAt)
        {
            Valid = valid;
            Version = version;
            Awake = awake;
            Sleeping = sleeping;
            Interest = interest;
            RequestedAt = requestedAt;
            CompletedAt = completedAt;
        }
    }

    /// <summary>Gameplay 粒子回读从请求、完成到发布的时间线。</summary>
    public readonly struct FluidGameplayDiagnostics
    {
        public readonly bool Valid;
        public readonly uint SnapshotVersion;
        public readonly double RequestedAt;
        public readonly double CompletedAt;
        public readonly double PublishedAt;
        public readonly uint ReadbackErrorCount;

        public FluidGameplayDiagnostics(bool valid, uint snapshotVersion, double requestedAt,
            double completedAt, double publishedAt, uint readbackErrorCount)
        {
            Valid = valid;
            SnapshotVersion = snapshotVersion;
            RequestedAt = requestedAt;
            CompletedAt = completedAt;
            PublishedAt = publishedAt;
            ReadbackErrorCount = readbackErrorCount;
        }

        public FluidGameplayDiagnostics WithReadbackErrorCount(uint errorCount)
        {
            return new FluidGameplayDiagnostics(Valid, SnapshotVersion, RequestedAt,
                CompletedAt, PublishedAt, errorCount);
        }
    }
}
