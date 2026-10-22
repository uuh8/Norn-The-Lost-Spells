using Game.Materials;

namespace Game.ElementField
{
    /// <summary>
    /// Router 热路径只依赖这个只读 Contract，不读取 ScriptableObject，也不认识 Water Mode。
    /// </summary>
    public interface IMaterialSimulationRouteReadOnly
    {
        bool TryResolve(MaterialId material, out MaterialSimulationBackendKind backend);
    }
}
