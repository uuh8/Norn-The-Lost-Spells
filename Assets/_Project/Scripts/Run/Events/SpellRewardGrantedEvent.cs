using Game.Core;
using Game.Skills;

namespace Game.Run
{
    /// <summary>
    /// 单局法术背包成功获得一个新法术后发布。
    /// 事件只携带结果快照；UI 根据库存事件刷新，不持有另一套可写法术库。
    /// </summary>
    public struct SpellRewardGrantedEvent : IGameEvent
    {
        public SpellDefinition Spell;
    }
}
