namespace Game.NetworkProtocol
{
    /// <summary>手写partial清理全部输入字段；MergeFrom不会自动清除下一包省略的值。</summary>
    public sealed partial class MovementInputMessage
    {
        public void ResetForReuse()
        {
            ProtocolVersion = 0;
            ConnectionEpoch = 0;
            Sequence = 0;
            MoveX = 0;
            MoveY = 0;
            ViewYaw = 0;
            JumpPressed = false;
            JumpHeld = false;
            DashPressed = false;
            InputSuppressed = false;
            SimulationHash = 0;
            // 私有字段来自锁定的protoc生成代码；升级Schema后必须同步清理并重跑完整字段测试。
            _unknownFields = null;
        }
    }

    /// <summary>每Actor平铺状态对象的复用边界，不在Reset中建立数组、集合或嵌套消息。</summary>
    public sealed partial class MovementStateMessage
    {
        public void ResetForReuse()
        {
            ProtocolVersion = 0;
            ActorId = 0;
            ConnectionEpoch = 0;
            ServerTick = 0;
            AcknowledgedInput = 0;
            IsEnemy = false;
            Connected = false;
            Targetable = false;
            PositionX = 0;
            PositionY = 0;
            PositionZ = 0;
            Yaw = 0;
            VerticalVelocity = 0;
            TargetFacingX = 0;
            TargetFacingY = 0;
            TargetFacingZ = 0;
            Locomotion = 0;
            IsGrounded = false;
            GroundNormalX = 0;
            GroundNormalY = 0;
            GroundNormalZ = 0;
            GroundAngle = 0;
            CoyoteTimeCounter = 0;
            JumpBufferCounter = 0;
            AttackBufferCounter = 0;
            AttackCooldownCounter = 0;
            DashCooldownCounter = 0;
            DashBufferCounter = 0;
            IsAirborneForDash = false;
            DashDirectionX = 0;
            DashDirectionY = 0;
            DashDirectionZ = 0;
            DashElapsed = 0;
            ExtraJumpsRemaining = 0;
            AirDashesRemaining = 0;
            JumpCutArmed = false;
            SimulationHash = 0;
            Frozen = false;
            SnapshotSequence = 0;
            _unknownFields = null;
        }
    }
}
