using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 弓箭手蓄力重击状态。按住进入：拉弓→满弓保持（循环, 累计蓄力, 封顶）；松开→放箭，
    /// 按蓄力比例(0~1)线性决定伤害/箭速。数据来自 ChargeAttackDefinition，与 PlayerBowAttackState 平行。
    /// 拉弓→保持 由 Animator HasExitTime 过渡驱动（Maintain 循环）；进入(拉弓)/放箭由代码 CrossFade。
    /// </summary>
    public class PlayerChargeAttackState : PlayerStateBase
    {
        private const float EndThreshold = 0.85f;     // 放箭动画进度达此值 → 结束
        private const float CrossFadeDuration = 0.1f;

        private readonly ArcherController _archer;

        private const int MaxAimHits = 16; // 屏幕中心射线一次最多记录的命中数（预分配，零 GC）

        private float _chargeElapsed; // 已累计蓄力时长（拉弓+保持期间，封顶）
        private bool _released;       // 是否已松手进入放箭阶段
        private bool _arrowSpawned;   // 放箭态是否已生成过箭（单点去重）
        private float _ratio;         // 松手瞬间锁定的蓄力比例 0~1

        // 瞄准射线命中缓冲（放箭时一次性用，预分配避免 GC）
        private readonly RaycastHit[] _aimHits = new RaycastHit[MaxAimHits];

        public PlayerChargeAttackState(ArcherController player) : base(player)
        {
            _archer = player;
        }

        #region 状态机函数

        public override void Enter()
        {
            _chargeElapsed = 0f;
            _released = false;
            _arrowSpawned = false;
            _ratio = 0f;
            _player.AttackBufferCounter = 0f;

            if (_archer.ChargeData == null)
            {
                GameLog.Warn("弓箭手 ChargeAttackDefinition 未配置，无法蓄力", "Combat");
                TransitionToMovement();
                return;
            }

            // CrossFade 拉弓；之后 拉弓→满弓保持 由 Animator HasExitTime 过渡自动流转
            _player.Animator.CrossFadeInFixedTime(_archer.ChargeDrawHash, CrossFadeDuration, 0);
            EventBus<AimStateChangedEvent>.Publish(new AimStateChangedEvent { Active = true }); // 蓄力瞄准取景（准心已由控制器常驻）
        }

        public override void Update()
        {
            HandleGravity();
            HandleMovement(); // 锁水平移动

            if (!_released)
            {
                // 蓄力累计（拉弓+保持期间），封顶
                float max = _archer.ChargeData.MaxChargeTime;
                _chargeElapsed += Time.deltaTime;
                if (_chargeElapsed > max) _chargeElapsed = max;

                HandleAimRotation(); // 蓄力期间角色转向相机水平朝向

                // 松开 → 放箭
                if (!_player.IsAttackHeld)
                    Release();
                return;
            }

            // 放箭阶段：必须确认当前真的在放箭态（CrossFade 过渡中 current 仍是保持态，避免误判进度）
            AnimatorStateInfo info = _player.Animator.GetCurrentAnimatorStateInfo(0);
            if (info.shortNameHash != _archer.ChargeLooseHash) return;

            float t = info.normalizedTime % 1f;
            if (!_arrowSpawned && t >= _archer.ChargeData.ArrowSpawnTime)
            {
                SpawnChargedArrow(_archer.ChargeData);
                _arrowSpawned = true;
            }
            if (t >= EndThreshold)
                TransitionToMovement();
        }

        public override void Exit()
        {
            _player.AttackBufferCounter = 0f;
            EventBus<AimStateChangedEvent>.Publish(new AimStateChangedEvent { Active = false }); // 退出蓄力取景，相机归位
        }

        #endregion

        #region 处理流程函数

        private void Release()
        {
            _released = true;
            float max = _archer.ChargeData.MaxChargeTime;
            _ratio = max > 0f ? Mathf.Clamp01(_chargeElapsed / max) : 1f;
            _player.Animator.CrossFadeInFixedTime(_archer.ChargeLooseHash, CrossFadeDuration, 0);
        }

        private void SpawnChargedArrow(ChargeAttackDefinition data)
        {
            if (_archer.ArrowPrefab == null || _archer.ArrowSpawnPoint == null)
            {
                GameLog.Warn("弓箭手 ArrowPrefab/ArrowSpawnPoint 未配置，无法生成箭矢", "Combat");
                return;
            }

            Transform sp = _archer.ArrowSpawnPoint;

            // 屏幕中心发射线求命中点（共享 PlayerStateBase.ResolveAimTargetPoint：命中 → 该点；未命中 → 相机朝向远点）
            Vector3 targetPoint = ResolveAimTargetPoint(_archer.AimMask, data.AimMaxDistance, _aimHits);

            // 从生成点直线指向目标点（含俯仰）；退化兜底用角色前向
            Vector3 dir = targetPoint - sp.position;
            if (dir.sqrMagnitude < 1e-6f) dir = _player.transform.forward;
            dir.Normalize();

            GameObject go = Object.Instantiate(_archer.ArrowPrefab, sp.position, Quaternion.LookRotation(dir));
            Arrow arrow = go.GetComponent<Arrow>();
            if (arrow == null)
            {
                GameLog.Warn("ArrowPrefab 上没有 Arrow 组件", "Combat");
                return;
            }

            float damage = Mathf.Lerp(data.MinDamage, data.MaxDamage, _ratio);
            float speed = Mathf.Lerp(data.MinSpeed, data.MaxSpeed, _ratio);
            byte team = _archer.Health != null ? _archer.Health.TeamId : (byte)0;
            int attackerId = _player.gameObject.GetInstanceID();
            // 瞄准直射：关重力，直线命中准心点
            arrow.Init(team, attackerId, damage, data.Type, dir * speed, _player.CharacterController, false);
        }

        private void HandleGravity()
        {
            if (_player.VerticalVelocity < 0f)
                _player.VerticalVelocity = -2f;
        }

        private void HandleMovement()
        {
            Vector3 velocity = Vector3.up * _player.VerticalVelocity;
            _player.MoveCharacter(velocity * Time.deltaTime);
        }

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
