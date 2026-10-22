using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 远程施法：站定播施法动画，到 Attack.ArrowSpawnTime 单点运行敌人的法术程序(单次释放去重 + 排除过渡帧 + shortNameHash 校验)。
    /// 结束判定与 Animator 退出连线协作(进入施法态后又离开即结束 + EndThreshold/超时兜底)，避免卡死。Exit 启动冷却。
    /// 注：施法动画名(AttackDefinition.AnimationStateName)需与 Animator 节点精确一致，否则进不去施法态、超时空放。
    /// </summary>
    public class EnemyRangedAttackState : EnemyStateBase
    {
        private const float EndThreshold = 0.95f;
        private const float MaxStateTime = 3f;

        private readonly RangedEnemyController _ranged;
        private bool _released;
        private bool _enteredAnimState;
        private float _elapsed;

        public EnemyRangedAttackState(RangedEnemyController enemy) : base(enemy) { _ranged = enemy; }

        public override void Enter()
        {
            _enemy.StopNavigation(true);
            _released = false;
            _enteredAnimState = false;
            _elapsed = 0f;
            if (_enemy.Perception.Target != null)
                _enemy.FaceTarget(_enemy.Perception.Target.position);
            _enemy.CrossFade(_enemy.AttackStateHash);
        }

        public override void Update()
        {
            _enemy.StayGrounded(); // 施法原地不动
            _elapsed += Time.deltaTime;

            Animator anim = _enemy.Animator;
            AttackDefinition atk = _enemy.Definition != null ? _enemy.Definition.Attack : null;
            if (anim == null || atk == null) { Finish(); return; }

            if (!anim.IsInTransition(0))
            {
                AnimatorStateInfo info = anim.GetCurrentAnimatorStateInfo(0);
                if (info.shortNameHash == _enemy.AttackStateHash)
                {
                    _enteredAnimState = true;
                    float t = info.normalizedTime % 1f;
                    if (!_released && t >= atk.ArrowSpawnTime)
                    {
                        _ranged.CastSelectedProgram();
                        // 即使 Authoring 配置无效，本轮动画也只尝试一次，避免之后每帧重复 Warn/施放。
                        _released = true;
                    }
                    if (t >= EndThreshold) Finish();
                    return;
                }
                if (_enteredAnimState) { Finish(); return; }
            }

            if (!_enteredAnimState && _elapsed >= MaxStateTime)
            {
                GameLog.Warn("远程敌人施法迟迟未进入施法动画态(检查 AttackDefinition.AnimationStateName)，超时结束", "Enemy");
                Finish();
            }
        }

        private void Finish()
        {
            EnemyPerception p = _enemy.Perception;
            _enemy.StateMachine.ChangeState(p.HasTarget ? _enemy.EngageState : _enemy.IdleState);
        }

        public override void Exit()
        {
            _enemy.AttackCooldownCounter = _enemy.Definition != null ? _enemy.Definition.AttackCooldown : 0f;
        }
    }
}
