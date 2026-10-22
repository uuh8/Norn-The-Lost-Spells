using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 飞行箭矢。抛物线弹道（Init 默认 useGravity=true），飞行中持续对齐速度方向（FaceVelocityInFlight，机头随下坠俯冲）。
    /// 命中后不立即消失：冻结物理、关碰撞、挂到命中物体下，按基类 _impactLingerTime 残留一小段时间再销毁
    /// （插在目标/地面上的效果）。
    /// 命中角色的"流血"表现不在这里做——由命中目标的 CharacterCombatFeedback 订阅 DamageReceivedEvent 触发
    /// （近战命中同样受益，避免与投射物耦合）。
    /// 注意：模型箭尖沿 +Y，预制体上 _modelForwardOffsetEuler 应为 (90,0,0)。
    /// </summary>
    public class Arrow : ProjectileBase
    {
        private ArrowVisibility _visibility;

        protected override bool FaceVelocityInFlight => true; // 抛物线：飞行中机头随速度方向俯仰

        protected override void Awake()
        {
            base.Awake();
            _visibility = GetComponent<ArrowVisibility>();
        }

        public override void Init(byte attackerTeam, int attackerId, float damage, DamageType type,
                                  Vector3 velocity, Collider casterCollider, bool useGravity = true)
        {
            base.Init(attackerTeam, attackerId, damage, type, velocity, casterCollider, useGravity);
            // 普通箭只启动短拖尾；元素附魔未来用独立表现组件控制模型 Emission，保持两类语义清晰。
            _visibility?.BeginTrail();
        }

        protected override void OnImpact(Collision collision, IDamageable target, Vector3 hitPoint, bool damaged)
        {
            _visibility?.StopTrail();
            // 残留：冻结物理、关碰撞，挂到命中物体下随其移动；基类按 _impactLingerTime 延迟销毁
            if (_rb != null)
            {
                _rb.linearVelocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
                _rb.isKinematic = true;
            }
            if (_collider != null) _collider.enabled = false;
            if (collision.collider != null)
                transform.SetParent(collision.collider.transform, worldPositionStays: true);
        }
    }
}
