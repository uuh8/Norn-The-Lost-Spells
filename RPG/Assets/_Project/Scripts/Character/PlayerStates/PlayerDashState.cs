using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 冲刺状态。地面触发、不可被打断、按固定时长沿"进入瞬间角色朝向"做水平位移。
    /// 1. Enter：锁定冲刺方向(=进入瞬间 transform.forward) + 消耗缓冲输入 + CrossFade 冲刺动画
    /// 2. Update：按 DashSpeed 沿锁定方向水平位移 + 贴地压力 + 计时；时间到 → 回移动状态
    /// 3. Exit：启动冷却（DashCooldownCounter = DashCooldown）—— 冷却启动的唯一责任点。
    ///    任何离开冲刺的路径（自然结束 / 未来受击打断）都经 ChangeState→Exit，保证冷却必启动。
    /// </summary>
    public class PlayerDashState : PlayerStateBase
    {
        // 冲刺目标"动画状态(state)"名已数据驱动：由 PlayerControllerBase._dashStateName 序列化、
        // Awake 预 hash 成 DashStateHash 暴露出来（仿 AttackDefinition.AnimationStateName）。
        // 各角色在自己的 Inspector 填自己 Animator Controller 里的实际节点名，不再硬编码双手剑专属名。
        private const float CrossFadeDuration = 0.1f; // 进入冲刺的 CrossFade 固定时长（秒）

        private Vector3 _dashDirection; // 进入瞬间锁定的世界空间冲刺方向（已去 y、归一化）
        private float _elapsed;         // 本次冲刺已过时间（秒）

        public Vector3 Direction => _dashDirection;
        public float Elapsed => _elapsed;

        /// <summary>恢复正在进行的 Dash，不重新起手、消费 Buffer 或修改垂直动量。</summary>
        public void RestoreSnapshot(Vector3 direction, float elapsed)
        {
            _dashDirection = direction;
            _elapsed = Mathf.Max(0f, elapsed);
        }

        public PlayerDashState(PlayerControllerBase player) : base(player) { }

        #region 状态机函数

        public override void Enter()
        {
            // 锁定冲刺方向 = 进入瞬间角色朝向（轨道相机风格，单一前向冲刺，本轮不做八方向）
            _dashDirection = _player.transform.forward;
            _dashDirection.y = 0f;
            _dashDirection.Normalize();

            _elapsed = 0f;
            _player.DashBufferCounter = 0f; // 消耗起手输入，防冲刺中/同帧重复触发

            // Air Dash 暂停垂直位移，但不应在结束时无条件恢复进入前的高速下坠。
            // Retention 暴露给 Inspector：0 清空动量，1 保持旧行为，中间值用于保留部分惯性。
            if (_player.IsAirborneForDash)
            {
                _player.VerticalVelocity = AirDashVerticalVelocityPolicy.Apply(
                    _player.VerticalVelocity,
                    _player.AirDashVerticalVelocityRetention);
            }

            // CrossFade 进入冲刺动画
            // 地面冲刺和空中冲刺用的不同的动画，因此这儿要做区分
            int stateHash = _player.IsAirborneForDash
                ? _player.AirDashStateHash
                : _player.DashStateHash;
            if (stateHash != 0 && _player.CanPlayMovementFeedback && _player.Animator != null &&
                _player.Animator.runtimeAnimatorController != null)
            {
                _player.Animator.CrossFadeInFixedTime(stateHash, CrossFadeDuration, 0);
            }
        }

        public override void Update()
        {
            HandleDashMovement();

            _elapsed += _player.SimulationDeltaTime;
            // 总时长 = 启动延迟 + 位移时长：延迟段只播动画/贴地，位移段保持原距离（DashSpeed × DashDuration）
            if (_elapsed >= _player.DashMoveDelay + _player.DashDuration)
                TransitionToMovement();
        }

        public override void Exit()
        {
            // 冷却启动唯一责任点：从冲刺结束起算（决策③）。经 ChangeState 的任何退出路径都会跑到这里，冷却必然启动。
            _player.DashCooldownCounter = _player.DashCooldown;

            // 刻意不清 DashBufferCounter（区别于 PlayerAttackState.Exit 清 AttackBufferCounter）：
            // 冲刺触发闸门是"DashBufferCounter>0 且 DashCooldownCounter<=0"的双重条件，
            // 而本方法刚把冷却设为正数，残留缓冲会被冷却挡住、绝不会漏触发新冲刺。
            // 因此无需显式清缓冲——这是有意为之，不是漏写。
        }

        #endregion

        #region 处理流程函数

        private void HandleDashMovement()
        {
            // 垂直：沿用 Attack 的 -2 贴地压力，保持 CC 贴地与离地判定有效（决策⑤）
            if (!_player.IsAirborneForDash && _player.VerticalVelocity < 0f)
                _player.VerticalVelocity = -2f;

            // 启动延迟内只贴地、不水平位移：等翻滚动画起势，避免"先闪后翻"。延迟过后才按锁定方向位移。
            float horizontalSpeed = _elapsed >= _player.DashMoveDelay ? _player.DashSpeed : 0f;

            // 水平：锁定方向 × 速度。冲刺期间不响应移动输入、不转向（方向锁定）
            Vector3 velocity = _dashDirection * horizontalSpeed;
            // 空中 Dash 暂停垂直速度但不清零，结束后继续原有抛物线；地面 Dash 保留贴地压力。
            velocity.y = _player.IsAirborneForDash ? 0f : _player.VerticalVelocity;
            _player.MoveCharacter(velocity * _player.SimulationDeltaTime);
        }

        #endregion

        #region 功能函数

        /// <summary>冲刺自然结束：用落点判定决定回地面还是空中（决策⑤离地边界由此吸收）。</summary>
        private void TransitionToMovement()
        {
            if (_player.GroundChecker.IsGrounded)
                _player.StateMachine.ChangeState(_player.GroundedState);
            else
                _player.StateMachine.ChangeState(_player.AirborneState);
        }

        #endregion
    }

    /// <summary>
    /// Air Dash 进入瞬间的垂直动量策略。保持纯函数，方便验证正向上升与负向下落使用同一语义。
    /// </summary>
    public static class AirDashVerticalVelocityPolicy
    {
        /// <summary>
        /// incomingVelocity：进入 Air Dash 前的垂直速度。
        /// retention：保留比例，0-1。0：完全停止；1：保持原速。
        /// </summary>
        public static float Apply(float incomingVelocity, float retention)
        {
            return incomingVelocity * Mathf.Clamp01(retention);
        }
    }
}
