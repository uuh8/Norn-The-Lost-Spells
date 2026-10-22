using System;
using Game.Materials;

namespace Game.ElementField
{
    /// <summary>
    /// 初始化完成后发布的最终 Backend 表。bool[] 单独表示“是否有 Route”，因此
    /// Backend=Unsupported(0) 仍能和未知 Material 清晰区分。
    /// </summary>
    public sealed class MaterialSimulationRouteSnapshot : IMaterialSimulationRouteReadOnly
    {
        private const int Capacity = byte.MaxValue + 1;
        private readonly MaterialSimulationBackendKind[] _backends;
        private readonly bool[] _hasRoute;

        private MaterialSimulationRouteSnapshot(
            MaterialSimulationBackendKind[] backends,
            bool[] hasRoute)
        {
            _backends = backends;
            _hasRoute = hasRoute;
        }

        public static MaterialSimulationRouteSnapshot Create(
            MaterialCatalogSnapshot catalog,
            MaterialSimulationRoutingProfile profile,
            in WaterSimulationCompatibilityDecision waterDecision)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            var backends = new MaterialSimulationBackendKind[Capacity];
            var hasRoute = new bool[Capacity];
            AddWaterRoute(catalog, in waterDecision, backends, hasRoute);

            for (int i = 0; i < profile.RouteCount; i++)
            {
                MaterialSimulationRoute route = profile.GetRoute(i);
                AddProfileRoute(catalog, in route, i, backends, hasRoute);
            }

            return new MaterialSimulationRouteSnapshot(backends, hasRoute);
        }

        public bool TryResolve(MaterialId material, out MaterialSimulationBackendKind backend)
        {
            int index = (byte)material;
            backend = _backends[index];
            return material != MaterialId.Empty && _hasRoute[index];
        }

        private static void AddWaterRoute(
            MaterialCatalogSnapshot catalog,
            in WaterSimulationCompatibilityDecision decision,
            MaterialSimulationBackendKind[] backends,
            bool[] hasRoute)
        {
            if (!catalog.TryGet(MaterialId.Water, out MaterialDefinitionSnapshot water)
                || water.Behavior != MaterialBehaviorKind.Liquid)
            {
                throw new InvalidOperationException("Material Catalog 必须包含 Behavior=Liquid 的 Water Definition。");
            }

            if (decision.WaterBackend != MaterialSimulationBackendKind.ElementCell
                && decision.WaterBackend != MaterialSimulationBackendKind.GpuPbfLiquid
                && decision.WaterBackend != MaterialSimulationBackendKind.GridLiquid)
            {
                throw new InvalidOperationException($"Water Compatibility Decision 使用无效 Backend：{decision.WaterBackend}。");
            }

            int index = (byte)MaterialId.Water;
            backends[index] = decision.WaterBackend;
            hasRoute[index] = true;
        }

        private static void AddProfileRoute(
            MaterialCatalogSnapshot catalog,
            in MaterialSimulationRoute route,
            int routeIndex,
            MaterialSimulationBackendKind[] backends,
            bool[] hasRoute)
        {
            if (!Enum.IsDefined(typeof(MaterialSimulationBackendKind), route.Backend))
                throw new InvalidOperationException($"Route[{routeIndex}] 使用无效 Backend：{route.Backend}。");
            if (route.Material == MaterialId.Water)
                throw new InvalidOperationException("Routing Profile 不允许保存 Water；Water Route 由 Compatibility Decision 唯一拥有。");
            if (!catalog.TryGet(route.Material, out MaterialDefinitionSnapshot definition))
                throw new InvalidOperationException($"Route[{routeIndex}] 的 Material {route.Material} 不在 Catalog 中。");

            int index = (byte)route.Material;
            if (hasRoute[index])
                throw new InvalidOperationException($"Routing Profile 包含重复 Material Route：{route.Material}。");
            if ((route.Backend == MaterialSimulationBackendKind.GpuPbfLiquid
                    || route.Backend == MaterialSimulationBackendKind.GridLiquid)
                && definition.Behavior != MaterialBehaviorKind.Liquid)
            {
                throw new InvalidOperationException(
                    $"Material {route.Material} 的 Behavior={definition.Behavior}，不能绑定液体模拟后端。");
            }

            // 身份、宏观行为和求解 Owner 各自独立，但发布时必须匹配；禁止固体误入流体。
            if (route.Backend == MaterialSimulationBackendKind.FixedSolid
                    && definition.Behavior != MaterialBehaviorKind.Solid
                || route.Backend == MaterialSimulationBackendKind.GridGas
                    && definition.Behavior != MaterialBehaviorKind.Gas
                || definition.Behavior == MaterialBehaviorKind.Solid
                    && route.Backend != MaterialSimulationBackendKind.FixedSolid
                    && route.Backend != MaterialSimulationBackendKind.Unsupported
                || definition.Behavior == MaterialBehaviorKind.Gas
                    && route.Backend != MaterialSimulationBackendKind.GridGas
                    && route.Backend != MaterialSimulationBackendKind.Unsupported)
                throw new InvalidOperationException($"Material {route.Material} 的行为与模拟后端不匹配。");

            backends[index] = route.Backend;
            hasRoute[index] = true;
        }
    }
}
