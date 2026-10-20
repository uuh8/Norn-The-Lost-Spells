namespace Game.Skills
{
    /// <summary>
    /// 法术指令在解释器中的语义类别。byte 明确了该 enum 的 C# 底层数值类型；
    /// 已发布成员的整数值不要随意改动，否则现有 ScriptableObject 资产可能被反序列化成另一种指令。
    /// </summary>
    public enum SpellKind : byte
    {
        Emit = 0,              // 消耗一次 Draw Budget，产出一条向前/触发型攻击命令
        Modify = 1,            // 不产出攻击，只更新后续 Emit 读取的修正状态
        Multicast = 2,         // 不直接复制投射物，而是扩大解释器可消费的 Emit 数量
        StaticProjectile = 3,  // 同样消耗预算并产出命令，但由 Runtime 生成落点/定点对象
        Copy = 4,             // V2动态借用action；不移动目标卡，不重复普通支付
    }
    public enum SpellCopyMode : byte { Alpha, Gamma }

    /// <summary>EmitCommand 在 Unity Runtime 中的空间生成策略；纯解释器只携带枚举，不访问 Transform 或 Scene。</summary>
    public enum SpellSpawnMode : byte
    {
        ForwardProjectile = 0, // 从发射点沿瞄准方向创建 Rigidbody 投射物
        SkyfallAtPoint = 1,    // 把传入位置解释为落点，在其上空创建下落投射物
        StaticAtPoint = 2,     // 在指定位置创建保护盾等非飞行 Runtime 对象
    }
}
