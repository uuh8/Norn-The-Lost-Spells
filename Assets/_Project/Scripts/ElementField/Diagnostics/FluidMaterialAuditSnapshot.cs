using Game.Materials;

namespace Game.ElementField
{
    /// <summary>
    /// 同一 GPU Queue 时点的单材料数量审计。UnexplainedGmu 是绝对差值；方向由
    /// AuthorityGmu 与 ExpectedGmu 比较，避免 ulong 转成有符号数时溢出。
    /// </summary>
    public readonly struct FluidMaterialAuditSnapshot
    {
        public readonly MaterialId Material;
        public readonly ulong Epoch;
        public readonly ulong ExpectedGmu;
        public readonly ulong GpuAuthorityGmu;
        public readonly ulong RamAuthorityGmu;
        public readonly ulong AuthorityGmu;
        public readonly ulong UnexplainedGmu;
        public readonly uint TopologyVersion;
        public readonly uint MutationVersion;
        public readonly bool Valid;
        public readonly ulong SubmittedParticleWatermark;
        public readonly double RequestedAt;
        public readonly double CompletedAt;

        public bool IsBalanced => Valid && UnexplainedGmu == 0u;
        public bool IsFreshFor(ulong submittedParticleWatermark, double lastWriteAt) =>
            Valid && SubmittedParticleWatermark == submittedParticleWatermark
            && RequestedAt > lastWriteAt && CompletedAt >= RequestedAt;

        internal FluidMaterialAuditSnapshot(MaterialId material, ulong epoch,
            ulong expectedGmu, ulong gpuAuthorityGmu, ulong ramAuthorityGmu,
            uint topologyVersion, uint mutationVersion, ulong submittedParticleWatermark,
            double requestedAt, double completedAt, bool valid)
        {
            Material = material;
            Epoch = epoch;
            ExpectedGmu = expectedGmu;
            GpuAuthorityGmu = gpuAuthorityGmu;
            RamAuthorityGmu = ramAuthorityGmu;
            TopologyVersion = topologyVersion;
            MutationVersion = mutationVersion;
            SubmittedParticleWatermark = submittedParticleWatermark;
            RequestedAt = requestedAt;
            CompletedAt = completedAt;
            Valid = valid && ulong.MaxValue - gpuAuthorityGmu >= ramAuthorityGmu;
            AuthorityGmu = Valid ? gpuAuthorityGmu + ramAuthorityGmu : 0u;
            UnexplainedGmu = !Valid ? ulong.MaxValue
                : AuthorityGmu >= expectedGmu ? AuthorityGmu - expectedGmu : expectedGmu - AuthorityGmu;
        }
    }
}
