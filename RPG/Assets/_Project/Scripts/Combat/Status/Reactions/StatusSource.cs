namespace Game.Combat
{
    /// <summary>
    /// 状态来源的值快照。只保存伤害归属需要的事实，不持有可能被销毁的攻击者对象。
    /// </summary>
    public readonly struct StatusSource
    {
        // 来源对象的 InstanceID 快照，用于 DamageRequest 的伤害归属。
        // 它不是 GameObject 引用，因此来源稍后被销毁也不会留下悬空引用。
        public readonly int Id;

        // 当前项目用 byte 表示阵营。范围小、值类型紧凑，复制 Snapshot 时没有 GC Alloc。
        public readonly byte Team;

        public StatusSource(int id, byte team)
        {
            // readonly struct 只能在构造函数中一次性写入字段，之后调用方只能读取。
            // 这能保证同一次反应求值期间，来源身份不会被外部代码悄悄修改。
            Id = id;
            Team = team;
        }
    }
}
