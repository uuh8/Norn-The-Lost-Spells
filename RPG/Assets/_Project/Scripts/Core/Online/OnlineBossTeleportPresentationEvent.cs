using UnityEngine;

namespace Game.Core
{
    /// <summary>权威 Boss 传送的低频表现投影；位置已经由 Server 的 NavMesh/Physics 检验，客户端不得据此自行位移。</summary>
    public struct OnlineBossTeleportPresentationEvent : IGameEvent
    {
        public ulong ActorId;
        public uint Stage; // 0=Telegraph，1=Departed，2=Arrived，3=Cancelled。
        public Vector3 SourcePosition, DestinationPosition;
    }
}
