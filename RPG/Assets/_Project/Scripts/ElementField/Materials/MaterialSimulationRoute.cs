using Game.Materials;

namespace Game.ElementField
{
    /// <summary>
    /// 初始化阶段从 Authoring Profile 复制出的单条不可变路由值。
    /// </summary>
    public readonly struct MaterialSimulationRoute
    {
        public readonly MaterialId Material;
        public readonly MaterialSimulationBackendKind Backend;

        public MaterialSimulationRoute(MaterialId material, MaterialSimulationBackendKind backend)
        {
            Material = material;
            Backend = backend;
        }
    }
}
