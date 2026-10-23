using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 接地状态。负责地面移动、转向，以及 Dash -> Attack -> Jump -> 离地 -> 超坡的转换优先级。
    /// 攻击分支只调用 PlayerControllerBase.TryStartAttack；共享 State 不依赖具体角色或法术模块。
    /// </summary>
    public class PlayerGroundedState : PlayerStateBase
    {
        public PlayerGroundedState(PlayerControllerBase player) : base(player)
        {
        }

        public override void Enter()
        {
            // 清理旧跳跃动画请求
            _player.ClearPendingJumpAnimation();

            // 给角色一个很小的向下速度，使 CharacterController 持续贴住地面。
            _player.VerticalVelocity = -2f;

            // “落地”可以重置空中能力：二段跳和空中 Dash 各恢复一次。
            _player.ResetAirActionBudget();

            // 标记下一次冲刺来自地面，因为空中冲刺和地面冲刺使用不同动画
            _player.IsAirborneForDash = false;

            if (_player.JumpBufferCounter > 0f)
                ExecuteJump(); // Jump Buffer：如果空中按了跳跃键、buffer 还没过期，落地立刻起跳
        }

        public override void Update()
        {
            // 顺序固定：
            HandleGravity(); // 1. 先算重力（更新垂直速度）
            HandleMovement(); // 2. 再移动（把垂直速度打包进 Move）
            base.HandleRotation(); // 3. 再转向（不影响位移计算）
            CheckTransition(); // 4. 最后检测是否要切状态
        }

        public override void Exit()
        {
        }

        #region 处理流程函数

        private void HandleGravity()
        {
            // 接地状态不会继续累积真实重力
            if (_player.VerticalVelocity < 0f)
                _player.VerticalVelocity = -2f;
        }

        private void HandleMovement()
        {
            // _player.MoveDirection 是一个 Vector3 方向向量，MoveSpeed是标量
            // StatusMoveSpeedMultiplier 也是标量，是一个比例，比如减速30%就是0.7（比如被粘稠状态了）
            // 这里体现了一个很常见的 Gameplay 设计：方向、基础属性和状态修正分开保存。
            Vector3 velocity =
                _player.MoveDirection
                * _player.MoveSpeed
                * _player.StatusMoveSpeedMultiplier;   // 水平速度

            velocity.y = _player.VerticalVelocity;  // velocity.y 替换为独立维护的垂直速度
            // CharacterController.Move 接收“本帧位移”而不是速度，所以速度必须乘 deltaTime。
            // Move 会执行胶囊体碰撞约束，但不会像 Rigidbody 一样自动施加重力；垂直速度由 Gameplay 自己维护。
            _player.MoveCharacter(velocity * _player.SimulationDeltaTime);
        }

        private void CheckTransition()
        {
            // 冲刺输入（最高优先级：Dash → Attack → Jump）。
            // 1. Dash：最高优先级  要求：
            // - 存在尚未过期的冲刺输入；
            // - 冲刺冷却已经结束。
            if (_player.DashBufferCounter > 0f && _player.DashCooldownCounter <= 0f)
            {
                _player.IsAirborneForDash = false;
                _player.StateMachine.ChangeState(_player.DashState);
                return;
            }

            // 2. Attack：由具体角色决定
            if (_player.ExecutionMode == Game.Core.PlayerExecutionMode.SinglePlayer && _player.TryStartAttack())
                return;

            // 3. Jump：消费跳跃缓冲
            if (_player.JumpBufferCounter > 0f)
            {
                ExecuteJump();
                return; // 已经切状态，不再往下走
            }

            // 4. 自然离地：进入 Airborne
            // 此时开启 Coyote Time，给玩家一小段宽限期仍然可以跳
            if (!_player.GroundChecker.IsGrounded)
            {
                _player.CoyoteTimeCounter = _player.CoyoteTime;
                _player.StateMachine.ChangeState(_player.AirborneState);
                return;
            }

            // 5. 坡度过大：进入 Sliding
            if (_player.GroundChecker.GroundAngle > _player.CharacterController.slopeLimit)
            {
                _player.StateMachine.ChangeState(_player.SlidingState);
            }
        }

        #endregion

        #region 功能函数

        private void ExecuteJump()
        {
            _player.PlayJumpStartedFeedback();              // 播放起跳动画和 VFX
            _player.VerticalVelocity = _player.JumpForce;   // 设置起跳速度
            _player.ArmJumpCut();                           // 允许本次 Jump Cut
            _player.JumpBufferCounter = 0f;                 // 消耗 Jump Buffer
            _player.CoyoteTimeCounter = 0f;                 // // 跳跃是主动起跳，不给 Coyote Time
            _player.StateMachine.ChangeState(_player.AirborneState);
        }

        #endregion
    }
}
