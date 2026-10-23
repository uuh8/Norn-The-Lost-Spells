using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 滑落状态：角色站在超过 SlopeLimit 的陡坡时自动下滑。
    /// 不响应移动输入，只施加重力在坡面的切向分量作为滑落速度。
    /// </summary>
    public class PlayerSlidingState : PlayerStateBase
    {
        public PlayerSlidingState(PlayerControllerBase player) : base(player) { }

        public override void Enter()
        {
            // 进入滑落状态时重置垂直速度，保持角色贴坡面的向下压力
            _player.VerticalVelocity = -2f;
        }
        public override void Update()
        {
            HandleSlide();
            CheckTransition();
        }
        public override void Exit() { }

        private void HandleSlide()
        {
            Vector3 g = Physics.gravity;
            Vector3 n = _player.GroundChecker.GroundNormal;

            // 切向分量 = 重力 - 法线方向分量
            Vector3 slideDirection = g - Vector3.Dot(g, n) * n;

            // normalized 后乘 SlideSpeed：速度大小可控
            // 如果想要"坡越陡滑越快"的物理效果，去掉 .normalized，改成 slideDirection * slideMultiplier
            Vector3 velocity = slideDirection.normalized * _player.SlideSpeed;

            // 保留向下的微小压力，确保 CC 能持续检测到坡面（防止反复离地）
            // velocity.y = _player.VerticalVelocity;

            _player.MoveCharacter(velocity * _player.SimulationDeltaTime);
        }

        private void CheckTransition()
        {
            // 离地（滑出坡底悬空）→ 空中状态
            if (!_player.GroundChecker.IsGrounded)
            {
                _player.StateMachine.ChangeState(_player.AirborneState);
                return;
            }

            // 坡度恢复到 SlopeLimit 以内（滑到平地）→ 恢复地面状态
            if (_player.GroundChecker.GroundAngle <= _player.CharacterController.slopeLimit)
                _player.StateMachine.ChangeState(_player.GroundedState);
        }
    }
}
