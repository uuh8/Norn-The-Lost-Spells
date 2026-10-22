using UnityEngine;

namespace Game.Skills
{
    /// <summary>
    /// 一段可执行的法术配置：有序 SpellDefinition 引用数组 + 基础产出预算。
    /// 数组下标就是解释顺序；Runtime Session 克隆这个容器和数组后供 UI 编辑，元素仍引用只读的法术定义资产。
    /// SpellCaster 读取当前 Runtime 配置，并以 IReadOnlyList 视图交给 CastEvaluator。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Skills/Wand Loadout", fileName = "WandLoadout")]
    public class WandLoadout : ScriptableObject
    {
        [Tooltip("法杖里从左到右的法术序列（求值器据此运行）")]
        public SpellDefinition[] Spells;

        [Tooltip("基础投射物施放数。多重法术在此之上叠加")]
        public int BaseDraws = 1;
    }
}
