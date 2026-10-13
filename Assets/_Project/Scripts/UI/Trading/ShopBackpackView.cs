using System.Collections.Generic;
using Game.Run;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>商店侧栏的真实占格只读镜像；点击选中待售实例，拖拽编辑仍在整备界面。</summary>
    public sealed class ShopBackpackView : MonoBehaviour
    {
        [SerializeField]private InventoryItemView _prefab;
        [SerializeField]private SinglePlayerInventoryController _inventory;
        [SerializeField]private Text _capacity;
        [SerializeField,Min(32)]private float _cellSize=58;
        private readonly List<InventoryItemView> _items=new List<InventoryItemView>();
        private readonly List<Image> _cells=new List<Image>();
        private InventoryGridLinesGraphic _lines;
        public void Refresh(ShopController shop,InventoryItemSnapshot[] items,int count)
        {
            string bag=null;int width=0,height=0;
            for(int i=0;i<count;i++)if(items[i].Location.Kind==ItemLocationKind.EquippedBackpack){bag=items[i].InstanceId;width=items[i].Rules.BackpackWidth;height=items[i].Rules.BackpackHeight;break;}
            float cell=_cellSize;int used=0,shown=0;var rect=(RectTransform)transform;rect.sizeDelta=new Vector2(width*cell,height*cell);
            while(_cells.Count<width*height){var go=new GameObject("Shop grid cell",typeof(RectTransform),typeof(Image));go.transform.SetParent(transform,false);var image=go.GetComponent<Image>();image.color=new Color(.12f,.16f,.21f);image.raycastTarget=false;_cells.Add(image);}
            for(int i=0;i<_cells.Count;i++){_cells[i].gameObject.SetActive(i<width*height);if(i>=width*height)continue;var r=_cells[i].rectTransform;r.anchorMin=r.anchorMax=r.pivot=new Vector2(0,1);r.anchoredPosition=new Vector2(i%width*cell,-i/width*cell);r.sizeDelta=Vector2.one*cell;r.SetAsFirstSibling();}
            if(_lines==null)_lines=InventoryGridLinesGraphic.Ensure(transform);
            _lines.Configure(width,height,cell);_lines.transform.SetAsLastSibling();
            for(int i=0;i<count;i++)
            {var item=items[i];if(item.Location.Kind!=ItemLocationKind.Backpack || item.Location.OwnerId!=bag)continue;
                bool first=true;int members=0;for(int j=0;j<count;j++)if(items[j].Location==item.Location){members++;if(j<i)first=false;}if(!first)continue;
                if(shown==_items.Count){var v=Instantiate(_prefab,transform);v.gameObject.AddComponent<ShopInventorySelection>();_items.Add(v);}var view=_items[shown++];view.gameObject.SetActive(true);
                view.Rect.anchorMin=view.Rect.anchorMax=view.Rect.pivot=new Vector2(0,1);view.Rect.anchoredPosition=new Vector2(item.Location.X*cell,-item.Location.Y*cell);view.Bind(_inventory,item,InventoryItemDisplay.Grid,cell,members);
                // 商店是较紧凑的只读镜像，允许数字缩小以保留100/150等完整余量；完整物品信息由详情承担。
                var caption=view.GetComponentInChildren<Text>(true);if(caption!=null){caption.resizeTextMinSize=10;caption.resizeTextMaxSize=14;caption.fontSize=14;}
                view.GetComponent<ShopInventorySelection>().Bind(shop,item.InstanceId);view.transform.SetAsLastSibling();used+=item.Rules.Shape.CellCount;
            }
            for(int i=shown;i<_items.Count;i++)_items[i].gameObject.SetActive(false);_capacity.text="我的携带背包 · "+used+" / "+width*height+" 格";_capacity.font=_inventory.UiFont;
        }
    }
}
