using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    [CreateAssetMenu(menuName = "Game/Element Field/Material Status Projection", fileName = "MaterialStatusProjection")]
    public sealed class MaterialStatusProjectionProfile : ScriptableObject
    {
        [SerializeField] private MaterialStatusProjectionBinding[] _bindings =
            System.Array.Empty<MaterialStatusProjectionBinding>();

        public MaterialStatusProjectionSnapshot CreateSnapshot(MaterialCatalogSnapshot catalog) =>
            new MaterialStatusProjectionSnapshot(catalog, _bindings);
    }
}
