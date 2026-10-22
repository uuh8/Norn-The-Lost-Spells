using Game.Core;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 原型中的单格地形开关：先向液体 owner 申请，再改变 Collider。
    /// 真实关卡的地形系统也必须遵循此顺序，不可事后补做数量展开。
    /// </summary>
    [DefaultExecutionOrder(-30)]
    public sealed class StableLiquidStaticCellAuthoring : MonoBehaviour
    {
        [SerializeField] private LiquidWorldRuntime _liquids;
        [SerializeField] private BoxCollider _obstacle;
        private Vector3Int _cell;
        private bool _ready;
        public bool IsClosed => _ready && _obstacle != null && _obstacle.enabled;

        private void Start()
        {
            if (_liquids == null || !_liquids.IsFluidInitialized || _obstacle == null)
            {
                GameLog.Error("单格地形开关缺少 Grid Liquid 或 BoxCollider。", "ElementField");
                return;
            }
            Vector3 scale = _obstacle.transform.lossyScale;
            Vector3 size = _obstacle.size;
            size = new Vector3(Mathf.Abs(size.x * scale.x), Mathf.Abs(size.y * scale.y),
                Mathf.Abs(size.z * scale.z));
            Vector3 center = _obstacle.transform.TransformPoint(_obstacle.center);
            Vector3 half = size * .5f;
            Vector3Int minimum = ElementWorldCoordinates.WorldToGlobalCell(
                center - half + Vector3.one * .001f, _liquids.Origin, _liquids.CellSize);
            Vector3Int maximum = ElementWorldCoordinates.WorldToGlobalCell(
                center + half - Vector3.one * .001f, _liquids.Origin, _liquids.CellSize);
            if (Quaternion.Angle(_obstacle.transform.rotation, Quaternion.identity) > .1f
                || minimum != maximum || size.x >= _liquids.CellSize
                || size.y >= _liquids.CellSize || size.z >= _liquids.CellSize)
            {
                GameLog.Error("单格地形开关的 Collider 超出一个液体格，无法只失效一个格。",
                    "ElementField");
                return;
            }
            _cell = minimum;
            _ready = true;
        }

        [ContextMenu("Toggle Internal Cell In Play Mode")]
        public void Toggle()
        {
            if (!_ready) return;
            bool becomesSolid = !_obstacle.enabled;
            if (!_liquids.TryPrepareStaticCellChange(_cell, becomesSolid))
            {
                GameLog.Warn($"单格地形修改被拒绝：{_liquids.LastStableExpansionFailure}。",
                    "ElementField");
                return;
            }
            _obstacle.enabled = becomesSolid;
            Physics.SyncTransforms();
            _liquids.InvalidateStaticCell(_cell);
        }
    }
}
