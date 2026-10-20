using UnityEngine;
using UnityEngine.Serialization;
using Game.Combat;

namespace Game.Skills
{
    /// <summary>
    /// 一个可复用法术指令的 Authoring Data。它是 Project Asset，不是场景中的投射物，也不保存某次施法的临时状态。
    /// 在现有语义范围内新增配置只需创建资产；若新增一种全新规则，仍需同步扩展 CastEvaluator、EmitCommand 与 SpellCaster。
    /// 字段按 Kind 分组读取：Emit/StaticProjectile 读取产出字段，Modify 读取 Mod*，Multicast 读取 ExtraDraws。
    /// </summary>
    // CreateAssetMenu 让策划可从 Unity 的 Assets/Create 菜单创建该 ScriptableObject 资产。
    [CreateAssetMenu(menuName = "Game/Skills/Spell Definition", fileName = "SpellDefinition")]
    public class SpellDefinition : ScriptableObject
    {
        #region 通用字段（所有 Kind 都可被 UI、法力系统或内容检索读取）

        [Header("通用")]
        public SpellKind Kind = SpellKind.Emit;
        public string DisplayName = "";
        [TextArea(2, 5)]
        [Tooltip("面向玩家的功能说明；为空时 Tooltip 会从权威行为字段生成基础说明。")]
        public string Description = "";
        [Tooltip("内容元素分类；它不等同于 Combat DamageType。")]
        public SpellElement Element = SpellElement.None;
        [Tooltip("技能图标")]
        public Sprite Icon;
        [Tooltip("施放本法术消耗的法力")]
        public float ManaCost = 0f;

        [Header("Draw V2（仅新解释器使用，Legacy 保持原语义）")]
        [Tooltip("当前 Scope 的施法间隔增量，单位秒；允许有限负值。")]
        public float CastDelayDelta = 0f;
        [Tooltip("本轮共享 Reload 增量，单位秒；Trigger 子层也参与。")]
        public float ReloadDelta = 0f;
        [Min(1), Tooltip("Trigger 构建独立子 Scope 时请求的 Draw 次数。")]
        public int TriggerDrawCount = 1;
        [Tooltip("Alpha优先discard首项/hand首项/deck首项；Gamma取deck末项，否则hand末项。")]
        public SpellCopyMode CopyMode;
        [Tooltip("-1沿用Prefab寿命；非负值覆盖，秒。V2内容建议显式配置。")]
        public float LifetimeSeconds = -1;
        [Tooltip("同Scope寿命加法，允许缩短；最终至少0.01秒，最多600秒。")]
        public float ModLifetimeAddSeconds;
        [Min(0), Tooltip("同Scope寿命倍率，叠乘；1不变，0.5使剩余寿命减半。先加秒再乘倍率。")]
        public float ModLifetimeMul = 1f;

        #endregion


        #region Emit / StaticProjectile 产出字段（解释器会把有效字段烘焙进 EmitCommand）

        [Header("Emit（投射物）—— 仅 Kind=Emit 用")]
        [Tooltip("要生成的投射物预制体（其上需有 ProjectileBase 派生组件，如 Fireball）")]
        public GameObject ProjectilePrefab;
        [Min(0f)]
        [Tooltip("投射物本体直接碰撞目标时造成的一次性伤害。复合法术的爆炸和持续区域伤害在下方独立配置。")]
        public float BaseDamage = 10f;
        public float BaseSpeed = 20f;
        public DamageType DamageType = DamageType.Magical;
        [Tooltip("施放音效。一次施法里同一音效只播一次")]
        public AudioClip CastSfx;
        [Tooltip("payload 释放条件：None=普通投射物；OnImpact=命中时释放后续法术；AfterDelay=存活满指定时间后释放后续法术。仅 Kind=Emit 有意义。")]
        public PayloadTriggerMode PayloadTrigger = PayloadTriggerMode.None;
        [Min(0f)]
        [Tooltip("定时触发延迟秒数。仅 PayloadTrigger=AfterDelay 时使用；应小于投射物 prefab 的 Max Lifetime。")]
        public float PayloadDelaySeconds = 1f;

        [Tooltip("产出类法术的生成方式：ForwardProjectile=沿释放方向发射；SkyfallAtPoint=以释放点作为落点从天而降。")]
        public SpellSpawnMode SpawnMode = SpellSpawnMode.ForwardProjectile;

        [Header("StaticProjectile / Skyfall（静态投射物）——仅 Kind=StaticProjectile 使用")]
        [Tooltip("落点提示预制体，例如 NovaFireball_LandingSite。")]
        public GameObject LandingSitePrefab;
        [Min(0f)]
        [Tooltip("陨石从落点上方多高处生成。")]
        public float SkyfallHeight = 12f;
        [Tooltip("沿释放方向反向偏移多少，0 表示垂直坠落。")]
        public float SkyfallBackOffset = 0f;
        [Min(0f)]
        [Tooltip("落点提示提前显示多久后生成陨石。")]
        public float LandingSiteDuration = 0.8f;

