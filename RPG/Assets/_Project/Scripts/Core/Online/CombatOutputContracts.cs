using UnityEngine;

namespace Game.Core
{
    /// <summary>
    /// Authority 已完成模式与 Init 的真实输出。Actor/Command/Ordinal 是复制身份，GameObject 仅供本进程适配层读取。
    /// Owned事务提交后才发布，避免Replica看见马上回滚的半份组合。供料只复用此处标记生命周期；
    /// 真实材料投放/数量仍走原SpellMaterialOutputEvent与World事务，不能由复制表现再次执行。
    /// </summary>
    public struct CombatOutputSpawnedEvent : IGameEvent
    {
        public GameObject Output, DefinitionPrefab;
        public ulong ActorId, CommandId;
        public uint OutputOrdinal;
    }
}
