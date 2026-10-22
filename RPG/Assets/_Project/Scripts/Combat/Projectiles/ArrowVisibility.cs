using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 普通 Arrow 的纯表现组件：只驱动短 TrailRenderer，帮助玩家从余光捕捉来袭方向。
    /// 箭矢模型保留原始材质；未来火、雷等元素附魔应以独立的元素表现组件开启 Emission，
    /// 避免“普通箭”与“元素箭”的语义混淆。它不参与伤害、碰撞或运动。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ArrowVisibility : MonoBehaviour
    {
        [Header("引用")]
        [SerializeField] private TrailRenderer _trail;

        [Header("可读性")]
        [Tooltip("普通箭仅使用这条短轨迹提示来袭方向，不改变箭矢模型颜色。")]
        [SerializeField] private Color _trailColor = new Color(1f, 0.18f, 0.01f, 1f);

        private void Awake()
        {
            if (_trail == null)
                _trail = GetComponent<TrailRenderer>();
        }

        /// <summary>
        /// 由 Arrow.Init 调用。只重置并开始拖尾；不读取阵营、不改 MeshRenderer，
        /// 因为普通箭的可读性来自运动轨迹，模型颜色留给元素附魔系统表达玩法状态。
        /// </summary>
        public void BeginTrail()
        {
            ApplyTrail(_trailColor);
        }

        /// <summary>命中后停止继续采样路径；已生成的轨迹仍按 Trail 的 time 自然淡出。</summary>
        public void StopTrail()
        {
            if (_trail != null)
                _trail.emitting = false;
        }

        /// <summary>
        /// Editor Authoring 工具调用此方法写入 Prefab 引用。显式持有引用比运行时反复搜索组件更稳定，
        /// 同时仍在 Awake 保留兜底，便于未来替换箭矢模型。
        /// </summary>
        public void Configure(TrailRenderer trail)
        {
            _trail = trail;
        }

        private void ApplyTrail(Color color)
        {
            if (_trail == null)
                return;

            // 新生成的箭矢不带旧轨迹；这里一次性配置即可，TrailRenderer 自己维护后续顶点。
            _trail.Clear();
            _trail.startColor = color;
            _trail.endColor = new Color(color.r, color.g, color.b, 0f);
            _trail.emitting = true;
        }
    }
}