        [Header("Impact Effects（复合命中效果）——陨石等法术使用")]
        [Min(0f)]
        [Tooltip("爆炸生成时只结算一次的范围伤害。0 表示没有爆炸伤害。")]
        public float ExplosionDamage = 0f;
        [Min(0f)]
        [Tooltip("火场每次 Tick 对范围内每个敌方目标造成的伤害。")]
        public float FireFieldDamagePerTick = 0f;
        [Min(0.05f)]
        [Tooltip("火场两次伤害结算之间的秒数；只改变节奏，不受 Damage Modifier 影响。")]
        public float FireFieldTickInterval = 0.5f;
        [Min(0f)]
        [Tooltip("火场运行时实例的存活秒数；0 表示不生成可造成伤害的持续火场。")]
        public float FireFieldDuration = 0f;

        [Header("StaticProjectile / Shield（保护盾）——仅 Kind=StaticProjectile 且 SpawnMode=StaticAtPoint 使用")]
        [Min(0)]
        [Tooltip("保护盾可反弹投射物的次数。每成功反弹一次减 1，耗尽后护盾销毁。")]
        public int ShieldReflectCount = 3;

        // 这两个隐藏字段只服务旧资产迁移：FormerlySerializedAs 让 Unity 把旧字段 IsTrigger 的序列化值读入新字段。
        [SerializeField, HideInInspector, FormerlySerializedAs("IsTrigger")]
        private bool _legacyIsTrigger;
        [SerializeField, HideInInspector]
        private bool _legacyTriggerMigrated;

        #endregion


        #region Modify 修正字段（不直接修改 SpellDefinition 资产，而是累计到 CastModifierState）

        [Header("Modify（修正）—— 仅 Kind=Modify 用（默认值为恒等：不改变任何东西）")]
        public float ModDamageAddFlat = 0f;
        public float ModDamageMul = 1f;
        public float ModSpeedMul = 1f;
        public float ModSpreadAddDegrees = 0f;
        [Min(0)]
        [Tooltip("给后续投射物增加的环境弹跳次数。仅 Kind=Modify 时使用。")]
        public int ModBounceAdd = 0;
        [Tooltip("让后续投射物启用 Unity Rigidbody 重力。仅 Kind=Modify 时使用。")]
        public bool ModUseGravity = false;
        [Min(0f)]
        [Tooltip("投射物追踪敌人的检测半径。仅 Kind=Modify 时使用。0 表示不启用追踪。")]
        public float ModHomingRadius = 0f;
        [Min(0f)]
        [Tooltip("检测到敌人后追踪持续时间。仅 Kind=Modify 时使用。")]
        public float ModHomingDuration = 0f;
        [Min(0f)]
        [Tooltip("追踪期间每秒最大转向角度。仅 Kind=Modify 时使用。")]
        public float ModHomingTurnRateDegrees = 0f;
        [Min(0f)]
        [Tooltip("轨道螺旋半径。仅 Kind=Modify 时使用。0 表示不启用轨道。")]
        public float ModOrbitRadius = 0f;
        [Tooltip("轨道每秒旋转角速度，单位：度/秒。正负号决定旋转方向。仅 Kind=Modify 时使用。0 表示不启用轨道。")]
        public float ModOrbitAngularSpeedDegrees = 0f;
        [Tooltip("轨道初始相位偏移，单位：度。仅 Kind=Modify 时使用。")]
        public float ModOrbitPhaseOffsetDegrees = 0f;
        [Tooltip("同批轨道投射物的轨道面基础倾斜角，单位：度。用于避免多颗投射物退化成同一个圆环。")]
        public float ModOrbitPlaneTiltDegrees = 0f;

        #endregion


        [Header("Multicast（多重）—— 仅 Kind=Multicast 用")]
        [Tooltip("本次施法额外增加的投射物预算。双重=1，三重=2")]
        public int ExtraDraws = 0;

        private void OnValidate()
        {
            // OnValidate 由 Unity Editor 在资产载入或 Inspector 改值时调用，不属于玩家每帧 Runtime 流程。
            // 迁移标记保证旧 bool 只转换一次，避免设计者之后主动选择 None 又被自动改回 OnImpact。
            if (!_legacyTriggerMigrated)
            {
                if (_legacyIsTrigger && PayloadTrigger == PayloadTriggerMode.None)
                    PayloadTrigger = PayloadTriggerMode.OnImpact;
                _legacyTriggerMigrated = true;
            }
        }
    }
}
