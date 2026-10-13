using UnityEngine;
using Game.Combat;
using Game.Core;
using Game.Run;

namespace Game.Character
{
    /// <summary>
    /// 法术角色控制器：在共享移动能力之上负责“输入意图”这一段攻击链。
    /// 按下攻击键 -> 锁存准心与缓存请求 -> FSM 允许后进入 PlayerWizardAttackState；
    /// 真正的法术求值与投射物生成分别交给 CastEvaluator 和 SpellCaster，本类不实现组合规则。
    /// 当前陨石与其他投射物一样都是法术数据，不再由角色控制器保存专用重击状态。
    /// </summary>
    public class WizardController : PlayerControllerBase
    {
        private static readonly int JumpStartHash = Animator.StringToHash("JumpStart_MagicWand");
        private static readonly int JumpAirHash = Animator.StringToHash("JumpAir_MagicWand");
        private static readonly int JumpEndHash = Animator.StringToHash("JumpEnd_MagicWand");
        // 当前 Wizard Controller 的待机 State 历史命名仍是 Idle_Bow；必须匹配真实节点名，CrossFade 不存在的名字会静默失败。
        private static readonly int IdleHash = Animator.StringToHash("Idle_Bow");
        private static readonly int RunHash = Animator.StringToHash("Run_MagicWand");

        [Header("Wizard Jump Animation")]
        [Tooltip("JumpStart 过渡到 JumpAir 的 CrossFade 时长（秒）。姿势差较大，建议 0.15~0.22。")]
        [SerializeField, Min(0f)] private float _jumpStartToAirBlendDuration = 0.18f;
        [Tooltip("进入 JumpStart、落地 JumpEnd、自然离地和回到 Idle/Run 的通用 CrossFade 时长（秒）。建议 0.08~0.16。")]
        [SerializeField, Min(0f)] private float _jumpOtherBlendDuration = 0.12f;

        [Header("Wizard Jump VFX")]
        [Tooltip("每次真正起跳时生成在角色脚底的特效；普通跳、Coyote Jump 与二段跳共用。")]
        [SerializeField] private GameObject _jumpVfxPrefab;
        [Tooltip("特效生成点相对 CharacterController 脚底的垂直偏移。地面穿插时可适当调高。")]
        [SerializeField] private float _jumpVfxVerticalOffset = 0.02f;
        [Tooltip("特效实例自动销毁延迟。当前 Jump Prefab 最长 ParticleSystem Duration 为 5 秒。")]
        [SerializeField, Min(0.1f)] private float _jumpVfxLifetime = 5.5f;

        [Header("Wizard Attack (法杖施放)")]
        // ComboDefinition 在这里仅复用“攻击冷却 + 动画 State 名 + 归一化出手进度”，不表示当前法术攻击是多段连招。
        [SerializeField] private ComboDefinition _combo;
        // 历史字段名仍叫 FireballSpawnPoint；语义是所有法术产出的通用世界空间生成 Transform。
        [SerializeField] private Transform _fireballSpawnPoint;
        [Tooltip("空中普攻动画状态名（Animator 节点 JumpAttack_MagicWand）；空 → 0 → 空中攻击退回地面普攻动画")]
        [SerializeField] private string _airAttackStateName = "JumpAttack_MagicWand";

        [Header("Aim")]
        [Tooltip("屏幕中心瞄准的可命中层（排除 Player 层，免瞄到自己）")]
        [SerializeField] private LayerMask _aimMask = ~0;
        [Tooltip("屏幕中心瞄准的射线最大距离；未命中时取相机朝向该远点")]
        [SerializeField] private float _aimMaxDistance = 100f;

        // State 是普通 C# 对象，在 Awake 只创建一次；运行中只切换引用，避免每次攻击 new 状态造成 GC Alloc。
        private PlayerWizardAttackState _wizardAttackState;
        private SpellCaster _spellCaster;             // 同物体上的法术施放器（运行法杖 → 生成投射物）
        private int[] _comboStateHashes;
        private HealthComponent _health;
        private OwnedWandCastAdapter _ownedWands;
        private bool _usesOwnedWands;
        private WandProgramSnapshot _pendingProgram, _activeProgram;
        private OwnedWandCastContext _pendingSource, _activeSource;

        // ── 攻击输入 Runtime State ──
        // 是否有待处理请求以及请求寿命统一由基类 AttackBufferCounter 表示。
        // 这里仅保存 Wizard 独有的瞄准数据，避免再用“待处理标记 + 独立计时器”重复表达同一状态。
        private Vector3 _pendingCastAimPoint;     // 下一次待处理施法的点击瞬间瞄准点；新点击可以覆盖它
        private Vector3 _activeCastAimPoint;      // 本次攻击已消费的瞄准点；动画出手前保持不变
        private readonly RaycastHit[] _aimHits = new RaycastHit[16]; // 点按锁存瞄准用射线缓冲（预分配，零每帧 GC）

