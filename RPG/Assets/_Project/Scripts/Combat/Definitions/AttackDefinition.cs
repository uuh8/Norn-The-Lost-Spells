using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 数据驱动的近战攻击定义。MeleeHitDetector 读取几何与数值；
    /// Character 侧按动画归一化时间驱动两套独立窗口：
    /// HitActiveStart/HitActiveEnd（命中判定）与 TrailActiveStart/TrailActiveEnd（剑刃拖尾特效）——
    /// 两者刻意不复用同一对值，调命中数值平衡不会牵连拖尾的视觉时长，反之亦然。
    /// 连段/技能系统将复用本资产。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Combat/Attack Definition", fileName = "AttackDefinition")]
    public class AttackDefinition : ScriptableObject
    {
        [Header("伤害")]
        public float BaseAmount = 10f;
        public DamageType Type = DamageType.Physical;

        [Header("命中体积 (OverlapBox 半尺寸)")]
        public Vector3 HalfExtents = new Vector3(0.5f, 0.5f, 0.5f);

        [Header("命中窗口 (归一化动画时间 0~1，供 MeleeHitDetector 开/关命中判定)")]
        [Range(0f, 1f)] public float HitActiveStart = 0.30f;
        [Range(0f, 1f)] public float HitActiveEnd   = 0.55f;

        [Header("挥砍特效窗口 (归一化动画时间 0~1，独立于命中窗口，供剑刃拖尾 Trail Renderer 开/关 emitting)")]
        [Range(0f, 1f)] public float TrailActiveStart = 0.20f;
        [Range(0f, 1f)] public float TrailActiveEnd   = 0.65f;

        [Header("连段输入窗口 (归一化动画时间 0~1，此区间内按攻击键可接下一段)")]
        [Range(0f, 1f)] public float ComboInputStart = 0.40f;
        [Range(0f, 1f)] public float ComboInputEnd   = 0.70f;

        [Header("远程箭矢生成 (归一化动画时间 0~1，单点：normalizedTime 越过此值的那一刻生成一次箭矢；近战不用此字段)")]
        [Range(0f, 1f)] public float ArrowSpawnTime = 0.4f;

        [Header("动画")]
        [Tooltip("本段对应的 Animator 状态名，必须与 Animator Controller 中的 state 名完全一致；" +
                 "Character 侧在 Awake 预 hash，本字段是纯字符串，不引用 Animator。")]
        public string AnimationStateName = "";
    }
}
