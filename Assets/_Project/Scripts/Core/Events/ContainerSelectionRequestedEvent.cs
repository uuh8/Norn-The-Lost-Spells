namespace Game.Core
{
    /// <summary>道具格请求选择实际容器；Character复核背包归属后通知原采集/释放链。</summary>
    public struct ContainerSelectionRequestedEvent : IGameEvent{public string InventoryId,ItemId;}
}
