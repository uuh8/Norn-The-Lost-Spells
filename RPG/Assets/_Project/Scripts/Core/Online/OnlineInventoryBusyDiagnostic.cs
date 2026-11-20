namespace Game.Core
{
    /// <summary>
    /// Authority 库存门的离散只读值副本，不是授权合同，也不持有 Store、Ticket 或可变集合。
    /// 默认值明确表示没有取得 Authority 数据；不能把 Client 的未知状态当成“没有占用”。
    /// 读取该值不推进 Tick、不清理 Reservation、不改变 Frozen。报告层须复制到自己的
    /// Serializable DTO：Unity JsonUtility 不序列化 readonly 字段，不能直接将本值写成现场 JSON。
    /// </summary>
    public readonly struct OnlineInventoryBusyDiagnostic
    {
        public readonly bool Captured;
        public readonly bool RaidActionsFrozen;
        public readonly bool ItemsResourceBusy;
        public readonly bool CastPending;
        public readonly int ActiveReservations;

        public OnlineInventoryBusyDiagnostic(bool raidActionsFrozen, bool itemsResourceBusy,
            bool castPending, int activeReservations)
        {
            Captured = true;
            RaidActionsFrozen = raidActionsFrozen;
            ItemsResourceBusy = itemsResourceBusy;
            CastPending = castPending;
            ActiveReservations = activeReservations;
        }
    }
}
