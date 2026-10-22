namespace Game.Combat
{
    /// <summary>
    /// 某个角色身上一种状态的可变运行时数据，与只读配置 StatusDefinition 分离。
    /// 这是小型 value type，StatusController 用定长数组保存四份实例；从数组取出后得到副本，
    /// 修改字段后必须写回原数组。它不保存来源集合：后一次 Apply 会覆盖 Source 快照，
    /// 这是当前 MVP 用较低复杂度换取“只追踪最近施加者”的明确取舍。
    /// </summary>
    public struct StatusInstance
    {
        /// <summary>是否参与衰减、DoT、反应与 UI；Intensity 归零后会被关闭。</summary>
        public bool Active;

        /// <summary>Gameplay 强度，约定范围为 [0,100]。</summary>
        public float Intensity;

        /// <summary>下一次 DoT 结算前的剩余秒数；非伤害状态保持为 0。</summary>
        public float TickTimer;

        /// <summary>
        /// 持续来源仍在补充该状态时，普通 NaturalDecay 还需暂停多少秒。
        /// Reaction 与 DoT 不读取此字段，因此环境接触不会阻止元素反应或伤害结算。
        /// </summary>
        public float NaturalDecayHoldRemaining;

        /// <summary>最近一次施加该状态的对象 InstanceID，用于后续 DamageRequest 归因。</summary>
        public int SourceId;

        /// <summary>最近来源的队伍编号，用于友军过滤。</summary>
        public byte SourceTeam;
    }
}
