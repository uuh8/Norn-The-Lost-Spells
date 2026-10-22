using UnityEngine;

namespace Game.Materials
{
    /// <summary>
    /// 最小 Material Authoring 数据。这里故意不放 Density、Viscosity、Shader、Status 或 Backend，
    /// 防止 Definition 演变成跨 Simulation/Gameplay/Rendering 的 God Object。
    /// </summary>
    [CreateAssetMenu(fileName = "MaterialDefinition", menuName = "Game/Materials/Material Definition")]
    public sealed class MaterialDefinition : ScriptableObject
    {
        [SerializeField] private MaterialId _id = MaterialId.Empty;
        [SerializeField] private MaterialBehaviorKind _behavior = MaterialBehaviorKind.None;

        public MaterialId Id => _id;
        public MaterialBehaviorKind Behavior => _behavior;

        public MaterialDefinitionSnapshot CreateSnapshot()
        {
            return new MaterialDefinitionSnapshot(_id, _behavior);
        }
    }
}
