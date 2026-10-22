namespace Game.Combat
{
    /// <summary>
    /// 目标防御档案。本轮仅预留字段，DamagePipeline 暂不套数值公式
    /// （Physical/Magical 走 passthrough）。未来 Buff/Debuff 通过修正此档案接入。
    /// </summary>
    public struct DefenseProfile
    {
        public float Armor;       // 预留：物理减伤参数
        public float MagicResist; // 预留：魔法减伤参数
    }
}
