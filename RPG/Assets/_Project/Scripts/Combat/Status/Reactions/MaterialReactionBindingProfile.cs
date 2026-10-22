using System;
using Game.Materials;
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// Designer 可编辑的 Material Pair -> Reaction 表。ScriptableObject 只存在于初始化边界，
    /// Runtime 热路径读取其不可变 Snapshot，不逐 Tick 访问序列化数组。
    /// </summary>
    [CreateAssetMenu(
        fileName = "MaterialReactionBindings",
        menuName = "Game/Combat/Material Reaction Bindings")]
    public sealed class MaterialReactionBindingProfile : ScriptableObject
    {
        [Serializable]
        private struct AuthoringBinding
        {
            [SerializeField] private MaterialId _first;
            [SerializeField] private MaterialId _second;
            [SerializeField] private ElementReactionId _reaction;

            public MaterialReactionBinding CreateSnapshot() =>
                new MaterialReactionBinding(_first, _second, _reaction);
        }

        [SerializeField] private AuthoringBinding[] _bindings = Array.Empty<AuthoringBinding>();

        public MaterialReactionCatalogSnapshot CreateSnapshot(MaterialCatalogSnapshot materials)
        {
            if (materials == null) throw new ArgumentNullException(nameof(materials));

            var bindings = new MaterialReactionBinding[_bindings.Length];
            for (int i = 0; i < bindings.Length; i++)
                bindings[i] = _bindings[i].CreateSnapshot();
            return MaterialReactionCatalogSnapshot.Create(materials, bindings);
        }
    }
}
