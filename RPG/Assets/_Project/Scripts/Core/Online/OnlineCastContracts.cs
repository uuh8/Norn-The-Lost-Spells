using UnityEngine;

namespace Game.Core
{
    /// <summary>施法只提交意图；不包含伤害、速度、Prefab 或资源余额。</summary>
    public struct OnlineCastIntent
    {
        public ulong CommandId;
        public uint ConnectionEpoch, ProgramRevision;
        public string WandInstanceId;
        public Vector3 ClaimedOrigin, AimPoint;
    }
    public enum OnlineCastResult : byte { Queued = 1, Released = 2, Rejected = 3 }
    public enum OnlineCastFailure : byte
    {
        None, NotReady, InvalidInput, NotOwned, StaleProgram, StaleCommand, Interrupted,
        ResourceRejected, EmptyOutput, UnsupportedMaterial
    }
    public struct OnlineCastReceipt
    {
        public ulong ActorId, CommandId;
        public uint ConnectionEpoch;
        public OnlineCastResult Result;
        public OnlineCastFailure Failure;
        public bool Airborne;
        public Vector3 AimPoint;
    }
    /// <summary>本机拒绝收据的只读提示；UI 不需要引用 Multiplayer 或资源事务。</summary>
    public struct OnlineCastFeedbackEvent : IGameEvent
    {
        public OnlineCastFailure Failure;
    }
    /// <summary>HUD 和复制读取的服务器结果，不把 Run 对象/可变库存泄漏给网络层。</summary>
    public struct OnlineCastResourceSnapshot
    {
        public string WandInstanceId;
        public uint ProgramRevision, PredictionSoundKey;
        public float CurrentMana, MaxMana, CooldownRemaining, CastInterval;
        public int RemainingCharges;
        public bool Unlimited;
    }
}
