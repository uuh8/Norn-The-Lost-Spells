using UnityEngine;

namespace Game.Skills
{
    /// <summary>
    /// 法术内容目录：保存一组可供工具/NPC配置使用的 SpellDefinition 引用。
    /// 目录列出法术规则，不代表玩家已经持有、解锁或拥有无限次数。
    /// 玩家财产由 Run 的物品实例表达，研究台读取真实库存，不能从这里复制一套开场背包。
    /// </summary>
    // CreateAssetMenu 在 Unity 的 Assets/Create 菜单中增加创建入口；fileName 是新资产的默认文件名。
    [CreateAssetMenu(menuName = "Game/Skills/Spell Library", fileName = "SpellLibrary")]
    public class SpellLibrary : ScriptableObject
    {
        // Tooltip 只改变 Inspector 的悬停说明，不参与序列化或运行逻辑。
        // 数组元素是共享 SpellDefinition 资产引用；复制数组并不等于深度克隆每个法术资产。
        [Tooltip("只读法术规则集合；不用于发放玩家开场物品")]
        public SpellDefinition[] Available;
    }
}
