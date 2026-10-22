using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 弓箭手控制器：在 PlayerControllerBase 共享能力之上，叠加远程普通攻击（点按，PlayerBowAttackState）
    /// 与蓄力重击（按住，PlayerChargeAttackState）。攻击输入路由（决策 A：代码计时 + 阈值门控）：
    /// 点按→普攻；按住过 TapThreshold→蓄力，tap 不经拉弓。
    /// </summary>
    public class ArcherController : PlayerControllerBase
    {
        [Header("Bow Attack")]
        [SerializeField] private ComboDefinition _combo; // 单段连段表（普通攻击 1 段）
        [SerializeField] private GameObject _arrowPrefab;     // 箭矢预制体（带 Rigidbody + Collider + Arrow）
        [SerializeField] private Transform _arrowSpawnPoint;  // 箭矢生成点（弓弦中点）
        [SerializeField] private float _projectileSpeed = 20f; // 普通攻击箭矢初速度

        [Header("Charge Attack")]
        [SerializeField] private ChargeAttackDefinition _chargeData; // 蓄力重击数据 SO
        [SerializeField] private LayerMask _aimMask = ~0; // 瞄准射线可命中层（务必排除 Player 层，免射到自己）

        [Header("Aim")]
        [Tooltip("普通箭矢屏幕中心瞄准的射线最大距离；未命中时取相机朝向该远点")]
        [SerializeField] private float _aimMaxDistance = 100f;

        private PlayerBowAttackState _bowAttackState;
        private PlayerChargeAttackState _chargeAttackState;
        private int[] _comboStateHashes;
        private HealthComponent _health;       // 阵营来源（缓存）
        private float _attackHeldTime;         // 攻击键按住累计时长（tap/hold 路由）
        private bool _attackTracking;          // 是否正在跟踪一次有效输入（仅由"本帧上升沿"开启）

        // 蓄力动画状态名预 hash（Awake 算一次）；满弓态由 Animator 过渡驱动，代码不 hash
        private int _chargeDrawHash;
        private int _chargeLooseHash;

        public ComboDefinition Combo => _combo;
        public GameObject ArrowPrefab => _arrowPrefab;
        public Transform ArrowSpawnPoint => _arrowSpawnPoint;
        public float ProjectileSpeed => _projectileSpeed;
        public HealthComponent Health => _health;
        public PlayerBowAttackState BowAttackState => _bowAttackState;
        public PlayerChargeAttackState ChargeAttackState => _chargeAttackState;

        public ChargeAttackDefinition ChargeData => _chargeData;
        public LayerMask AimMask => _aimMask;
        public float AimMaxDistance => _aimMaxDistance;
        public int ChargeDrawHash => _chargeDrawHash;
        public int ChargeLooseHash => _chargeLooseHash;

        protected override void Awake()
        {
            base.Awake();   // 基类默认的工作

            _health = GetComponent<HealthComponent>();
            _bowAttackState = new PlayerBowAttackState(this);
            _chargeAttackState = new PlayerChargeAttackState(this);
            BuildComboStateHashes();    // 构建所有Combo动画 Hash
            BuildChargeHashes();        // 构建蓄力动画 Hash
        }

        // 远程角色全程常驻准心：Start 保证初始可见（越过 OnEnable 与 CrosshairUI 订阅的先后竞态），
        // OnEnable 覆盖运行时重新启用（如复活），OnDisable（含死亡禁用控制器）隐藏。
        protected override void Start()
        {
            base.Start();
            EventBus<CrosshairVisibilityEvent>.Publish(new CrosshairVisibilityEvent { Visible = true });
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            EventBus<CrosshairVisibilityEvent>.Publish(new CrosshairVisibilityEvent { Visible = true });
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            EventBus<CrosshairVisibilityEvent>.Publish(new CrosshairVisibilityEvent { Visible = false });
        }

        /// <summary>
        /// 攻击输入路由（边沿门控，tap 不经拉弓）：仅"本帧刚按下"才开始跟踪一次输入，
        /// 避免把上一发蓄力残留的"按住"误判为新长按。跟踪中：按住过 TapThreshold → 蓄力态；
        /// 未达阈值松手（含亚帧点按）→ 普通攻击点射。
        /// </summary>
        public override bool TryStartAttack()
        {
            // 仅上升沿开启跟踪：从上个动作残留下来的按住没有新边沿，不会启动新蓄力
            if (AttackPressedThisFrame)
            {
                _attackTracking = true;
                _attackHeldTime = 0f;
            }
            if (!_attackTracking)
                return false; // 没有正在跟踪的有效输入

            // 跟踪中且仍按住：累计时长，过阈值进蓄力
            if (IsAttackHeld)
            {
                _attackHeldTime += Time.deltaTime;
                if (_chargeData != null && _attackHeldTime >= _chargeData.TapThreshold)
                {
                    EndAttackTracking();
                    StateMachine.ChangeState(_chargeAttackState);   // 蓄力状态
                    return true;
                }
                return false; // 仍在 tap 窗口内，按住等待
            }

            // 跟踪中已松手且未达阈值 → 普通攻击点射（含亚帧点按：上升沿+同帧已松开）
            EndAttackTracking();
            if (AttackCooldownCounter > 0f)
                return false; // 射速冷却中：本次点按作废，不进攻击态（蓄力重击不受此冷却限制）
            AttackCooldownCounter = _combo != null ? _combo.AttackCooldown : 0f; // 启动射速冷却（与动画长度解耦）
            StateMachine.ChangeState(_bowAttackState);
            return true;
        }

        /// <summary>结束一次输入跟踪：清跟踪标记、累计时长与攻击缓冲，防止下一发误触发。</summary>
        private void EndAttackTracking()
        {
            _attackTracking = false;
            _attackHeldTime = 0f;
            AttackBufferCounter = 0f;
        }

        /// <summary>取第 index 段的 Animator 状态 hash；越界或未配置返回 0。</summary>
        public int GetComboStateHash(int index)
        {
            if (_comboStateHashes == null || index < 0 || index >= _comboStateHashes.Length)
                return 0;
            return _comboStateHashes[index];
        }

        /// <summary>把连段表各段 AnimationStateName 预 hash 成 int[]（仿 WarriorController，平行实现）。</summary>
        private void BuildComboStateHashes()
        {
            int count = _combo != null ? _combo.SegmentCount : 0;
            _comboStateHashes = new int[count];
            for (int i = 0; i < count; i++)
            {
                AttackDefinition seg = _combo.Segments[i];
                string stateName = seg != null ? seg.AnimationStateName : null;
                if (string.IsNullOrEmpty(stateName))
                {
                    _comboStateHashes[i] = 0;
                    GameLog.Warn($"弓箭手连段第 {i} 段 AnimationStateName 为空，CrossFade 将无法切换动画", "Combat");
                }
                else
                {
                    _comboStateHashes[i] = Animator.StringToHash(stateName);
                }
            }
        }

        /// <summary>预 hash 蓄力拉弓/放箭两段状态名（满弓态由 Animator 过渡驱动，代码不 hash）。</summary>
        private void BuildChargeHashes()
        {
            if (_chargeData == null)
            {
                GameLog.Warn("弓箭手 ChargeAttackDefinition 未配置，蓄力重击不可用", "Combat");
                return;
            }
            _chargeDrawHash = HashState(_chargeData.DrawStateName);
            _chargeLooseHash = HashState(_chargeData.LooseStateName);
        }

        private static int HashState(string stateName)
        {
            if (string.IsNullOrEmpty(stateName))
            {
                GameLog.Warn("蓄力动画状态名为空，CrossFade 将无法切换动画", "Combat");
                return 0;
            }
            return Animator.StringToHash(stateName);
        }
    }
}
