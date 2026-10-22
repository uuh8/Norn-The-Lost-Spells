using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 数据驱动的蓄力重击定义。代码进入的两个动画状态名 + 输入/蓄力参数 + 蓄力比例(0~1)到伤害/箭速的线性端点。
    /// 仿 AttackDefinition/ComboDefinition：纯数据，不引用 Animator/Character。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Combat/Charge Attack Definition", fileName = "ChargeAttackDefinition")]
    public class ChargeAttackDefinition : ScriptableObject
    {
        [Header("动画状态名 (须与 Animator Controller 节点名精确一致)")]
        public string DrawStateName = "Attack01Start_Bow";        // 拉弓（代码 CrossFade 进入）
        public string LooseStateName = "Attack01RepeatFire_Bow";  // 松开放箭（代码 CrossFade 进入）

        [Header("输入 / 蓄力")]
        [Tooltip("按住超过此秒数才进入蓄力；短于此为点按普攻")]
        public float TapThreshold = 0.15f;
        [Tooltip("蓄满所需秒数；超过按满（比例封顶 1）")]
        public float MaxChargeTime = 1.5f;

        [Header("蓄力比例 0~1 线性映射 → 伤害")]
        public float MinDamage = 15f;
        public float MaxDamage = 45f;

        [Header("蓄力比例 0~1 线性映射 → 箭速")]
        public float MinSpeed = 18f;
        public float MaxSpeed = 36f;

        [Header("放箭生成 (归一化动画时间 0~1，RepeatFire 越过此值生成一次蓄力箭)")]
        [Range(0f, 1f)] public float ArrowSpawnTime = 0.3f;

        [Header("瞄准 (蓄力精确命中)")]
        [Tooltip("屏幕中心射线最大距离；未命中任何碰撞体时取此距离的远点作为目标")]
        public float AimMaxDistance = 100f;

        [Header("伤害类型")]
        public DamageType Type = DamageType.Physical;
    }
}
