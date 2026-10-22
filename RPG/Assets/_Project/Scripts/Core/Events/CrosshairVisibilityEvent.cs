namespace Game.Core
{
    /// <summary>
    /// 准心显隐（远程角色全程常驻准心）。与 AimStateChangedEvent 分离：后者只管"蓄力瞄准取景"(把角色推到左下的运镜)，
    /// 本事件只管"准心是否显示"。远程控制器(弓手/法师)在进入/Start 发 true、禁用时发 false。
    /// gameplay 与 UI 经 EventBus 解耦——发布方 Game.Character，订阅方 Game.Rendering(CrosshairUI)。
    /// </summary>
    public struct CrosshairVisibilityEvent : IGameEvent
    {
        public bool Visible; // true=显示准心；false=隐藏
    }
}