        private int _airAttackStateHash;

        public ComboDefinition Combo => _combo;
        public Transform FireballSpawnPoint => _fireballSpawnPoint;
        public HealthComponent Health => _health;
        public SpellCaster SpellCaster => _spellCaster;
        public Vector3 ActiveCastAimPoint => _activeCastAimPoint;

        public float AimMaxDistance => _aimMaxDistance;
        public int AirAttackStateHash => _airAttackStateHash;

        /// <summary>Scene-local工作区在角色Awake后就绪，显式更新缓存，避免新背包仍走旧Library施法。</summary>
        public void BindOwnedInventory(OwnedWandCastAdapter adapter)
        {
            _ownedWands?.CancelOutstanding();
            AttackBufferCounter=0;_pendingProgram=_activeProgram=default;_pendingSource=_activeSource=default;
            _ownedWands=adapter;_usesOwnedWands=true;
            // 来源随后失效也保持owned语义，不能退回无限资源旧路径。
            if(StateMachine!=null)StateMachine.ChangeState(GroundedState);
            EventBus<CrosshairVisibilityEvent>.Publish(new CrosshairVisibilityEvent{Visible=true});
        }

        # region Unity 周期函数
        protected override void Awake()
        {
            // override 的 Awake 必须先调用 base.Awake，保证共享组件、Input Actions 和 FSM 已初始化，子类随后才能安全创建持有 this 引用的专属攻击 State。
            base.Awake();

            _health = GetComponent<HealthComponent>();
            _spellCaster = GetComponent<SpellCaster>();
            _ownedWands = GetComponent<OwnedWandCastAdapter>();
            _usesOwnedWands = _ownedWands != null;
            _wizardAttackState = new PlayerWizardAttackState(this);
            BuildComboStateHashes();

            _airAttackStateHash = string.IsNullOrEmpty(_airAttackStateName)
                ? 0 : Animator.StringToHash(_airAttackStateName);
        }

        // 远程角色全程常驻准心：Start 保证初始可见，OnEnable 覆盖重新启用，OnDisable 隐藏。
        protected override void Start()
        {
            base.Start();
            // 远程角色屏幕中间显示准心
            EventBus<CrosshairVisibilityEvent>.Publish(new CrosshairVisibilityEvent { Visible = true });
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            EventBus<RaidStateChangedEvent>.Subscribe(OnRaidStateChanged);
            EventBus<HeldItemUseResetEvent>.Subscribe(OnHeldUseReset);
            EventBus<CrosshairVisibilityEvent>.Publish(new CrosshairVisibilityEvent { Visible = true });
        }

        protected override void OnDisable()
        {
            EventBus<RaidStateChangedEvent>.Unsubscribe(OnRaidStateChanged);
            EventBus<HeldItemUseResetEvent>.Unsubscribe(OnHeldUseReset);
            base.OnDisable();
            EventBus<CrosshairVisibilityEvent>.Publish(new CrosshairVisibilityEvent { Visible = false });
        }
        private void OnRaidStateChanged(RaidStateChangedEvent change)
        {
            if (!_usesOwnedWands || _ownedWands?.ReadOnly == null || change.InventoryId != _ownedWands.ReadOnly.InventoryId
                || (change.Phase != RaidPhase.Departing && change.Phase != RaidPhase.Settling)) return;
            // 跨局资源/场景切换不能让旧动画稍后释放排队施法；只清本角色攻击，不削弱其它角色控制能力。
            AttackBufferCounter = 0; _pendingProgram = _activeProgram = default; _pendingSource = _activeSource = default;
            if (StateMachine != null) StateMachine.ChangeState(GroundedState);
        }
        private void OnHeldUseReset(HeldItemUseResetEvent change)
        {
            if (!_usesOwnedWands || _ownedWands?.ReadOnly == null || change.InventoryId != _ownedWands.ReadOnly.InventoryId) return;
            // 只取消尚未进入攻击的缓存；已开始的动画/可靠资源提交继续，不退费或吞掉已发Trigger。
            AttackBufferCounter = 0; _pendingProgram = default; _pendingSource = default;
        }
        # endregion

