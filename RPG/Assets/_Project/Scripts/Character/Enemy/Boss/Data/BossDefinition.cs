using UnityEngine;
using UnityEngine.Serialization;

namespace Game.Character
{
    /// <summary>
    /// P8 Boss 的全局 Authoring 数据；运行时 Cooldown、History 和当前 Phase 不写回本资产。
    /// </summary>
    [CreateAssetMenu(
        menuName = "Game/Character/Boss Definition",
        fileName = "BossDefinition")]
    public sealed class BossDefinition : ScriptableObject
    {
        [Header("Identity")]
        public string DisplayName = "奥术大法师";

        [Header("Movement / Decision")]
        [Min(0f)] public float MoveSpeed = 3.5f;
        [Tooltip("CharacterController 的向下加速度；它不会像 Rigidbody 一样自动受 Gravity 影响。")]
        [Min(0f)] public float GravityAcceleration = 20f;
        [Tooltip("落地后保留的轻微向下速度，用于稳定 isGrounded，避免沿坡移动时短暂悬空。")]
        [Min(0f)] public float GroundedStickSpeed = 2f;
        [FormerlySerializedAs("StopDistance")]
        [Min(0f)] public float AttackEnterDistance = 15f;
        [Min(0f)] public float AttackExitDistance = 22f;
        [Min(0f)] public float PreferredCombatMinDistance = 10f;
        [Min(0f)] public float PreferredCombatMaxDistance = 16f;
        [Range(0f, 1f)] public float CombatMoveSpeedMultiplier = 0.7f;
        [Range(0f, 1f)] public float CastMoveSpeedMultiplier = 0.6f;
        [Min(0.1f)] public float OrbitDirectionInterval = 2f;
        [Min(0.1f)] public float CombatNavigationStepDistance = 3f;
        [Min(0f)] public float TurnSpeedDegrees = 540f;
        [Min(0.05f)] public float DecisionInterval = 0.2f;

        [Header("NavMesh Navigation")]
        [Min(0.05f)] public float PathRefreshInterval = 0.2f;
        [Min(0f)] public float DestinationMoveThreshold = 0.5f;
        [Min(0.05f)] public float DestinationSampleRadius = 2f;
        [Min(0.1f)] public float RetreatStepDistance = 3f;
        [Min(0.05f)] public float RetreatSampleRadius = 1.5f;
        [Min(0.05f)] public float StuckCheckInterval = 0.5f;
        [Min(0f)] public float StuckProgressDistance = 0.05f;

        // 兼容旧测试与少量 Editor 工具；Scene/Prefab 序列化由 FormerlySerializedAs 迁移到新字段。
        public float StopDistance
        {
            get => AttackEnterDistance;
            set => AttackEnterDistance = value;
        }

        [Header("Cast / Phase Timing")]
        [Min(0f)] public float CastTelegraphDuration = 0.35f;
        [Min(0f)] public float CastRecoveryDuration = 0.45f;
        [Min(0f)] public float PhaseTransitionDuration = 1.2f;
        [Min(0f)] public float TeleportTelegraphDuration = 0.6f;
        [Min(0f)] public float TeleportRecoveryDuration = 0.25f;
        [Min(0)] public int RecentHistoryCapacity = 3;

        [Tooltip("可选 Animator Trigger；留空时只运行 Gameplay Timing。")]
        public string CastAnimatorTrigger = "cast";

        [Tooltip("可选 Animator Trigger；留空时只运行无敌与 Phase Timing。")]
        public string PhaseTransitionAnimatorTrigger = "phaseTransition";

        [Header("Phases")]
        public BossPhaseDefinition Phase1 = new BossPhaseDefinition
        {
            Phase = BossPhase.Phase1,
            EnterAtOrBelowHealthRatio = 1f,
            CastInterval = 1.2f,
            TeleportCooldown = 0f,
            TeleportWeight = 0f,
        };
        public BossPhaseDefinition Phase2 = new BossPhaseDefinition
        {
            Phase = BossPhase.Phase2,
            EnterAtOrBelowHealthRatio = 0.7f,
            CastInterval = 0.9f,
            TeleportCooldown = 8f,
            TeleportWeight = 1f,
        };
        public BossPhaseDefinition Phase3 = new BossPhaseDefinition
        {
            Phase = BossPhase.Phase3,
            EnterAtOrBelowHealthRatio = 0.35f,
            CastInterval = 0.65f,
            TeleportCooldown = 5f,
            TeleportWeight = 1.5f,
        };

        [Header("Programs")]
        public BossSpellProgramDefinition[] Programs;

        [Header("Teleport Sampling")]
        [Min(0f)] public float TeleportMinRadius = 6f;
        [Min(0f)] public float TeleportMaxRadius = 12f;
        [Min(1)] public int TeleportCandidateCount = 12;
        [Min(0f)] public float NavMeshSampleDistance = 2f;
        [Range(0f, 89f)] public float MaxGroundSlopeDegrees = 45f;
        [Tooltip("NavMesh Area Mask；-1 表示允许所有已烘焙 Area。")]
        public int TeleportNavMeshAreaMask = -1;
        [Tooltip("向下确认真实地面时使用的 Layer，P8 应选择 Ground。")]
        public LayerMask TeleportGroundMask = 1 << 8;
        [Tooltip("检测 Boss 胶囊目标空间时使用的障碍 Layer，P8 环境 Collider 当前主要位于 Default。")]
        public LayerMask TeleportBlockingMask = 1 << 0;

        public BossPhaseDefinition GetPhase(BossPhase phase)
        {
            return phase switch
            {
                BossPhase.Phase2 => Phase2,
                BossPhase.Phase3 => Phase3,
                _ => Phase1,
            };
        }
    }
}
