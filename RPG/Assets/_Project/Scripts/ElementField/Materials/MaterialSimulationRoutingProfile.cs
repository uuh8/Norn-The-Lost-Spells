using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 非 Water Material 的 Backend Authoring 表。Water 的最终 Backend 由硬件兼容策略注入，
    /// 因此这里禁止保存第二份 Water 选择，避免两个 Inspector 值互相冲突。
    /// </summary>
    [CreateAssetMenu(
        fileName = "MaterialSimulationRouting",
        menuName = "Game/Element Field/Material Simulation Routing")]
    public sealed class MaterialSimulationRoutingProfile : ScriptableObject
    {
        [Serializable]
        private struct AuthoringRoute
        {
            [SerializeField] private MaterialId _material;
            [SerializeField] private MaterialSimulationBackendKind _backend;

            public MaterialSimulationRoute CreateSnapshot() =>
                new MaterialSimulationRoute(_material, _backend);
        }

        [SerializeField] private AuthoringRoute[] _routes = Array.Empty<AuthoringRoute>();

        internal int RouteCount => _routes.Length;

        internal MaterialSimulationRoute GetRoute(int index)
        {
            return _routes[index].CreateSnapshot();
        }
    }
}
