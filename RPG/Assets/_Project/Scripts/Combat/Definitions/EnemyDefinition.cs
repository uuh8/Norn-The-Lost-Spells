using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 数据驱动的敌人通用定义：AI 感知 / 移动 / 攻击 / 受击 的全部可调参数。
    /// 纯数据，仅引用 AttackDefinition（同模块），不引用 Animator/Character。新怪 = 新建一份本资产。
    /// HP/阵营仍在 HealthComponent 上配置（避免重复），本 SO 不含 HP。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Combat/Enemy Definition", fileName = "EnemyDefinition")]
    public class EnemyDefinition : ScriptableObject
    {
        [Header("移动")]
        public float MoveSpeed = 3.5f;

        [Header("NavMesh 寻路")]
        [Tooltip("两次常规路径刷新之间的最短时间(秒)。限制 SetDestination 频率，避免多个 Enemy 每帧重复寻路")]
        public float PathRefreshInterval = 0.2f;
        [Tooltip("目标水平移动超过此距离(米)时允许提前刷新路径")]
        public float DestinationMoveThreshold = 0.5f;
        [Tooltip("把玩家位置投影到最近 NavMesh 时允许搜索的半径(米)")]
        public float DestinationSampleRadius = 2f;
        [Tooltip("远程 Enemy 每次尝试后撤的目标距离(米)")]
        public float RetreatStepDistance = 4f;
        [Tooltip("后撤候选点投影到 NavMesh 时允许搜索的半径(米)")]
        public float RetreatSampleRadius = 1.5f;
        [Tooltip("每隔多久检查一次 CharacterController 是否产生了实际水平位移(秒)")]
        public float StuckCheckInterval = 0.5f;
        [Tooltip("一次卡住检查中至少应前进的水平距离(米)")]
        public float StuckProgressDistance = 0.05f;

        [Header("感知 (半径，带滞回防抖)")]
        [Tooltip("进入战斗的侦测半径")]
        public float DetectRadius = 12f;
        [Tooltip("进入战斗允许的最大绝对高度差(米)，与 DetectRadius 共同形成有限高度圆柱")]
        [Min(0f)] public float DetectHeight = 6f;
        [Tooltip("脱战半径；须 > DetectRadius，避免在边界反复进出战")]
        public float LoseRadius = 16f;
        [Tooltip("锁定后允许的最大绝对高度差(米)；应大于 DetectHeight，避免高度边界反复锁定/丢失")]
        [Min(0f)] public float LoseHeight = 8f;

        [Header("攻击")]
        [Tooltip("玩家进入此距离且冷却就绪 → 出招")]
        public float AttackRange = 2.2f;
        [Tooltip("两次攻击的最小间隔(秒)")]
        public float AttackCooldown = 1.5f;
        [Tooltip("攻击数据：近战读取命中盒、命中窗口和伤害；远程读取动画状态名与 ArrowSpawnTime 作为施法时序")]
        public AttackDefinition Attack;

        [Header("受击")]
        [Tooltip("受击硬直时长(秒)：被打中后这段时间定身播受击动画，不追不打")]
        public float HurtDuration = 0.4f;
        [Tooltip("受击动画状态名(可空则不 CrossFade)；须与敌人 Animator 节点名精确一致")]
        public string HurtStateName = "";

        [Header("远程走位 (仅远程敌人 RangedEnemyController 使用)")]
        [Tooltip("玩家比此距离更近时后撤；与 AttackRange 一起构成站档输出的范围带 [RetreatDistance, AttackRange]")]
        public float RetreatDistance = 4f;
        [Tooltip("触发后撤允许的最大绝对高度差(米)；玩家明显位于高台下方时不视为贴脸威胁")]
        [Min(0f)] public float RangedRetreatMaxHeight = 1.5f;
        [Tooltip("远程攻击允许的最大绝对高度差(米)；高台与地面无需 NavMesh 连通，但仍受此高度限制")]
        [Min(0f)] public float RangedAttackMaxHeight = 6f;
        [Tooltip("阻挡直线法术的场景 Layer。默认只检查 Ground，必须排除 Enemy 自身与 Player")]
        public LayerMask RangedLineOfSightObstacleMask = 1 << 8;

        [Header("CrossFade")]
        public float CrossFadeDuration = 0.1f;
    }
}
