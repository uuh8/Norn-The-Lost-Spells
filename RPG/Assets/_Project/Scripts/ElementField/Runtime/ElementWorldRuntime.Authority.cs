using System;
using Game.Core;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementWorldRuntime : IWorldAuthoritySimulation
    {
        [Header("Simulation Ownership")]
        [Tooltip("必须在初始化前配置。SinglePlayer 自动 Update；Authority 仅外部服务器推进；Replica 本阶段不初始化玩法世界。")]
        [SerializeField] private WorldExecutionMode _executionMode;
        private uint _lastAuthorityServerTick;
        private bool _authorityStepInProgress;
        private bool _initializationStarted;
        private bool _worldStepInProgress;
        private bool _simulationFaulted;

        public WorldExecutionMode ExecutionMode => _executionMode;
        public bool IsSimulationFaulted => _simulationFaulted;
        // Formal 写入口共用同一个生命周期判定：故障世界可能保留半份状态，不能继续接收材料或扣库存。
        // DebugPause/Frozen 只停止 Solver，仍允许已有实验收取/投放；它们不等于必须重建的 Faulted。
        // Phase 内部的 Stage/Undo 仍由原事务 Owner 调用，这个门不参与半途回滚的合法性判断。
        internal bool CanAcceptWorldWrites => IsInitialized && !_simulationFaulted
            && _executionMode != WorldExecutionMode.Replica;
        // Source 与时钟观察统一提交边界：Authority 外层、单机/测试材料 Tick、库存事务都不能中途导出或交错推进。
        internal bool IsWorldStepInProgress => _worldStepInProgress || _authorityStepInProgress || _materialTransactionInProgress;

        /// <summary>
        /// 必须在 inactive 场景对象激活前或序列化配置中确定所有权。初始化后 Store、Phase Owner、
        /// Registry、库存 Port 均已绑定；此时只改一个枚举不能撤销所有旧写权限，因此禁止热切换。
        /// Replica 当前是未激活的只读模式占位，后续应安装独立 ReplicaStore，不能复用已初始化的权威 Store。
        /// </summary>
        public void ConfigureExecutionMode(WorldExecutionMode mode)
        {
            if (mode != WorldExecutionMode.SinglePlayer && mode != WorldExecutionMode.Authority
                && mode != WorldExecutionMode.Replica)
                throw new ArgumentOutOfRangeException(nameof(mode));
            if (_executionMode == mode) return;
            if (IsInitialized || _initializationStarted || _authorityStepInProgress)
                throw new InvalidOperationException("World execution ownership must be configured before initialization.");
            _executionMode = mode;
            _lastAuthorityServerTick = 0;
        }

        /// <summary>
        /// 外部 30Hz 只给既有材料累加器喂一次时间，不能每个 serverTick 都直接跑一次 10Hz Solver。
        /// 先校验输入再提交编号，非法高编号不会挡住后续合法步。Frozen 步仍提交编号，防止解冻后重放，
        /// 但不触碰累加器，保留冻结前合法的小数余数，冻结时长不形成追赶债务。
        /// </summary>
        public void StepAuthority(float deltaTime, uint serverTick)
        {
            // 重入不改写外层正在汇报的 LastFrameTickCount，也不提交嵌套调用的编号。
            if (IsWorldStepInProgress) return;
            LastFrameTickCount = 0;
            if (_executionMode != WorldExecutionMode.Authority || !IsInitialized || _simulationFaulted
                || !float.IsFinite(deltaTime) || deltaTime <= 0f || serverTick == 0
                || serverTick <= _lastAuthorityServerTick)
                return;

            _lastAuthorityServerTick = serverTick;
            if (SimulationPausedForDebug || SimulationSuspended) return;

            // 材料写入/反应可同步触发外部事件；重入不得再次向同一个累加器和 Store 写入。
            // 不读取 Time.timeScale：单机 Pause 与服务器冻结属于不同生命周期，由外部明确设置冻结门。
            _authorityStepInProgress = true;
            try { AdvanceSimulationClock(deltaTime); }
            finally { _authorityStepInProgress = false; }
        }

        private int AdvanceSimulationClock(float deltaTime)
        {
            RefreshInterestChunk();
            RefreshWorldActivity();
            int ticks = _tickAccumulator.Consume(deltaTime);
            LastFrameTickCount = 0;
            if (_tickAccumulator.DroppedDebtLastConsume) _droppedDebtFrameCount++;
            for (int i = 0; i < ticks; i++)
            {
                // 反应事件/ReceiveHit 同步执行，首个 Tick 可能冻结或销毁世界。每个补步都重新检查；
                // Consume 已取出的整 Tick 预算直接放弃，不能解冻后补做；合法不足一帧余数仍留在累加器。
                if (this == null || !IsInitialized || _simulationFaulted
                    || SimulationPausedForDebug || SimulationSuspended) break;
                SimulateOneTick();
                LastFrameTickCount++;
            }
            return LastFrameTickCount;
        }
    }
}
