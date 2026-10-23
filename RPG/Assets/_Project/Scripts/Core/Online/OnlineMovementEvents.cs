namespace Game.Core
{
    /// <summary>Presentation只收到模式/连接视图，不获取角色Owner或Mirror引用。</summary>
    public struct OnlineMovementPresentationEvent : IGameEvent
    { public bool Active, MenuOpen, Frozen, Connected; }
    public struct OnlineMovementMenuRequestEvent : IGameEvent
    { public bool Open; }
    /// <summary>Development采样窗口暂停所有诊断JSON输出，避免探针IO污染全帧Profiler。</summary>
    public struct OnlineDiagnosticSamplingEvent : IGameEvent
    { public bool Active; }
}
