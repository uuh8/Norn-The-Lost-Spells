using Game.Core;

namespace Game.Run
{
    /// <summary>
    /// 地图二 Encounter Authority 发布的启动事实。
    /// 事件只携带稳定 Instance ID，避免 Game.Run 直接引用具体 Boss Controller 类型。
    /// </summary>
    public struct BossEncounterStartedEvent : IGameEvent
    {
        public int BossId;
        public int PlayerId;
    }
}
