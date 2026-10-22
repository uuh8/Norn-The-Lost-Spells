using UnityEngine;

namespace Game.Core
{
    public enum WorldActivitySourceKind : byte { Unit, Projectile }

    /// <summary>
    /// Combat 仅报告对象生命周期，不决定元素世界的 AOI 大小或模拟策略。
    /// Transform 是移动位置的只读来源；注册时发送一次，避免每帧广播位置。
    /// </summary>
    public struct WorldActivitySourceEvent : IGameEvent
    {
        public Transform Source;
        public WorldActivitySourceKind Kind;
        public bool Enabled;
    }
}
