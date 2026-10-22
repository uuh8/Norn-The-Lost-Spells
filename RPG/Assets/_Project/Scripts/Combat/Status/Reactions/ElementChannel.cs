namespace Game.Combat
{
    /// <summary>
    /// Reaction Kernel 使用的抽象元素通道，不等同于角色 StatusKind 或未来 Cell MaterialKind。
    /// Adapter 负责把不同载体的数据映射到这四个稳定通道。
    /// </summary>
    public enum ElementChannel : byte
    {
        // Burning 在 Reaction Kernel 中映射为 Fire；这里不用 StatusKind，
        // 是为了让未来角色状态、环境 Cell 和投射物都能提交同一种元素语义。
        Fire = 0,
        // Wet 映射为 Water。Water 本身通常无伤害，但会参与灭火和通用清洗。
        Water = 1,
        // Poisoned 映射为 Poison，用于 Toxic Combustion 和 Wet 优先清洗。
        Poison = 2,
        // Sticky 映射为 Goo。名称使用 Goo，是为了强调这是元素材料通道，
        // 而不是把角色身上的 Sticky Buff 当成世界材料本身。
        Goo = 3,
    }
}
