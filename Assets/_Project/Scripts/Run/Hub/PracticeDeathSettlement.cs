using System.Collections.Generic;

namespace Game.Run
{
    /// <summary>死亡保留装备背包本体，按最终父链清除其内容及其他随身装备；仓库和钱包保留。</summary>
    internal static class PracticeDeathSettlement
    {
        internal static bool TryCreate(SinglePlayerProfile source,out SinglePlayerProfile candidate)
        {
            var retained=new List<InventoryEntry>();
            foreach(var entry in source.Items)
                // 保护的是这一条本体记录，不能沿父链递归保护内容，也不能新建基础背包替换原容量。
                if(!RaidProfileOps.IsCarried(source,entry.Item.InstanceId)
                    || entry.Location.Kind==ItemLocationKind.EquippedBackpack && entry.Location.OwnerId==source.ActorId)
                    retained.Add(entry);
            // RestoreEntries同时复制钱包/商店/据点地面记录，并清理失效快捷绑定，保证没有孤儿物品。
            return RaidProfileOps.RestoreEntries(source,retained,source.ActorId,out candidate);
        }
    }
}
