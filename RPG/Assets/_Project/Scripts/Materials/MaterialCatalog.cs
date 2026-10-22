using System;
using UnityEngine;

namespace Game.Materials
{
    /// <summary>
    /// MaterialDefinition 的显式目录。所有校验集中在初始化边界，Gameplay/Rendering 热路径
    /// 只消费不可变 Snapshot，不使用 Resources.Load、场景查找或 Dictionary/LINQ 查询。
    /// </summary>
    [CreateAssetMenu(fileName = "MaterialCatalog", menuName = "Game/Materials/Material Catalog")]
    public sealed class MaterialCatalog : ScriptableObject
    {
        private const int MaterialIdCapacity = byte.MaxValue + 1;

        [SerializeField] private MaterialDefinition[] _definitions = Array.Empty<MaterialDefinition>();

        public MaterialCatalogSnapshot CreateSnapshot()
        {
            var byId = new MaterialDefinitionSnapshot[MaterialIdCapacity];
            for (int i = 0; i < _definitions.Length; i++)
            {
                MaterialDefinition authoring = _definitions[i];
                if (authoring == null)
                {
                    throw new InvalidOperationException(
                        $"Material Catalog 的第 {i} 项为空；Catalog 不允许 null Definition。");
                }

                MaterialDefinitionSnapshot definition = authoring.CreateSnapshot();
                ValidateDefinition(in definition, i);
                int index = (byte)definition.Id;
                if (byId[index].Id != MaterialId.Empty)
                {
                    throw new InvalidOperationException(
                        $"Material Catalog 包含重复 ID：{definition.Id} ({index})。");
                }

                byId[index] = definition;
            }

            return new MaterialCatalogSnapshot(byId);
        }

        private static void ValidateDefinition(in MaterialDefinitionSnapshot definition, int index)
        {
            if (definition.Id == MaterialId.Empty)
            {
                throw new InvalidOperationException(
                    $"Material Catalog 的第 {index} 项使用 Empty；Empty 是无物质哨兵，不能注册 Definition。");
            }

            if (definition.Behavior == MaterialBehaviorKind.None
                || !Enum.IsDefined(typeof(MaterialBehaviorKind), definition.Behavior))
            {
                throw new InvalidOperationException(
                    $"Material {definition.Id} 使用无效 Behavior：{definition.Behavior}。");
            }
        }
    }
}
