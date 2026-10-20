using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 法术攻击的 Unity 时序层：进入时播放攻击动画，达到数据配置的出手进度后只调用一次 SpellCaster。
    /// 它连接 Gameplay FSM、Animator 与 Runtime Adapter，但不解释法术模块，也不直接 Instantiate 投射物。
    /// 当前复用 ComboDefinition 第 0 段保存动画名、冷却与出手时机；“Combo/ArrowSpawnTime”是历史命名。
    /// </summary>
    public class PlayerWizardAttackState : PlayerStateBase
    {
        private const float EndThreshold = 0.85f;     // 未成功释放时的兜底退出进度，避免配置错误导致永久卡在攻击 State
        private const float CrossFadeDuration = 0.1f; // CrossFade 使用秒而非归一化百分比，保证不同长度 Clip 具有一致过渡手感

        private readonly WizardController _wizard;

        private int _comboIndex;                        // 当前固定为 0；保留索引是为了复用 AttackDefinition 时序数据
        private bool _castReleased;                    // 单次播放去重位：越过阈值后的后续帧不能重复创建攻击对象
        private bool _airborne;                        // 进入 State 时锁存；决定本次动画以及是否继续积分重力
        private int _animationHash;

        public PlayerWizardAttackState(WizardController player) : base(player)
        {
            _wizard = player;
        }

        #region 状态机函数

        public override void Enter()
        {
            _comboIndex = 0;
            _castReleased = false;
            // 在 Enter 锁存起手环境，避免攻击过程中 Ground Check 抖动导致动画/重力规则来回切换。
            _airborne = !_player.GroundChecker.IsGrounded;

            if (_wizard.Combo == null || _wizard.Combo.SegmentCount == 0)
            {
                GameLog.Warn("法师 ComboDefinition 未配置或无段落（施法动画/时机来自 Combo 段），无法施法", "Combat");
                TransitionToMovement();
                return;
            }

            StartSegment(0);
        }

        public override void Update()
        {
            HandleGravity();
            HandleMovement();      // 边走边施：保留完整水平移动
            HandleAimRotation();   // 身体转向相机水平朝向
            HandleCastRelease();   // 更新读取攻击动画的归一化进度，越过 ArrowSpawnTime 运行一次法杖
            CheckEnd();            // 释放即交还控制权（节奏交给射速冷却）
        }

        public override void Exit()
        {
            _comboIndex = 0;
        }

        #endregion

        #region 处理流程函数

        private void StartSegment(int index)
        {
            // 将“是否已释放法术/生成投射物”的标志位清零，防止同一帧或后续帧重复生成法术。
            _castReleased = false;

            //  决定播放哪个动画 (空中与地面的分支)
            _animationHash = _airborne && _wizard.AirAttackStateHash != 0
                ? _wizard.AirAttackStateHash        // 使用空中攻击的 Hash
                : _wizard.GetComboStateHash(index); // 获取对应的地面连招动画 Hash

            // CrossFadeInFixedTime：让角色的 Animator 立刻平滑过渡到上一步计算出的目标动画
            // 代码直接指定目标状态进行跳转，进入不需要 Animator 中连接 Transition，但目标 State 仍需配置正常的退出过渡。
            _player.Animator.CrossFadeInFixedTime(_animationHash, CrossFadeDuration, 0, 0f);
        }

        private void HandleGravity()
        {
            if (_airborne)
            {
                float multiplier = _player.VerticalVelocity < 0f
                    ? _player.FallGravityMultiplier
                    : _player.GravityMultiplier;
                _player.VerticalVelocity += Physics.gravity.y * multiplier * Time.deltaTime;
            }
            else if (_player.VerticalVelocity < 0f)
            {
                _player.VerticalVelocity = -2f;
            }
        }

        private void HandleMovement()
        {
            Vector3 velocity = _player.MoveDirection * _player.MoveSpeed * _player.StatusMoveSpeedMultiplier;
            velocity.y = _player.VerticalVelocity;
            // 攻击状态仍主动调用 Move，因此施法期间保留水平移动；传入的是本帧位移而不是“速度属性”。
            _player.MoveCharacter(velocity * Time.deltaTime);
        }

        private void HandleCastRelease()
        {
            // 第一层检查：是否已经释放过
            if (_castReleased) return;
            // IsInTransition(0) 表示 Base Layer 正在混合两个 State；此时 Current State 进度可能仍属于旧动画，先不判出手。
            if (_player.Animator.IsInTransition(0)) return;

            AttackDefinition seg = _wizard.Combo.Segments[_comboIndex];
            if (seg == null) return;

            // normalizedTime 的整数部分表示循环次数，小数部分表示当前循环的 0~1 进度；表示当前动画的归一化进度。
            var state = _player.Animator.GetCurrentAnimatorStateInfo(0);
            if (state.shortNameHash != _animationHash) return;
            // 不取%1：卡顿跨过一个Clip时仍需释放一次，不能把1.05误读成0.05而吞掉攻击。
            float t = state.normalizedTime;
            if (t >= seg.ArrowSpawnTime)
            {
                ReleaseCast();
                _castReleased = true;
            }
        }

        /// <summary>运行当前法杖：朝本次攻击已经消费并锁定的瞄准点，从法杖前端施放所有 EmitCommand。</summary>
        private void ReleaseCast()
        {
            if (_wizard.SpellCaster == null || _wizard.FireballSpawnPoint == null)
            {
                GameLog.Warn("法师 SpellCaster/FireballSpawnPoint 未配置，无法施法", "Skills");
                return;
            }

            // Transform.position 是生成挂点的世界空间坐标；历史名 FireballSpawnPoint 实际是通用法术生成点。
            Vector3 spawnPos = _wizard.FireballSpawnPoint.position;
            // 当前攻击的 active 快照在切入 State 前已经固定；动画期间的新输入只影响下一次 pending 请求。
            Vector3 aimPoint = _wizard.ActiveCastAimPoint;
            byte team = _wizard.Health != null ? _wizard.Health.TeamId : (byte)0;
            // GetInstanceID 返回当前 Unity Object 在本次运行期间的实例标识，用于伤害来源和事件过滤，不是跨存档稳定 ID。
            int attackerId = _player.gameObject.GetInstanceID();

            _wizard.ReleaseActiveCast(
                spawnPos,       // 攻击对象从哪里生成
                aimPoint,       // 玩家按键时瞄准的世界坐标
                team,           // 攻击者阵营，用于过滤友军
                attackerId,     // 伤害来源的唯一标识
                _player.CharacterController);   // 施法者碰撞体，用于避免投射物撞到自己
        }

        /// <summary>释放即结束回到移动态；兜底：动画接近播完(EndThreshold)也强制结束，避免卡死。</summary>
        private void CheckEnd()
        {
            // 松开按钮不取消已经开始的单击；只有实际越过释放阈值才完成本次攻击。
            if (_castReleased)
            {
                TransitionToMovement();
                return;
            }

            // 2. 兜底检查——在某些极端情况下（如网络延迟、逻辑bug、Animator参数设置异常），玩家可能已经松开了按键，但 _castReleased 没有被成功触发，或者动画没有设置退出条件，导致角色永远卡在施法动画里
            // 如果动画还在过渡期，就先不看，等会儿再来检查（return）
            if (_player.Animator.IsInTransition(0)) return;

            // 如果发现这一遍拉弓动作已经快播完了（到达门限值了），就强行断掉回到移动
            var state = _player.Animator.GetCurrentAnimatorStateInfo(0);
            if(state.shortNameHash!=_animationHash){TransitionToMovement();return;}
            float t = state.normalizedTime;
            float release = _wizard.Combo.Segments[_comboIndex]?.ArrowSpawnTime ?? 0f;
            if (t >= Mathf.Max(EndThreshold, release + .01f))
                TransitionToMovement();
        }

        #endregion

        #region 功能函数

        private void TransitionToMovement()
        {
            // 释放后不假定仍在地面：边走边施或空中施法可能改变接地结果，所以退出时重新查询实际环境。
            if (_player.GroundChecker.IsGrounded)
                _player.StateMachine.ChangeState(_player.GroundedState);
            else
                _player.StateMachine.ChangeState(_player.AirborneState);
        }

        #endregion
    }
}
