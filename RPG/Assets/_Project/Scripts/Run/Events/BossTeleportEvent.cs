using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// Boss Teleport 的低频 Gameplay 事实。事件只携带阶段和位置，
    /// 不携带 Prefab 或 UI 类型，避免 Game.Character 反向依赖表现层。
    /// </summary>
    public enum BossTeleportStage : byte
    {
        Telegraph = 0,
        Departed = 1,
        Arrived = 2,
        Cancelled = 3,
    }

    public struct BossTeleportEvent : IGameEvent
    {
        public int BossId;
        public BossTeleportStage Stage;
        public Vector3 SourcePosition;
        public Vector3 DestinationPosition;
    }
}
