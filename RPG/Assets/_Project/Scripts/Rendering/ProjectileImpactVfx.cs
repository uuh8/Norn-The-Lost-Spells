using Game.Combat;
using Game.Core;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 把 ProjectileBase 的一次性 Impacted 通知转换为纯视觉命中特效。
    ///
    /// Damage 与 Element Deposit 仍由各自 Gameplay 组件负责；本组件只生成并回收 VFX，
    /// 因此更换粒子 Prefab 不会改变伤害、水量、Wet 或 ElementField Simulation。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ProjectileImpactVfx : MonoBehaviour
    {
        [Header("Impact VFX")]
        [Tooltip("投射物命中敌人或环境时，在命中点生成的纯视觉 Prefab。")]
        [SerializeField] private GameObject _impactPrefab;

        [Tooltip("命中特效实例的最长存活时间。应略长于 Prefab 内最长 Particle System。")]
        [SerializeField, Min(0.05f)] private float _lifetime = 2f;

        private ProjectileBase _projectile;

        private void Awake()
        {
            _projectile = GetComponent<ProjectileBase>();
        }

        private void OnEnable()
        {
            // GetComponent 不放在 Update 热路径；这里只处理组件启用这一离散生命周期事件。
            // 支持在 Editor 中调整组件顺序，或运行时先添加本组件、后补齐 Projectile。
            if (_projectile == null)
                _projectile = GetComponent<ProjectileBase>();

            if (_projectile != null)
                _projectile.Impacted += OnProjectileImpacted;
        }

        private void Start()
        {
            if (_projectile == null)
            {
                GameLog.Error(
                    "ProjectileImpactVfx 必须与一个具体的 ProjectileBase 子类挂在同一 GameObject 上。",
                    "Rendering");
            }
        }

        private void OnDisable()
        {
            // Projectile 可能通过 Destroy 离场；成对退订可避免对象池或重新启用时重复生成 VFX。
            if (_projectile != null)
                _projectile.Impacted -= OnProjectileImpacted;
        }

        private void OnProjectileImpacted(Vector3 hitPoint, Vector3 hitDirection)
        {
            // 当前 WaterExplosion 是径向特效，不需要朝向飞行方向。若未来需要贴合法线的
            // Decal，应扩展独立的 Surface Impact 契约，而不是把 hitDirection 当成 Surface Normal。
            SpawnImpact(hitPoint);
        }

        /// <summary>
        /// 生成一次命中特效。保留 Prefab 根节点的美术旋转，避免 WaterExplosion 已配置的
        /// -90° X 轴修正被 Quaternion.identity 覆盖。
        /// </summary>
        internal GameObject SpawnImpact(Vector3 hitPoint)
        {
            if (_impactPrefab == null)
                return null;

            GameObject instance = Object.Instantiate(
                _impactPrefab,
                hitPoint,
                _impactPrefab.transform.rotation);

            // EditMode 单元测试只验证生成数据，不推进 Unity PlayerLoop；延迟 Destroy 仅在
            // Runtime 安排。Play Mode 中这是一笔命中时的一次性分配，不属于每帧热路径。
            if (Application.isPlaying)
                Object.Destroy(instance, Mathf.Max(0.05f, _lifetime));

            return instance;
        }

        private void OnValidate()
        {
            _lifetime = Mathf.Max(0.05f, _lifetime);
        }
    }
}
