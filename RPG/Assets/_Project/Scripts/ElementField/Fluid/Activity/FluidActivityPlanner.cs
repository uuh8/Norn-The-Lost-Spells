using System;

namespace Game.ElementField
{
    /// <summary>
    /// Metadata.y 的 Activity Bit Contract。Alive 管数据寿命，InterestActive 管玩家附近活动域，
    /// RemoteSpawnActive 是远处新生粒子落稳前的临时 Simulation Lease；RequiresSimulation 管
    /// Solver target，Sleeping 表示 Interest 内仍可见但暂不作为 target 推进。
    /// </summary>
    public static class FluidActivityFlags
    {
        public const uint Alive = 1u << 0;
        public const uint InterestActive = 1u << 1;
        public const uint RequiresSimulation = 1u << 2;
        public const uint Sleeping = 1u << 3;
        public const uint RemoteSpawnActive = 1u << 4;
        public const uint ArchiveLocked = 1u << 5;
        public const uint RestoreLoading = 1u << 6;

        public static bool IsAlive(uint flags) => (flags & Alive) != 0u;
        public static bool RequiresSolver(uint flags) =>
            (flags & (Alive | RequiresSimulation)) == (Alive | RequiresSimulation)
            && (flags & (InterestActive | RemoteSpawnActive)) != 0u
            && (flags & (Sleeping | ArchiveLocked | RestoreLoading)) == 0u;
        public static bool ContributesToSurface(uint flags) =>
            (flags & Alive) != 0u && (flags & RestoreLoading) == 0u;
        public static bool IsSleeping(uint flags) =>
            (flags & (Alive | InterestActive | Sleeping))
            == (Alive | InterestActive | Sleeping);
    }

    public readonly struct FluidActivityTransition
    {
        public FluidActivityTransition(uint flags, uint stableTicks)
        {
            Flags = flags;
            StableTicks = stableTicks;
        }

        public uint Flags { get; }
        public uint StableTicks { get; }
    }

    /// <summary>与 GPU Kernel 对齐的 Pure 状态迁移与 Indirect Dispatch 规划。</summary>
    public static class FluidActivityPlanner
    {
        public static FluidActivityTransition Evaluate(
            uint currentFlags,
            uint currentStableTicks,
            bool insideInterest,
            bool isStable,
            bool wakeRequested,
            uint requiredStableTicks)
        {
            if (!FluidActivityFlags.IsAlive(currentFlags))
                return default;
            // Archive 事务锁定后，普通 Activity Tick 不得覆盖 Metadata；否则失败回滚和成功释放
            // 都失去对同一批 slot 的明确所有权。
            if ((currentFlags & FluidActivityFlags.ArchiveLocked) != 0u)
                return new FluidActivityTransition(currentFlags, currentStableTicks);
            if (!insideInterest)
            {
                // 普通 Resident 只保留 Alive；只有刚在远处出生、仍持有 Lease 的粒子才继续模拟。
                // 这样修复远程投射物产生的悬空液体，同时不会唤醒历史上所有远处水滩。
                if ((currentFlags & FluidActivityFlags.RemoteSpawnActive) == 0u)
                    return new FluidActivityTransition(FluidActivityFlags.Alive, 0u);

                uint remoteStableTicks = isStable
                    ? currentStableTicks == uint.MaxValue ? uint.MaxValue : currentStableTicks + 1u
                    : 0u;
                if (isStable && remoteStableTicks >= Math.Max(1u, requiredStableTicks))
                {
                    return new FluidActivityTransition(
                        FluidActivityFlags.Alive,
                        remoteStableTicks);
                }

                return new FluidActivityTransition(
                    FluidActivityFlags.Alive
                    | FluidActivityFlags.RemoteSpawnActive
                    | FluidActivityFlags.RequiresSimulation,
                    remoteStableTicks);
            }

            bool wasInside = (currentFlags & FluidActivityFlags.InterestActive) != 0u;
            if (!wasInside || wakeRequested)
            {
                return new FluidActivityTransition(
                    FluidActivityFlags.Alive
                    | FluidActivityFlags.InterestActive
                    | FluidActivityFlags.RequiresSimulation,
                    0u);
            }

            if ((currentFlags & FluidActivityFlags.Sleeping) != 0u)
            {
                return new FluidActivityTransition(
                    FluidActivityFlags.Alive
                    | FluidActivityFlags.InterestActive
                    | FluidActivityFlags.Sleeping,
                    currentStableTicks);
            }

            uint stableTicks = isStable
                ? currentStableTicks == uint.MaxValue ? uint.MaxValue : currentStableTicks + 1u
                : 0u;
            if (isStable && stableTicks >= Math.Max(1u, requiredStableTicks))
            {
                return new FluidActivityTransition(
                    FluidActivityFlags.Alive
                    | FluidActivityFlags.InterestActive
                    | FluidActivityFlags.Sleeping,
                    stableTicks);
            }

            return new FluidActivityTransition(
                FluidActivityFlags.Alive
                | FluidActivityFlags.InterestActive
                | FluidActivityFlags.RequiresSimulation,
                stableTicks);
        }

        public static int CalculateSolverThreadGroups(
            uint awakeCount,
            int particleCapacity,
            int threadsPerGroup)
        {
            if (particleCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(particleCapacity));
            if (threadsPerGroup <= 0)
                throw new ArgumentOutOfRangeException(nameof(threadsPerGroup));
            // slot 并不 compact：只要有一个 awake，就必须覆盖完整 capacity，不能用 AwakeCount 算 groups。
            return awakeCount == 0u
                ? 0
                : (particleCapacity + threadsPerGroup - 1) / threadsPerGroup;
        }
    }
}
