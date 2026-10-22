using Game.Core;
using Game.ElementField;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Rendering
{
    /// <summary>
    /// Phase A/B 的只读 GPU Particle 可视化器。每个 particle slot 由 Shader 用 SV_VertexID 展开成
    /// 六个 camera-facing triangle vertices；它不回读 VRAM，也不会参与后续 P7 正式 Renderer。
    /// </summary>
    public sealed class GpuFluidParticleDebugRenderer : MonoBehaviour
    {
        private static readonly int PositionsId = Shader.PropertyToID("_FluidPositions");
        private static readonly int ParticleSizeId = Shader.PropertyToID("_FluidParticleSize");
        private static readonly int ParticleColorId = Shader.PropertyToID("_FluidParticleColor");

        [Tooltip("应引用实现 IFluidGpuSource 的 Runtime；用 MonoBehaviour 序列化以避免 Unity 不支持接口字段。")]
        [SerializeField] private MonoBehaviour _fluidSourceComponent;
        [SerializeField] private Material _material;
        [SerializeField, Min(0.001f)] private float _particleSize = 0.12f;
        [SerializeField] private Color _particleColor = new Color(0.2f, 0.7f, 1f, 0.8f);

        private IFluidGpuSource _fluidSource;
        private MaterialPropertyBlock _materialPropertyBlock;
        private RenderParams _renderParams;
        private bool _hasReportedConfigurationFailure;

        private void Awake()
        {
            _fluidSource = _fluidSourceComponent as IFluidGpuSource;
            if (_fluidSource == null || _material == null)
            {
                ReportConfigurationFailure("GpuFluidParticleDebugRenderer 缺少 IFluidGpuSource 或 Material，组件已禁用。");
                enabled = false;
                return;
            }

            // MaterialPropertyBlock 是长期对象；每帧复用而非 new，避免 Debug Renderer 自身制造 GC Alloc。
            _materialPropertyBlock = new MaterialPropertyBlock();
            _renderParams = new RenderParams(_material)
            {
                matProps = _materialPropertyBlock,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
        }

        private void Update()
        {
            if (_fluidSource == null || !_fluidSource.TryGetGpuSnapshot(out FluidGpuSnapshot snapshot))
                return;

            if (snapshot.ParticleCapacity > int.MaxValue / 6)
            {
                ReportConfigurationFailure("Fluid particle capacity 超出 Debug Billboard 的 Int32 顶点上限，组件已禁用。");
                enabled = false;
                return;
            }

            _materialPropertyBlock.SetBuffer(PositionsId, snapshot.Positions);
            _materialPropertyBlock.SetFloat(ParticleSizeId, _particleSize);
            _materialPropertyBlock.SetColor(ParticleColorId, _particleColor);
            _renderParams.worldBounds = snapshot.ActiveBounds;
            Graphics.RenderPrimitives(
                _renderParams,
                MeshTopology.Triangles,
                snapshot.ParticleCapacity * 6);
        }

        private void ReportConfigurationFailure(string message)
        {
            if (_hasReportedConfigurationFailure)
                return;

            _hasReportedConfigurationFailure = true;
            GameLog.Warn(message, "Rendering");
        }
    }
}
