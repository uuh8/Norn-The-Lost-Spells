using System;
using UnityEngine;

namespace Game.Core
{
    /// <summary>
    /// 完整检查点的Coin事实，与仅发送活动币的Ground表现表不同。最多保留原12个NPC身份，
    /// Collected和CollectorActorId保留单赢家墓碑；不代表永久钱包已结算，不向外暴露Run写服务。
    /// </summary>
    [Serializable]
    public struct OnlineCoinCheckpointRecord
    {
        public ulong Id, CollectorActorId;
        public long Amount;
        public Vector3 Position;
        public bool Collected;
    }
}
