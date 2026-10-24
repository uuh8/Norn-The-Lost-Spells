using UnityEngine;
using Game.Core;

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
            if (ExecutionMode == CombatExecutionMode.Replica) return;
            GameObject fx = null;
            try
            {
                if (_explosionPrefab != null)
                {
                    fx = Instantiate(_explosionPrefab, hitPoint, Quaternion.identity);
                    if (SimulationWorld != null) SimulationWorld.ConfigureHierarchy(fx, ExecutionMode);
                    // 生产 Fireball 的爆炸以 VFX 为主；若后续配置增加 AreaDamage，也必须继承阵营与 Tick 寿命。
                    AreaDamage[] areas = fx.GetComponentsInChildren<AreaDamage>(true);
                    for (int i = 0; i < areas.Length; i++)
                    {
                        areas[i].Init(_attackerId, _attackerTeam);
                        if (ExecutionMode == CombatExecutionMode.Authority)
                            areas[i].ConfigureAuthorityLifetime(_explosionLifetime, fx);
                    }
                    // 纯 VFX 的视觉寿命不参与 Gameplay 判定；EditMode 由调用方 Owner 清理，不能调用延迟 Destroy。
                    if (Application.isPlaying && (ExecutionMode != CombatExecutionMode.Authority || areas.Length == 0))
                        Destroy(fx, _explosionLifetime);
                }
                if (damaged) ApplyBurn(collision.collider);
                if (fx != null && ExecutionMode == CombatExecutionMode.Authority)
                {
                    CombatOutputIdentity source = GetComponent<CombatOutputIdentity>();
                    if (source != null) EventBus<CombatOutputSpawnedEvent>.Publish(source.CreateChildFact(fx, _explosionPrefab));
                }
            }
            catch
            {
                // 这层子产出发生在命中时，已经离开根施法的 SpawnTransaction，必须自行回滚。
                CancelSpawnedEffect(fx);
                throw;
            }
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
