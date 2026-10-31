using UnityEngine;

namespace Game.Core
{
    /// <summary>
    /// 正式副本的材料场景装配边界。Multiplayer只看Core合同，不向上依赖ElementField或Rendering。
    /// 调用方必须先保持整棵世界Root inactive，完成TryConfigure，再激活Root；Awake发生后不能改所有权。
    /// AuthoritySimulation/Source/Buffer只在Authority配置中存在；ReplicaInstaller只在Replica中存在。
    /// 网络身份、数据发送和ACK仍由连接Owner负责，本组件不通过本地Token或事件猜测已认证玩家。
    /// </summary>
    public interface IOnlineMaterialWorldBinding
    {
        bool TryConfigure(WorldExecutionMode mode, ulong epoch);
        IWorldAuthoritySimulation AuthoritySimulation { get; }
        IWorldReplicationSource ReplicationSource { get; }
        WorldReplicaBuffer CaptureBuffer { get; }
        IWorldChunkPayloadInstaller ReplicaInstaller { get; }
        bool TrySetActor(int slot, ulong actorId, Vector3 position);
        void ClearActor(int slot);
        void SetSuspended(bool suspended);
        bool IsReady { get; }
        bool Faulted { get; }
        /// <summary>停表、移除Actor引用并解绑Source；场景卸载和配置失败都应调用，允许重复调用。</summary>
        void Detach();
    }
}
