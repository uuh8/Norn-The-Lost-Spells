using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 近战出招：原地播攻击动画；按 normalizedTime 在 [HitActiveStart, HitActiveEnd] 开/关命中窗口(复用 MeleeHitDetector)。
    /// 结束判定与 Animator 退出连线协作(进入攻击态后又离开即结束 + EndThreshold/超时兜底)，避免卡死。Exit 关窗 + 启动冷却。
    /// </summary>
    public class EnemyAttackState : EnemyStateBase
    {
        private const float EndThreshold = 0.95f;
        private const float MaxStateTime = 3f;

        private readonly MeleeEnemyController _melee;
        private bool _windowOpen;
        private bool _enteredAnimState;
        private float _elapsed;

        public EnemyAttackState(MeleeEnemyController enemy) : base(enemy) { _melee = enemy; }

        public override void Enter()
        {
            _enemy.StopNavigation(true);
            _windowOpen = false;
            _enteredAnimState = false;
            _elapsed = 0f;
            if (_enemy.Perception.Target != null)
                _enemy.FaceTarget(_enemy.Perception.Target.position);
            _enemy.CrossFade(_enemy.AttackStateHash);
        }

        public override void Update()
        {
            _enemy.StayGrounded();
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
                    if (!_windowOpen && t >= atk.HitActiveStart) { _melee.OpenAttackWindow(); _windowOpen = true; }
                    if (_windowOpen && t >= atk.HitActiveEnd) { _melee.CloseAttackWindow(); _windowOpen = false; }
                    if (t >= EndThreshold) Finish();
                    return;
                }
                if (_enteredAnimState) { Finish(); return; }
            }

            if (!_enteredAnimState && _elapsed >= MaxStateTime)
            {
                GameLog.Warn("敌人攻击迟迟未进入攻击动画态(检查 AttackDefinition.AnimationStateName 是否与 Animator 节点名精确一致)，超时结束", "Enemy");
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
            if (_windowOpen) { _melee.CloseAttackWindow(); _windowOpen = false; }
            _enemy.AttackCooldownCounter = _enemy.Definition != null ? _enemy.Definition.AttackCooldown : 0f;
        }
    }
}
