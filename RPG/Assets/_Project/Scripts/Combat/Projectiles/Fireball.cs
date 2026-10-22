using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 飞行火球。投射物基类 + 命中表现：命中点生成爆炸特效(Fireball_Explosion)，
    /// 并给被命中的角色施加 Burning 状态。直线飞行（生成时 Init 传 useGravity=false）。
    /// </summary>
    public class Fireball : ProjectileBase
    {
        [Header("命中表现")]
        [SerializeField] private GameObject _explosionPrefab;   // Fireball_Explosion
        [SerializeField] private float _explosionLifetime = 2f;

        [Header("燃烧状态")]
        [SerializeField, Min(0f)] private float _burningApplyAmount = 45f;

        protected override void OnImpact(Collision collision, IDamageable target, Vector3 hitPoint, bool damaged)
        {
            // 命中任何东西都放爆炸
            if (_explosionPrefab != null)
            {
                GameObject fx = Instantiate(_explosionPrefab, hitPoint, Quaternion.identity);
                Destroy(fx, _explosionLifetime);
            }

            // 仅对结算了伤害的角色附加燃烧
            if (damaged)
                ApplyBurn(collision.collider);
        }

        /// <summary>在被命中目标承载 StatusController 的根物体上施加 Burning。</summary>
        private void ApplyBurn(Collider hitCollider)
        {
            StatusController status = hitCollider.GetComponentInParent<StatusController>();
            if (status == null)
                return;

            status.ApplyStatus(StatusKind.Burning, _burningApplyAmount, _attackerId, _attackerTeam);
        }
    }
}
