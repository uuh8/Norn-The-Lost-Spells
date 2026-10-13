using Game.Run;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>目录筛选/统计与当前携装的表现。整理只改变目录，不提交假资产操作。</summary>
    public sealed class WarehouseWorkspaceView : MonoBehaviour
    {
        [SerializeField] private WarehouseCatalogGridView _grid;
        [SerializeField] private SinglePlayerInventoryController _controller;
        [SerializeField] private InputField _search;
        [SerializeField] private Dropdown _category;
        [SerializeField] private Button _organize;
        [SerializeField] private Text _capacity, _bagCaption, _bagCapacity;
        [SerializeField] private Image _bagIcon;
        [SerializeField] private Sprite _bagFallback;
        [SerializeField] private ToolbeltView _tools;
        private int _buttonCategory;
        public void SetCategoryFilter(int category) { _buttonCategory = category; Filter(); }
        private void OnEnable()
        { _search?.onValueChanged.AddListener(Search); _category?.onValueChanged.AddListener(Category); _organize?.onClick.AddListener(Organize); }
        private void OnDisable()
        { _search?.onValueChanged.RemoveListener(Search); _category?.onValueChanged.RemoveListener(Category); _organize?.onClick.RemoveListener(Organize); }
        private void Search(string _) => Filter();
        private void Category(int _) => Filter();
        private void Filter() { _grid?.SetFilter(_search != null ? _search.text : "", _category != null ? _category.value : _buttonCategory); Count(); }
        private void Organize() { _grid?.Organize(); Count(); }
        private void Count()
        { if (_capacity != null && _grid != null) _capacity.text = "物品 " + _grid.TotalKinds + " 类 · " + _grid.InstanceCount + " 份 · 无限仓库"; }
        public void Refresh(InventoryItemSnapshot[] items, int count, string bagId)
        {
            if (_controller != null) _tools?.Refresh(_controller);
            Count(); InventoryItemSnapshot bag = default; bool found = false; int used = 0;
            for (int i = 0; i < count; i++)
            {
                var item = items[i]; if (item.InstanceId == bagId) { bag = item; found = true; }
                if (item.Location.Kind != ItemLocationKind.Backpack || item.Location.OwnerId != bagId) continue;
                bool represented = false;
                for (int j = 0; j < i; j++) if (items[j].Location == item.Location) { represented = true; break; }
                if (!represented) used += item.Rules.Shape.CellCount;
            }
            if (_bagCaption != null) _bagCaption.text = found ? "当前背包\n" + bag.DisplayName + "\n已占 " + used + "/" + (bag.Rules.BackpackWidth * bag.Rules.BackpackHeight) + " 格" : "未装备背包";
            if (_bagCapacity != null) _bagCapacity.text = found ? "携带背包 · 已占 " + used + "/" + (bag.Rules.BackpackWidth * bag.Rules.BackpackHeight) + " 格" : "未装备背包";
            if (_bagIcon != null) { _bagIcon.sprite = found ? bag.Icon != null ? bag.Icon : _bagFallback : null; _bagIcon.enabled = found && _bagIcon.sprite != null; _bagIcon.preserveAspect = true; }
        }
    }
}
