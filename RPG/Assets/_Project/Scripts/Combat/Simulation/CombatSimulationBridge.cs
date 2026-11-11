using System;
using Game.Core;
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 跨程序集的薄时钟桥：内部仍使用Combat原参与者/状态和固定槽，不创建第二时钟或扩大容量。
    /// 上层只绑定一次固定回调；模式、动态写门、同Tick去重和出生Tick均由原Combat状态裁决。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CombatSimulationBridge : MonoBehaviour, ICombatSimulationParticipant
    {
        private CombatExecutionState _state;
        private Action<float, uint> _step;
        public CombatExecutionMode Mode => _state.Mode;
        public CombatSimulationWorld World => _state.World;
        public bool AllowsWrites => _state.AllowsGameplayWrites;

        public void Configure(CombatExecutionMode mode, CombatSimulationWorld world, Action<float, uint> step)
        {
            if (step == null) throw new ArgumentNullException(nameof(step));
            if (_step != null && _step != step) throw new InvalidOperationException("活跃时钟桥不能替换已绑定的推进回调。");
            if (mode == CombatExecutionMode.Authority && world == null) throw new ArgumentNullException(nameof(world));
            _step = step;
            _state.Configure(this, mode, world, CombatTickPhase.AfterPhysics);
        }

        public void Resume()
        { if (_step != null && isActiveAndEnabled) _state.Resume(this, CombatTickPhase.AfterPhysics); }
        public void Suspend() => _state.Suspend(this);
        public void StepAuthority(float deltaTime, uint serverTick)
        { if (_step != null && _state.TryStep(deltaTime, serverTick)) _step(deltaTime, serverTick); }
        private void OnEnable() => Resume();
        private void OnDisable() => Suspend();
        private void OnDestroy() => Suspend();
    }
}
