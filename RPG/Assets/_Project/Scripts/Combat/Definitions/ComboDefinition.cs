using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 数据驱动的线性连段定义：按出招顺序排列的段落，每段引用一个 AttackDefinition。
    /// 纯数据，不引用 Animator/InputSystem。新武器 = 新建一份本资产，无需改代码。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Combat/Combo Definition", fileName = "ComboDefinition")]
    public class ComboDefinition : ScriptableObject
    {
        [Tooltip("按出招顺序排列的连段段落；索引 0 为起手段。")]
        public AttackDefinition[] Segments;

        [Header("射速 (远程普攻)")]
        [Tooltip("两次攻击的最小间隔(秒)，即攻击冷却/射速。与动画长度解耦：射出后由它决定何时能再射。\n" +
                 "0 = 不设冷却(沿用旧行为，节奏退回由动画长度决定)。当前仅远程普攻读取此值。")]
        public float AttackCooldown = 0f;

        /// <summary>段数；Segments 为空时返回 0。</summary>
        public int SegmentCount => Segments != null ? Segments.Length : 0;
    }
}
