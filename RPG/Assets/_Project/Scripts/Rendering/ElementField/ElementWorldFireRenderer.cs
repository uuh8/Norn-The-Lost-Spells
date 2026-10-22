using Game.Materials;
using System;
using Game.Core;
using Game.ElementField;
using Unity.Profiling;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 把稀疏连续世界中可见的 Fire Cell 映射到共享的 Body/Core/Sparks Particle Systems。
    ///
    /// Gameplay 仍由 ElementWorldRuntime 持有；本组件只读取 IElementWorldReadOnly，按视觉预算抽样，
    /// 不会修改 Fire Amount，也不会为每个 Cell/Chunk 创建 ParticleSystem 或 GameObject。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ElementWorldFireRenderer : MonoBehaviour
    {
        private const uint BodyLayerSeed = 0xA341316Cu;
        private const uint CoreLayerSeed = 0xC8013EA4u;
        private const uint SparksLayerSeed = 0xAD90777Du;
        private const uint SharedPositionSeed = 0x7E95761Eu;

        private static readonly ProfilerMarker EmitMarker =
            new ProfilerMarker("ElementWorldFire.Emit");

        [Header("Read Only World")]
        [Tooltip("拖入场景中的 ElementWorldRuntime；Rendering 只通过 IElementWorldReadOnly 读取。")]
        [SerializeField] private ElementWorldRuntime _worldRuntime;

        [Header("Presentation Data")]
        [Tooltip("复用 P6-A 的 Fire 粒子抽样、概率、尺寸与 Jitter 配置。")]
        [SerializeField] private ElementFireRenderProfile _profile;

        [Header("Shared Three-Layer Particle Systems")]
        [Tooltip("Alpha Blend 外焰主体。允许暂时为空，但至少要连接一层。")]
        [SerializeField] private ParticleSystem _bodyParticleSystem;

        [Tooltip("Additive 高温亮芯。")]
        [SerializeField] private ParticleSystem _coreParticleSystem;

        [Tooltip("少量高速火星。")]
        [SerializeField] private ParticleSystem _sparksParticleSystem;

        [Header("Runtime Debug (Read Only In Play Mode)")]
        [SerializeField] private int _lastVisibleElementChunkCount;
        [SerializeField] private int _lastActiveFireCellCount;
        [SerializeField] private int _lastSampledFireCellCount;
        [SerializeField] private int _lastBodyEmissionCount;
        [SerializeField] private int _lastCoreEmissionCount;
        [SerializeField] private int _lastSparksEmissionCount;
        [SerializeField] private uint _visualSequence;

        private IElementWorldReadOnly _world;
        private ElementChunkKey[] _visibleKeys;
        private ParticleLayerState _bodyLayer;
        private ParticleLayerState _coreLayer;
        private ParticleLayerState _sparksLayer;
        private float _emissionAccumulator;
        private bool _isInitialized;

        private void Start()
        {
            // Runtime 在 Awake 创建 Store；Renderer 在 Start 取得只读接口，避免依赖跨物体 Awake 顺序。
            if (!TryInitialize())
            {
                enabled = false;
                return;
            }

            EmitFromVisibleFireCells();
        }

        private void Update()
        {
            if (!_isInitialized || !_world.IsInitialized || Time.timeScale <= 0f)
                return;

            _emissionAccumulator += Time.deltaTime;
            if (_emissionAccumulator < _profile.EmissionInterval)
                return;

            // Presentation 不追赶卡顿期间积累的视觉 Tick，否则恢复帧会集中补发粒子并制造 Overdraw 峰值。
            _emissionAccumulator = 0f;
            EmitFromVisibleFireCells();
        }

        private bool TryInitialize()
        {
            if (_isInitialized)
                return true;
            if (_worldRuntime == null)
            {
                GameLog.Error("ElementWorldFireRenderer requires an ElementWorldRuntime.", "Rendering");
                return false;
            }
            if (_profile == null)
            {
                GameLog.Error("ElementWorldFireRenderer requires an ElementFireRenderProfile.", "Rendering");
                return false;
            }
            if (_bodyParticleSystem == null
                && _coreParticleSystem == null
                && _sparksParticleSystem == null)
            {
                GameLog.Error("ElementWorldFireRenderer requires at least one ParticleSystem layer.", "Rendering");
                return false;
            }

            _world = _worldRuntime;
            if (!_world.IsInitialized)
            {
                GameLog.Error(
                    "ElementWorldFireRenderer cannot initialize before ElementWorldRuntime.",
                    "Rendering");
                return false;
            }

            try
            {
                // Snapshot Buffer 一次分配并长期复用；视觉 Tick 热路径只覆盖数组内容，不产生 GC Alloc。
                _visibleKeys = new ElementChunkKey[_world.MaximumResidentChunkCount];
            }
            catch (OverflowException)
            {
                GameLog.Error("ElementWorldFireRenderer capacity exceeds Int32 limits.", "Rendering");
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
                // EmitParams.position 将按 World Space 解释；已生成的火焰不会随 Renderer 根物体移动。
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                GameLog.Warn(
                    $"ParticleSystem '{particleSystem.name}' was forced to World Simulation Space.",
                    "Rendering");
            }

            // 关闭内置 Emission，只保留本组件的显式 Emit；否则 Prefab Shape 还会额外自动冒火。
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

        private void EmitFromVisibleFireCells()
        {
            using (EmitMarker.Auto())
            {
                _lastVisibleElementChunkCount = _world.CopyVisibleChunkKeys(_visibleKeys);
                int activeCount = CountActiveFireCells();
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
                int chunkSize = _world.ChunkSize;
                float cellSize = _world.CellSize;
                Vector3 origin = _world.Origin;

                // CopyVisibleChunkKeys 已按距离和 Key 稳定排序；局部循环顺序也固定。
                // Ordinal 只控制预算轮换，真正的随机身份始终来自 Global Cell。
                for (int chunkIndex = 0; chunkIndex < _lastVisibleElementChunkCount; chunkIndex++)
                {
                    ElementChunkKey chunkKey = _visibleKeys[chunkIndex];
                    for (int z = 0; z < chunkSize; z++)
                    for (int y = 0; y < chunkSize; y++)
                    for (int x = 0; x < chunkSize; x++)
                    {
                        Vector3Int globalCell = ElementWorldCoordinates.ComposeGlobalCell(
                            chunkKey,
                            new Vector3Int(x, y, z),
                            chunkSize);
                        if (!_world.TryGetCell(globalCell, out ElementCell cell)
                            || cell.IsEmpty
                            || cell.MaterialKind != MaterialId.Fire)
                        {
                            continue;
                        }

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
                            (globalCell.x + 0.5f) * cellSize,
                            (globalCell.y + 0.5f) * cellSize,
                            (globalCell.z + 0.5f) * cellSize);
                        Vector3 position = cellCenter + FireCellEmissionSampler.CalculateJitter(
                            globalCell,
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
                                globalCell,
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
                                globalCell,
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
                                globalCell,
                                cell.Amount,
                                _profile.SparkProbabilityAtFullAmount,
                                position,
                                normalizedAmount,
                                sizeMultiplier))
                        {
                            _lastSparksEmissionCount++;
                        }
                    }
                }

                _visualSequence++;
            }
        }

        private int CountActiveFireCells()
        {
            int count = 0;
            int chunkSize = _world.ChunkSize;
            for (int chunkIndex = 0; chunkIndex < _lastVisibleElementChunkCount; chunkIndex++)
            {
                ElementChunkKey chunkKey = _visibleKeys[chunkIndex];
                for (int z = 0; z < chunkSize; z++)
                for (int y = 0; y < chunkSize; y++)
                for (int x = 0; x < chunkSize; x++)
                {
                    Vector3Int globalCell = ElementWorldCoordinates.ComposeGlobalCell(
                        chunkKey,
                        new Vector3Int(x, y, z),
                        chunkSize);
                    if (_world.TryGetCell(globalCell, out ElementCell cell)
                        && !cell.IsEmpty
                        && cell.MaterialKind == MaterialId.Fire)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private bool TryEmitLayer(
            ParticleLayerState layer,
            Vector3Int globalCell,
            byte amount,
            float probabilityAtFullAmount,
            Vector3 position,
            float normalizedAmount,
            float sizeMultiplier)
        {
            if (layer == null
                || layer.System.particleCount >= layer.MaxParticles
                || !FireCellEmissionSampler.ShouldEmit(
                    globalCell,
                    _visualSequence,
                    layer.LayerSeed,
                    amount,
                    probabilityAtFullAmount))
            {
                return false;
            }

            float sizeRandom = FireCellEmissionSampler.Hash01(
                globalCell, _visualSequence, layer.LayerSeed, channel: 4u);
            float colorRandom = FireCellEmissionSampler.Hash01(
                globalCell, _visualSequence, layer.LayerSeed, channel: 5u);
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
