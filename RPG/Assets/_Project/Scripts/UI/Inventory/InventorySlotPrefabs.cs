using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>仅保存UI模板；库存规则与真实InstanceId仍由Game.Run负责。</summary>
    [CreateAssetMenu(menuName="Norn/UI/Inventory Slot Prefabs")]
    public sealed class InventorySlotPrefabs : ScriptableObject
    {
        public Image BackpackCell, NearbyCell, WarehouseCell;
        public InventoryItemView BackpackItem, NearbyItem, WarehouseItem;
        public InventorySlotView WandProgramSlot, SpellStorageSlot, WandSelectorSlot;
        public ToolbeltSlotView BackpackToolSlot, WorkspaceToolSlot;
        public InventoryInfoPopup InfoPopup;

        // 兼容已有Scene不必自动保存接线；Controller首次访问缓存，滚动/拖拽热路径不查Resources。
        internal static InventorySlotPrefabs LoadDefault() => Resources.Load<InventorySlotPrefabs>("InventorySlotPrefabs");
        internal static Image CreateCell(Image prefab,Transform parent,string name,Color fallback)
        {
            Image image;
            if(prefab!=null)image=Object.Instantiate(prefab,parent);
            else
            {
                var go=new GameObject(name,typeof(RectTransform),typeof(Image));go.transform.SetParent(parent,false);
                image=go.GetComponent<Image>();image.color=fallback;image.raycastTarget=false;
            }
            image.name=name;return image;
        }
    }
}
