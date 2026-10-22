using System.Collections.Generic;
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 实现了挂在武器上的命中判定组件。
    /// 在"攻击激活窗口"开启期间，每帧在武器位置做一次盒形重叠检测，找出碰到的所有可受击目标，过滤掉不该打的（自己/队友/已死/本次已打过的）
    /// 给每个合法目标提交一次伤害请求
    /// </summary>
    public class MeleeHitDetector : MonoBehaviour
    {
        [SerializeField] private AttackDefinition _attack;
        [SerializeField] private Transform _weaponPivot; // 命中体积中心；为空时退回本 Transform
        [SerializeField] private Transform _ownerRoot; // 攻击者根，用于 AttackerId

        [Tooltip("必须与攻击者自身 HealthComponent.TeamId 一致；命中判定据此跳过同阵营（含自身）。")] [SerializeField]
        private byte _attackerTeam = 0;

        [SerializeField] private LayerMask _hitMask = ~0;

        // 单帧 OverlapBox 命中上限。重叠 collider 超过此数时多余者被静默丢弃；如需更多请调大。
        private const int MaxHitsPerFrame = 16;
        private readonly Collider[] _buf = new Collider[MaxHitsPerFrame];
        private readonly HashSet<int> _hitSet = new HashSet<int>(); // per-swing 去重，预分配复用

        private bool _windowActive;
        private int _attackerId;

        // Public API
        public AttackDefinition Attack => _attack;

        /// <summary>
        /// 切换当前生效的攻击定义（连段换段时由 Character 侧调用）。
        /// 不主动清空去重集；换段方应配合 CloseHitWindow，使新段窗口在 ActiveStart
        /// 重新 OpenHitWindow 时（幂等 open 会 Clear）自然得到一次干净的 per-swing 去重。
        /// </summary>
        public void SetAttack(AttackDefinition attack)
        {
            _attack = attack;
        }

        /// <summary>
        /// 开启攻击激活窗口。幂等：窗口已开时不重复清空去重集,
        /// 以便 Character 侧每帧调用 EnsureOpen 语义。</summary>
        /// <summary>
        public void OpenHitWindow()
        {
            if (_windowActive) return;
            _windowActive = true;
            _hitSet.Clear();
            _attackerId = _ownerRoot != null ? _ownerRoot.GetInstanceID() : gameObject.GetInstanceID();
        }

        /// <summary>关闭攻击激活窗口。幂等。</summary>
        public void CloseHitWindow()
        {
            _windowActive = false;
        }

        private void Update()
        {
            if (!_windowActive || _attack == null) return;
            DoOverlap();
        }

        /// <summary>
        /// 执行攻击区域检测，对范围内的可伤害对象造成伤害
        /// </summary>
        private void DoOverlap()
        {
            // 获取武器枢轴点，如果未设置则使用自身Transform
            Transform pivot = _weaponPivot != null ? _weaponPivot : transform;
            // Physics.OverlapBoxNonAlloc：Unity 物理系统的"重叠查询"——问引擎 "这个盒子区域里现在有哪些碰撞体"
            int count = Physics.OverlapBoxNonAlloc(
                pivot.position, _attack.HalfExtents, _buf, pivot.rotation,
                _hitMask, QueryTriggerInteraction.Ignore);

            // 遍历所有检测到的碰撞体
            for (int i = 0; i < count; i++)
            {
                // 从碰撞体解析出可伤害接口
                IDamageable target = ResolveDamageable(_buf[i]);
                if (target == null) continue; // 跳过无法解析为可伤害对象的碰撞体
                // 防御性：跳过攻击者自身层级，命中体绝不打到自己身体——即使阵营(_attackerTeam)配置有误也不自伤
                Transform ownerT = _ownerRoot != null ? _ownerRoot : transform;
                if (_buf[i].transform.IsChildOf(ownerT)) continue;
                if (!target.IsAlive) continue; // 跳过已死亡的目标
                if (target.TeamId == _attackerTeam) continue; // 跳过同阵营（含队友）

                // 获取目标对象的实例ID用于去重
                Object targetObj = target as Object;
                if (targetObj == null) continue;
                int id = targetObj.GetInstanceID();
                if (!_hitSet.Add(id)) continue; // 本次挥砍已命中 → 去重

                // 计算命中点和受击方向
                Vector3 hitPoint = _buf[i].ClosestPoint(pivot.position);
                // pivot 在目标碰撞体内部时 ClosestPoint 返回 pivot 本身，差值为零 → 退回武器朝向，
                // 避免 HitDirection 为 (0,0,0) 污染下游受击反应/VFX。
                Vector3 toHit = hitPoint - pivot.position;
                Vector3 hitDir = toHit.sqrMagnitude > 1e-6f ? toHit.normalized : pivot.forward;

                var req = new DamageRequest(
                    _attackerId, _attackerTeam, _attack.BaseAmount,
                    _attack.Type, hitPoint, hitDir);
                target.ReceiveHit(in req);
            }
        }

        // 命中体 Collider → IDamageable 解析。集中于此，便于后续按需缓存（见计划文末性能注记）。
        private static IDamageable ResolveDamageable(Collider col)
        {
            return col.GetComponentInParent<IDamageable>();
        }

        private void OnDrawGizmosSelected()
        {
            if (_attack == null) return;
            Transform pivot = _weaponPivot != null ? _weaponPivot : transform;
            Gizmos.color = _windowActive ? Color.red : Color.yellow;
            Gizmos.matrix = Matrix4x4.TRS(pivot.position, pivot.rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, _attack.HalfExtents * 2f);
        }
    }
}
