using Game.Core;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>已知湖盆的可控缺口；开关时只使局部 Collider 缓存失效。</summary>
    [DefaultExecutionOrder(-30)]
    public sealed class StableLiquidOutletAuthoring : MonoBehaviour
    {
        [SerializeField] private StableLiquidRegionAuthoring _source;
        [SerializeField] private Transform _outletCenter;
        [SerializeField] private Transform _receivingCell;
        [SerializeField] private Collider _plugCollider;
        [SerializeField, Min(.01f)] private float _openingAreaCells = 4f;
        [SerializeField, Min(.01f)] private float _conductanceGmuPerSecond = 3000f;
        [SerializeField] private bool _openOnStart;

        private int _outletId = -1;
        private Vector3Int _target;
        public bool IsOpen { get; private set; }

        private void Start()
        {
            LiquidWorldRuntime liquids = _source != null ? _source.Liquids : null;
            if (liquids == null || _source.RegionId < 0
                || _outletCenter == null || _receivingCell == null)
            {
                GameLog.Error("稳定液体出口缺少上游区域、出口或接收点。", "ElementField");
                return;
            }
            _target = ElementWorldCoordinates.WorldToGlobalCell(
                _receivingCell.position, liquids.Origin, liquids.CellSize);
            float heightCells = (_outletCenter.position.y - liquids.Origin.y) / liquids.CellSize;
            if (!liquids.TryRegisterStableOutlet(_source.RegionId, _target,
                heightCells, _openingAreaCells, _conductanceGmuPerSecond,
                _openOnStart, out _outletId))
            {
                GameLog.Error("稳定液体出口注册失败：接收点需在区域之外且预算充足。",
                    "ElementField");
                return;
            }
            SetOpen(_openOnStart);
        }

        [ContextMenu("Toggle Outlet In Play Mode")]
        public void Toggle() => SetOpen(!IsOpen);

        public void SetOpen(bool open)
        {
            if (_outletId < 0) return;
            LiquidWorldRuntime liquids = _source.Liquids;
            if (!liquids.TryPrepareStaticCellChange(_target, !open)) return;
            if (!liquids.SetStableOutletOpen(_outletId, open)) return;
            IsOpen = open;
            if (_plugCollider != null) _plugCollider.enabled = !open;
            Physics.SyncTransforms();
            liquids.InvalidateStaticCell(_target);
        }
    }
}
