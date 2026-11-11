using UnityEngine;
using Game.Core;

namespace Game.Combat
{
    /// <summary>
    /// 恢复型法力资源。用于限制法术释放频率，不表示弹药数量。
    /// Inspector 中的字段是初始配置（Authoring Data），_currentMana 才是一局游戏中持续变化的 Runtime State。
    /// 挂在施法者身上，由 SpellCaster 查询并扣费，由 UI 只读显示。
    /// </summary>
    public class ManaComponent : MonoBehaviour, ICombatSimulationParticipant
    {
        [SerializeField, Min(1f)] private float _maxMana = 100f;
        [SerializeField, Min(0f)] private float _manaRegenPerSecond = 20f;
        [SerializeField] private float _currentMana;
        [SerializeField] private bool _startFull = true;
        private CombatExecutionState _execution;
        public CombatExecutionMode ExecutionMode => _execution.Mode;

        public void ConfigureExecutionMode(CombatExecutionMode mode, CombatSimulationWorld world = null)
        { _execution.Configure(this, mode, world, CombatTickPhase.AfterPhysics); }
        public void StepAuthority(float deltaTime, uint serverTick)
        { if (_execution.TryStep(deltaTime, serverTick)) Regenerate(deltaTime); }
        public void ApplyReplicatedState(in ManaReplicaSnapshot snapshot)
        {
            if (_execution.Mode != CombatExecutionMode.Replica || !float.IsFinite(snapshot.CurrentMana)
                || !float.IsFinite(snapshot.MaxMana) || snapshot.MaxMana <= 0f) return;
            _maxMana = snapshot.MaxMana; _currentMana = Mathf.Clamp(snapshot.CurrentMana, 0f, _maxMana);
        }
        private void OnEnable() => _execution.Resume(this, CombatTickPhase.AfterPhysics);
        private void OnDisable() => _execution.Suspend(this);

        // 不使用 ScriptableObject 保存当前法力，否则多个角色可能会意外共享同一份可变状态。

        public float CurrentMana => _currentMana;
        public float MaxMana => _maxMana;
        public float ManaRegenPerSecond => _manaRegenPerSecond;
        // Clamp01 把结果限制到 [0, 1]，可直接交给进度条的 fillAmount 使用。
        public float Normalized => _maxMana > 0f ? Mathf.Clamp01(_currentMana / _maxMana) : 0f;

        private void Awake()
        {
            // Awake 在组件启用前调用一次，适合把 Inspector 数据校正为合法范围并初始化 Runtime State。
            // Mathf.Max 返回两个数中较大的一个，防止最大法力为 0、恢复速度为负数。
            _maxMana = Mathf.Max(1f, _maxMana);
            _manaRegenPerSecond = Mathf.Max(0f, _manaRegenPerSecond);
            _currentMana = _startFull ? _maxMana : 0f;
        }

        private void Update()
        {
            if (_execution.Mode != CombatExecutionMode.SinglePlayer) return;
            Regenerate(Time.deltaTime);
        }

        private void Regenerate(float deltaTime)
        {
            if (!_execution.AllowsGameplayWrites || _currentMana >= _maxMana || _manaRegenPerSecond <= 0f)
                return;

            // Update 每帧执行；“每秒恢复量 × deltaTime”可消除帧率差异。
            // Mathf.Min 保证恢复后的数值不会超过最大法力。
            _currentMana = Mathf.Min(_maxMana, _currentMana + _manaRegenPerSecond * deltaTime);
        }

        /// <summary>只查询当前是否支付得起，不修改法力；便于施法前先做完整性检查。</summary>
        public bool CanSpend(float amount)
        {
            // 外部传入负数时按 0 处理，避免“负消耗”反向增加法力。
            float cost = Mathf.Max(0f, amount);
            return _currentMana >= cost;
        }

        /// <summary>
        /// 以 all-or-nothing 方式支付法力：足够则一次扣除并返回 true，不足则保持原值并返回 false。
        /// </summary>
        public bool Spend(float amount)
        {
            // 原始扣费口与权威时钟共用动态门；查询 CanSpend 与 Awake 初值装配仍保持只读/初始化语义。
            if (!_execution.AllowsGameplayWrites || !float.IsFinite(amount)) return false;
            float cost = Mathf.Max(0f, amount);
            if (_currentMana < cost)
                return false;

            _currentMana -= cost;
            return true;
        }
    }
}
