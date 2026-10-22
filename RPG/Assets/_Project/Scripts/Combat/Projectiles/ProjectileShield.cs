using UnityEngine;
using Game.Core;

namespace Game.Combat
{
    /// <summary>
    /// Shield trigger that reflects incoming ProjectileBase instances.
    /// Static shields can use it directly; moving shield projectiles usually place it on a child trigger.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class ProjectileShield : MonoBehaviour
    {
        [SerializeField, Min(0)] private int _fallbackReflectCount = 3;

        private Collider _shieldCollider;
        private Collider _ownerCollider;
        private ProjectileBase _ownerProjectile;
        private byte _ownerTeam;
        private int _ownerAttackerId;
        private int _reflectRemaining;
        private bool _initialized;

        private void Awake()
        {
            _shieldCollider = GetComponent<Collider>();
            _ownerProjectile = GetComponentInParent<ProjectileBase>();

            if (_shieldCollider != null && !_shieldCollider.isTrigger)
                GameLog.Warn("ProjectileShield collider should usually be a Trigger so it can reflect projectiles reliably.", "Combat");
        }

        public void Init(byte ownerTeam, int ownerAttackerId, int reflectCount, Collider ownerCollider)
        {
            _ownerTeam = ownerTeam;
            _ownerAttackerId = ownerAttackerId;
            _ownerCollider = ownerCollider;
            _reflectRemaining = Mathf.Max(0, reflectCount);
            _initialized = true;

            if (_shieldCollider != null && _ownerCollider != null)
                Physics.IgnoreCollision(_shieldCollider, _ownerCollider);

            if (_reflectRemaining <= 0)
                DestroyShield();
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!_initialized)
                Init(_ownerTeam, _ownerAttackerId, _fallbackReflectCount, _ownerCollider);

            if (_reflectRemaining <= 0 || other == null)
                return;

            ProjectileBase projectile = other.GetComponentInParent<ProjectileBase>();
            if (projectile == null || projectile == _ownerProjectile)
                return;

            Vector3 normal = other.transform.position - transform.position;
            if (normal.sqrMagnitude <= 1e-6f)
                normal = other.bounds.center - transform.position;
            if (normal.sqrMagnitude <= 1e-6f)
                normal = transform.forward;

            if (!projectile.ReflectByShield(normal, _ownerTeam, _ownerAttackerId))
                return;

            _reflectRemaining--;
            if (_reflectRemaining <= 0)
                DestroyShield();
        }

        private void DestroyShield()
        {
            Destroy(_ownerProjectile != null ? _ownerProjectile.gameObject : gameObject);
        }
    }
}
