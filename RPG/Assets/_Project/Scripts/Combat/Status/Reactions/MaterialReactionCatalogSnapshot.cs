using System;
using Game.Materials;

namespace Game.Combat
{
    /// <summary>
    /// 无序 Material Pair 的 O(1) 查找表。MaterialId 是 byte，所以把较小 ID 放高 8 位、较大 ID
    /// 放低 8 位即可得到唯一 Key；查询不需要 Dictionary、Hash 或运行时分配。
    /// </summary>
    public sealed class MaterialReactionCatalogSnapshot
    {
        private const int PairCapacity = (byte.MaxValue + 1) * (byte.MaxValue + 1);
        private readonly ElementReactionId[] _reactions;
        private readonly bool[] _hasBinding;

        private MaterialReactionCatalogSnapshot(
            ElementReactionId[] reactions,
            bool[] hasBinding)
        {
            _reactions = reactions;
            _hasBinding = hasBinding;
        }

        public static MaterialReactionCatalogSnapshot Create(
            MaterialCatalogSnapshot materials,
            MaterialReactionBinding[] bindings)
        {
            if (materials == null) throw new ArgumentNullException(nameof(materials));
            if (bindings == null) throw new ArgumentNullException(nameof(bindings));

            var reactions = new ElementReactionId[PairCapacity];
            var hasBinding = new bool[PairCapacity];
            for (int i = 0; i < bindings.Length; i++)
            {
                MaterialReactionBinding binding = bindings[i];
                ValidateBinding(materials, in binding, i);
                int key = PairKey(binding.First, binding.Second);
                if (hasBinding[key])
                {
                    throw new InvalidOperationException(
                        $"Reaction Binding[{i}] 重复注册无序 Material Pair：{binding.First} + {binding.Second}。");
                }

                reactions[key] = binding.Reaction;
                hasBinding[key] = true;
            }

            return new MaterialReactionCatalogSnapshot(reactions, hasBinding);
        }

        public bool TryResolve(
            MaterialId first,
            MaterialId second,
            out ElementReactionId reaction)
        {
            int key = PairKey(first, second);
            reaction = _reactions[key];
            return first != MaterialId.Empty && first != second && _hasBinding[key];
        }

        private static void ValidateBinding(
            MaterialCatalogSnapshot materials,
            in MaterialReactionBinding binding,
            int bindingIndex)
        {
            if (binding.First == MaterialId.Empty || binding.Second == MaterialId.Empty)
                throw new InvalidOperationException($"Reaction Binding[{bindingIndex}] 不允许使用 Empty Material。");
            if (binding.First == binding.Second)
                throw new InvalidOperationException($"Reaction Binding[{bindingIndex}] 的两侧 Material 必须不同。");
            if (!materials.TryGet(binding.First, out _))
                throw new InvalidOperationException($"Reaction Binding[{bindingIndex}] 的 {binding.First} 不在 Material Catalog 中。");
            if (!materials.TryGet(binding.Second, out _))
                throw new InvalidOperationException($"Reaction Binding[{bindingIndex}] 的 {binding.Second} 不在 Material Catalog 中。");
            if (!Enum.IsDefined(typeof(ElementReactionId), binding.Reaction))
                throw new InvalidOperationException($"Reaction Binding[{bindingIndex}] 使用无效 Reaction：{binding.Reaction}。");
        }

        private static int PairKey(MaterialId first, MaterialId second)
        {
            int a = (byte)first;
            int b = (byte)second;
            return a <= b ? (a << 8) | b : (b << 8) | a;
        }
    }
}
