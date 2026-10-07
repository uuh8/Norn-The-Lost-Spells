using System;

namespace Game.ElementField
{
    /// <summary>
    /// 把 GPU 回传的“粒子数净变化”换算成审计所需的 GamePlay Material Units。
    /// 只做有界整数运算；任何下溢/上溢都让本 Epoch 失效，不能用回绕值伪造守恒。
    /// </summary>
    public static class FluidReactionAuditLedger
    {
        public static bool TryApplyParticleDelta(
            ulong submittedGmu,
            int particleDelta,
            uint amountUnitsPerParticle,
            out ulong expectedGmu)
        {
            expectedGmu = 0u;
            if (amountUnitsPerParticle == 0u) return false;
            if (particleDelta >= 0)
            {
                ulong added = (ulong)(uint)particleDelta * amountUnitsPerParticle;
                if (ulong.MaxValue - submittedGmu < added) return false;
                expectedGmu = submittedGmu + added;
                return true;
            }

            ulong removedParticles = particleDelta == int.MinValue
                ? (ulong)int.MaxValue + 1u
                : (ulong)(-particleDelta);
            ulong removed = removedParticles * amountUnitsPerParticle;
            if (removed > submittedGmu) return false;
            expectedGmu = submittedGmu - removed;
            return true;
        }
    }
}
