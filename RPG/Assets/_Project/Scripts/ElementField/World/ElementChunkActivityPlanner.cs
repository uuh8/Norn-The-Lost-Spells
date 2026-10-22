using System;

namespace Game.ElementField
{
    /// <summary>
    /// Resident Chunk 相对当前 Interest Point 的调度状态。
    /// Reclaimable 只是“允许 Store 回收”的判定结果，不代表 Planner 自己会修改 Dictionary。
    /// </summary>
    public enum ElementChunkActivityState : byte
    {
        Sleeping = 0,
        Active = 1,
        Reclaimable = 2,
    }

    /// <summary>
    /// 不依赖 MonoBehaviour 的 Chunk 生命周期纯规则。玩家只决定数据是否工作，绝不改变世界坐标或 Chunk Key。
    /// </summary>
    public static class ElementChunkActivityPlanner
    {
        public static bool IsInsideActiveRegion(
            ElementChunkKey candidate,
            ElementChunkKey interest,
            int activeRadiusXZ,
            int activeRadiusY)
        {
            ValidateRadii(activeRadiusXZ, activeRadiusY);

            // 使用 long 做差，避免两个极端 int 坐标相减时先溢出。
            long deltaX = (long)candidate.X - interest.X;
            long deltaY = (long)candidate.Y - interest.Y;
            long deltaZ = (long)candidate.Z - interest.Z;

            // 第一版采用 Axis-Aligned Chunk Box，而不是球形距离：边界稳定、无需 sqrt，
            // 且能完整覆盖一个规则的 Streaming 窗口，后续跨 Chunk 邻居预取更容易推理。
            return Math.Abs(deltaX) <= activeRadiusXZ
                && Math.Abs(deltaY) <= activeRadiusY
                && Math.Abs(deltaZ) <= activeRadiusXZ;
        }

        public static ElementChunkActivityState Evaluate(
            ElementChunkKey candidate,
            ElementChunkKey interest,
            bool hasAnyElement,
            bool requiresSimulation,
            long lastRelevantTick,
            long currentTick,
            long sleepGraceTicks,
            int activeRadiusXZ,
            int activeRadiusY)
        {
            ValidateRadii(activeRadiusXZ, activeRadiusY);
            if (lastRelevantTick < 0)
                throw new ArgumentOutOfRangeException(nameof(lastRelevantTick));
            if (currentTick < lastRelevantTick)
                throw new ArgumentOutOfRangeException(nameof(currentTick));
            if (sleepGraceTicks < 0)
                throw new ArgumentOutOfRangeException(nameof(sleepGraceTicks));

            bool insideInterestRegion = IsInsideActiveRegion(
                candidate,
                interest,
                activeRadiusXZ,
                activeRadiusY);

            // “在玩家附近”只表示数据应当驻留（Resident），不表示空 Chunk 必须执行 Solver。
            // 若把 Interest Region 内所有空 Chunk 都标为 Active，四个 Solver Stage 会持续扫描
            // 512 个 Cell/Chunk；窗口越大，CPU 成本就越接近一个固定的大型三维数组。
            long inactiveTicks = currentTick - lastRelevantTick;
            if (!hasAnyElement)
            {
                // 附近空 Chunk 或刚被边界 Planner 触及的空 Chunk 可以缓存，但没有工作时必须 Sleeping。
                // 离开 Interest Region 且 Grace Lease 到期后才允许 Store 真正回收它。
                return insideInterestRegion || inactiveTicks < sleepGraceTicks
                    ? ElementChunkActivityState.Sleeping
                    : ElementChunkActivityState.Reclaimable;
            }

            // 非空不等于“每 Tick 都有工作”。稳定水需要继续驻留、接受 Exposure 和 Rendering 查询，
            // 但在法术写入或相邻边界重新唤醒前，不应继续执行 Solver 全扫描。
            if (!requiresSimulation)
                return ElementChunkActivityState.Sleeping;

            // 非空 Chunk 在玩家附近，或刚被法术/跨 Chunk 传输刷新过 Lease 时继续运行。
            // 这让远处刚命中的元素有时间完成流动和反应，同时不会永久占用 CPU。
            if (insideInterestRegion || inactiveTicks < sleepGraceTicks)
                return ElementChunkActivityState.Active;

            // 非空数据代表玩家曾改变过世界。距离只能让它暂停工作，不能让历史凭空消失。
            return ElementChunkActivityState.Sleeping;
        }

        private static void ValidateRadii(int activeRadiusXZ, int activeRadiusY)
        {
            if (activeRadiusXZ < 0)
                throw new ArgumentOutOfRangeException(nameof(activeRadiusXZ));
            if (activeRadiusY < 0)
                throw new ArgumentOutOfRangeException(nameof(activeRadiusY));
        }
    }
}
