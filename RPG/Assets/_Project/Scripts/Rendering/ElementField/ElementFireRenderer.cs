using Game.Materials;
using Game.Core;
using Game.ElementField;
using Unity.Profiling;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 把只读 Fire Cell Snapshot 转换为一组三层共享 ParticleSystem 的手动发射请求。
    ///
    /// Gameplay 决定哪里有 Fire、Amount 是多少；FireCellEmissionSampler 决定视觉抽样；
    /// 本组件只负责固定视觉 Tick、Field 扫描和 ParticleSystem.EmitParams。它没有 Cell 写权限，
    /// 也不会为每个 Cell 创建 GameObject 或 ParticleSystem。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ElementFireRenderer : MonoBehaviour
    {
        private const uint BodyLayerSeed = 0xA341316Cu;
        private const uint CoreLayerSeed = 0xC8013EA4u;
        private const uint SparksLayerSeed = 0xAD90777Du;
        private const uint SharedPositionSeed = 0x7E95761Eu;

        private static readonly ProfilerMarker EmitMarker =
            new ProfilerMarker("ElementFire.Emit");

        [Header("Read Only Field")]
        [Tooltip("拖入场景中的 ElementFieldRuntime；Renderer 只读取 IElementFieldReadOnly。")]
        [SerializeField] private ElementFieldRuntime _fieldRuntime;

        [Header("Presentation Data")]
        [Tooltip("只保存 Fire 粒子抽样、概率、尺寸与 Jitter，不进入 Gameplay Simulation。")]
        [SerializeField] private ElementFireRenderProfile _profile;

        [Header("Shared Three-Layer Particle Systems")]
        [Tooltip("Alpha Blend 外焰主体。允许暂时为空，但至少要连接一层。")]
        [SerializeField] private ParticleSystem _bodyParticleSystem;

        [Tooltip("Additive 高温亮芯。")]
        [SerializeField] private ParticleSystem _coreParticleSystem;

        [Tooltip("少量高速火星。")]
        [SerializeField] private ParticleSystem _sparksParticleSystem;

        [Header("Runtime Debug (Read Only In Play Mode)")]
        [SerializeField] private int _lastActiveFireCellCount;
        [SerializeField] private int _lastSampledFireCellCount;
        [SerializeField] private int _lastBodyEmissionCount;
        [SerializeField] private int _lastCoreEmissionCount;
        [SerializeField] private int _lastSparksEmissionCount;
        [SerializeField] private uint _visualSequence;

        private IElementFieldReadOnly _field;
        private ParticleLayerState _bodyLayer;
        private ParticleLayerState _coreLayer;
        private ParticleLayerState _sparksLayer;
        private float _emissionAccumulator;
        private bool _isInitialized;

        private void Start()
        {
            // ElementFieldRuntime 在 Awake 创建 Grid；Start 再取得只读接口，避免依赖不同
            // GameObject 之间未声明的 Awake 顺序。初始化成功后立即发射一次，避免等待 0.1s 才可见。
            if (!TryInitialize())
            {
                enabled = false;
                return;
            }

            EmitFromFireCells();
        }

        private void Update()
        {
            // Wand Editor/暂停菜单会把 Time.timeScale 设为 0；Presentation 应与 Gameplay 一起暂停。
            if (!_isInitialized || !_field.IsInitialized || Time.timeScale <= 0f)
                return;

            _emissionAccumulator += Time.deltaTime;
            if (_emissionAccumulator < _profile.EmissionInterval)
                return;

            // 粒子表现不追赶卡顿期间错过的视觉 Tick。一次卡顿后同帧补发多轮只会造成 Overdraw 尖峰，
            // 也不会提高 Gameplay 正确性，因此每帧最多扫描/发射一次并丢弃旧视觉债务。
            _emissionAccumulator = 0f;
            EmitFromFireCells();
        }

        private bool TryInitialize()
        {
            if (_isInitialized)
                return true;
            if (_fieldRuntime == null)
            {
                GameLog.Error("ElementFireRenderer requires an ElementFieldRuntime.", "Rendering");
                return false;
            }
            if (_profile == null)
            {
                GameLog.Error("ElementFireRenderer requires an ElementFireRenderProfile.", "Rendering");
                return false;
            }
            if (_bodyParticleSystem == null
                && _coreParticleSystem == null
                && _sparksParticleSystem == null)
            {
                GameLog.Error("ElementFireRenderer requires at least one ParticleSystem layer.", "Rendering");
                return false;
            }

            _field = _fieldRuntime.ReadOnlyField;
            if (_field == null || !_field.IsInitialized)
            {
                GameLog.Error(
                    "ElementFireRenderer cannot initialize before ElementFieldRuntime.",
                    "Rendering");
                return false;
            }

            _bodyLayer = CreateLayer(_bodyParticleSystem, BodyLayerSeed);
            _coreLayer = CreateLayer(_coreParticleSystem, CoreLayerSeed);
            _sparksLayer = CreateLayer(_sparksParticleSystem, SparksLayerSeed);
            _emissionAccumulator = 0f;
            _isInitialized = true;
            return true;
        }

        private static ParticleLayerState CreateLayer(ParticleSystem particleSystem, uint layerSeed)
        {
            if (particleSystem == null)
                return null;

            ParticleSystem.MainModule main = particleSystem.main;
            if (main.simulationSpace != ParticleSystemSimulationSpace.World)
            {
                // EmitParams.position 使用 Particle System 的 Simulation Space。强制 World 后，
                // 已发射粒子不会因为 FireRenderer 根物体移动而从旧火区“瞬移”到新位置。
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                GameLog.Warn(
                    $"ParticleSystem '{particleSystem.name}' was forced to World Simulation Space.",
                    "Rendering");
            }

            // 内置 Emission Module 若仍在自动发射，会与 Cell 驱动的 EmitParams 叠加并从 Prefab
            // Shape 位置冒火。关闭它不会阻止显式 ParticleSystem.Emit 调用。
            ParticleSystem.EmissionModule emission = particleSystem.emission;
            emission.enabled = false;
            if (!particleSystem.isPlaying)
                particleSystem.Play(withChildren: false);

            return new ParticleLayerState(
                particleSystem,
                main.startSize,
                main.startColor,
                Mathf.Max(1, main.maxParticles),
                layerSeed);
        }

        private void EmitFromFireCells()
        {
            using (EmitMarker.Auto())
            {
                Vector3Int dimensions = _field.Dimensions;
                int activeCount = CountActiveFireCells(dimensions);
                _lastActiveFireCellCount = activeCount;
                _lastSampledFireCellCount = 0;
                _lastBodyEmissionCount = 0;
                _lastCoreEmissionCount = 0;
                _lastSparksEmissionCount = 0;

                if (activeCount == 0)
                {
                    _visualSequence++;
                    return;
                }

                int activeOrdinal = 0;
                int cellIndex = 0;
                float cellSize = _field.CellSize;
                Vector3 origin = _field.Origin;

                // X 为最内层循环，与 ElementField 的线性索引协议一致：
                // index = x + sizeX * (y + sizeY * z)。cellIndex 因此可以无额外函数调用地递增。
                for (int z = 0; z < dimensions.z; z++)
                for (int y = 0; y < dimensions.y; y++)
                for (int x = 0; x < dimensions.x; x++, cellIndex++)
                {
                    ElementCell cell = _field.GetCell(x, y, z);
                    if (cell.IsEmpty || cell.MaterialKind != MaterialId.Fire)
                        continue;

                    int currentOrdinal = activeOrdinal;
                    activeOrdinal++;
                    if (!FireCellEmissionSampler.ShouldSampleActiveOrdinal(
                            currentOrdinal,
                            activeCount,
                            _profile.MaxSampledFireCells,
                            _visualSequence))
                    {
                        continue;
                    }

                    _lastSampledFireCellCount++;
                    Vector3 cellCenter = origin + new Vector3(
                        (x + 0.5f) * cellSize,
                        (y + 0.5f) * cellSize,
                        (z + 0.5f) * cellSize);
                    Vector3 position = cellCenter + FireCellEmissionSampler.CalculateJitter(
                        cellIndex,
                        _visualSequence,
                        SharedPositionSeed,
                        cellSize,
                        _profile.VerticalJitter);

                    float normalizedAmount = FireCellEmissionSampler.NormalizeAmount(cell.Amount);
                    float sizeMultiplier = FireCellEmissionSampler.CalculateSizeMultiplier(
                        cell.Amount,
                        _profile.MinSizeMultiplier,
                        _profile.MaxSizeMultiplier);

                    if (TryEmitLayer(
                            _bodyLayer,
                            cellIndex,
                            cell.Amount,
                            _profile.BodyProbabilityAtFullAmount,
                            position,
                            normalizedAmount,
                            sizeMultiplier))
                    {
                        _lastBodyEmissionCount++;
                    }

                    if (TryEmitLayer(
                            _coreLayer,
                            cellIndex,
                            cell.Amount,
                            _profile.CoreProbabilityAtFullAmount,
                            position,
                            normalizedAmount,
                            sizeMultiplier))
                    {
                        _lastCoreEmissionCount++;
                    }

                    if (TryEmitLayer(
                            _sparksLayer,
                            cellIndex,
                            cell.Amount,
                            _profile.SparkProbabilityAtFullAmount,
                            position,
                            normalizedAmount,
                            sizeMultiplier))
                    {
                        _lastSparksEmissionCount++;
                    }
                }

                _visualSequence++;
            }
        }

        private int CountActiveFireCells(Vector3Int dimensions)
        {
            int count = 0;
            for (int z = 0; z < dimensions.z; z++)
            for (int y = 0; y < dimensions.y; y++)
            for (int x = 0; x < dimensions.x; x++)
            {
                ElementCell cell = _field.GetCell(x, y, z);
                if (!cell.IsEmpty && cell.MaterialKind == MaterialId.Fire)
                    count++;
            }

            return count;
        }

        private bool TryEmitLayer(
            ParticleLayerState layer,
            int cellIndex,
            byte amount,
            float probabilityAtFullAmount,
            Vector3 position,
            float normalizedAmount,
            float sizeMultiplier)
        {
            if (layer == null
                || layer.System.particleCount >= layer.MaxParticles
                || !FireCellEmissionSampler.ShouldEmit(
                    cellIndex,
                    _visualSequence,
                    layer.LayerSeed,
                    amount,
                    probabilityAtFullAmount))
            {
                return false;
            }

            // Evaluate 的 lerpFactor 同样来自确定性 Hash，因此 Two Constants/Two Colors 仍能保留
            // 5A～5D Prefab 的随机区间，同时不触碰 UnityEngine.Random 的全局状态。
            float sizeRandom = FireCellEmissionSampler.Hash01(
                cellIndex, _visualSequence, layer.LayerSeed, channel: 4u);
            float colorRandom = FireCellEmissionSampler.Hash01(
                cellIndex, _visualSequence, layer.LayerSeed, channel: 5u);
            float baseStartSize = layer.BaseStartSize.Evaluate(0f, sizeRandom);
            Color startColor = layer.BaseStartColor.Evaluate(0f, colorRandom);
            startColor.a *= normalizedAmount;

            ParticleSystem.EmitParams emitParams = default;
            emitParams.position = position;
            emitParams.startSize = baseStartSize * sizeMultiplier;
            emitParams.startColor = startColor;
            layer.System.Emit(emitParams, count: 1);
            return true;
        }

        private sealed class ParticleLayerState
        {
            public ParticleLayerState(
                ParticleSystem system,
                ParticleSystem.MinMaxCurve baseStartSize,
                ParticleSystem.MinMaxGradient baseStartColor,
                int maxParticles,
                uint layerSeed)
            {
                System = system;
                BaseStartSize = baseStartSize;
                BaseStartColor = baseStartColor;
                MaxParticles = maxParticles;
                LayerSeed = layerSeed;
            }

            public ParticleSystem System { get; }
            public ParticleSystem.MinMaxCurve BaseStartSize { get; }
            public ParticleSystem.MinMaxGradient BaseStartColor { get; }
            public int MaxParticles { get; }
            public uint LayerSeed { get; }
        }
    }
}
