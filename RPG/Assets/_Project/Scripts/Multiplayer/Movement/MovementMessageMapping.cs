using Game.Core;
using Game.NetworkProtocol;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>网络身份留在外层，运动Owner只接收值Snapshot；不把protobuf对象借用到下一帧。</summary>
    public struct MovementStateFrame
    {
        public ulong ActorId, SimulationHash;
        public uint ConnectionEpoch, ServerTick, AcknowledgedInput, SnapshotSequence;
        public bool IsEnemy, Connected, Targetable, Frozen;
        public PlayerMovementSnapshot Movement;
    }

    public static class MovementMessageMapping
    {
        public static PlayerSimulationFrame Read(MovementInputMessage m) => new PlayerSimulationFrame(
            m.Sequence, m.ConnectionEpoch, new Vector2(m.MoveX, m.MoveY), m.ViewYaw,
            m.JumpPressed, m.JumpHeld, m.DashPressed, m.InputSuppressed);

        public static void Write(in PlayerSimulationFrame f, ulong hash, MovementInputMessage m)
        {
            m.ResetForReuse(); m.ProtocolVersion = ProtocolCompatibility.CurrentVersion;
            m.Sequence = f.Sequence; m.ConnectionEpoch = f.ConnectionEpoch; m.MoveX = f.MoveAxis.x; m.MoveY = f.MoveAxis.y;
            m.ViewYaw = f.ViewYaw; m.JumpPressed = f.JumpPressed; m.JumpHeld = f.JumpHeld;
            m.DashPressed = f.DashPressed; m.InputSuppressed = f.InputSuppressed; m.SimulationHash = hash;
        }
        public static MovementStateFrame Read(MovementStateMessage m)
        {
            return new MovementStateFrame
            {
                ActorId = m.ActorId, SimulationHash = m.SimulationHash, ConnectionEpoch = m.ConnectionEpoch,
                ServerTick = m.ServerTick, AcknowledgedInput = m.AcknowledgedInput, SnapshotSequence = m.SnapshotSequence,
                IsEnemy = m.IsEnemy, Connected = m.Connected, Targetable = m.Targetable, Frozen = m.Frozen,
                Movement = new PlayerMovementSnapshot
                {
                    Position = new Vector3(m.PositionX, m.PositionY, m.PositionZ), Rotation = Quaternion.Euler(0, m.Yaw, 0),
                    TargetFacing = new Vector3(m.TargetFacingX, m.TargetFacingY, m.TargetFacingZ), VerticalVelocity = m.VerticalVelocity,
                    Locomotion = (PlayerLocomotionKind)m.Locomotion, IsGrounded = m.IsGrounded,
                    GroundNormal = new Vector3(m.GroundNormalX, m.GroundNormalY, m.GroundNormalZ), GroundAngle = m.GroundAngle,
                    CoyoteTimeCounter = m.CoyoteTimeCounter, JumpBufferCounter = m.JumpBufferCounter,
                    AttackBufferCounter = m.AttackBufferCounter, AttackCooldownCounter = m.AttackCooldownCounter,
                    DashCooldownCounter = m.DashCooldownCounter, DashBufferCounter = m.DashBufferCounter,
                    IsAirborneForDash = m.IsAirborneForDash, DashDirection = new Vector3(m.DashDirectionX, m.DashDirectionY, m.DashDirectionZ),
                    DashElapsed = m.DashElapsed, ExtraJumpsRemaining = (int)m.ExtraJumpsRemaining,
                    AirDashesRemaining = (int)m.AirDashesRemaining, JumpCutArmed = m.JumpCutArmed
                }
            };
        }
        public static void Write(in MovementStateFrame f, MovementStateMessage m)
        {
            m.ResetForReuse(); var s = f.Movement;
            m.ProtocolVersion = ProtocolCompatibility.CurrentVersion; m.ActorId = f.ActorId; m.SimulationHash = f.SimulationHash;
            m.ConnectionEpoch = f.ConnectionEpoch; m.ServerTick = f.ServerTick; m.AcknowledgedInput = f.AcknowledgedInput;
            m.SnapshotSequence = f.SnapshotSequence; m.IsEnemy = f.IsEnemy; m.Connected = f.Connected; m.Targetable = f.Targetable; m.Frozen = f.Frozen;
            m.PositionX = s.Position.x; m.PositionY = s.Position.y; m.PositionZ = s.Position.z; m.Yaw = s.Rotation.eulerAngles.y;
            m.VerticalVelocity = s.VerticalVelocity; m.TargetFacingX = s.TargetFacing.x; m.TargetFacingY = s.TargetFacing.y; m.TargetFacingZ = s.TargetFacing.z;
            m.Locomotion = (uint)s.Locomotion; m.IsGrounded = s.IsGrounded;
            m.GroundNormalX = s.GroundNormal.x; m.GroundNormalY = s.GroundNormal.y; m.GroundNormalZ = s.GroundNormal.z; m.GroundAngle = s.GroundAngle;
            m.CoyoteTimeCounter = s.CoyoteTimeCounter; m.JumpBufferCounter = s.JumpBufferCounter;
            m.AttackBufferCounter = s.AttackBufferCounter; m.AttackCooldownCounter = s.AttackCooldownCounter;
            m.DashCooldownCounter = s.DashCooldownCounter; m.DashBufferCounter = s.DashBufferCounter;
            m.IsAirborneForDash = s.IsAirborneForDash; m.DashDirectionX = s.DashDirection.x; m.DashDirectionY = s.DashDirection.y; m.DashDirectionZ = s.DashDirection.z;
            m.DashElapsed = s.DashElapsed; m.ExtraJumpsRemaining = (uint)s.ExtraJumpsRemaining;
            m.AirDashesRemaining = (uint)s.AirDashesRemaining; m.JumpCutArmed = s.JumpCutArmed;
        }
    }
}