        /// <summary>
        /// Attack Action 的 performed 回调：在按键触发的同一刻保存瞄准点，并写入共享攻击缓存。
        /// AttackBufferCounter 是唯一的请求有效期，不再额外维护待施法 bool 与计时器。
        /// </summary>
        protected override void OnAttackPerformed(UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        {
            // 暂停时 UI 也会使用鼠标，不能把 UI 点击记录成恢复游戏后的施法。
            if (Time.timeScale == 0f)
                return;
            if (_usesOwnedWands && (_ownedWands == null || !_ownedWands.TryCapture(out _pendingProgram, out _pendingSource))) return;

            _pendingCastAimPoint = ResolveAimTargetPoint(
                _aimMask,   // 射线检测的层级遮罩，保证射线只与合法的目标发生碰撞
                _aimMaxDistance,    // 最大瞄准距离
                _aimHits);          // 接收射线碰撞结果的缓冲区

            // 保留原有手感：每次点击都获得“一次完整射速冷却 + 普通 Buffer”的有效期，
            // 因此动画或冷却期间稍早按下的下一发不会丢失。新点击覆盖旧请求，仍不会无限积压次数。
            float queueLifetime = (_combo != null ? _combo.AttackCooldown : 0f) + AttackBufferTime;
            if (_usesOwnedWands && _ownedWands.ReadOnly.TryGetState(_pendingProgram.SourceWandId, out WandStateSnapshot state))
                queueLifetime = state.CastCooldown + AttackBufferTime;
            AttackBufferCounter = queueLifetime;
        }

        /// <summary>
        /// Wizard 的跳跃动画由 Gameplay FSM 与垂直速度共同驱动，并使用 Inspector 中可调的
        /// CrossFade 时长。Animator Controller 不再保存一套无法在 Runtime 调参的重复时序。
        /// </summary>
        protected override void SyncCharacterAnimatorParameters(bool animIsGrounded)
        {
            bool isAirborneLocomotion = ReferenceEquals(StateMachine.CurrentState, AirborneState);
            bool isGroundedLocomotion = ReferenceEquals(StateMachine.CurrentState, GroundedState) ||
                                        ReferenceEquals(StateMachine.CurrentState, SlidingState);

            if (!isAirborneLocomotion && !isGroundedLocomotion)
                return; // Dash/Attack 自己拥有动画，直到 Gameplay FSM 回到 locomotion 才重新接管。

            AnimatorStateInfo stateInfo = ReadEffectiveAnimatorState();
            JumpAnimationPhase currentPhase = GetJumpAnimationPhase(stateInfo.shortNameHash);

            if (isAirborneLocomotion)
            {
                JumpAnimationPhase targetPhase = JumpAnimationPhaseResolver.ResolveAirbornePhase(
                    currentPhase,
                    stateInfo.normalizedTime,
                    JumpAnimationPhaseResolver.IsFalling(animIsGrounded, VerticalVelocity));

                if (targetPhase == JumpAnimationPhase.JumpAir && currentPhase != targetPhase)
                {
                    float duration = currentPhase == JumpAnimationPhase.JumpStart
                        ? _jumpStartToAirBlendDuration
                        : _jumpOtherBlendDuration;
                    CrossFadeJumpState(JumpAirHash, duration);
                }

                return;
            }

            JumpAnimationPhase groundedTarget = JumpAnimationPhaseResolver.ResolveGroundedPhase(
                currentPhase,
                stateInfo.normalizedTime);

            if (groundedTarget == JumpAnimationPhase.JumpEnd && currentPhase != groundedTarget)
            {
                CrossFadeJumpState(JumpEndHash, _jumpOtherBlendDuration);
            }
            else if (groundedTarget == JumpAnimationPhase.Locomotion &&
                     currentPhase == JumpAnimationPhase.JumpEnd)
            {
                int locomotionHash = MoveInput.sqrMagnitude >= 0.01f ? RunHash : IdleHash;
                CrossFadeJumpState(locomotionHash, _jumpOtherBlendDuration);
            }
        }

        /// <summary>
        /// Wizard 不再依赖 Animator 中写死时长的 jump Trigger Transition。
        /// 每次 Gameplay 真正批准跳跃时，从目标 Clip 的 0 秒重新 CrossFade，二段跳因此会可靠重播 JumpStart。
        /// </summary>
        public override void PlayJumpStartAnimation()
        {
            ClearPendingJumpAnimation();
            Animator.CrossFadeInFixedTime(
                JumpStartHash,
                Mathf.Max(0f, _jumpOtherBlendDuration),
                0,
                0f);
        }

        /// <summary>
        /// 特效使用世界坐标生成且不挂到角色下面：起跳后粒子留在起跳点，避免跟着角色一起飞。
        /// Jump 是离散输入，Instantiate/Destroy 的一次性分配符合项目热路径约束。
        /// </summary>
        protected override void PlayJumpVfx()
        {
            if (_jumpVfxPrefab == null) return;

            Vector3 spawnPosition = JumpVfxPlacement.ResolveFootPosition(
                CharacterController.bounds,
                _jumpVfxVerticalOffset);
            GameObject instance = Instantiate(
                _jumpVfxPrefab,
                spawnPosition,
                _jumpVfxPrefab.transform.rotation);
            Destroy(instance, Mathf.Max(0.1f, _jumpVfxLifetime));
        }

        /// <summary>
        /// CrossFade 期间以目标 State 为“当前语义阶段”，防止每帧重复启动同一次过渡。
        /// </summary>
        private AnimatorStateInfo ReadEffectiveAnimatorState()
        {
            return Animator.IsInTransition(0)
                ? Animator.GetNextAnimatorStateInfo(0)
                : Animator.GetCurrentAnimatorStateInfo(0);
        }

        private static JumpAnimationPhase GetJumpAnimationPhase(int stateHash)
        {
            if (stateHash == JumpStartHash) return JumpAnimationPhase.JumpStart;
            if (stateHash == JumpAirHash) return JumpAnimationPhase.JumpAir;
            if (stateHash == JumpEndHash) return JumpAnimationPhase.JumpEnd;
            return JumpAnimationPhase.Locomotion;
        }

        private void CrossFadeJumpState(int targetHash, float duration)
        {
            Animator.CrossFadeInFixedTime(targetHash, Mathf.Max(0f, duration), 0, 0f);
        }

        /// <summary>
        /// 地面状态的攻击扩展入口。这里只判断“有请求 + 冷却结束”并切状态，尚未计算法术或创建投射物。
        /// 返回 true 告诉 GroundedState 本帧已经发生状态转换，不要继续处理 Jump/离地等低优先级分支。
        /// </summary>
        public override bool TryStartAttack()
        {
            if (AttackBufferCounter <= 0f || (_usesOwnedWands ? _ownedWands == null || !_ownedWands.CanStart(_pendingProgram) : AttackCooldownCounter > 0f))
                return false;

            ConsumeBufferedCast();
            return true;
        }

        /// <summary>空中状态的平行入口；复用同一个攻击缓存，只由攻击 State 选择空中动画。</summary>
        public override bool TryStartAirAttack()
        {
            if (AttackBufferCounter <= 0f || (_usesOwnedWands ? _ownedWands == null || !_ownedWands.CanStart(_pendingProgram) : AttackCooldownCounter > 0f))
                return false;

            ConsumeBufferedCast();
            return true;
        }

        /// <summary>原子地消费攻击意图与瞄准点、启动冷却并进入施法 State；真正出手等待 Animator 进度阈值。</summary>
        private void ConsumeBufferedCast()
        {
            // 进入攻击状态前锁定本次瞄准点。攻击动画期间的新点击只会更新 pending，不能污染 active。
            _activeCastAimPoint = _pendingCastAimPoint;
            _activeProgram = _pendingProgram; _activeSource = _pendingSource;

            // 清除共享缓存，保证一次 performed 只执行一次攻击。
            AttackBufferCounter = 0f;

            // 冷却在批准攻击时开始，而不是命中时开始；射速因此与投射物飞行时间完全解耦。
            // Owned 模式实际出手提交源杖冷却；旧角色路径保留已有起手时序。
            AttackCooldownCounter = !_usesOwnedWands && _combo != null ? _combo.AttackCooldown : 0f;

            // 切换到施法状态
            StateMachine.ChangeState(_wizardAttackState);
        }
        public int ReleaseActiveCast(Vector3 spawn, Vector3 aim, byte team, int attacker, Collider collider)
        {
            if (_spellCaster == null) return 0;
            return _usesOwnedWands
                ? _spellCaster.CastOwnedProgram(_activeProgram, _activeSource, spawn, aim, team, attacker, collider)
                : _spellCaster.CastWand(spawn, aim, team, attacker, collider);
        }

        /// <summary>取第 index 段的 Animator 状态 hash；越界或未配置返回 0。</summary>
        public int GetComboStateHash(int index)
        {
            if (_comboStateHashes == null || index < 0 || index >= _comboStateHashes.Length)
                return 0;
            return _comboStateHashes[index];
        }

        /// <summary>
        /// 把 Animator State 名预先转换成 int hash。StringToHash 避免运行时反复进行字符串查找，
        /// 之后 CrossFade 直接使用整数；名称必须与 Animator Controller 中的 State 完全一致。
        /// </summary>
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
                    GameLog.Warn($"法师连段第 {i} 段 AnimationStateName 为空，CrossFade 将无法切换动画", "Combat");
                }
                else
                {
                    _comboStateHashes[i] = Animator.StringToHash(stateName);
                }
            }
        }

    }

    /// <summary>把 CharacterController 的世界 Bounds 转换为脚底 VFX 生成点。</summary>
    public static class JumpVfxPlacement
    {
        public static Vector3 ResolveFootPosition(Bounds characterBounds, float verticalOffset)
        {
            Vector3 position = characterBounds.center;
            position.y = characterBounds.min.y + verticalOffset;
            return position;
        }
    }
}
