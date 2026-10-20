using System;
using System.Collections.Generic;
using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>同一真实库存的两种排布；只在打开/选择/资产变更时投影，不建立第二份可写库存。</summary>
    public sealed class InventoryWorkspaceView : MonoBehaviour
    {
        [SerializeField]private SinglePlayerInventoryController _inventory;
        [SerializeField]private RectTransform _warehouse,_bag,_wands,_paletteRoot;
        [SerializeField]private Text _title,_capacity,_bagEquipment,_containerDetails;
        [SerializeField]private InputField _search;
        [SerializeField]private Dropdown _category;
        [SerializeField]private InventoryItemView _prefab;
        [SerializeField]private RectTransform _nearbyRegion,_toolsRoot;
        [SerializeField]private Text _wandStats;
        [SerializeField]private Button _pickup;
        [SerializeField]private Button _drop;
        private readonly List<InventoryItemView> _tools=new List<InventoryItemView>();
        private readonly List<InventoryItemView> _palette=new List<InventoryItemView>();
        private readonly MaterialAmount[] _composition=new MaterialAmount[8];
        private void OnEnable(){if(_search!=null)_search.onValueChanged.AddListener(OnSearch);if(_category!=null)_category.onValueChanged.AddListener(OnCategory);}
        private void OnDisable(){if(_search!=null)_search.onValueChanged.RemoveListener(OnSearch);if(_category!=null)_category.onValueChanged.RemoveListener(OnCategory);}
        private void OnSearch(string _)=>_inventory.RefreshPresentation();
        private void OnCategory(int _)=>_inventory.RefreshPresentation();
        public bool ShowWarehouseItem(in InventoryItemSnapshot item)
        {
            if(!string.IsNullOrWhiteSpace(_search.text) && item.DisplayName.IndexOf(_search.text,StringComparison.OrdinalIgnoreCase)<0)return false;
            int filter=_category.value;return filter==0 || filter==1 && item.Kind==ItemKind.Spell || filter==2 && ItemInventoryClassification.IsTool(item.Kind,item.Rules.UseKind)
                || filter==3 && ItemInventoryClassification.IsMaterial(item.Kind) && !ItemInventoryClassification.IsTool(item.Kind,item.Rules.UseKind)
                || filter==4 && item.Kind==ItemKind.Wand || filter==5 && item.Kind==ItemKind.Backpack;
        }
        public void Arrange(bool warehouse)
        {
            _warehouse.gameObject.SetActive(warehouse);_title.text=warehouse?"仓库与魔法整备":"背包与法杖";
            Place(_bag,warehouse?new Vector2(510,-90):new Vector2(20,-90),warehouse?new Vector2(850,380):new Vector2(580,500));
            Place(_wands,warehouse?new Vector2(510,-480):new Vector2(620,-90),warehouse?new Vector2(850,500):new Vector2(740,890));
            // 仓库把装备放在格子右侧；箱庭左栏较窄，装备改放格子下方，避免侵入编程列。
            Place(_bagEquipment.rectTransform,warehouse?new Vector2(518,-58):new Vector2(14,-420),new Vector2(warehouse?300:540,60));
            if(_nearbyRegion!=null)_nearbyRegion.gameObject.SetActive(!warehouse);
            if(_toolsRoot!=null)
            {
                // 两模式共用视图，按打开事件重排已有Rect，不在Update扫描层级或创建控件。
                _wandStats.gameObject.SetActive(!warehouse);
                Place((RectTransform)_wands.Find("ProgramTitle"),new Vector2(12,warehouse?-180:-306),new Vector2(716,38));
                Place((RectTransform)_wands.Find("Program"),new Vector2(12,warehouse?-228:-352),new Vector2(716,102));
                Place((RectTransform)_wands.Find("PaletteTitle"),new Vector2(12,warehouse?-344:-472),new Vector2(716,30));
                Place((RectTransform)_wands.Find("Available"),new Vector2(12,warehouse?-386:-510),new Vector2(716,104));
                _toolsRoot.parent.parent.gameObject.SetActive(!warehouse);_wands.Find("ToolsTitle").gameObject.SetActive(!warehouse);
            }
        }
        public void Refresh(InventoryItemSnapshot[] items,int count,string bagId)
        {
            int used=0,cells=0,shown=0,tools=0;string bagName="未装备背包";
            for(int i=0;i<count;i++)
            {
                var item=items[i];if(item.InstanceId==bagId){cells=item.Rules.BackpackWidth*item.Rules.BackpackHeight;bagName=item.DisplayName;}
                if(item.Location.Kind==ItemLocationKind.Backpack && item.Location.OwnerId==bagId)
                {
                    bool first=true;for(int j=0;j<i;j++)if(items[j].Location==item.Location){first=false;break;}
                    if(first)used+=item.Rules.Shape.CellCount;
                    if(item.Kind!=ItemKind.Spell)
                    {
                        if(_toolsRoot!=null && (item.Kind==ItemKind.Container || item.Kind==ItemKind.Material))
                        {
                            if(tools==_tools.Count)_tools.Add(Instantiate(_prefab,_toolsRoot));var tool=_tools[tools];tool.gameObject.SetActive(true);
                            tool.Rect.anchorMin=tool.Rect.anchorMax=tool.Rect.pivot=new Vector2(0,1);tool.Rect.anchoredPosition=new Vector2(tools*86,0);tool.Rect.sizeDelta=new Vector2(80,100);
                            tool.Bind(_inventory,item,InventoryItemDisplay.Program,48);tools++;
                        }
                        continue;
                    }
                    if(shown==_palette.Count)_palette.Add(Instantiate(_prefab,_paletteRoot));var view=_palette[shown];view.gameObject.SetActive(true);
                    view.Rect.anchorMin=view.Rect.anchorMax=view.Rect.pivot=new Vector2(0,1);view.Rect.anchoredPosition=new Vector2(shown*86,0);view.Rect.sizeDelta=new Vector2(80,100);
                    view.Bind(_inventory,item,InventoryItemDisplay.Program,48);shown++;
                }
            }
            for(int i=shown;i<_palette.Count;i++)_palette[i].gameObject.SetActive(false);
            for(int i=tools;i<_tools.Count;i++)_tools[i].gameObject.SetActive(false);
            if(_toolsRoot!=null)_toolsRoot.sizeDelta=new Vector2(tools*86,104);
            _paletteRoot.sizeDelta=new Vector2(shown*86,104);_capacity.text="携带背包 · 已占 "+used+" / "+cells+" 格";
            _bagEquipment.text="当前背包："+bagName+"\n装备本体位于独立装备位";
            _containerDetails.text="";
            if(_inventory.Session.TryCopyContainer(_inventory.SelectedItemId,_composition,out int n,out ulong total,out ulong capacity))
            {
                string text="容器："+total+" / "+capacity+" · 剩余 "+(capacity-total)+"\n";
                // 最多八组分用两列文字；有限详情区域保留全部组分与操作提示，不把后几项截掉。
                for(int i=0;i<n;i++)text+=MaterialName(_composition[i].MaterialKey)+" "+_composition[i].Quantity+(i%2==1 || i==n-1?"\n":"    |    ");
                _containerDetails.text=text+"E 采集 · H 持续撒出 · G 投掷破碎\n关闭面板后靠近材料操作。";
            }
            if(_pickup!=null)_pickup.gameObject.SetActive(_inventory.Session.ReadOnly.TryGetView(_inventory.SelectedItemId,out var selected) && selected.Location.Kind==ItemLocationKind.WorldItem);
            if(_drop!=null)_drop.gameObject.SetActive(_inventory.Session.ReadOnly.TryGetView(_inventory.SelectedItemId,out var discard)
                && discard.Location.Kind!=ItemLocationKind.WorldItem && discard.Location.Kind!=ItemLocationKind.Warehouse);
            var labels=GetComponentsInChildren<Text>(true);for(int i=0;i<labels.Length;i++)labels[i].font=_inventory.UiFont;
        }
        public static string MaterialName(byte key)
        {switch(key){case 1:return "水";case 2:return "火";case 3:return "毒液";case 4:return "黏液";case 5:return "冰";case 6:return "干黏液";case 7:return "蒸汽";case 8:return "毒气";default:return "未知材料";}}
        private static void Place(RectTransform r,Vector2 position,Vector2 size){r.anchoredPosition=position;r.sizeDelta=size;}
    }
}
