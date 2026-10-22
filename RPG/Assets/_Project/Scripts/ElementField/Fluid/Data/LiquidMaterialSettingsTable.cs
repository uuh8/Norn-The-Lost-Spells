using System;
using Game.Materials;

namespace Game.ElementField
{
    /// <summary>固定 256 行的 CPU/GPU 参数源；初始化后只读，热路径按 byte ID 直接索引。</summary>
    public sealed class LiquidMaterialSettingsTable
    {
        public const int Capacity = byte.MaxValue + 1;
        private readonly LiquidMaterialSettings[] _rows = new LiquidMaterialSettings[Capacity];
        private readonly bool[] _valid = new bool[Capacity];
        public bool HasAnyCohesion { get; private set; }
        public bool HasAnyViscosity { get; private set; }

        public LiquidMaterialSettingsTable(LiquidMaterialSettings[] rows)
        {
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            for (int i = 0; i < rows.Length; i++)
            {
                MaterialId material = rows[i].Material;
                // 该构造用于纯数据测试/工具链；生产构造会继续通过 MaterialCatalog 验证 Liquid Behavior。
                // 这里只拒绝 Empty，避免每增加一种正式液体都必须修改基础容器的白名单。
                if (material == MaterialId.Empty)
                    throw new InvalidOperationException("Empty 不能注册为 Liquid Material。");
                Add(in rows[i]);
            }
        }

        public LiquidMaterialSettingsTable(
            MaterialCatalogSnapshot materials,
            LiquidMaterialProfile[] profiles)
        {
            if (materials == null) throw new ArgumentNullException(nameof(materials));
            if (profiles == null) throw new ArgumentNullException(nameof(profiles));
            for (int i = 0; i < profiles.Length; i++)
            {
                if (profiles[i] == null) throw new InvalidOperationException($"Liquid Profile[{i}] 为空。");
                LiquidMaterialSettings row = profiles[i].CreateSettings();
                if (!materials.TryGet(row.Material, out MaterialDefinitionSnapshot definition)
                    || definition.Behavior != MaterialBehaviorKind.Liquid)
                    throw new InvalidOperationException($"{row.Material} 不是 Catalog 中的 Liquid Material。");
                Add(in row);
            }
        }

        public bool TryGet(MaterialId material, out LiquidMaterialSettings settings)
        {
            settings = _rows[(byte)material];
            return material != MaterialId.Empty && _valid[(byte)material];
        }

        public LiquidMaterialAmountScaleSnapshot CreateAmountScaleSnapshot()
        {
            var entries = new LiquidMaterialAmountScale[CountRows()];
            int write = 0;
            for (int i = 1; i < Capacity; i++)
                if (_valid[i]) entries[write++] = new LiquidMaterialAmountScale((MaterialId)i, _rows[i].AmountUnitsPerParticle);
            return LiquidMaterialAmountScaleSnapshot.Create(entries);
        }

        public FluidGpuLiquidMaterialParameters[] CreateGpuRows()
        {
            var rows = new FluidGpuLiquidMaterialParameters[Capacity];
            for (int i = 1; i < Capacity; i++)
                if (_valid[i]) rows[i] = new FluidGpuLiquidMaterialParameters(in _rows[i]);
            return rows;
        }

        private int CountRows()
        {
            int count = 0;
            for (int i = 1; i < Capacity; i++) if (_valid[i]) count++;
            return count;
        }

        private void Add(in LiquidMaterialSettings row)
        {
            int index = (byte)row.Material;
            if (_valid[index]) throw new InvalidOperationException($"重复 Liquid Material Row：{row.Material}。");
            _rows[index] = row;
            _valid[index] = true;
            HasAnyCohesion |= row.CohesionStrength > 0f;
            HasAnyViscosity |= row.Viscosity > 0f;
        }
    }
}
