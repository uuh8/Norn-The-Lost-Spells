using Game.Core;
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// Reaction Runtime 的离散生命周期事件。Rendering 只消费 Started/Paused/Resumed/Resolved/Cancelled，
    /// 不接收逐帧 Progress，避免 EventBus 成为高频状态同步通道。
    /// </summary>
    public struct ElementReactionEvent : IGameEvent
    {
        // TargetId 用 GameObject InstanceID 标识发生反应的载体，表现层据此过滤其他角色的事件。
        public int TargetId;
        public ElementReactionId Reaction;
        public ElementReactionPhase Phase;

        // 世界坐标让全局 Burst 表现不必反查 Target；TargetId=0 时表示反应发生在环境物质中。
        public Vector3 WorldPosition;

        // [0,1] 强度与预计时长只用于表现调制，不允许表现层据此反写战斗状态。
        public float NormalizedStrength;
        public float ExpectedDuration;
    }
}
