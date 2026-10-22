using Game.Combat;
using Game.Core;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 把 Combat 状态强度转换成角色 Shader 参数的“只读表现桥”。
    ///
    /// 架构边界：
    /// 1. StatusController 是 Gameplay 权威数据源，决定状态叠加、衰减和状态反应；
    /// 2. 本组件只读取状态，不反向修改 Gameplay；
    /// 3. Shader 只负责把 0~1 参数画成火焰纹理、湿润高光等视觉结果。
    ///
    /// 这样拆分后，即使将来替换 Shader，也不会改变燃烧伤害或潮湿灭火规则。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(StatusController))]
    public sealed class StatusMaterialController : MonoBehaviour
    {
        // Shader Graph Blackboard 中的 Reference 必须和这里逐字一致。
        // PropertyToID 会把字符串预先转换成整数 ID，避免在 Update 热路径重复做字符串查找。
        private static readonly int BurningId = Shader.PropertyToID("_BurningIntensity");
        private static readonly int WetId = Shader.PropertyToID("_WetIntensity");
        private static readonly int PoisonedId = Shader.PropertyToID("_PoisonedIntensity");
        private static readonly int StickyId = Shader.PropertyToID("_StickyIntensity");

        // 浮点数经过多帧运算后很难保证绝对相等，因此用一个极小误差判断“已经到达目标”。
        private const float SettleEpsilonSqr = 0.00000001f;

        [Header("目标 Renderer")]
        [Tooltip("留空时会在 Awake 自动收集当前启用的 MeshRenderer/SkinnedMeshRenderer；也可手动指定需要状态表现的部件。")]
        [SerializeField] private Renderer[] _targetRenderers;

        [Header("视觉响应")]
        [Tooltip("Shader 显示强度每秒最多变化多少。默认 4 表示从 0 到 1 最快约需 0.25 秒。")]
        [SerializeField, Min(0.01f)] private float _responseSpeed = 4f;

        private StatusController _statusController;
        private MaterialPropertyBlock _propertyBlock;
        private int _targetId;

        // Vector4 的四个分量分别保存 Burning/Wet/Poisoned/Sticky。
        // 它是值类型，不会因为逐帧复制而产生托管堆 GC Alloc。
        private Vector4 _current;
        private Vector4 _target;
        private bool _isTransitioning;

        // 只读属性既方便运行时诊断，也让自动化测试能够观察真实状态，
        // 不需要向生产代码加入 TickForTests 之类的测试专用接口。
        public float BurningVisualIntensity => _current.x;
        public float WetVisualIntensity => _current.y;
        public float PoisonedVisualIntensity => _current.z;
        public float StickyVisualIntensity => _current.w;
        public bool IsTransitioning => _isTransitioning;

        private void Awake()
        {
            _targetId = gameObject.GetInstanceID();
            _statusController = GetComponent<StatusController>();
            _propertyBlock = new MaterialPropertyBlock();
            CacheTargetRenderers();
        }

        private void OnEnable()
        {
            EventBus<StatusChangedEvent>.Subscribe(OnStatusChanged);

            // 对象池重新启用角色时，状态事件可能早已发布过。
            // 主动读取一次权威状态，可以避免等待下一次事件才恢复正确画面。
            SyncFromStatusController();
        }

        private void OnDisable()
        {
            EventBus<StatusChangedEvent>.Unsubscribe(OnStatusChanged);

            // 清零可避免对象池中的角色下次复用时，短暂显示上一次留下的状态。
            _target = Vector4.zero;
            _current = Vector4.zero;
            _isTransitioning = false;
            ApplyPropertyBlock();
        }

        private void Update()
        {
            if (!_isTransitioning)
                return;

            float maxDelta = Mathf.Max(0.01f, _responseSpeed) * Time.deltaTime;
            Vector4 next = _current;
            next.x = Mathf.MoveTowards(_current.x, _target.x, maxDelta);
            next.y = Mathf.MoveTowards(_current.y, _target.y, maxDelta);
            next.z = Mathf.MoveTowards(_current.z, _target.z, maxDelta);
            next.w = Mathf.MoveTowards(_current.w, _target.w, maxDelta);

            if ((next - _target).sqrMagnitude <= SettleEpsilonSqr)
            {
                next = _target;
                _isTransitioning = false;
            }

            // 只有参数真的改变时才向 Renderer 写数据，稳定状态不会重复 SetPropertyBlock。
            if ((next - _current).sqrMagnitude <= SettleEpsilonSqr)
                return;

            _current = next;
            ApplyPropertyBlock();
        }

        private void OnStatusChanged(StatusChangedEvent statusEvent)
        {
            // EventBus 是全局通道；TargetId 相当于“收件人地址”。
            // 不过滤会导致任意敌人着火时，所有角色一起变色。
            if (statusEvent.TargetId != _targetId)
                return;

            float normalized = statusEvent.IsActive ? Normalize(statusEvent.Intensity) : 0f;
            switch (statusEvent.Kind)
            {
                case StatusKind.Burning:
                    _target.x = normalized;
                    break;
                case StatusKind.Wet:
                    _target.y = normalized;
                    break;
                case StatusKind.Poisoned:
                    _target.z = normalized;
                    break;
                case StatusKind.Sticky:
                    _target.w = normalized;
                    break;
            }

            _isTransitioning = true;
        }

        private void SyncFromStatusController()
        {
            if (_statusController == null)
                return;

            _target = new Vector4(
                Normalize(_statusController.GetIntensity(StatusKind.Burning)),
                Normalize(_statusController.GetIntensity(StatusKind.Wet)),
                Normalize(_statusController.GetIntensity(StatusKind.Poisoned)),
                Normalize(_statusController.GetIntensity(StatusKind.Sticky)));

            // Enable 时直接同步，不播放从 0 渐变到旧状态的错误过渡。
            _current = _target;
            _isTransitioning = false;
            ApplyPropertyBlock();
        }

        private void ApplyPropertyBlock()
        {
            if (_propertyBlock == null || _targetRenderers == null)
                return;

            for (int i = 0; i < _targetRenderers.Length; i++)
            {
                Renderer targetRenderer = _targetRenderers[i];
                if (targetRenderer == null)
                    continue;

                // MaterialPropertyBlock（MPB）保存“这个 Renderer 独有”的 Shader 参数覆盖值。
                // 先 Get 回已有 Block，再只修改自己的四个 Float，能保留受击闪红写入的 _BaseColor。
                targetRenderer.GetPropertyBlock(_propertyBlock);
                _propertyBlock.SetFloat(BurningId, _current.x);
                _propertyBlock.SetFloat(WetId, _current.y);
                _propertyBlock.SetFloat(PoisonedId, _current.z);
                _propertyBlock.SetFloat(StickyId, _current.w);
                targetRenderer.SetPropertyBlock(_propertyBlock);
            }
        }

        private void CacheTargetRenderers()
        {
            if (_targetRenderers != null && _targetRenderers.Length > 0)
                return;

            // 扫描只发生在 Awake。数组分配放在初始化阶段是可接受的，不能移入 Update。
            Renderer[] candidates = GetComponentsInChildren<Renderer>();
            int supportedCount = 0;

            for (int i = 0; i < candidates.Length; i++)
            {
                if (IsCharacterSurfaceRenderer(candidates[i]))
                    supportedCount++;
            }

            _targetRenderers = new Renderer[supportedCount];
            int writeIndex = 0;
            for (int i = 0; i < candidates.Length; i++)
            {
                Renderer candidate = candidates[i];
                if (!IsCharacterSurfaceRenderer(candidate))
                    continue;

                _targetRenderers[writeIndex] = candidate;
                writeIndex++;
            }

            if (_targetRenderers.Length == 0)
                GameLog.Warn("StatusMaterialController 没有找到角色表面 Renderer", "Rendering");
        }

        private static bool IsCharacterSurfaceRenderer(Renderer targetRenderer)
        {
            // 排除 ParticleSystemRenderer、TrailRenderer 等特效 Renderer，
            // 第一版只驱动角色模型常用的静态 Mesh 与蒙皮 Mesh。
            return targetRenderer is MeshRenderer || targetRenderer is SkinnedMeshRenderer;
        }

        private static float Normalize(float statusIntensity)
        {
            // Gameplay 使用便于策划阅读的 0~100；Shader 插值通常使用 0~1。
            // Clamp01 同时防御错误数据，例如 150 会被限制为 1。
            return Mathf.Clamp01(statusIntensity * 0.01f);
        }
    }
}
