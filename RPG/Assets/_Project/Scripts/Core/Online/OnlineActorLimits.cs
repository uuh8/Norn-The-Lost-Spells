namespace Game.Core
{
    /// <summary>
    /// 本轮副本复制的共同身份预算。连接席位始终只有四个，NPC席位按地图配置占用；
    /// Codec、Registry和事实去重数组共同引用，避免某层允许Actor16而另一层越界。
    /// 每Actor仍独立发送有界消息，本常量不表示把十六个状态塞进同一个UDP包。
    /// </summary>
    public static class OnlineActorLimits
    {
        public const int PlayerCount = 4;
        public const int MaximumActors = 16;
        public const int FirstNpcActorId = PlayerCount + 1;
        public const int MaximumNpcs = MaximumActors - PlayerCount;
        // 遭遇进度是独立Wire预算；十二个NPC不代表允许十二个阶段。
        public const int MaximumEncounters = 4;
    }
}
