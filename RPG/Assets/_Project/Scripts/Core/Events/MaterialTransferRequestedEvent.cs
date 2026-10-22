using UnityEngine;

namespace Game.Core
{
    public enum MaterialTransferFailure : byte
    {
        None, InvalidRequest, StaleVersion, DuplicateRequest, WrongOwnership, ResourceBusy,
        Capacity, NoMaterial, UnsupportedBackend, SamplingLimit, WorldUnavailable, GeometryRejected,
        IncompatibleMaterial
    }

    /// <summary>目标只在主线程同步命令内预约/提交；Buffer 在调用返回前有效，不允许保存引用。</summary>
    public interface IMaterialTransferDestination
    {
        ushort AllowedMaterials { get; }
        ulong FreeCapacity { get; }
        bool TryReserve(MaterialAmount[] amounts, int count, out MaterialTransferFailure failure);
        void Commit();
        void Cancel();
    }
    public struct MaterialTransferRequestedEvent : IGameEvent
    {
        public string InventoryId, WorldId, SourceId, ItemId, BackpackId, MaterialContentId;
        public ulong RequestId, Budget;
        public uint ExpectedVersion;
        public Vector3 CollectorPosition, SampleCenter;
        public float Radius;
        public bool SolidItem;
    }
    public sealed class MaterialTransferExchange
    {
        public MaterialTransferRequestedEvent Request;
        public IMaterialTransferDestination Destination;
        public MaterialTransferResultEvent Result;
        public bool Handled;
    }
    public struct MaterialWorldTransferRequestedEvent : IGameEvent
    {
        public MaterialTransferExchange Exchange;
    }
}
