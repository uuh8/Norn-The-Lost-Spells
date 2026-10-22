namespace Game.Combat
{
    /// <summary>
    /// 稳定的反应标识。显式数值会被 ScriptableObject 和 EventBus 事件持久化，禁止随意重排。
    /// </summary>
    public enum ElementReactionId : byte
    {
        // Fire + Water：连续消耗双方强度，表现层可据此播放 Steam。
        Extinguish = 0,
        // Fire + Poison：经过 Wind-up 后一次性消耗 Poison 并产生范围伤害。
        ToxicCombustion = 1,
        // Fire + Goo：连续把 Goo 转化为 Fire；Wet 足够高时禁止启动。
        IgniteGoo = 2,
        // Water + Goo：Sticky 催化接触处的 Water 粒子原位转化为 Sticky；数值 3 保持序列化兼容。
        AbsorbWater = 3,
    }
}
