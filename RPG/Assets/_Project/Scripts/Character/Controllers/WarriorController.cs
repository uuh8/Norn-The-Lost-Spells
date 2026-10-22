using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 战士控制器：在 PlayerControllerBase 的共享能力之上，叠加连段近战攻击专属逻辑
    /// （MeleeHitDetector / ComboDefinition / 刀光拖尾 / PlayerAttackState / 连段 hash 缓存）。
    /// 本类由 PlayerController 连同 .meta 重命名而来，GUID 不变——故 Player 预制体的组件引用与序列化字段全部保留。
    /// </summary>
    public class WarriorController : PlayerControllerBase
    {
        [Header("Combat")]
        [SerializeField] private MeleeHitDetector _meleeHitDetector; // 拖入武器上的命中判定组件
        [SerializeField] private ComboDefinition _combo; // 当前武器的连段表

        [Header("VFX")]
        [SerializeField] private TrailRenderer _bladeTrail; // 剑刃拖尾，挂在 VFX_BladeTip 上

        private PlayerAttackState _attackState;
        private int[] _comboStateHashes; // 连段各段 Animator 状态名的预 hash（Awake 算一次）


        public MeleeHitDetector MeleeHitDetector => _meleeHitDetector;
        public ComboDefinition Combo => _combo;
        public TrailRenderer BladeTrail => _bladeTrail;
        public PlayerAttackState AttackState => _attackState;

        protected override void Awake()
        {
            base.Awake();   // 基类默认的工作

            _attackState = new PlayerAttackState(this);
            BuildComboStateHashes();
        }

        /// <summary>攻击 seam 实现：有缓冲攻击输入则切到连段攻击态。保留 Dash→Attack→Jump 优先级。</summary>
        public override bool TryStartAttack()
        {
            if (AttackBufferCounter > 0f)
            {
                StateMachine.ChangeState(_attackState);
                return true;
            }
            return false;
        }

        /// <summary>取第 index 段的 Animator 状态 hash；越界或未配置返回 0。</summary>
        public int GetComboStateHash(int index)
        {
            if (_comboStateHashes == null || index < 0 || index >= _comboStateHashes.Length)
                return 0;
            return _comboStateHashes[index];
        }

        /// <summary>
        /// 把连段表各段的 AnimationStateName 预 hash 成 int[]，运行期切段直接用 hash CrossFade。
        /// 状态名为空时记 0 并告警（CrossFade 0 不会切动画）。
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
                    GameLog.Warn($"连段第 {i} 段 AnimationStateName 为空，CrossFade 将无法切换动画", "Combat");
                }
                else
                {
                    _comboStateHashes[i] = Animator.StringToHash(stateName);
                }
            }
        }
    }
}
