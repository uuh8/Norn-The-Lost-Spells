namespace Game.Core
{
    /// <summary>Run请求黑幕，UI独立拥有Canvas；场景路由不能反向依赖表现Assembly。</summary>
    public struct PortalFadeRequestedEvent : IGameEvent
    {
        public bool Black;
        public float Duration;
    }
}
