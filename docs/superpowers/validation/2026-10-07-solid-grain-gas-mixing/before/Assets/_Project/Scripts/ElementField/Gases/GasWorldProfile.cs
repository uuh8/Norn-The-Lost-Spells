using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    [Serializable]
    public struct GasMaterialBehavior
    {
        public MaterialId Material;
        [Range(0f, 10f)] public float DiffusionPerSecond;
        [Range(-5f, 5f)] public float BuoyancyPerSecond;
    }
    [CreateAssetMenu(menuName = "Game/Element Field/Gas World", fileName = "GasWorld")]
    public sealed class GasWorldProfile : ScriptableObject
    {
        [SerializeField, Min(1)] private int _maximumChunks = 256;
        // Gas 的 GMU/体积必须低于 Liquid；这是真实浓度上限，不能拿“显示透明度”代替密度。
        [SerializeField, Min(1)] private int _cellCapacityGmu = 32;
        [Tooltip("出生时只填充这部分浓度，预留扩散/重叠空间；质量决定气团格数，不凭空删量。")]
        [SerializeField, Range(.1f,1f)] private float _initialConcentration = .5f;
        [SerializeField, Min(1)] private int _maximumActiveCells = 8192;
        [SerializeField, Min(1)] private int _maximumCellsPerTick = 1024;
        [SerializeField, Min(1)] private int _maximumPendingDeposits = 128;
        [SerializeField, Min(1)] private int _maximumTerrainSamplesPerTick = 128;
        [Tooltip("所有命中共用的出生候选扫描上限；Physics 预算独立，不随此数增长。")]
        [SerializeField, Range(128,4096)] private int _maximumBirthScansPerTick = 2048;
        [Tooltip("世界空间 Wind Velocity，单位 m/s；影响真实气体数量与接触范围。墙面仍由地形阻挡。")]
        [SerializeField] private Vector3 _windVelocity = new Vector3(.25f,0f,.08f);
        [Header("Extremely Dilute Gas")]
        [Tooltip("0 关闭尾雾消散。生产值 1 GMU/格；混合气体总浓度也必须满足此门槛。")]
        [SerializeField, Min(0)] private int _diluteMaximumCellGmu;
        [Tooltip("整个 Chunk 的平均浓度上限；局部格和平均值同时极低才清理。")]
        [SerializeField, Range(0f,.02f)] private float _diluteMaximumChunkConcentration=.005f;
        [SerializeField, Min(1f)] private float _diluteDelaySeconds=3f;
        [SerializeField, Range(1,8)] private int _diluteChunksPerTick=2;
        [SerializeField] private GasMaterialBehavior[] _materials =
        {
            new GasMaterialBehavior { Material = MaterialId.Steam, DiffusionPerSecond = 1f, BuoyancyPerSecond = .6f },
            new GasMaterialBehavior { Material = MaterialId.ToxicVapor, DiffusionPerSecond = 1f, BuoyancyPerSecond = 0f }
        };
        public int MaximumChunks => _maximumChunks;
        public uint CellCapacityGmu => (uint)_cellCapacityGmu;
        public float InitialConcentration => _initialConcentration;
        public uint DepositCellAmountGmu => Math.Max(1u,(uint)(CellCapacityGmu*_initialConcentration));
        public int MaximumActiveCells => _maximumActiveCells;
        public int MaximumCellsPerTick => _maximumCellsPerTick;
        public int MaximumPendingDeposits => _maximumPendingDeposits;
        public int MaximumTerrainSamplesPerTick => _maximumTerrainSamplesPerTick;
        public int MaximumBirthScansPerTick => _maximumBirthScansPerTick;
        public Vector3 WindVelocity => _windVelocity;
        public uint DiluteMaximumCellGmu => (uint)Math.Max(0,_diluteMaximumCellGmu);
        public float DiluteMaximumChunkConcentration => _diluteMaximumChunkConcentration;
        public float DiluteDelaySeconds => _diluteDelaySeconds;
        public int DiluteChunksPerTick => _diluteChunksPerTick;
        public int MaterialCount => _materials != null ? _materials.Length : 0;
        public GasMaterialBehavior GetMaterial(int index) => _materials[index];
    }
}
