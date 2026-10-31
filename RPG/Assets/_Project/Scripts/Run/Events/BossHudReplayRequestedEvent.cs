using Game.Core;

namespace Game.Run
{
    /// <summary>
    /// 共用 HUD 晚于 Boss 初始化或重新绑定时，请求当前世界的只读初始化快照。
    /// 不保存全局 Boss 引用，不请求开战/阶段切换；WorldSceneHandle 只在本 Unity 进程内有意义。
    /// 联机 Client 由本地 Replica 应答，不能把 Dedicated Server 的 Scene handle 当作客户端场景身份。
    /// </summary>
    public struct BossHudReplayRequestedEvent : IGameEvent
    {
        public int WorldSceneHandle;
    }
}
