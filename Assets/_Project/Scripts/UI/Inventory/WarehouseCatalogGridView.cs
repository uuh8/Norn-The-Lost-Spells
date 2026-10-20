using System;
using System.Collections.Generic;
using Game.Run;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>同一ContentId一格的无限目录。分组不合并资产；可见池只表达当前选定的具体一份。</summary>
    public sealed class WarehouseCatalogGridView : MonoBehaviour, IDropHandler
    {
        [SerializeField, Min(48)] private float _cellSize = 112;
        [SerializeField, Min(1)] private int _minimumRows = 4;
        private struct Group { public InventoryItemSnapshot Item; public int Count, MemberIndex; public string Caption; }
        private sealed class GroupOrder : IComparer<Group>
        {
            public static readonly GroupOrder Instance = new GroupOrder();
            public int Compare(Group a, Group b)
            { int name = StringComparer.Ordinal.Compare(a.Item.DisplayName, b.Item.DisplayName);if(name!=0)return name;
                int kind=StringComparer.Ordinal.Compare(a.Item.ContentId,b.Item.ContentId);return kind!=0?kind:StringComparer.Ordinal.Compare(a.Item.InstanceId,b.Item.InstanceId); }
        }
        private readonly List<Group> _groups = new List<Group>();
        private readonly Dictionary<string, int> _byContent = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> _allKinds = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<Image> _cells = new List<Image>();
        private InventoryGridLinesGraphic _lines;
        private readonly List<InventoryItemView> _views = new List<InventoryItemView>();
        private ScrollRect _scroll;
        private RectTransform _viewport;
        private SinglePlayerInventoryController _controller;
        private InventoryItemView _prefab;
        private InventoryItemSnapshot[] _items;
        private int _count, _columns, _rows, _poolRows, _stamp, _renderedStamp = -1, _firstRow = -1, _instanceCount;
        private bool _resizePending;
        private string _query = "";
        private int _category;
        private InventoryViewportPan _pan;
        private static readonly ProfilerMarker RenderMarker = new ProfilerMarker("Norn.Warehouse.CatalogRender");
        public int GroupCount => _groups.Count;
        public int TotalKinds => _allKinds.Count;
        public int InstanceCount => _instanceCount;
        public int VisibleCellCount { get; private set; }
        public int AllocatedItemViewCount => _views.Count;
        public InventoryItemSnapshot GetRepresentative(int index) => _groups[index].Item;
        public int GetMemberCount(int index) => _groups[index].Count;

        private void OnDisable() { _pan?.Cancel(); }
        private void OnDestroy() { if (_scroll != null) _scroll.onValueChanged.RemoveListener(OnScroll); }
        private void OnRectTransformDimensionsChange() => _resizePending = true;
        private void LateUpdate()
        { if (_resizePending && _scroll != null && _items != null) { _resizePending = false; LayoutAndWarmPool(); Render(); } }
        public void Refresh(SinglePlayerInventoryController controller, InventoryItemView prefab, InventoryItemSnapshot[] items, int count)
        {
            _controller=controller;_prefab=controller.SlotPrefabs!=null && controller.SlotPrefabs.WarehouseItem!=null?controller.SlotPrefabs.WarehouseItem:prefab;_items=items;_count=count;
            if (_scroll == null)
            {
                _scroll = GetComponentInParent<ScrollRect>();
                if (_scroll == null) return;
                _viewport = _scroll.viewport != null ? _scroll.viewport : (RectTransform)_scroll.transform;
                _scroll.onValueChanged.AddListener(OnScroll);
                _pan = InventoryViewportPan.Ensure(_scroll);
            }
            Rebuild();
        }
        public void SetFilter(string query, int category)
        { _query = query == null ? "" : query.Trim(); _category = category; if (_scroll != null) { Rebuild(); _scroll.verticalNormalizedPosition = 1; } }
        public void Organize()
        { if (_scroll == null) return; Rebuild(); _scroll.StopMovement(); _scroll.verticalNormalizedPosition = 1; }
        private bool Show(in InventoryItemSnapshot item)
        {
            if (_query.Length > 0 && item.DisplayName.IndexOf(_query, StringComparison.OrdinalIgnoreCase) < 0
                && item.ContentId.IndexOf(_query, StringComparison.OrdinalIgnoreCase) < 0) return false;
            switch (_category)
            {
                case 1: return item.Kind == ItemKind.Spell;
                case 2: return ItemInventoryClassification.IsTool(item.Kind, item.Rules.UseKind);
                case 3: return ItemInventoryClassification.IsMaterial(item.Kind) && !ItemInventoryClassification.IsTool(item.Kind, item.Rules.UseKind);
                case 4: return item.Kind == ItemKind.Wand;
                case 5: return item.Kind == ItemKind.Backpack;
                case 6: return item.Category == ItemCategory.Food;
                case 7: return item.Category == ItemCategory.Herb;
                case 8: return item.Kind == ItemKind.Consumable || item.Kind == ItemKind.Alchemy;
                case 9: return ItemInventoryClassification.IsTool(item.Kind, item.Rules.UseKind);
                default: return true;
            }
        }
        private void Rebuild()
        {
            _groups.Clear(); _byContent.Clear(); _allKinds.Clear(); _instanceCount = 0;
            string selected = _controller != null ? _controller.SelectedItemId : null;
            for (int i = 0; i < _count; i++)
            {
                var item = _items[i]; if (item.Location.Kind != ItemLocationKind.Warehouse) continue;
                _instanceCount++; _allKinds.Add(item.ContentId); if (!Show(item)) continue;
                // 容器一份一格显示自己的组分；其他物品继续按种类汇总，不更改真实 InstanceId。
                if(item.Kind==ItemKind.Container)
                {_groups.Add(new Group {Item=item,Count=1,MemberIndex=1});continue;}
                if (!_byContent.TryGetValue(item.ContentId, out int index))
                { index = _groups.Count; _byContent.Add(item.ContentId, index); _groups.Add(new Group { Item = item, Count = 1, MemberIndex = 1 }); }
                else
                { var group = _groups[index]; group.Count++; if (item.InstanceId == selected) { group.Item = item; group.MemberIndex = group.Count; } _groups[index] = group; }
            }
            _groups.Sort(GroupOrder.Instance);
            // Caption在库存/筛选变化时预计算。滑动经过新的组时只交换字符串引用和Sprite，不产生每帧格式化。
            for (int i = 0; i < _groups.Count; i++)
            {
                var group = _groups[i]; var item = group.Item;
                string state = item.Kind == ItemKind.Spell ? (_controller != null ? _controller.SpellCountCaption(item) : item.HasUnlimitedCharges ? "" : item.RemainingCharges.ToString())
                    : item.Kind == ItemKind.Material ? item.MaterialQuantity.ToString() : "";
                // 容器逐份展示，储量由同 Sprite 底部填充表达，不产生溢出的长容量文字。
                group.Caption = item.Kind==ItemKind.Spell?state:item.DisplayName + (item.Kind == ItemKind.Container ? "" : "\n×" + group.Count + (group.Count > 1 ? " · " + group.MemberIndex + "/" + group.Count : ""))
                    + (state.Length > 0 ? "\n" + state : ""); _groups[i] = group;
            }
            _stamp++; LayoutAndWarmPool(); Render();
        }
        private void LayoutAndWarmPool()
        {
            _columns = Mathf.Max(1, Mathf.FloorToInt(_viewport.rect.width / _cellSize));
            _poolRows = Mathf.Max(2, Mathf.CeilToInt(_viewport.rect.height / _cellSize) + 2);
            _rows = Mathf.Max(_minimumRows, _poolRows, (_groups.Count + _columns - 1) / _columns + 1);
            var rect = (RectTransform)transform; var size = new Vector2(_columns * _cellSize, _rows * _cellSize);
            if (rect.sizeDelta != size) rect.sizeDelta = size;
            int required = _columns * _poolRows;
            while (_cells.Count < required)
            {
                var styles=_controller.SlotPrefabs;
                _cells.Add(InventorySlotPrefabs.CreateCell(styles!=null?styles.WarehouseCell:null,transform,"Warehouse empty cell",new Color(.09f,.16f,.22f,1)));
            }
            while (_views.Count < required && _views.Count < _groups.Count)
            { var view = Instantiate(_prefab, transform); view.PrepareCatalog(); view.gameObject.SetActive(false); _views.Add(view); }
            _renderedStamp = -1;
        }
        private void OnScroll(Vector2 _) => Render();
        private void Render()
        {
            if (_scroll == null || _columns == 0) return;
            using var sample = RenderMarker.Auto();
            int first = Mathf.Clamp(Mathf.FloorToInt(((RectTransform)transform).anchoredPosition.y / _cellSize), 0, _rows - 1);
            if (first == _firstRow && _renderedStamp == _stamp) return;
            _firstRow = first; _renderedStamp = _stamp; int visible = Mathf.Min(_poolRows, _rows - first) * _columns;
            VisibleCellCount = visible;
            for (int i = 0; i < _cells.Count; i++)
            {
                bool used = i < visible; _cells[i].gameObject.SetActive(used); if (!used) continue;
                int index = first * _columns + i; var rect = _cells[i].rectTransform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
                rect.anchoredPosition = new Vector2(index % _columns * _cellSize, -index / _columns * _cellSize);
                rect.sizeDelta = Vector2.one * _cellSize; rect.SetAsFirstSibling();
            }
            if(_lines==null)_lines=InventoryGridLinesGraphic.Ensure(transform);
            _lines.Configure(_columns,visible/_columns,_cellSize,first);_lines.transform.SetAsLastSibling();
            for (int i = 0; i < _views.Count; i++)
            {
                int index = first * _columns + i; bool used = i < visible && index < _groups.Count;
                _views[i].gameObject.SetActive(used); if (!used) continue;
                var group = _groups[index]; var view = _views[i];
                view.Rect.anchorMin = view.Rect.anchorMax = view.Rect.pivot = new Vector2(0, 1);
                view.Rect.anchoredPosition = new Vector2(index % _columns * _cellSize, -index / _columns * _cellSize);
                view.BindCatalog(_controller, group.Item, _cellSize, group.Caption); view.transform.SetAsLastSibling();
            }
        }
        public void TickInteraction(bool open, bool dragging, Vector2 pointer, Camera camera, float delta)
        { _pan?.Tick(open && !dragging); }
        public void OnDrop(PointerEventData data) => _controller?.SubmitToTarget(InventoryDropKind.Warehouse);
    }
}
