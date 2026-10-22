using Game.Core;

namespace Game.Run
{
    /// <summary>
    /// Scene Adapter 成功启动当前 Encounter 后发布。
    /// UI 只消费这个既成事实，不自行通过 Trigger 或 Enemy 激活状态猜测流程。
    /// </summary>
    public struct EncounterStartedEvent : IGameEvent
    {
        public int EncounterIndex;
        public int EncounterCount;
        public int EnemyCount;
    }
}
