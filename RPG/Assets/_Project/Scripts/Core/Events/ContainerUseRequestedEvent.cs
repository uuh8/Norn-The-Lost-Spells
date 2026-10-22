using UnityEngine;

namespace Game.Core
{
    public enum ContainerUseKind : byte { Pour, Throw, ReleaseSolid }
    public struct ContainerUseRequestedEvent : IGameEvent
    {
        public string InventoryId, WorldId, SourceId, ItemId;
        public ulong RequestId, Budget;
        public uint ExpectedVersion;
        public ContainerUseKind Kind;
        public Vector3 ActorPosition, TargetPosition, ThrowVelocity;
        public float Radius;
    }
    public struct ContainerUseResultEvent : IGameEvent
    {
        public MaterialTransferResultEvent Transfer;
        public ContainerUseKind Kind;
        public string FlightId;
        public bool Success;
    }
    /// <summary>同步主线程来源预约；World 只提交已锁定的真实来源，不持有库存类型。</summary>
    public interface IMaterialTransferSource
    {
        bool TryReserve(MaterialAmount[] amounts, int count, out MaterialTransferFailure failure);
        void Commit();
        void Cancel();
    }
    public sealed class MaterialDepositExchange
    {
        public string WorldId;
        public Vector3 Position;
        public float Radius;
        public MaterialAmount[] Amounts;
        public int Count;
        public IMaterialTransferSource Source;
        public MaterialTransferFailure Failure;
        public ulong Transferred;
        public bool Handled;
    }
    public struct MaterialWorldDepositRequestedEvent : IGameEvent { public MaterialDepositExchange Exchange; }
}
