using UnityEngine;

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
            SpawnEffect(
                _explosionPrefab,
                hitPoint,
                _explosionRotationEuler,
                _explosionLifetime,
                _explosionDamage,
                0f);

            // Duration=0 明确表示本次法术关闭持续火场；不再偷偷回退到 Prefab 的旧时长。
            if (_configuredFireFieldDuration > 0f)
            {
                SpawnEffect(
                    _fireFieldPrefab,
                    hitPoint,
                    _fireFieldRotationEuler,
                    _configuredFireFieldDuration,
                    _fireFieldDamagePerTick,
                    _fireFieldTickInterval);
            }
        }

        /// <summary>生成一个特效实例，若其上有 AreaDamage 则注入本陨石的阵营，按 lifetime 延时销毁。</summary>
        private void SpawnEffect(
            GameObject prefab,
            Vector3 position,
            Vector3 rotationEuler,
            float lifetime,
            float damagePerHit,
            float tickInterval)
        {
            if (prefab == null) return;
            GameObject go = Instantiate(prefab, position, Quaternion.Euler(rotationEuler));
            // 注入阵营/攻击者，使 AOE 跳过施法者一方、只伤敌人（须在该物体 Start 之前，同帧 Instantiate 后调用即满足）。
            // 用 GetComponentsInChildren：特效预制体常把 AreaDamage 挂在子物体上，只查根会漏掉 → 漏注入则阵营默认 0 会误伤。
            AreaDamage[] areas = go.GetComponentsInChildren<AreaDamage>(true);
            for (int i = 0; i < areas.Length; i++)
            {
                areas[i].Init(_attackerId, _attackerTeam);
                areas[i].ConfigureDamage(damagePerHit, _type);
                areas[i].ConfigureTickInterval(tickInterval);
                areas[i].ConfigureDuration(tickInterval > 0f ? lifetime : 0f);
            }
            Destroy(go, lifetime);
        }
    }
}
