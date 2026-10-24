using UnityEngine;

namespace Game.Core
{
    /// <summary>
    /// Authority 已完成模式与 Init 的真实输出。Actor/Command/Ordinal 是复制身份，GameObject 仅供本进程适配层读取。
    /// Owned 事务提交后才发布，避免 Replica 看见马上回滚的半份组合；材料世界产出不借用此合同。
    /// </summary>
    public struct CombatOutputSpawnedEvent : IGameEvent
    {
        public GameObject Output, DefinitionPrefab;
        public ulong ActorId, CommandId;
        public uint OutputOrdinal;
    }
}
