using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// Emit-style shield projectile. It flies like a normal ProjectileBase and delegates reflection to ProjectileShield.
    /// </summary>
    public sealed class ShieldProjectile : ProjectileBase
    {
        [SerializeField] private ProjectileShield _shield;

        private int _reflectCount;

        protected override void Awake()
        {
            base.Awake();

            if (_shield == null)
                _shield = GetComponentInChildren<ProjectileShield>();
        }

        public void ConfigureShield(int reflectCount)
        {
            _reflectCount = Mathf.Max(0, reflectCount);
        }

        public override void Init(byte attackerTeam, int attackerId, float damage, DamageType type,
                                  Vector3 velocity, Collider casterCollider, bool useGravity = true)
        {
            base.Init(attackerTeam, attackerId, damage, type, velocity, casterCollider, useGravity);

            if (_shield == null)
                _shield = GetComponentInChildren<ProjectileShield>();

            if (_shield != null)
                _shield.Init(attackerTeam, attackerId, _reflectCount, casterCollider);
        }

        protected override bool TryHandleCollisionBeforeDefault(Collision collision, IDamageable target)
        {
            return true;
        }
    }
}
