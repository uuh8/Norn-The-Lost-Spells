using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 弓箭手普通攻击状态（远程）。与 PlayerAttackState 同骨架（CrossFade 单段 + normalizedTime +
    /// ComboResolver 走向 End + 回移动），但"活动期"是在 ArrowSpawnTime 单点生成一支箭矢，
    /// 而非 OverlapBox 命中窗口；无连段推进（1 段）、无刀光。平行实现，不复用 Warrior 的 PlayerAttackState。
    /// </summary>
    public class PlayerBowAttackState : PlayerStateBase
    {
        private const float EndThreshold = 0.85f;     // 动画进度达此值 → 结束（1 段永不 Advance）
        private const float CrossFadeDuration = 0.1f; // 进入攻击段 CrossFade 固定时长

        private const int MaxAimHits = 16; // 屏幕中心射线一次最多记录的命中数（预分配，零 GC）

        private readonly ArcherController _archer; // typed 子类引用，拿弓箭手专属成员

        private int _comboIndex;
        private bool _arrowSpawned; // 本次播放是否已生成过箭矢（单点越阈触发一次的去重位）

        // 瞄准射线命中缓冲（生成时一次性用，状态对象只在 Awake 建一次 → 零每帧 GC）
        private readonly RaycastHit[] _aimHits = new RaycastHit[MaxAimHits];

        public PlayerBowAttackState(ArcherController player) : base(player)
        {
            _archer = player;
        }

        #region 状态机函数

        public override void Enter()
        {
            _comboIndex = 0;
            _player.AttackBufferCounter = 0f; // 消耗起手输入
            _arrowSpawned = false;

            if (_archer.Combo == null || _archer.Combo.SegmentCount == 0)
            {
                GameLog.Warn("弓箭手 ComboDefinition 未配置或无段落，无法攻击", "Combat");
                TransitionToMovement();
                return;
            }

            StartSegment(0);
        }

        public override void Update()
        {
            HandleGravity();
            HandleMovement();      // 边走边射：保留完整水平移动（不锁脚）
            HandleAimRotation();   // 身体转向相机水平朝向（朝准心方向，不再随移动乱转）
            HandleArrowSpawn();    // 单点：normalizedTime 越过 ArrowSpawnTime 生成一次
            CheckEnd();            // 射出即交还控制权（节奏交给射速冷却，与动画长度解耦）
        }

        public override void Exit()
        {
            _comboIndex = 0;
            _player.AttackBufferCounter = 0f; // 清残留，防攻击结束后误触发
        }

        #endregion

        #region 处理流程函数

        private void StartSegment(int index)
        {
            _arrowSpawned = false; // 新段重置去重位
            int hash = _archer.GetComboStateHash(index);
            _player.Animator.CrossFadeInFixedTime(hash, CrossFadeDuration, 0);
        }

        private void HandleGravity()
        {
            if (_player.VerticalVelocity < 0f)
                _player.VerticalVelocity = -2f;
        }

        private void HandleMovement()
        {
            // 边走边射：与接地态相同的完整移动（水平 MoveDirection*MoveSpeed + 垂直 VerticalVelocity）
            Vector3 velocity = _player.MoveDirection * _player.MoveSpeed * _player.StatusMoveSpeedMultiplier;
            velocity.y = _player.VerticalVelocity;
            _player.MoveCharacter(velocity * Time.deltaTime);
        }

        private void HandleArrowSpawn()
        {
            if (_arrowSpawned) return;
            if (_player.Animator.IsInTransition(0)) return; // 过渡期 normalizedTime 不可信

            AttackDefinition seg = _archer.Combo.Segments[_comboIndex];
            if (seg == null) return;

            float t = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
            if (t >= seg.ArrowSpawnTime)
            {
                SpawnArrow(seg);
                _arrowSpawned = true;
            }
        }

        private void SpawnArrow(AttackDefinition seg)
        {
            if (_archer.ArrowPrefab == null || _archer.ArrowSpawnPoint == null)
            {
                GameLog.Warn("弓箭手 ArrowPrefab/ArrowSpawnPoint 未配置，无法生成箭矢", "Combat");
                return;
            }

            Transform sp = _archer.ArrowSpawnPoint;

            // 朝屏幕中心（轨道相机）瞄准：从生成点直线指向准心命中点（含俯仰），不再用角色朝向。
            // 修复"出手前 0.x 秒里转身导致箭飞向转身中途朝向"的瞄准漂移。
            Vector3 targetPoint = ResolveAimTargetPoint(_archer.AimMask, _archer.AimMaxDistance, _aimHits);
            Vector3 dir = targetPoint - sp.position;
            if (dir.sqrMagnitude < 1e-6f) dir = _player.transform.forward; // 退化兜底
            dir.Normalize();

            GameObject go = Object.Instantiate(_archer.ArrowPrefab, sp.position, Quaternion.LookRotation(dir));

            Arrow arrow = go.GetComponent<Arrow>();
            if (arrow == null)
            {
                GameLog.Warn("ArrowPrefab 上没有 Arrow 组件", "Combat");
                return;
            }

            byte team = _archer.Health != null ? _archer.Health.TeamId : (byte)0;
            int attackerId = _player.gameObject.GetInstanceID();
            Vector3 velocity = dir * _archer.ProjectileSpeed;
            // 瞄准直射：关重力，直线命中准心点（否则抛物线会偏离准心）
            arrow.Init(team, attackerId, seg.BaseAmount, seg.Type, velocity, _player.CharacterController, useGravity: false);
        }

        /// <summary>
        /// 提前结束：箭一旦射出就立刻交还控制权回到移动态，不再干等到动画 EndThreshold(0.85)——
        /// 让两发的最小间隔由 ArcherController 的射速冷却(AttackCooldown)决定，而非动画长度。
        /// 动画本身仍由 Animator 的退出连线自然播完/淡出；下一发到来时 CrossFade 盖过其尾巴。
        /// 兜底：若因配置问题始终没射出，动画接近播完(EndThreshold)也强制结束，避免卡死在攻击态。
        /// </summary>
        private void CheckEnd()
        {
            if (_arrowSpawned)
            {
                TransitionToMovement();
                return;
            }
            if (_player.Animator.IsInTransition(0)) return; // 过渡期 normalizedTime 不可信
            float t = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
            if (t >= EndThreshold)
                TransitionToMovement();
        }

        #endregion

        #region 功能函数

        private void TransitionToMovement()
        {
            if (_player.GroundChecker.IsGrounded)
                _player.StateMachine.ChangeState(_player.GroundedState);
            else
                _player.StateMachine.ChangeState(_player.AirborneState);
        }

        #endregion
    }
}
