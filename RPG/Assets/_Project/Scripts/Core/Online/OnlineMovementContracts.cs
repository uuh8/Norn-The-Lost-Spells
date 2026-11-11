using UnityEngine;

namespace Game.Core
{
    /// <summary>执行职责属于 Gameplay 契约；领域对象不需要引用 Mirror 才能决定自己是否推进。</summary>
    public enum PlayerExecutionMode : byte
    {
        SinglePlayer,
        Authority,
        Prediction,
        Replica
    }

    /// <summary>稳定的运动阶段编号，不把进程内 State 对象引用放进 Snapshot。</summary>
    public enum PlayerLocomotionKind : byte
    {
        Grounded,
        Airborne,
        Sliding,
        Dash
    }

    /// <summary>一次固定步长的输入。边沿只属于这一帧，ViewYaw 是世界 Y 轴角度（度），不传 Camera 引用。</summary>
    public struct PlayerSimulationFrame
    {
        public uint Sequence;
        public uint ConnectionEpoch;
        public Vector2 MoveAxis;
        public float ViewYaw;
        public bool JumpPressed;
        public bool JumpHeld;
        public bool DashPressed;
        public bool InputSuppressed;

        public PlayerSimulationFrame(uint sequence, uint connectionEpoch, Vector2 moveAxis, float viewYaw,
            bool jumpPressed, bool jumpHeld, bool dashPressed, bool inputSuppressed)
        {
            Sequence = sequence;
            ConnectionEpoch = connectionEpoch;
            MoveAxis = moveAxis;
            ViewYaw = viewYaw;
            JumpPressed = jumpPressed;
            JumpHeld = jumpHeld;
            DashPressed = dashPressed;
            InputSuppressed = inputSuppressed;
        }
    }

    /// <summary>角色 Owner 导出的完整运动状态。身份／Tick／Ack 留在传输外层，Camera 与表现状态不回滚。</summary>
    public struct PlayerMovementSnapshot
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 TargetFacing;
        public float VerticalVelocity;
        public PlayerLocomotionKind Locomotion;
        public bool IsGrounded;
        public Vector3 GroundNormal;
        public float GroundAngle;
        public float CoyoteTimeCounter;
        public float JumpBufferCounter;
        public float AttackBufferCounter;
        public float AttackCooldownCounter;
        public float DashCooldownCounter;
        public float DashBufferCounter;
        public bool IsAirborneForDash;
        public Vector3 DashDirection;
        public float DashElapsed;
        public int ExtraJumpsRemaining;
        public int AirDashesRemaining;
        public bool JumpCutArmed;
    }

    /// <summary>Driver 只推进预先注册的参与者；不会每 Tick 扫描 Scene，也不承担领域规则。</summary>
    public interface IAuthorityTickParticipant
    {
        void StepAuthority(float deltaTime, uint serverTick);
    }

    /// <summary>Prediction 只依赖运动 Owner 的受控入口，不知道 Unity Controller 的具体实现或网络身份。</summary>
    public interface IPlayerMovementSimulation
    {
        PlayerMovementSnapshot CaptureMovementSnapshot();
        void RestoreMovementSnapshot(in PlayerMovementSnapshot snapshot);
        void Simulate(in PlayerSimulationFrame input, float deltaTime, bool replay = false);
    }

    /// <summary>
    /// 显式诊断的可选只读口，与会消费边沿的SampleInput/推进模拟的接口分离。
    /// Run只依赖Core；InputSystem私有门由Character复制为值，不把输入资产或控制权限传出。
    /// 接口存在不代表启用采样，没有Update/tap/额外Physics查询。
    /// </summary>
    public interface IPlayerMovementReadOnlyObservation
    {
        Transform CurrentInputActor { get; }
        PlayerMovementReadOnlyObservation CaptureReadOnlyObservation();
    }

    /// <summary>
    /// 同一主线程读取时刻的值。Action值是InputAction当前值，不宣称OS按键已送达。
    /// CC flags/velocity属于最近一次Move，读取帧号不是Move发生帧；未采集请求位移。
    /// </summary>
    [System.Serializable]
    public struct PlayerMovementReadOnlyObservation
    {
        public int Frame, ActorInstanceId;
        public bool Initialized, InputSubscribed, HasFocus, InputSuppressed, OnlineInputBlocked;
        public bool WaitJumpRelease, WaitDashRelease, JumpPending, DashPending;
        public bool ActionAssetAvailable, PlayerMapEnabled, MoveActionEnabled, JumpActionEnabled, JumpHeld;
        public bool ActiveAndEnabled, ControllerAvailable, ControllerEnabled, ControllerGrounded;
        public PlayerExecutionMode ExecutionMode;
        public Vector2 MoveActionValue, ProcessedMove;
        public Vector3 Position, ControllerVelocity;
        public int ControllerCollisionFlags;
        public float VerticalVelocity;
    }

    /// <summary>AI 读取有界角色列表；只读查询不允许跳过角色 Owner 修改 HP／连接状态。</summary>
    public interface ICombatActorRosterReadOnly
    {
        int Count { get; }
        bool TryGetActor(int index, out CombatActorSnapshot actor);
    }

    public struct CombatActorSnapshot
    {
        public ulong ActorId;
        public Vector3 Position;
        public int TeamId;
        public bool IsAlive;
        public bool IsConnected;
        public bool Targetable;
        // 仅本进程的只读映射，用于兼容已有瞄准接缝；任何 Wire Schema 都不得序列化它。
        public Transform Transform;
    }
}
