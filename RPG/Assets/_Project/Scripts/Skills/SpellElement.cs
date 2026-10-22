namespace Game.Skills
{
    /// <summary>
    /// 法术的内容分类标签，供 UI 图标/文字、掉落池和内容检索读取。
    /// 它不等同于 Combat.DamageType：Element 回答“这个内容属于什么元素主题”，
    /// DamageType 回答“伤害管线应该使用哪类结算规则”，两者不能互相代替。
    /// 显式 byte 声明 C# 底层数值类型；真正保证 ScriptableObject 兼容性的关键是不要改动已发布成员的整数值。
    /// </summary>
    public enum SpellElement : byte
    {
        None = 0,   // 无元素或不参与元素分类。
        Fire = 1,   // 火元素内容标签。
        Water = 2,  // 水元素内容标签。
        Arcane = 3, // 奥术内容标签。
        Poison = 4, // 毒元素内容标签；追加在末尾以保护已有 ScriptableObject 的序列化整数。
        Sticky = 5, // 粘液控制内容标签；只用于内容分类，不改变 DamageType 或状态规则。
    }
}
