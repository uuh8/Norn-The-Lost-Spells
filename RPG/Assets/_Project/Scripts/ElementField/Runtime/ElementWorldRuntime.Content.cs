using System;
using System.Globalization;
using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementWorldRuntime
    {
        [Header("地图材料基底")]
        [Tooltip("指定本地图的元胞世界。原 World Profile 保留旧后端及旧作者数据的回退路径。")]
        [SerializeField] private ElementCellWorldProfile _cellularContentProfile;
        [SerializeField] private string _cellularMapContentId;
        [SerializeField, HideInInspector] private string _cellularGeometryDigest;
        [SerializeField] private BoxCollider _cellularGroundBox;
        [SerializeField] private Terrain _cellularHeightTerrain;
        [Tooltip("地图根下的天然岩体路径，只接管这些 MeshCollider；不会扫描道具、建筑或角色。")]
        [SerializeField] private string[] _cellularRockPaths = Array.Empty<string>();

        private CellularMapTerrain.Source[] _mapSources;
        private bool _contentSelected;
        private ElementCellWorldProfile _selectedContentProfile;
        public string CellularMapContentId => _cellularMapContentId;

        public ulong CellularMapBaseFingerprint => _cellularGeometryDigest != null && _cellularGeometryDigest.Length == 64 &&
            ulong.TryParse(_cellularGeometryDigest.Substring(0, 16), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out ulong digest) ? digest : 0;

        internal ElementCellWorldProfile ResolveCellularContentProfile()
        {
            if (_cellularContentProfile == null) return _profile != null ? _profile.CellularWorld : null;
            if (_contentSelected) return _selectedContentProfile;
            _contentSelected = true;
            // 单机与 inactive DS/Replica prefab 均按作者的引用选择。无法解释旧材料时，所有权整体留给原后端。
            try
            {
                if (_profile == null || CellularMapBaseFingerprint == 0 || string.IsNullOrWhiteSpace(_cellularMapContentId))
                    throw new InvalidOperationException("地图标识、源几何摘要或原 World Profile 缺失。");
                _mapSources = CellularMapTerrain.ReadSources(this, _cellularGroundBox, _cellularHeightTerrain, _cellularRockPaths);
                CellularMapTerrain.CheckNativeReplacement(_mapSources, _cellularContentProfile, transform.position, _profile.CellSize);
                PrepareCellularInitialContents(_cellularContentProfile);
                _selectedContentProfile = _cellularContentProfile;
            }
            catch (Exception exception)
            {
                _mapSources = null;
                GameLog.Warn($"地图 {_cellularMapContentId} 保留原材料后端：{exception.Message}", "ElementField");
            }
            return _selectedContentProfile;
        }

        internal ElementTerrainVolume CreateCellularTerrain(ElementCellWorldProfile profile) =>
            new ElementTerrainVolume(profile, transform, _profile.CellSize, _profile.ChunkSize, _mapSources);

        // 命中原始 Terrain/Box/Mesh 或开挖后的局部表面均由同一个 Owner 接收。斜面恰跨细格中心时，
        // 沿真实法线检查至多一格，不能因为附近恰好有地形就把角色、建筑命中转换成挖掘。
        private bool TryResolveExcavationCell(Collider hitCollider, Vector3 point, Vector3 normal, out Vector3Int cell)
        {
            cell = default;
            if (_cellularTerrain == null || hitCollider == null || !_cellularTerrain.OwnsCollider(hitCollider)) return false;
            for (int step = 0; step < 5; step++)
            {
                cell = ElementWorldCoordinates.WorldToGlobalCell(point - normal * (_profile.CellSize * (.001f + .24f * step)),
                    _origin, _profile.CellSize);
                if (_cellularTerrain.Get(cell) != MaterialId.Empty) return true;
            }
            return false;
        }
    }
}
