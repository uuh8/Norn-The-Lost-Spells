using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 单一种角色状态的静态配置资产。ScriptableObject 只保存“规则参数和资源引用”，
    /// 每个角色身上的实时强度、来源与 DoT 计时器由 StatusController/StatusInstance 保存。
    /// 这种 Data-driven 分离允许策划调参时不修改 C#，也避免多个角色共享同一份运行时状态。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Combat/Status Definition", fileName = "StatusDefinition")]
    public class StatusDefinition : ScriptableObject
    {
        [Header("Common")]
        // Kind 同时作为四槽数组的索引，因此 StatusKind 的顺序不能随意改变。
        public StatusKind Kind;
        public string DisplayName = "";
        public Sprite Icon;

        // 普通状态每秒衰减：nextIntensity = max(0, current - rate * deltaTime)。
        [Min(0f)] public float NaturalDecayPerSecond = 8f;

        // 这是角色身上的状态表现 Prefab；元素区域的 Water/Fire Shader 属于另一套环境表现。
        public GameObject VfxPrefab;

        [Header("Damage Over Time")]
        // DoT 每次伤害会按当前强度百分比缩放，而不是固定使用 BaseDamagePerTick。
        public bool DealsDamage;
        [Min(0.05f)] public float DamageInterval = 1f;
        [Min(0f)] public float BaseDamagePerTick = 0f;
        public DamageType DamageType = DamageType.Magical;
        public bool TriggerHitReaction = false;

        [Header("Wet Cleanse")]
        [Tooltip("启用后，该状态会消耗 Wet Cleanse 的有限预算；Burning/Wet 应保持关闭。")]
        public bool WetCleanseable;

        // multiplier 既控制清洗预算竞争权重，也控制同一预算能清除的实际强度。
        [Min(0f)] public float WetCleanseMultiplier = 1f;

        [Header("Movement Modifier")]
        public bool AffectsMoveSpeed;

        // 当前减速比例 = MaxSlowRatio * intensity / 100，最终移动倍率为各状态倍率的乘积。
        [Range(0f, 0.95f)] public float MaxMoveSpeedSlowRatio = 0f;
    }
}
