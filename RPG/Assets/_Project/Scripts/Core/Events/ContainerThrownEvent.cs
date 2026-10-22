namespace Game.Core
{
    /// <summary>先准备不可见代理，资产迁到在途后再 Launch；失败取消不会消耗瓶。</summary>
    public interface IContainerThrowProxy { void Launch(); void Cancel(); }
    public sealed class ContainerThrowExchange
    {
        public ContainerUseRequestedEvent Request;
        public string FlightId;
        public IContainerThrowProxy Proxy;
    }
    public struct ContainerThrownEvent : IGameEvent { public ContainerThrowExchange Exchange; }
}
