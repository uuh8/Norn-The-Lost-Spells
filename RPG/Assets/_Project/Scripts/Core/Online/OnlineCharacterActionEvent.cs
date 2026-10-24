namespace Game.Core
{
    /// <summary>权威角色实际起手后的纯表现事实；动画只读消费，不能据此运行 AI、扣资源或释放法术。</summary>
    public struct OnlineCharacterActionEvent : IGameEvent
    {
        public ulong ActorId;
        // Animator Hash 是受玩法配置 Hash 保护的本地标识，不接受客户端任意指定。
        public int AnimationHash;
        public uint ActionKind; // 1=CrossFade State，2=Animator Trigger。
        public float FadeSeconds;
    }
}
