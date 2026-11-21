using System;
using System.Collections.Generic;
using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementWorldRuntime
    {
        private CellularInitialContents _initialContents;
        private sealed class ContentMigrationException : Exception
        {
            internal ContentMigrationException(string message) : base(message) { }
        }

        private void PrepareCellularInitialContents(ElementCellWorldProfile profile)
        {
            _initialContents = null;
            if (_gridLiquidRuntime != null && _gridLiquidRuntime.TotalQuantity != 0 ||
                _gasRuntime != null && _gasRuntime.TotalQuantity != 0 || _fixedSolidRuntime != null && _fixedSolidRuntime.TotalQuantity != 0)
                throw new InvalidOperationException("原材料世界已经持有真实数量，不能再次从作者数据注入。");
            var roots = new List<GameObject>();
            // 导出 prefab 和线上实例均只有一个 World 根；单机场景的作者体积可以是材料 Runtime 的兄弟节点。
            var map = CellularMapTerrain.FindMapInstanceRoot(transform);
            if (map != null) roots.Add(map.gameObject);
            else CellularMapTerrain.CollectSceneRoots(transform, roots);
            var volumes = new List<GridLiquidInitialVolume>(); var lakes = new List<StableLiquidRegionAuthoring>();
            foreach (var root in roots)
            {
                foreach (var outlet in root.GetComponentsInChildren<StableLiquidOutletAuthoring>(true))
                    if (outlet.enabled && outlet.Source != null && outlet.Source.Liquids == _gridLiquidRuntime)
                        throw new InvalidOperationException("作者闸门引用旧水库；原闸门和水位须由同一世界持有。");
                foreach (var gate in root.GetComponentsInChildren<StableLiquidStaticCellAuthoring>(true))
                    if (gate.enabled && gate.Owner == _gridLiquidRuntime)
                        throw new InvalidOperationException("关卡使用旧水库的静态开关，保留原交互。");
                foreach (var volume in root.GetComponentsInChildren<GridLiquidInitialVolume>(true))
                    if (volume.enabled && volume.Owner == _gridLiquidRuntime)
                    {
                        if (!AuthorStartsWithMap(volume, root)) throw new InvalidOperationException("分时启用的旧水量必须留给原播放时序。");
                        volumes.Add(volume);
                    }
                foreach (var lake in root.GetComponentsInChildren<StableLiquidRegionAuthoring>(true))
                    if (lake.enabled && lake.Liquids == _gridLiquidRuntime)
                    {
                        if (!AuthorStartsWithMap(lake, root)) throw new InvalidOperationException("分时启用的旧湖泊必须留给原播放时序。");
                        lakes.Add(lake);
                    }
            }
            if (volumes.Count == 0 && lakes.Count == 0) return;
            if (_gridLiquidRuntime == null || _gridLiquidRuntime.ConfigurationProfile == null)
                throw new InvalidOperationException("作者数据缺少原原生格容量和格距配置。");
            var draft = new CellularInitialContents(profile, _gridLiquidRuntime.ConfigurationProfile, transform.position,
                _profile.CellSize, cell => AuthoredTerrainAt(profile, cell));
            foreach (var lake in lakes)
            {
                lake.ReadForMigration(draft); draft.Authors.Add(lake);
            }
            foreach (var volume in volumes)
            {
                volume.ReadForMigration(draft); draft.Authors.Add(volume);
            }
            draft.Finish();
            foreach (var lake in lakes) lake.MigrationWorld = this;
            foreach (var volume in volumes) volume.MigrationWorld = this;
            _initialContents = draft;
        }

        private static bool AuthorStartsWithMap(Component component, GameObject root)
        {
            for (var p = component.transform; p != null && p != root.transform; p = p.parent)
                if (!p.gameObject.activeSelf) return false;
            return root.activeSelf || root.GetComponentInChildren<ElementWorldRuntime>(true) != null;
        }

        private bool AuthoredTerrainAt(ElementCellWorldProfile profile, Vector3Int cell)
        {
            for (int i = 0; i < profile.RegionCount; i++)
            {
                var region = profile.Region(i);
                if (region.Material != MaterialId.Empty && region.Cells.Contains(cell)) return true;
            }
            if (_mapSources != null) foreach (var source in _mapSources)
                if (source.Contains(cell, transform.position, _profile.CellSize)) return true;
            return false;
        }

        internal bool OwnsImportedAuthor(MonoBehaviour author) => UsesCellularWorld &&
            _initialContents != null && _initialContents.Authors.Contains(author);

        internal static bool IsReplicaAuthor(LiquidWorldRuntime liquids)
        {
            if (liquids == null) return false;
            var world = liquids.GetComponentInParent<ElementWorldRuntime>(true);
            return world != null && world.ExecutionMode == WorldExecutionMode.Replica;
        }

        internal CellularRestingLiquidRegion[] ResolveCellularRestingRegions(ElementCellWorldProfile profile) =>
            _initialContents?.Regions ?? profile.RestingRegions;

        private void InstallMigratedDynamicContents()
        {
            if (_initialContents == null) return;
            foreach (var pair in _initialContents.Dynamic)
            {
                var row = pair.Value;
                if (!CellularSimulation.TryDeposit(pair.Key, row.Material, row.Quantity))
                    throw new ContentMigrationException("实际 Collider 或槽位容量不足，未发布这次旧材料交接。");
            }
        }
    }
}
