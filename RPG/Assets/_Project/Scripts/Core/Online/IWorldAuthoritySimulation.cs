namespace Game.Core
{
    /// <summary>
    /// 世界模拟的时间所有者。默认值保持既有单机资产的自动推进；Authority 由服务器显式喂时，
    /// Replica 只承接后续复制数据，不能因为客户端也有 Update 就生成第二份玩法事实。
    /// 此合同属于 Core，不提及 ElementField 的 Store 或 Material，避免 Multiplayer 反向依赖材料实现。
    /// </summary>
    public enum WorldExecutionMode : byte
    {
        SinglePlayer = 0,
        Authority = 1,
        Replica = 2,
    }

    /// <summary>
    /// 场景装配层持有的低层模拟入口。deltaTime 是外部服务器步长（秒），serverTick 是非零、严格递增的
    /// 外部步编号；材料实现自行累积为配置的固定频率，调用方不可再额外驱动它的 Update 或子 Solver。
    /// 冻结时接收步编号但不接收时间，重复/倒退编号和非法时间均无副作用地拒绝。
    /// </summary>
    public interface IWorldAuthoritySimulation
    {
        WorldExecutionMode ExecutionMode { get; }
        /// <summary>
        /// 某个材料 Tick 曾异常退出，世界可能已经部分改变。此状态不表示已回滚；装配层必须停止推进和
        /// 快照发布，销毁并重新建立世界后才能恢复，不能只解除 Frozen 或重新注册同一个实例。
        /// </summary>
        bool IsSimulationFaulted { get; }
        void StepAuthority(float deltaTime, uint serverTick);
    }
}
