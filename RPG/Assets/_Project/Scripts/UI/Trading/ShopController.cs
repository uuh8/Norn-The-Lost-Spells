using System;
using System.Collections;
using System.Collections.Generic;
using Game.Core;
using Game.Run;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>商店三栏：有限供货/实际持有/所选详情。跨模块写入统一用Core命令，布局不依赖美术贴图。</summary>
    public sealed class ShopController : MonoBehaviour
    {
        [SerializeField] private SinglePlayerShopSession _shop;
        [SerializeField] private SinglePlayerInventorySession _inventory;
        [SerializeField] private SinglePlayerInventoryController _inventoryUi;
        [SerializeField] private RunPauseCoordinator _pause;
        [SerializeField] private GameObject _panel;
        [SerializeField] private RectTransform _offersRoot,_ownedRoot;
        [SerializeField] private ShopEntryView _entryPrefab;
        [SerializeField] private Text _summary,_details,_status;
        [SerializeField] private InputField _quantity;
        [SerializeField] private Dropdown _containers;
        [SerializeField] private Toggle _toBackpack;
        [SerializeField] private Button _openButton,_closeButton,_buyButton,_sellItemButton,_sellMaterialButton;
        [SerializeField] private InputActionReference _toggleAction;
        [SerializeField] private bool _openOnStart=true;
        [SerializeField] private HubInteractionController _hubAccess;
        [SerializeField] private ShopBackpackView _backpackView;
        [SerializeField] private InputField _search;
        [SerializeField] private Dropdown _category;
        [SerializeField] private bool _warehouseOnly;
        [SerializeField] private SinglePlayerInventoryController _embeddedInventory;
        [SerializeField] private bool _quickPurchase, _detailsPlaceholder;
        [SerializeField] private ShopQuickPurchasePopup _quickPurchasePopup;
        [SerializeField] private ShopPurchaseDialog _purchaseDialog;
        private string _statusText = "", _purchaseRequest;
        private ScrollRect _offersScroll;
        private InputAction _escape;
        private void Awake(){_escape=new InputAction("CloseShop",InputActionType.Button,"<Keyboard>/escape");_escape.performed+=Escape;}
        private void Escape(InputAction.CallbackContext _)
        { if (!_open) return; if (_purchaseDialog != null && _purchaseDialog.IsOpen) _purchaseDialog.Hide(); else if (_quickPurchasePopup != null && _quickPurchasePopup.IsOpen) _quickPurchasePopup.Hide(); else Close(); }
        private void OnDestroy(){_escape.performed-=Escape;_escape.Dispose();}
        private readonly List<ShopEntryView> _offerViews=new List<ShopEntryView>(),_ownedViews=new List<ShopEntryView>();
        private readonly List<string> _containerIds=new List<string>();
        private InventoryItemSnapshot[] _items=Array.Empty<InventoryItemSnapshot>();
        private int _itemCount;
        private string _offerId,_itemId;
        private uint _inventoryVersion=uint.MaxValue;
        private long _shopVersion=-1;
        private bool _open,_available;
        private static readonly ProfilerMarker UpdateMarker=new ProfilerMarker("Norn.Shop.UiUpdate");
        public bool IsOpen=>_open;
        public string SelectedItemId=>_itemId;
        public string Status=>_statusText;
        private bool Confirming => _purchaseDialog != null && _purchaseDialog.IsOpen;
        private void SetStatus(string message) { _statusText = message ?? ""; if (_status != null) _status.text = _statusText; }
        private Font Font=>_inventoryUi!=null?_inventoryUi.UiFont:null;
        private void OnEnable()
        {
            if (_embeddedInventory != null) _embeddedInventory.SelectionChanged += SelectOwned;
            _escape.Enable();
            _search?.onValueChanged.AddListener(OnSearch);_category?.onValueChanged.AddListener(OnCategory);
            _quantity?.onValueChanged.AddListener(OnQuantity);
            // 商店Panel在作者Scene里默认关闭；启动时仍要找到其ScrollRect并订阅，否则首次滚动不会收起小菜单。
            _offersScroll = _offersRoot != null ? _offersRoot.GetComponentInParent<ScrollRect>(true) : null;
            _offersScroll?.onValueChanged.AddListener(OnOffersScroll);
            EventBus<ShopTradeResultEvent>.Subscribe(OnResult);
            EventBus<HubInteractionResultEvent>.Subscribe(OnHubInteraction);
            if(_toggleAction!=null){_toggleAction.action.performed+=Toggle;_toggleAction.action.Enable();}
            _openButton?.onClick.AddListener(Open);_closeButton?.onClick.AddListener(Close);
            _buyButton?.onClick.AddListener(Buy);_sellItemButton?.onClick.AddListener(SellItem);_sellMaterialButton?.onClick.AddListener(SellMaterial);
        }
        private void OnDisable()
        {
            if (_embeddedInventory != null) _embeddedInventory.SelectionChanged -= SelectOwned;
            _escape.Disable();
            _search?.onValueChanged.RemoveListener(OnSearch);_category?.onValueChanged.RemoveListener(OnCategory);
            _quantity?.onValueChanged.RemoveListener(OnQuantity);
            _offersScroll?.onValueChanged.RemoveListener(OnOffersScroll);
            EventBus<ShopTradeResultEvent>.Unsubscribe(OnResult);
            EventBus<HubInteractionResultEvent>.Unsubscribe(OnHubInteraction);
            if(_toggleAction!=null){_toggleAction.action.performed-=Toggle;_toggleAction.action.Disable();}
            _openButton?.onClick.RemoveListener(Open);_closeButton?.onClick.RemoveListener(Close);
            _buyButton?.onClick.RemoveListener(Buy);_sellItemButton?.onClick.RemoveListener(SellItem);_sellMaterialButton?.onClick.RemoveListener(SellMaterial);
            Close();
        }
        private IEnumerator Start()
        {
            while(_inventory.ReadOnly==null || Font==null)yield return null;
            // 展示Panel可以由用户搬到另一张Canvas；字体跟随实际Panel，不能依赖控制器的旧层级。
            var labels=_panel.GetComponentsInChildren<Text>(true);for(int i=0;i<labels.Length;i++)labels[i].font=Font;
            // DefaultControls默认Toggle黑字适合浅色面板；占位暗面板需要显式对比度，随后美术可替换背景。
            var toggleLabel=_toBackpack != null ? _toBackpack.GetComponentInChildren<Text>() : null;
            if(toggleLabel != null){toggleLabel.color=new Color(.88f,.92f,.96f);toggleLabel.fontSize=20;}
            if(_quantity!=null)_quantity.textComponent.fontSize=20;
            // 新版不依赖Dropdown；作者删除隐藏的旧节点后也不能影响商店启动。
            if(_containers!=null){_containers.captionText.fontSize=20;_containers.itemText.fontSize=20;}
            // 全屏Image继续接收Raycast隔离世界点击，但视觉透明，让商店像背包一样叠在游戏画面上。
            if(_panel.TryGetComponent<Image>(out var backdrop)){var color=backdrop.color;color.a=0;backdrop.color=color;}
            if(_openOnStart)Open();else _panel.SetActive(false);
        }
        private void Toggle(InputAction.CallbackContext context){if(_open)Close();else Open();}
        private void OnSearch(string _){if(_open)Refresh();}
        private void OnCategory(int _){if(_open)Refresh();}
        private void OnOffersScroll(Vector2 _) => _quickPurchasePopup?.Hide();
        private void OnQuantity(string _){if(_open){_quickPurchasePopup?.Hide();RefreshDetails();ExplainMaterialTarget();}}
        private void OnHubInteraction(HubInteractionResultEvent result)
        {
            if(!result.Success || result.InventoryId!=_inventory.ReadOnly?.InventoryId)return;
            // 业务入口由常驻面板控制器消费；用户隐藏提示Canvas时，不能连带切断F交互。
            if(result.Kind==HubInteractionKind.Shop)Open();
            else if(result.Kind==HubInteractionKind.Warehouse)Close();
        }
        public void Open()
        {
            if(_hubAccess!=null && (!_hubAccess.TryGetStation(out var station) || station.Kind!=HubStationKind.Shop))return;
            if(_shop.ReadOnly==null)_shop.InitializeConfigured();
            if(!_shop.CanTrade || !_shop.Synchronize())
            {_panel.SetActive(true);_open=true;_pause?.RequestPause(RunPauseReason.Shop);SetStatus(_shop.Message??"商店初始化尚未完成。");return;}
            _pause?.RequestPause(RunPauseReason.Shop);_inventoryUi?.Close();_panel.SetActive(true);_open=true;
            _embeddedInventory?.SetEmbeddedOpen(true);if(_quickPurchase)_itemId=_embeddedInventory?.SelectedItemId;Refresh();
            if(_quickPurchase)SetStatus("选择商品后点击购买，在购买框中确认数量与总价。");
        }
        public void Close(){_purchaseDialog?.Hide();_quickPurchasePopup?.Hide();_purchaseRequest=null;_open=false;_embeddedInventory?.SetEmbeddedOpen(false);if(_panel!=null)_panel.SetActive(false);_pause?.ReleasePause(RunPauseReason.Shop);}
        private void Update()
        {
            using(UpdateMarker.Auto())
            {
            if(!_open)return;
            if(_inventoryUi!=null && _inventoryUi.IsOpen){Close();return;}
            if(_shop.ReadOnly==null)return;
            if(!_shop.CanTrade){Close();return;}
            if(_inventoryVersion==_inventory.ReadOnly.Version && _shopVersion==_shop.ReadOnly.Version && _available==_shop.CanTrade)return;
            // 只有资产版本或阶段变化才刷新；字符串/列表分配是离散UI操作，稳定帧不重新生成。
            Refresh();
            }
        }
        // 商品与中列实例是两个独立选择：点击水不能取消已选瓶子，点击瓶子也不能取消水商品。
        public void SelectOffer(string id){if(Confirming)return;_quickPurchasePopup?.Hide();_offerId=id;if(!_quickPurchase)_itemId=null;RefreshDetails();ExplainMaterialTarget();}
        public void ClickOffer(string id, Vector2 screenPosition)
        {
            if (!_open || Confirming || !TrySelectedOffer(id, out _)) return;
            bool again = _offerId == id && (_quickPurchase || _itemId == null); SelectOffer(id);
            if (again && _quickPurchase && _buyButton != null && _buyButton.interactable) _quickPurchasePopup?.Show(screenPosition);
        }
        public void SelectOwned(string id){if(Confirming)return;_quickPurchasePopup?.Hide();_itemId=id;if(!_quickPurchase)_offerId=null;RefreshDetails();ExplainMaterialTarget();}
        private void Refresh()
        {
            var shop=_shop.ReadOnly;var inventory=_inventory.ReadOnly;if(shop==null || inventory==null)return;
            _quickPurchasePopup?.Hide();
            _inventoryVersion=inventory.Version;_shopVersion=shop.Version;_available=_shop.CanTrade;
            if(_summary!=null)_summary.text=_quickPurchase?"收购额度："+shop.RemainingBudget+" · 补货还需 "+shop.ProgressUntilRefresh+" 次成功冒险\n普通商品先入背包，放不下自动入仓。":
                "钱包："+shop.Currency+"    收购额度："+shop.RemainingBudget+"    补货还需 "+shop.ProgressUntilRefresh+" 次有效成功冒险    Tab 背包/法杖 · Esc 关闭";
            int shownOffers=0; bool selectedVisible=false;
            for(int i=0;i<shop.Count;i++)
            {var offer=shop.GetOffer(i);if(!ShowOffer(offer))continue;if(offer.OfferId==_offerId)selectedVisible=true;var row=Row(_offerViews,_offersRoot,shownOffers++);row.Bind(this,offer.OfferId,
                offer.DisplayName+"\n单价 "+offer.BuyPrice+(offer.IsMaterial?" / GMU":" / 份")+"\n供货 "+offer.Stock,false,Font,offer.Icon);}
            HideAfter(_offerViews,shownOffers);
            if(!selectedVisible)_offerId=null;
            if(_items.Length<inventory.Count)_items=new InventoryItemSnapshot[Math.Max(inventory.Count,16)];
            int count=inventory.CopyItemsTo(_items),shown=0;_itemCount=count;
            if(_itemId!=null && !inventory.TryGetView(_itemId,out _))_itemId=null;
            string previous=!_quickPurchase && _containers!=null && _containers.value<_containerIds.Count?_containerIds[_containers.value]:null;
            // 新版从中列选瓶，不再建立无消费者的旧Dropdown选项。
            _containerIds.Clear();var choices=!_quickPurchase && _containers!=null?new List<Dropdown.OptionData>():null;
            for(int i=0;i<count;i++)
            {
                var item=_items[i];if(item.Location.Kind==ItemLocationKind.WorldItem || item.Location.Kind==ItemLocationKind.ThrownContainer)continue;
                // 新草稿把携带背包和仓库分区；显示筛选不能删掉真实资产或排除可用于购买元素的背包容器。
                if(_embeddedInventory == null && (!_warehouseOnly || item.Location.Kind==ItemLocationKind.Warehouse))
                {
                    var row=Row(_ownedViews,_ownedRoot,shown++);
                    string state=item.Kind==ItemKind.Spell?_inventoryUi.DescribeSpellUse(item,true):
                        item.Kind==ItemKind.Container?item.ContainerTotal+" / "+item.ContainerCapacity+" GMU":item.Kind==ItemKind.Material?item.MaterialQuantity+" GMU":"独立实例";
                    row.Bind(this,item.InstanceId,item.DisplayName+" · "+state+"\n"+(item.IsStarterEquipment?"免费基础本体 · 禁止出售":item.Location.Kind==ItemLocationKind.Warehouse?"仓库":"携带 / 已装备"),true,Font);
                }
                if(choices!=null && item.Kind==ItemKind.Container){_containerIds.Add(item.InstanceId);choices.Add(new Dropdown.OptionData(item.DisplayName+" #"+_containerIds.Count+"（"+item.ContainerTotal+"/"+item.ContainerCapacity+"）"));}
            }
            HideAfter(_ownedViews,shown);
            if(_containers!=null){_containers.gameObject.SetActive(!_quickPurchase);if(!_quickPurchase){_containers.ClearOptions();_containers.AddOptions(choices);}}
            if (_embeddedInventory == null) _backpackView?.Refresh(this,_items,count);
            else _embeddedInventory.RefreshPresentation();
            if(!_quickPurchase && _containers!=null){int selected=_containerIds.IndexOf(previous);_containers.SetValueWithoutNotify(Math.Max(0,selected));_containers.RefreshShownValue();}
            RefreshDetails();
            RefreshPurchaseDialog();
        }
        private ShopEntryView Row(List<ShopEntryView> rows,RectTransform parent,int index)
        {if(index==rows.Count){var row=Instantiate(_entryPrefab,parent);rows.Add(row);}rows[index].gameObject.SetActive(true);return rows[index];}
        private static void HideAfter(List<ShopEntryView> rows,int count){for(int i=count;i<rows.Count;i++)rows[i].gameObject.SetActive(false);}
        private bool ShowOffer(in ShopOfferSnapshot offer)
        {
            if(_search!=null && !string.IsNullOrWhiteSpace(_search.text) && offer.DisplayName.IndexOf(_search.text,StringComparison.OrdinalIgnoreCase)<0)return false;
            int category=_category!=null?_category.value:0;
            // IsMaterial 仍表示 GMU 报价；展示分类还包含食品/草药，不能改动交易数量语义。
            return category==0 || category==1 && offer.IsSpell || category==2 && ItemInventoryClassification.IsTool(offer.Kind,offer.UseKind)
                || category==3 && (offer.IsMaterial || ItemInventoryClassification.IsMaterial(offer.Kind) && !ItemInventoryClassification.IsTool(offer.Kind,offer.UseKind))
                || category==4 && offer.Kind==ItemKind.Wand || category==5 && offer.Kind==ItemKind.Backpack;
        }
        private void RefreshDetails()
        {
            bool buy=false,sell=false,material=false;var shop=_shop.ReadOnly;
            if(shop==null)return;
            if (_detailsPlaceholder)
            {
                bool continuous = TrySelectedOffer(_offerId, out var selected) && selected.IsMaterial;
                buy = _offerId != null && TrySelectedOffer(_offerId, out selected) && selected.Stock > 0;
                if(_quickPurchase && continuous)buy=buy && TryMaterialTarget(selected,out _,out _);
                if (_itemId != null && _inventory.ReadOnly.TryGetView(_itemId, out var owned))
                { sell = owned.Kind != ItemKind.Material; material = owned.Kind == ItemKind.Container || owned.Kind == ItemKind.Material; }
                _details.text = "物品详情待后续开发";
                if(_containers!=null)_containers.gameObject.SetActive(!_quickPurchase && continuous);
                SetButtons(buy, sell, material);
                RefreshSelection(); return;
            }
            _details.text="选左侧商品购买，或选持有物品出售。\n\n买元素需要兼容容器；出售混样按实际比例计价。\n免费基础装备不能直接出售。\n\nE 区域交互 · Tab 背包 · Esc 关闭。";
            if(_offerId!=null)for(int i=0;i<shop.Count;i++)
            {var offer=shop.GetOffer(i);if(offer.OfferId!=_offerId)continue;buy=true;_details.text=offer.DisplayName+"\n\n单价："+offer.BuyPrice+(offer.IsMaterial?" / GMU":" / 份")+"\n剩余供货："+offer.Stock
                +(offer.IsMaterial?"\n\n购买后转入下方选中的兼容容器，保留原有组分。":"\n\n购买产生独立物品实例，默认放入仓库；勾选后尝试放入背包。法杖/背包本体请先买入仓库，再装备。");
                if(offer.IsSpell)_details.text+="\n到手状态："+(offer.Unlimited?(offer.IsQualification?"无限入局":"无限使用"):offer.InitialCharges+(offer.IsQualification?" 次入局":" 次施法"))+"。";
                break;}
            if(_itemId!=null && _inventory.ReadOnly.TryGetView(_itemId,out var item))
            {sell=item.Kind!=ItemKind.Material;material=item.Kind==ItemKind.Container || item.Kind==ItemKind.Material;
                _details.text=item.DisplayName+"\n\n当前位置："+(item.Location.Kind==ItemLocationKind.Warehouse?"仓库":item.Location.Kind==ItemLocationKind.Backpack?"携带背包":"已装备 / 法杖程序")+"\n"+(item.Kind==ItemKind.Spell?(item.HasUnlimitedCharges?"无限使用":"剩余 "+item.RemainingCharges+" 次"):item.Kind==ItemKind.Container?"材料储量 "+item.ContainerTotal+" / "+item.ContainerCapacity+" GMU":"实际持有的一份物品")
                    +"\n\n出售物品：只出售这一份，有限法术按剩余次数折价。\n出售材料：扣实际GMU，混样保持比例，瓶壳保留。\n非空容器须先处理材料，装有程序的法杖须先卸下法术。";}
            if(_quickPurchase && TrySelectedOffer(_offerId,out var quickOffer) && quickOffer.IsMaterial)buy=buy && TryMaterialTarget(quickOffer,out _,out _);
            SetButtons(buy, sell, material);
            RefreshSelection();
        }
        private void SetButtons(bool buy, bool sell, bool material)
        {
            // 作者可移除暂不展示的旧按钮；业务命令仍可复用，不能让一个缺失按钮阻断整张商店。
            if (_buyButton != null) _buyButton.interactable = buy && _shop.CanTrade;
            if (_sellItemButton != null) _sellItemButton.interactable = sell && _shop.CanTrade;
            if (_sellMaterialButton != null) _sellMaterialButton.interactable = material && _shop.CanTrade;
        }
        private void RefreshSelection()
        { for(int i=0;i<_offerViews.Count;i++)_offerViews[i].SetSelected(_offerViews[i].OfferId==_offerId); }
        private bool TrySelectedOffer(string id, out ShopOfferSnapshot offer)
        {
            var read=_shop.ReadOnly;
            for(int i=0;read!=null && i<read.Count;i++){offer=read.GetOffer(i);if(offer.OfferId==id && ShowOffer(offer))return true;}
            offer=default;return false;
        }
        public void Buy()
        {
            _quickPurchasePopup?.Hide();
            if (Confirming) return;
            if (!_open || !_shop.CanTrade || !TrySelectedOffer(_offerId, out var offer)) { SetStatus("请先选中一个当前商品。"); return; }
            if(offer.IsMaterial && !TryMaterialTarget(offer,out _,out var reason)){SetStatus(reason);return;}
            if (_purchaseDialog == null) { SetStatus("缺少购买确认框接线，请应用购买确认框设置。"); return; }
            _purchaseDialog.Show(offer, offer.IsMaterial ? SelectedContainerId : null);
            RefreshPurchaseDialog();
        }
        public void SellItem(){if(!Confirming)Send(ShopTradeKind.SellItem,1);}
        public void SellMaterial()
        {
            if(Confirming)return;
            // 作者已移除数量区；新版出售材料默认1 GMU，兼容旧场景可选的数量输入。
            if(!ReadLegacyQuantity(out var amount)){SetStatus("数量必须是正整数。");return;}
            Send(ShopTradeKind.SellMaterial,amount);
        }
        private bool ReadLegacyQuantity(out ulong amount)
        {amount=1;return _quantity==null || ulong.TryParse(_quantity.text,out amount) && amount>0;}
        private string SelectedContainerId => _quickPurchase ? _itemId : _containers!=null && _containers.value<_containerIds.Count?_containerIds[_containers.value]:null;
        private void Send(ShopTradeKind kind, ulong quantity, string target=null, string requestId=null)
        {
            if(!_open || !_shop.CanTrade)return;
            var read=_shop.ReadOnly;if(read==null)return;
            if(quantity==0){SetStatus("数量必须是正整数。");return;}
            string bag=null;for(int i=0;i<_itemCount;i++)if(_items[i].Location.Kind==ItemLocationKind.EquippedBackpack){bag=_items[i].InstanceId;break;}
            EventBus<ShopTradeRequestedEvent>.Publish(new ShopTradeRequestedEvent{InventoryId=read.InventoryId,RequestId=requestId??Guid.NewGuid().ToString("N"),
                InventoryVersion=_inventory.ReadOnly.Version,ShopVersion=read.Version,OfferId=_offerId,ItemInstanceId=_itemId,Kind=kind,
                Quantity=kind==ShopTradeKind.SellItem?1:quantity,ToBackpack=_quickPurchase || _toBackpack!=null && _toBackpack.isOn,AllowWarehouseFallback=_quickPurchase,BackpackId=bag,
                ContainerId=target??SelectedContainerId});
        }
        private bool TryMaterialTarget(in ShopOfferSnapshot offer,out InventoryItemSnapshot target,out string reason)
            => TryMaterialTarget(offer, SelectedContainerId, out target, out reason);
        private bool TryMaterialTarget(in ShopOfferSnapshot offer,string id,out InventoryItemSnapshot target,out string reason,bool allowFull=false)
        {
            target=default;reason="请在中间的背包或仓库选中一个兼容容器。";
            var read=_inventory.ReadOnly;
            if(id==null || read==null || !read.TryGetView(id,out target) || target.Kind!=ItemKind.Container)return false;
            bool accessible=target.Location.Kind==ItemLocationKind.Warehouse;
            if (target.Location.Kind == ItemLocationKind.EquippedTool) accessible = target.Location.OwnerId == read.ActorId;
            if(target.Location.Kind==ItemLocationKind.Backpack)
                accessible=read.TryGetView(target.Location.OwnerId,out var bag) && bag.Location.Kind==ItemLocationKind.EquippedBackpack;
            if(!accessible)return false;
            if((target.Rules.ContainerMaterials & 1<<offer.MaterialKey)==0){reason="所选容器不能容纳这个元素，请选择兼容容器。";return false;}
            if(!allowFull && target.ContainerTotal>=target.ContainerCapacity){reason="所选容器已满。";return false;}
            reason=null;return true;
        }
        private ulong PurchaseMaximum(in ShopOfferSnapshot offer,in InventoryItemSnapshot target,out string reason)
        {
            reason=null;var maximum=ShopPurchaseQuantity.Maximum(offer.Stock,offer.BuyPrice,_shop.ReadOnly.Currency,offer.IsMaterial,
                offer.IsMaterial && target.ContainerTotal<target.ContainerCapacity?target.ContainerCapacity-target.ContainerTotal:0);
            if(maximum==0)reason=offer.Stock==0?"商品已售罄。":offer.BuyPrice<=0?"商品价格配置无效。":offer.IsMaterial && target.ContainerTotal>=target.ContainerCapacity?"所选容器已满。":"金币不足，无法购买一份。";
            return maximum;
        }
        private void RefreshPurchaseDialog()
        {
            if(!Confirming || _purchaseDialog.Pending)return;
            if(!_shop.CanTrade || !TrySelectedOffer(_purchaseDialog.OfferId,out var offer) || _offerId!=_purchaseDialog.OfferId)
            {_purchaseDialog.Hide();SetStatus("商品或交易状态已变化，请重新选择。");return;}
            InventoryItemSnapshot target=default;
            if(offer.IsMaterial && !TryMaterialTarget(offer,_purchaseDialog.ContainerId,out target,out var targetReason,true))
            {
                _purchaseDialog.Hide();SetStatus(targetReason);return;
            }
            var maximum=PurchaseMaximum(offer,target,out var reason);
            _purchaseDialog.Refresh(offer,maximum,_shop.ReadOnly.Currency,target,reason);
        }
        internal void ConfirmPurchase(ShopPurchaseDialog dialog)
        {
            // 数量与版本必须在最后一次确认时复核，不使用打开窗口时的旧报价作为资产权威。
            if(!_open || dialog!=_purchaseDialog || !Confirming || dialog.Pending)return;
            ulong requested=dialog.Quantity;RefreshPurchaseDialog();
            if(!Confirming)return;
            if(requested==0 || requested>dialog.Maximum){dialog.ShowFailure("可购买数量已变化，请检查数量后再次确认。");return;}
            if(!TrySelectedOffer(dialog.OfferId,out var offer))return;
            _purchaseRequest=Guid.NewGuid().ToString("N");dialog.SetPending(true);
            Send(offer.IsMaterial?ShopTradeKind.BuyMaterial:ShopTradeKind.BuyItem,requested,dialog.ContainerId,_purchaseRequest);
        }
        private void ExplainMaterialTarget()
        {
            if(!_quickPurchase || !TrySelectedOffer(_offerId,out var offer) || !offer.IsMaterial)return;
            if(!TryMaterialTarget(offer,out var target,out var reason)){SetStatus(reason);return;}
            SetStatus("购买目标："+target.DisplayName+" · "+target.ContainerTotal+"/"+target.ContainerCapacity+" GMU");
        }
        private void OnResult(ShopTradeResultEvent result)
        {
            if(result.InventoryId!=_inventory.ReadOnly?.InventoryId)return;
            bool confirmingResult=_purchaseRequest!=null && result.RequestId==_purchaseRequest;
            if(confirmingResult){_purchaseRequest=null;if(result.Success)_purchaseDialog.Hide();else _purchaseDialog.SetPending(false);}
            SetStatus(result.Success?(result.Replayed?"原成交收据，未重复交易。":"成交 "+result.ActualQuantity+"，货币变化 "+result.CurrencyDelta+"。"):
                Explain(result.Failure));Refresh();
            if(result.Success && _quickPurchase && result.Materials==null && result.CurrencyDelta<0)
                SetStatus(result.WarehouseQuantity==0?"购买 "+result.ActualQuantity+" 份，已放入背包。":
                    "背包 "+(result.ActualQuantity-result.WarehouseQuantity)+" 份，仓库 "+result.WarehouseQuantity+" 份。");
            if(result.Success && result.Materials!=null)
            {
                for(int i=0;i<result.Materials.Length;i++)
                {var amount=result.Materials[i];string name="材料 "+amount.MaterialKey;var read=_shop.ReadOnly;
                    for(int j=0;j<read.Count;j++){var offer=read.GetOffer(j);if(offer.MaterialKey==amount.MaterialKey){name=offer.DisplayName;break;}}
                    SetStatus(_statusText+"  "+name+" "+amount.Quantity+" GMU");}
            }
            if(confirmingResult && !result.Success && Confirming)_purchaseDialog.ShowFailure(_statusText);
        }
        private static string Explain(ShopTradeFailure failure)
        {
            switch(failure)
            {
                case ShopTradeFailure.InsufficientCurrency:return "钱包余额不足，资产未改变。";
                case ShopTradeFailure.OutOfStock:return "供货不足，整笔未成交。";
                case ShopTradeFailure.InventorySpace:return "当前物品状态不允许这笔交易，资产未改变。";
                case ShopTradeFailure.ContainerFull:return "容器剩余空间不足，未扣钱。";
                case ShopTradeFailure.IncompatibleMaterial:return "请选一个允许该材料的容器。";
                case ShopTradeFailure.InsufficientMaterial:return "实际材料量不足，未部分出售。";
                case ShopTradeFailure.BudgetExceeded:return "商店收购剩余额度不足，材料/物品保留。";
                case ShopTradeFailure.ContainerNotEmpty:return "请先出售或转移材料；出售瓶壳不会静默带走材料。";
                case ShopTradeFailure.NotSellable:return "这份物品不可出售：请检查免费来源、剩余次数或未卸下的内容。";
                case ShopTradeFailure.SaveFailed:return "交易保存失败，资产未改变，可重试。";
                case ShopTradeFailure.StaleVersion:return "库存或供货已变化，请重新选择再交易。";
                case ShopTradeFailure.NotInHub:return "交易只在据点进行。";
                case ShopTradeFailure.ResourceBusy:return "材料/施法还在处理中，稍后再交易。";
                default:return "请求或配置不合法，交易未提交。";
            }
        }
    }
}
