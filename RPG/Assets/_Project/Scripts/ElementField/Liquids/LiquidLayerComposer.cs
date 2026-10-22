using System;
using Game.Materials;

namespace Game.ElementField
{
    /// <summary>
    /// 数量→液层的纯逻辑。Density目录初始化后不变；Compose只覆盖固定数组，
    /// 同密度份额保留每种材料身份，不把混色解释成Material转换。
    /// </summary>
    public sealed class LiquidLayerComposer
    {
        private readonly MaterialId[] _materials;
        private readonly int[] _lookup = new int[256], _groups;
        private readonly float[] _densities, _groupDensities, _shares;
        private readonly uint[] _groupQuantities;
        private readonly LiquidLayerSample[] _layers;
        private readonly int _capacity;
        public int MaterialCount => _materials.Length;
        public int DensityGroupCount { get; }

        public LiquidLayerComposer(MaterialId[] materials, float[] densities, int capacity)
        {
            if (materials == null || densities == null || materials.Length == 0
                || materials.Length != densities.Length || capacity < 1)
                throw new ArgumentException("液层目录或容量无效。");
            _materials = (MaterialId[])materials.Clone();
            _densities = (float[])densities.Clone();
            _groups = new int[materials.Length]; _shares = new float[materials.Length];
            _groupDensities = new float[materials.Length];
            _groupQuantities = new uint[materials.Length];
            _layers = new LiquidLayerSample[materials.Length]; _capacity = capacity;
            for (int i = 0; i < _lookup.Length; i++) _lookup[i] = -1;
            int count = 0;
            for (int i = 0; i < materials.Length; i++)
            {
                if (!float.IsFinite(densities[i]) || densities[i] <= 0f
                    || materials[i] == MaterialId.Empty || _lookup[(byte)materials[i]] >= 0)
                    throw new ArgumentException("Density必须为有限正数，材料不能重复。");
                _lookup[(byte)materials[i]] = i;
                bool found = false;
                for (int g = 0; g < count; g++) found |= _groupDensities[g] == densities[i];
                if (!found) _groupDensities[count++] = densities[i];
            }
            // 从重到轻排序一次；确切相同的SO值同组，避免epsilon归组的非传递性。
            for (int i = 0; i < count; i++) for (int j = i + 1; j < count; j++)
                if (_groupDensities[j] > _groupDensities[i])
                { float t = _groupDensities[i]; _groupDensities[i] = _groupDensities[j]; _groupDensities[j] = t; }
            for (int i = 0; i < materials.Length; i++) for (int g = 0; g < count; g++)
                if (_densities[i] == _groupDensities[g]) _groups[i] = g;
            DensityGroupCount = count;
        }

        public MaterialId GetMaterial(int slot) => _materials[slot];
        public int GetDensityGroup(MaterialId material) => _lookup[(byte)material] >= 0
            ? _groups[_lookup[(byte)material]] : -1;

        public void Compose(uint[] quantities)
        {
            if (quantities == null || quantities.Length < MaterialCount) throw new ArgumentException();
            Array.Clear(_groupQuantities, 0, _groupQuantities.Length);
            for (int i = 0; i < MaterialCount; i++) _groupQuantities[_groups[i]] += quantities[i];
            float bottom = 0f;
            for (int g = 0; g < DensityGroupCount; g++)
            {
                float thickness = _groupQuantities[g] / (float)_capacity;
                _layers[g] = new LiquidLayerSample(g, bottom, thickness); bottom += thickness;
            }
            for (int i = 0; i < MaterialCount; i++) _shares[i] = _groupQuantities[_groups[i]] == 0
                ? 0f : quantities[i] / (float)_groupQuantities[_groups[i]];
        }

        public LiquidLayerSample GetLayer(MaterialId material) => GetLayer(GetDensityGroup(material));
        public LiquidLayerSample GetLayer(int group) => group >= 0 && group < DensityGroupCount
            ? _layers[group] : default;
        public float GetShare(MaterialId material) => _lookup[(byte)material] >= 0
            ? _shares[_lookup[(byte)material]] : 0f;
    }
}
