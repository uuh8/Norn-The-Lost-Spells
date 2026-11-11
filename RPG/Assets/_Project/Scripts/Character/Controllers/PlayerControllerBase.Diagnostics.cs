using Game.Core;
using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 角色 Owner 的可选只读诊断出口。partial 让观察代码读取同一份私有状态，
    /// 避免为了探针公开输入包装器、复制 Current 缓存或改变正式运动入口。
    /// 本文件没有生命周期回调；只有显式调用才复制快照，不增加默认逐帧采样。
    /// </summary>
    public abstract partial class PlayerControllerBase
    {
        /// <summary>
        /// 每次都读取生产 Current；旧 Actor 上的观察接口也能发现后来绑定的新 Actor。
        /// 用 Unity Object 的空值语义处理已经销毁的实例，不把托管残留引用当成可用角色。
        /// </summary>
        public Transform CurrentInputActor => Current != null ? Current.transform : null;

        /// <summary>
        /// 仅复制当前门、Action 值与 CC 已有结果，不创建输入资产、不 Refresh 接地、
        /// 不推进运动，也不调用会消费 pending / release 栅栏的 SampleInput。
        /// ReadValue / IsPressed 读取 Input System 已处理的当前状态，不注入或消费按键边沿。
        /// CC velocity / collisionFlags 是最近一次 Move 的结果；Frame 只是本次读取帧号。
        /// </summary>
        public PlayerMovementReadOnlyObservation CaptureReadOnlyObservation()
        {
            bool inputAvailable = _inputActions != null;
            bool controllerAvailable = _characterController != null;
            return new PlayerMovementReadOnlyObservation
            {
                Frame = Time.frameCount,
                ActorInstanceId = gameObject.GetInstanceID(),
                Initialized = _initialized,
                InputSubscribed = _inputSubscribed,
                HasFocus = _hasFocus,
                InputSuppressed = _inputSuppressed,
                OnlineInputBlocked = _onlineInputBlocked,
                WaitJumpRelease = _waitJumpRelease,
                WaitDashRelease = _waitDashRelease,
                JumpPending = _jumpPressedPending,
                DashPending = _dashPressedPending,
                ActionAssetAvailable = inputAvailable,
                PlayerMapEnabled = inputAvailable && _inputActions.Player.enabled,
                MoveActionEnabled = inputAvailable && _inputActions.Player.Move.enabled,
                JumpActionEnabled = inputAvailable && _inputActions.Player.Jump.enabled,
                JumpHeld = inputAvailable && _inputActions.Player.Jump.IsPressed(),
                ActiveAndEnabled = isActiveAndEnabled,
                ControllerAvailable = controllerAvailable,
                ControllerEnabled = controllerAvailable && _characterController.enabled,
                ControllerGrounded = controllerAvailable && _characterController.isGrounded,
                ExecutionMode = _executionMode,
                MoveActionValue = inputAvailable ? _inputActions.Player.Move.ReadValue<Vector2>() : Vector2.zero,
                ProcessedMove = _moveInput,
                Position = transform.position,
                ControllerVelocity = controllerAvailable ? _characterController.velocity : Vector3.zero,
                ControllerCollisionFlags = controllerAvailable ? (int)_characterController.collisionFlags : 0,
                VerticalVelocity = VerticalVelocity
            };
        }
    }
}
