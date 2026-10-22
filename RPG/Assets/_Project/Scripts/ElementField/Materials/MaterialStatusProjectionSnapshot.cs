using System;
using Game.Combat;
using Game.Materials;

namespace Game.ElementField
{
    public readonly struct MaterialStatusProjection
    {
        public readonly MaterialId Material;
        public readonly StatusKind Status;
        public readonly float MaximumApplyPerExposureTick;

        public MaterialStatusProjection(MaterialId material, StatusKind status, float maximumApply)
        {
            Material = material;
            Status = status;
            MaximumApplyPerExposureTick = maximumApply;
        }
    }

    /// <summary>初始化期构造的定长规则快照；Exposure 热路径只按索引遍历，不查询 Dictionary。</summary>
    public sealed class MaterialStatusProjectionSnapshot
    {
        private readonly MaterialStatusProjection[] _entries;
        public int Count => _entries.Length;

        public MaterialStatusProjectionSnapshot(
            MaterialCatalogSnapshot catalog,
            MaterialStatusProjectionBinding[] bindings)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            if (bindings == null) throw new ArgumentNullException(nameof(bindings));
            _entries = new MaterialStatusProjection[bindings.Length];
            var seen = new bool[256];
            for (int i = 0; i < bindings.Length; i++)
            {
                MaterialStatusProjectionBinding binding = bindings[i];
                int id = (byte)binding.Material;
                if (binding.Material == MaterialId.Empty
                    || !catalog.TryGet(binding.Material, out _)
                    || seen[id]
                    || binding.MaximumApplyPerExposureTick < 0f
                    || float.IsNaN(binding.MaximumApplyPerExposureTick)
                    || float.IsInfinity(binding.MaximumApplyPerExposureTick))
                {
                    throw new InvalidOperationException($"Invalid material status projection at index {i}.");
                }
                seen[id] = true;
                _entries[i] = new MaterialStatusProjection(
                    binding.Material,
                    binding.Status,
                    binding.MaximumApplyPerExposureTick);
            }
        }

        public MaterialStatusProjection Get(int index) => _entries[index];
    }
}
