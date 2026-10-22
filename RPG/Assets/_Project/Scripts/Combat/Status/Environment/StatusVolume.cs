using System.Collections.Generic;
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 环境元素区域的 Gameplay 载体：按固定间隔执行一次 Box Overlap，
    /// 对范围内每个 StatusController 施加指定状态。它与 Water/Fire 可见 Shader 解耦，
    /// 因而同一规则区域可以更换 Mesh/VFX，而不影响碰撞查询和数值逻辑。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BoxCollider))]
    public sealed class StatusVolume : MonoBehaviour
    {
        private const int MaxCollidersPerTick = 32;
        private const float MinimumTickInterval = 0.01f;

        [Header("Status Application")]
        [SerializeField] private StatusKind _statusKind = StatusKind.Wet;
        [SerializeField, Min(MinimumTickInterval)] private float _tickInterval = 0.25f;
        [SerializeField, Min(0f)] private float _applyAmount = 5f;
        [SerializeField, Min(0f)] private float _naturalDecayHoldGraceSeconds = 0.1f;
        [SerializeField] private byte _sourceTeam = byte.MaxValue;
        [SerializeField] private LayerMask _targetLayers = ~0;

        private readonly Collider[] _colliderBuffer = new Collider[MaxCollidersPerTick];
        // 一个角色可能由多个 Collider 组成；每个环境 tick 按 StatusController InstanceID 去重。
        private readonly HashSet<int> _appliedTargetIds = new HashSet<int>(MaxCollidersPerTick);
        private BoxCollider _box;
        private float _elapsedTime;
        private int _sourceId;

        private void Awake()
        {
            CacheIdentityAndShape();
        }

        private void Reset()
        {
            // Volume 只描述环境覆盖范围，不应作为实体障碍参与碰撞响应。
            _box = GetComponent<BoxCollider>();
            if (_box != null)
                _box.isTrigger = true;
        }

        private void OnValidate()
        {
            // OnValidate 只在 Editor 配置变化时执行，用来阻止负数/零间隔进入运行时。
            _tickInterval = Mathf.Max(MinimumTickInterval, _tickInterval);
            _applyAmount = Mathf.Max(0f, _applyAmount);
            _naturalDecayHoldGraceSeconds = Mathf.Max(0f, _naturalDecayHoldGraceSeconds);
        }

        private void Update()
        {
            Tick(Time.deltaTime);
        }

        public void ConfigureForTests(StatusKind kind, float tickInterval, float applyAmount, byte sourceTeam)
        {
            _statusKind = kind;
            _tickInterval = Mathf.Max(MinimumTickInterval, tickInterval);
            _applyAmount = Mathf.Max(0f, applyAmount);
            _sourceTeam = sourceTeam;
            CacheIdentityAndShape();
        }

        public void TickForTests(float deltaTime)
        {
            Tick(deltaTime);
        }

        private void Tick(float deltaTime)
        {
            if (deltaTime <= 0f || _applyAmount <= 0f)
                return;

            _elapsedTime += deltaTime;
            if (_elapsedTime < _tickInterval)
                return;

            int completedTicks = Mathf.FloorToInt(_elapsedTime / _tickInterval);
            _elapsedTime -= completedTicks * _tickInterval;

            // 卡顿帧可能跨过多个 tick。合并施加强度可避免同一帧重复执行 Physics 查询，
            // 对当前“强度累加并 Clamp”的状态语义与逐次施加等价。
            QueryAndApply(_applyAmount * completedTicks);
        }

        private void QueryAndApply(float amount)
        {
            if (_box == null)
                CacheIdentityAndShape();
            if (_box == null)
                return;

            Transform boxTransform = _box.transform;
            Vector3 center = boxTransform.TransformPoint(_box.center);
            Vector3 scale = boxTransform.lossyScale;

            // BoxCollider.size 是 Local Space 全尺寸；Physics.OverlapBox 需要 World Space 半尺寸。
            // 因此 halfExtents = 0.5 * localSize ⊙ abs(lossyScale)，其中 ⊙ 表示逐分量乘法。
            Vector3 halfExtents = Vector3.Scale(
                _box.size * 0.5f,
                new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));

            int hitCount = Physics.OverlapBoxNonAlloc(
                center,
                halfExtents,
                _colliderBuffer,
                boxTransform.rotation,
                _targetLayers,
                QueryTriggerInteraction.Collide);

            // 选择 Collide 是为了让角色的 Trigger Hitbox 也能成为状态接收入口；最终仍在父级找 Controller。

            _appliedTargetIds.Clear();
            for (int i = 0; i < hitCount; i++)
            {
                Collider hit = _colliderBuffer[i];
                if (hit == null)
                    continue;

                StatusController target = hit.GetComponentInParent<StatusController>();
                if (target == null || !_appliedTargetIds.Add(target.GetInstanceID()))
                    continue;

                target.ApplySustainedStatus(
                    _statusKind,
                    amount,
                    _sourceId,
                    _sourceTeam,
                    _tickInterval + _naturalDecayHoldGraceSeconds);
            }
        }

        private void CacheIdentityAndShape()
        {
            _box = GetComponent<BoxCollider>();

            // 环境本身没有角色攻击者，使用 Volume 的 InstanceID 作为可追踪来源，Team 用配置值表达阵营。
            _sourceId = gameObject.GetInstanceID();
        }

        private void OnDrawGizmosSelected()
        {
            BoxCollider box = _box != null ? _box : GetComponent<BoxCollider>();
            if (box == null)
                return;

            Color color = GetStatusColor(_statusKind);
            Matrix4x4 previousMatrix = Gizmos.matrix;
            Color previousColor = Gizmos.color;

            // Collider 的 center/size 位于 Local Space；localToWorldMatrix 一次性加入位移、旋转和缩放，
            // 让 Scene 视图中的 Gizmo 与 Physics.OverlapBoxNonAlloc 的 World Space 查询保持一致。
            Gizmos.matrix = box.transform.localToWorldMatrix;
            Gizmos.color = new Color(color.r, color.g, color.b, 0.12f);
            Gizmos.DrawCube(box.center, box.size);
            Gizmos.color = new Color(color.r, color.g, color.b, 0.9f);
            Gizmos.DrawWireCube(box.center, box.size);

            Gizmos.matrix = previousMatrix;
            Gizmos.color = previousColor;
        }

        private static Color GetStatusColor(StatusKind kind)
        {
            switch (kind)
            {
                case StatusKind.Burning:
                    return new Color(1f, 0.25f, 0.05f);
                case StatusKind.Wet:
                    return new Color(0.05f, 0.45f, 1f);
                case StatusKind.Poisoned:
                    return new Color(0.25f, 1f, 0.15f);
                case StatusKind.Sticky:
                    return new Color(0.7f, 0.2f, 0.85f);
                default:
                    return Color.white;
            }
        }
    }
}
