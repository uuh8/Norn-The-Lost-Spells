namespace Game.Core
{
    /// <summary>
    /// 用户设置快照发生真实变化后同步发布。
    /// Character、UI 等上层模块各自消费，不需要互相建立反向引用。
    /// </summary>
    public struct UserSettingsChangedEvent : IGameEvent
    {
        public UserSettingsValues Values;
    }
}
