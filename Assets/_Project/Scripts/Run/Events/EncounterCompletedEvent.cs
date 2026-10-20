using Game.Core;
using Game.Skills;

namespace Game.Run
{
    /// <summary>
    /// 当前 Encounter 被纯 Tracker 判定为清场后发布一次。
    /// RewardSpell 是只读资产引用；真正创建持有物品由当前库存的奖励 Adapter 负责。
    /// </summary>
    public struct EncounterCompletedEvent : IGameEvent
    {
        public int EncounterIndex;
        public int EncounterCount;
        public SpellDefinition RewardSpell;
    }
}
