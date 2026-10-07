using UnityEngine;
using Game.Core;

namespace Game.Combat
{
    /// <summary>目标生命值归零后同帧派发。</summary>
    public struct DeathEvent : IGameEvent
    {
        public int TargetId;        // 被攻击者Id
        public int AttackerId;      // 攻击者Id
        public Vector3 Position;
    }
}
