using System;
using Game.Core;
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 权威世界中预警/残留表现的有界寿命。它登记到现有 CombatSimulationWorld，不另建 Update 时钟或 Coroutine。
    /// Timer 由产出本身持有：施法者死亡取消尚未生成的陨石后，已生成的预警仍会在有限 Gameplay 时间后清理。
    /// Replica 不在 Prefab 上配置此组件，视觉退出由服务器终止事实控制。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CombatVisualLifetime : MonoBehaviour, ICombatSimulationParticipant
    {
        private CombatExecutionState _execution;
        private bool _configured;
        public float RemainingSeconds { get; private set; }
        public bool Expired { get; private set; }

        public void ConfigureAuthority(CombatSimulationWorld world, float seconds)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (!float.IsFinite(seconds) || seconds < 0f) throw new ArgumentOutOfRangeException(nameof(seconds));
            if (_configured) throw new InvalidOperationException("每个落点预警只允许配置一次权威寿命。");
            // 登记先于设置状态；固定预算不足时直接拒绝，由生成事务撤销整份产出，不能退回真实时间 Destroy。
            _execution.Configure(this, CombatExecutionMode.Authority, world, CombatTickPhase.BeforePhysics);
            RemainingSeconds = seconds; Expired = false; _configured = true;
        }

        public void StepAuthority(float deltaTime, uint serverTick)
        {
            if (!_configured || Expired || !_execution.TryStep(deltaTime, serverTick)) return;
            RemainingSeconds = Mathf.Max(0f, RemainingSeconds - deltaTime);
            if (RemainingSeconds > 0f) return;
            Expired = true;
            _execution.Suspend(this);
            // Destroy 延至帧尾；先停用并归还槽位，阻止本 Rendering frame 后续补跑 Tick 再次清理同一对象。
            gameObject.SetActive(false);
            Destroy(gameObject);
        }

        private void OnEnable()
        { if (_configured && !Expired) _execution.Resume(this, CombatTickPhase.BeforePhysics); }
        private void OnDisable() => _execution.Suspend(this);
        private void OnDestroy() => _execution.Suspend(this);
    }
}
