namespace Game.Core
{
    /// <summary>Presentation只收到模式/连接视图，不获取角色Owner或Mirror引用。</summary>
    public struct OnlineMovementPresentationEvent : IGameEvent
    {
        // Scene handle是本进程的生命周期标识，不作为网络身份。常驻HUD用它排除已卸载World的迟到视图。
        public int WorldSceneHandle;
        public bool Active, MenuOpen, Frozen, Connected;
        public bool InputCaptured;
    }
    /// <summary>
    /// 纯只读的视图重放请求。HUD晚绑定或重新启用时可能错过Frozen变化，因而在生命周期边界请求一次当前事实。
    /// World按Scene归属回应；请求不改变暂停、输入、连接或战斗状态，也不需要UI反向依赖Multiplayer。
    /// </summary>
    public struct OnlineMovementViewRequestedEvent : IGameEvent
    { public int WorldSceneHandle; }
    public struct OnlineMovementMenuRequestEvent : IGameEvent
    { public bool Open; }
    /// <summary>Development采样窗口暂停所有诊断JSON输出，避免探针IO污染全帧Profiler。</summary>
    public struct OnlineDiagnosticSamplingEvent : IGameEvent
    { public bool Active; }
}
