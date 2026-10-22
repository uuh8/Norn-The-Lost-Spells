using Game.Core;

namespace Game.Run
{
    /// <summary>
    /// Gameplay Portal 的低频生命周期事实。表现层据此播放 Open -> Idle 或 Close，
    /// 但不能反向决定 Collider 是否可进入。
    /// </summary>
    public enum PortalVfxState : byte
    {
        Opening = 0,
        Idle = 1,
        Closing = 2
    }

    public struct PortalStateChangedEvent : IGameEvent
    {
        public int PortalId;
        public PortalVfxState State;
    }
}
