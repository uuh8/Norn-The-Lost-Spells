using UnityEngine;
using Game.Core;

namespace Game.Combat
{
    /// <summary>
    /// 陨石投射物。从天空斜上方直线砸向引导锁定的落点；命中角色/地面时：
    ///   ① 生成爆炸特效(NovaExplosion_Hit)——其上的 AreaDamage 组件做范围伤害(一次性)；
    ///   ② 生成地面火场(FireField)——其上的 AreaDamage 组件做持续范围伤害(周期跳伤)。
    /// 直击伤害由 ProjectileBase 处理：砸中角色 → 直击 + 爆炸 AOE 都吃；砸中地面 → 无直击，仅 AOE。
    /// 爆炸/火场半径留在各自 Prefab；伤害与火场节奏来自本次 EmitCommand 快照，
    /// 本脚本负责生成效果并在 AreaDamage.Start 首跳前注入阵营与数值。
    /// 直线飞行（生成时 Init 传 useGravity=false）。
    /// </summary>
    public class NovaFireball : ProjectileBase
    {
        [Header("爆炸（一次性 AOE，半径由 Prefab 配置）")]
        [SerializeField] private GameObject _explosionPrefab;   // NovaExplosion_Hit（建议挂 AreaDamage，TickInterval=0）
        [SerializeField] private float _explosionLifetime = 2f;
        // 爆炸特效旋转(欧拉角)。多数竖直朝向的特效填 (90,0,0) 可平铺到地面；若仍歪改这里。
        [SerializeField] private Vector3 _explosionRotationEuler = new Vector3(90f, 0f, 0f);

        [Header("火场（持续 AOE，半径由 Prefab 配置）")]
        [SerializeField] private GameObject _fireFieldPrefab;   // FireField（建议挂 AreaDamage，TickInterval>0）
        [SerializeField] private Vector3 _fireFieldRotationEuler = Vector3.zero;

        // 以下四个值属于“本次施法快照”，不是所有陨石共享的 Prefab Authoring 数据。
        // SpellCaster 必须在陨石首次命中前注入，避免直击伤害被错误复用到每个 DoT Tick。
        private float _explosionDamage;
        private float _fireFieldDamagePerTick;
        private float _fireFieldTickInterval = 0.5f;
        private float _configuredFireFieldDuration;

        /// <summary>
        /// 配置陨石的二、三段伤害。直击仍由 ProjectileBase._damage 独立负责，
        /// 因而直击、爆炸、火场每跳各自只有一个明确的数据来源。
        /// </summary>
        public void ConfigureImpactDamage(
            float explosionDamage,
            float fireFieldDamagePerTick,
            float fireFieldTickInterval,
            float fireFieldDuration)
        {
            _explosionDamage = Mathf.Max(0f, explosionDamage);
            _fireFieldDamagePerTick = Mathf.Max(0f, fireFieldDamagePerTick);
            _fireFieldTickInterval = Mathf.Max(0.05f, fireFieldTickInterval);
            _configuredFireFieldDuration = Mathf.Max(0f, fireFieldDuration);
        }

        protected override void OnImpact(Collision collision, IDamageable target, Vector3 hitPoint, bool damaged)
        {
            if (ExecutionMode == CombatExecutionMode.Replica) return;
            GameObject explosion = null, field = null;
            try
            {
                explosion = SpawnEffect(_explosionPrefab, hitPoint, _explosionRotationEuler,
                    _explosionLifetime, _explosionDamage, 0f);
                if (_configuredFireFieldDuration > 0f)
                    field = SpawnEffect(_fireFieldPrefab, hitPoint, _fireFieldRotationEuler,
                        _configuredFireFieldDuration, _fireFieldDamagePerTick, _fireFieldTickInterval);
                if (ExecutionMode == CombatExecutionMode.Authority)
                {
                    CombatOutputIdentity source = GetComponent<CombatOutputIdentity>();
                    if (source != null)
                    {
                        // 先配置并取得两个事实，最后才同步公开；第二个实例失败时客户端不会先收到半份爆炸。
                        CombatOutputSpawnedEvent explosionFact = explosion != null ? source.CreateChildFact(explosion, _explosionPrefab) : default;
                        CombatOutputSpawnedEvent fieldFact = field != null ? source.CreateChildFact(field, _fireFieldPrefab) : default;
                        if (explosion != null) EventBus<CombatOutputSpawnedEvent>.Publish(explosionFact);
                        if (field != null) EventBus<CombatOutputSpawnedEvent>.Publish(fieldFact);
                    }
                }
            }
            catch
            {
                // 爆炸与火场属于同次命中的产出事务；第二个预算失败时，第一个也不能留着等待自动首跳。
                CancelSpawnedEffect(explosion); CancelSpawnedEffect(field); throw;
            }
        }

        /// <summary>生成一个特效实例，若其上有 AreaDamage 则注入本陨石的阵营，按 lifetime 延时销毁。</summary>
        private GameObject SpawnEffect(
            GameObject prefab,
            Vector3 position,
            Vector3 rotationEuler,
            float lifetime,
            float damagePerHit,
            float tickInterval)
        {
            if (prefab == null) return null;
            GameObject go = Instantiate(prefab, position, Quaternion.Euler(rotationEuler));
            try
            {
                if (SimulationWorld != null) SimulationWorld.ConfigureHierarchy(go, ExecutionMode);
                // 必须在 Start 首跳前注入本次快照；常见 VFX 把 AreaDamage 挂在子物体上，不能只查根节点。
                AreaDamage[] areas = go.GetComponentsInChildren<AreaDamage>(true);
                for (int i = 0; i < areas.Length; i++)
                {
                    areas[i].ConfigureExecutionMode(ExecutionMode, SimulationWorld);
                    areas[i].Init(_attackerId, _attackerTeam);
                    areas[i].ConfigureDamage(damagePerHit, _type);
                    areas[i].ConfigureTickInterval(tickInterval);
                    areas[i].ConfigureDuration(tickInterval > 0f ? lifetime : 0f);
                    if (ExecutionMode == CombatExecutionMode.Authority) areas[i].ConfigureAuthorityLifetime(lifetime, go);
                }
                // Authority 火场与 Gameplay Tick 一起冻结；纯表现继续采用 Unity 的延迟销毁。
                if (ExecutionMode != CombatExecutionMode.Authority || areas.Length == 0) Destroy(go, lifetime);
                return go;
            }
            catch
            {
                // 容量预检拒绝发生在权限注入之前；若不先停用，克隆体会以默认 SinglePlayer 跑 Start。
                CancelSpawnedEffect(go); throw;
            }
        }
    }
}
