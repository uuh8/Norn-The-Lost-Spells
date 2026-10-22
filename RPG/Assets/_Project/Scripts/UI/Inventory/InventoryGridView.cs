using System.Collections.Generic;
using Game.Run;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>版本变化时投影格子和堆叠代表；自己不维护第二份资产占用表。</summary>
    public sealed class InventoryGridView : MonoBehaviour, IDropHandler
    {
        [SerializeField, Min(8)] private float _cellSize = 48f;
        [SerializeField] private bool _fitToViewport;
        [SerializeField] private bool _fitWidthToViewport;
        public void ConfigureViewportWidthFit(bool fit){_fitWidthToViewport=fit;}
        [SerializeField, Min(8)] private float _minimumCellSize = 24f;
        public void ConfigureViewportFit(bool fit) { _fitToViewport=fit; }
        private readonly List<InventoryItemView> _views = new List<InventoryItemView>();
        private readonly List<Image> _cells = new List<Image>();
        private readonly List<Color> _cellColors = new List<Color>();
        private InventoryGridLinesGraphic _lines;
        private SinglePlayerInventoryController _controller;
        private InventoryItemView _prefab;
        private string _backpackId;
        public float CellSize => _cellSize;
        public string BackpackId => _backpackId;
        public void Initialize(SinglePlayerInventoryController controller, InventoryItemView prefab)
        { _controller=controller; _prefab=controller.SlotPrefabs!=null && controller.SlotPrefabs.BackpackItem!=null?controller.SlotPrefabs.BackpackItem:prefab; }
        public void Refresh(InventoryItemSnapshot[] items, int count, string backpackId)
        {
            _backpackId = backpackId;
            InventoryItemSnapshot bag = default; bool found = false;
            for (int i = 0; i < count; i++) if (items[i].InstanceId == backpackId) { bag = items[i]; found = true; break; }
            int width = found ? bag.Rules.BackpackWidth : 0, height = found ? bag.Rules.BackpackHeight : 0;
            if ((_fitToViewport || _fitWidthToViewport) && width>0 && height>0 && transform.parent is RectTransform viewport)
                // 放大操作区时按宽度显示完整列，超出高度用手动滚动；按较小轴缩放会让加宽后仍留下大片空白。
                _cellSize=Mathf.Max(_minimumCellSize,_fitWidthToViewport?viewport.rect.width/width:Mathf.Min(viewport.rect.width/width,viewport.rect.height/height));
            ((RectTransform)transform).sizeDelta = new Vector2(width * _cellSize, height * _cellSize);
            int required = width * height;
            while (_cells.Count < required)
            {
                var styles=_controller.SlotPrefabs;
                var image=InventorySlotPrefabs.CreateCell(styles!=null?styles.BackpackCell:null,transform,"Grid cell",new Color(.13f,.17f,.21f,1));
                _cells.Add(image);_cellColors.Add(image.color);
            }
            for (int i = 0; i < _cells.Count; i++)
            {
                bool used = i < required; _cells[i].gameObject.SetActive(used); if (!used) continue;
                RectTransform rect = _cells[i].rectTransform; rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
                rect.anchoredPosition = new Vector2(i % width * _cellSize, -i / width * _cellSize);
                rect.sizeDelta = Vector2.one * _cellSize; rect.SetAsFirstSibling();
            }
            if(_lines==null)_lines=InventoryGridLinesGraphic.Ensure(transform);
            _lines.Configure(width,height,_cellSize);_lines.transform.SetAsLastSibling();
            int shown = 0;
            for (int i = 0; i < count; i++)
            {
                InventoryItemSnapshot item = items[i];
                bool draft=_controller.TryArrangement(item.InstanceId,backpackId,out var candidate);
                if (!draft && (item.Location.Kind != ItemLocationKind.Backpack || item.Location.OwnerId != backpackId)) continue;
                bool represented = false; int members = 0;
                for (int j = 0; j < count; j++) if (items[j].Location == item.Location && _controller.TryArrangement(items[j].InstanceId,backpackId,out _)==draft) { members++; if (j < i) represented = true; }
                if (represented) continue;
                while (_views.Count <= shown) _views.Add(Instantiate(_prefab, transform));
                InventoryItemView view = _views[shown++]; view.gameObject.SetActive(true);
                view.Rect.anchorMin = view.Rect.anchorMax = view.Rect.pivot = new Vector2(0, 1);
                var display=draft?candidate:new GridPlacement(item.Location.X,item.Location.Y,item.Location.QuarterTurns);
                view.Rect.anchoredPosition = new Vector2(display.X * _cellSize, -display.Y * _cellSize);
                view.Bind(_controller, item, InventoryItemDisplay.Grid, _cellSize, members,display.QuarterTurns); view.transform.SetAsLastSibling();
            }
            for (int i = shown; i < _views.Count; i++) _views[i].gameObject.SetActive(false);
            for(int i=0;i<shown;i++)if(_controller.IsArrangementInvalid(_views[i].Snapshot.InstanceId))_views[i].transform.SetAsLastSibling();
        }
        public bool TryGetCell(Vector2 screenPoint, Camera camera, out Vector2Int cell)
        {
            cell = default;
            RectTransform rect = (RectTransform)transform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, screenPoint, camera, out Vector2 local)) return false;
            cell = new Vector2Int(Mathf.FloorToInt((local.x - rect.rect.xMin) / _cellSize), Mathf.FloorToInt((rect.rect.yMax - local.y) / _cellSize));
            return true;
        }
        public void ClearPreview()
        { for(int i=0;i<_cells.Count;i++)_cells[i].color=_cellColors[i]; }
        public void ShowPreview(InventoryShape shape,int x,int y,int turns,bool valid)
        {
            ClearPreview();
            int width=Mathf.RoundToInt(((RectTransform)transform).rect.width/_cellSize);
            if(width<=0) return;
            for(int i=0;i<shape.CellCount;i++)
            {
                var cell=shape.GetCell(i,turns);int px=x+cell.x,py=y+cell.y,index=py*width+px;
                if(px<0 || px>=width || py<0 || index>=_cells.Count)continue;
                _cells[index].color=valid?new Color(.18f,.65f,.34f,1):new Color(.8f,.2f,.2f,1);
            }
        }
        public void OnDrop(PointerEventData data)
        {
            _controller.TryDropOnGrid(data.position, data.pressEventCamera);
        }
    }
}
