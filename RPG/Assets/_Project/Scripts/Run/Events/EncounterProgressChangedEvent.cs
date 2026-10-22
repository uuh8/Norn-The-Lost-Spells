using Game.Core;

namespace Game.Run
{
    /// <summary>
    /// Encounter 接受一次有效 Enemy Death 后发布的剩余数量快照。
    /// HUD 消费权威快照，而不是自行对 DeathEvent 做减法，避免重复/外部死亡造成显示漂移。
    /// </summary>
    public struct EncounterProgressChangedEvent : IGameEvent
    {
        public int EncounterIndex;
        public int EncounterCount;
        public int RemainingEnemyCount;
    }
}
