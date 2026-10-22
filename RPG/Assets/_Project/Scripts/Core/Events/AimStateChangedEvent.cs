namespace Game.Core
{
    /// <summary>
    /// 瞄准状态变化（弓箭手蓄力进入/退出瞄准）。表现层（准心 UI）据此显隐，
    /// gameplay 与 UI 经 EventBus 解耦——发布方 Game.Character，订阅方 Game.Rendering。
    /// </summary>
    public struct AimStateChangedEvent : IGameEvent
    {
        public bool Active; // true=进入瞄准（显示准心）；false=退出
    }
}
