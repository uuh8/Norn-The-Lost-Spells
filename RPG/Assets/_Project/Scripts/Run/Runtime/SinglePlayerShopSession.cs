using Game.Core;
using Unity.Profiling;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 原商店的Unity边界：单机调用原Profile可靠事务；账号Hub只冻结配置、安装服务器读数、转交HTTP意图。
    /// 两种资产Owner共用IShopReadOnly与原UI，但账号分支绝不创建旧Profile或调用本地Repository。
    /// </summary>
    public sealed class SinglePlayerShopSession : MonoBehaviour
    {
        [SerializeField] private SinglePlayerInventorySession _inventory;
        [SerializeField] private ShopDefinition _definition;
        private ShopConfiguration _configuration;
        private ShopReadModel _read;
        private AccountShopReadModel _accountRead;
        private ItemCatalogIndex _accountCatalog;
        private HubInteractionController _accountHub;
        private bool _accountOpenPending;
        private uint _accountRequestedVersion;
        private string _accountConfigHash;
        private readonly ShopTradeService _trades=new ShopTradeService();
        private bool _attempted;
        private static readonly ProfilerMarker UpdateMarker=new ProfilerMarker("Norn.Shop.SessionUpdate");
        public bool IsAccountSession => _inventory != null && _inventory.AccountInventoryReadOnly;
        public IShopReadOnly ReadOnly => IsAccountSession ? _accountRead : _read;
        internal SinglePlayerInventorySession InventorySession => _inventory;
        /// <summary>配置和账号适配就绪才开放F；取得服务器供货仍是另一道交易门，不能拿本地stock占位。</summary>
        public bool AccountEntryAvailable => IsAccountSession && _accountCatalog != null && _configuration != null
            && _inventory.TryGetAccountHubSummary(out _) && EventBus<OnlineAccountShopOpenRequestedEvent>.HasSubscribers;
        public bool AccountPanelAvailable => AccountEntryAvailable && AtAccountShop();
        public string Message { get; private set; }
        public bool CanTrade => IsAccountSession ? AccountPanelAvailable && !_accountOpenPending && _accountRead != null
            && _inventory.TryGetAccountHubSummary(out var summary) && _accountRead.PlayerId == summary.PlayerId
            && _accountRead.InventoryId == summary.InventoryId && _accountRead.InventoryVersion == summary.Version
            : _inventory != null && _inventory.ReadOnly != null && _inventory.Profile.PendingRaid == null && !_inventory.Profile.RaidActionsFrozen
                && (_inventory.RunCoordinator == null || _inventory.RunCoordinator.State.Phase == RaidPhase.HubReady)
                && (_inventory.HubInteraction == null || _inventory.HubInteraction.TryGetStation(out var station) && station.Kind == HubStationKind.Shop);
        public bool InitializeConfigured()=>Initialize(_inventory,_definition);
        private void OnEnable()
        {
            EventBus<ShopTradeRequestedEvent>.Subscribe(OnTrade); EventBus<RaidStateChangedEvent>.Subscribe(OnRaidState);
            EventBus<OnlineAccountShopChangedEvent>.Subscribe(OnAccountShopChanged);
            EventBus<OnlineAccountInventoryHubChangedEvent>.Subscribe(OnAccountInventoryChanged);
        }
        private void OnDisable()
        {
            EventBus<ShopTradeRequestedEvent>.Unsubscribe(OnTrade); EventBus<RaidStateChangedEvent>.Unsubscribe(OnRaidState);
            EventBus<OnlineAccountShopChangedEvent>.Unsubscribe(OnAccountShopChanged);
            EventBus<OnlineAccountInventoryHubChangedEvent>.Unsubscribe(OnAccountInventoryChanged);
            if (_accountHub != null) _accountHub.ReleaseAccountShop(this);
            _accountHub = null; _accountRead = null; _accountOpenPending = false; _accountRequestedVersion = 0;
        }
        private void Update()
        {
            // 标记包含就绪检查；初始化是一次性操作，稳定帧不创建候选或刷新供货。
            using (UpdateMarker.Auto())
            {
                if (_inventory == null) return;
                if (!_attempted && (IsAccountSession ? _inventory.TryGetAccountHubSummary(out _) : _inventory.ReadOnly != null))
                    Initialize(_inventory, _definition);
                if (IsAccountSession && _configuration != null) BindAccountHub();
            }
        }
        public bool Initialize(SinglePlayerInventorySession inventory,ShopDefinition definition)
        {
            if (inventory != null && inventory.AccountInventoryReadOnly)
            {
                _inventory = inventory; _definition = definition;
                if (!inventory.TryGetAccountHubSummary(out _)) { Message = "等待本次账号资产读取完成。"; return false; }
                _attempted = true;
                if (_accountCatalog == null)
                {
                    if (!inventory.TryGetAccountCatalog(out var catalog, out var error)
                        || definition == null || !definition.TryFreeze(catalog, out var configuration))
                    { Message = error ?? "原商店配置无法与账号目录匹配，未创建本地资产。"; return false; }
                    _accountCatalog = catalog; _configuration = configuration;
                }
                BindAccountHub(); Message = "等待服务器确认商店供货。"; return true;
            }
            if(_read!=null)return true;
            _inventory=inventory;_definition=definition;
            if(inventory?.ReadOnly==null)return false;
            _attempted=true;
            if(definition==null || !definition.TryFreeze(inventory.Profile.Items.Catalog,out _configuration))
            {Message="商店配置无效，未重置供货/资产。";return false;}
            if(!Synchronize())return false;
            _read=new ShopReadModel(inventory.Profile,_configuration,inventory.ReadOnly.InventoryId);return true;
        }
        public bool Synchronize()
        {
            if (IsAccountSession)
            {
                if (_accountCatalog == null && !InitializeConfigured()) return false;
                if (!AccountPanelAvailable || !_inventory.TryGetAccountHubSummary(out var summary))
                { Message = "请在当前账号据点商店区域内操作。"; return false; }
                // 一个版本只保留一个在途读取；库存更新后用新版本重新请求，旧回复不能装入新账号/版本。
                if (_accountOpenPending && _accountRequestedVersion == summary.Version) return false;
                _accountOpenPending = true; _accountRequestedVersion = summary.Version; Message = "正在读取服务器商店供货…";
                EventBus<OnlineAccountShopOpenRequestedEvent>.Publish(new OnlineAccountShopOpenRequestedEvent
                { SceneHandle = gameObject.scene.handle, CallerId = GetInstanceID(), InventoryId = summary.InventoryId });
                return CanTrade;
            }
            if(!CanTrade || _configuration==null){Message="交易仅限据点，当前阶段不可用。";return false;}
            if(!ShopProgressTracker.TryPrepare(_inventory.Profile,_configuration,out var candidate,out bool changed))
            {Message="商店配置/资源状态不兼容，原资产和供货已保留。";return false;}
            if(changed)
            {
                var saved=_inventory.Repository.TrySave(candidate);
                if(!saved.Success){Message="商店同步保存失败，可重试："+saved.Message;return false;}
                _inventory.AdoptCommitted(candidate,true);
            }
            Message=changed?"供货已依据可靠冒险进展同步。":"供货与收购额度沿用已保存状态。";return true;
        }
        private void OnRaidState(RaidStateChangedEvent state)
        {if(state.InventoryId==_inventory?.ReadOnly?.InventoryId && state.Phase==RaidPhase.HubReady && _configuration!=null)Synchronize();}
        private void OnTrade(ShopTradeRequestedEvent request)
        {
            if (IsAccountSession)
            {
                if (!_inventory.TryGetAccountHubSummary(out var summary) || request.InventoryId != summary.InventoryId) return;
                if (!CanTrade || request.InventoryVersion != summary.Version || request.ShopVersion != _accountRead.Version)
                {
                    EventBus<ShopTradeResultEvent>.Publish(new ShopTradeResultEvent { InventoryId = request.InventoryId,
                        RequestId = request.RequestId, Failure = CanTrade ? ShopTradeFailure.StaleVersion : ShopTradeFailure.NotInHub,
                        Message = "账号商店或库存尚未确认，原资产保持不变。" }); return;
                }
                // 原UI仍只表达Offer/数量/目标；账号路径绝不进入下面的本地Profile候选或Repository保存。
                EventBus<OnlineAccountShopTradeRequestedEvent>.Publish(new OnlineAccountShopTradeRequestedEvent
                { CallerId = GetInstanceID(), SceneHandle = gameObject.scene.handle, Request = request }); return;
            }
            if(request.InventoryId!=_inventory?.ReadOnly?.InventoryId)return;
            if(_read==null || !CanTrade){Publish(request,new ShopTradeResult(ShopTradeFailure.NotInHub));return;}
            var result=_trades.TryTrade(_inventory.Profile,_inventory.Repository,_configuration,request,out var candidate);
            if(result.Success && !result.Replayed)_inventory.AdoptCommitted(candidate,true);
            Publish(request,result);
        }
        private bool AtAccountShop()
        {
            var hub = _inventory != null ? _inventory.HubInteraction : null;
            return hub != null && hub.gameObject.scene == gameObject.scene
                && hub.TryGetStation(out var station) && station.Kind == HubStationKind.Shop;
        }
        private void BindAccountHub()
        {
            var hub = _inventory != null ? _inventory.HubInteraction : null;
            if (hub == _accountHub) return;
            if (_accountHub != null) _accountHub.ReleaseAccountShop(this);
            _accountHub = hub;
            if (_accountHub != null) _accountHub.BindAccountShop(this);
        }
        private void OnAccountInventoryChanged(OnlineAccountInventoryHubChangedEvent change)
        {
            if (!IsAccountSession || change.SceneHandle != gameObject.scene.handle) return;
            if (!_inventory.TryGetAccountHubSummary(out var summary))
            { _accountRead = null; _accountOpenPending = false; return; }
            // Scope会在同一次Profile安装/场景准备时重复通知。已经由服务器确认的同账号、
            // 同库存版本报价无需因此重新请求；否则仅重放只读metadata也会把ready改为pending。
            // 用户明确重新Open仍走Synchronize重新读取；若Open已在途，这里也不提前解除pending。
            if (_accountRead != null && _accountRead.PlayerId == summary.PlayerId
                && _accountRead.InventoryId == summary.InventoryId && _accountRead.InventoryVersion == summary.Version) return;
            _accountRead = null;
            if (_accountRequestedVersion != summary.Version) _accountOpenPending = false;
            if (_accountCatalog != null && AccountPanelAvailable) Synchronize();
        }
        private void OnAccountShopChanged(OnlineAccountShopChangedEvent change)
        {
            if (!IsAccountSession || change.CallerId != GetInstanceID() || change.SceneHandle != gameObject.scene.handle) return;
            _accountOpenPending = false;
            if (!change.Success) { _accountRead = null; Message = change.Message ?? "服务器商店暂时不可用。"; return; }
            var value = change.Snapshot;
            if (!_inventory.TryGetAccountHubSummary(out var summary) || value == null
                || value.SceneHandle != gameObject.scene.handle || value.PlayerId != summary.PlayerId
                || value.InventoryId != summary.InventoryId || value.InventoryVersion != summary.Version
                || !OnlineAccountInventoryHubScope.TryCopyForScene(gameObject.scene.handle, out var profile) || value.Gold != profile.Gold)
            { _accountRead = null; Message = "商店回复不属于当前账号库存版本，未采用旧数据。"; return; }
            if (!AccountShopReadModel.TryCreate(_accountCatalog, _configuration, value, out var candidate, out var error)
                || _accountRead != null && candidate.Version < _accountRead.Version
                || _accountConfigHash != null && candidate.ConfigHash != _accountConfigHash)
            {
                // 上面的Caller/Scene门仍保护别的窗口；这里已经确认是当前商店自己的回复。
                // 先用旧版本完成单调检查，再撤销旧ready，不能因错误报价结束pending而重新放行旧读数交易。
                _accountRead = null; Message = error ?? "商店配置来源或供货版本已变化，未开放交易。"; return;
            }
            _accountRead = candidate; _accountConfigHash = candidate.ConfigHash;
            Message = change.Message ?? "已读取服务器真实供货与统一金币。";
        }
        private void Publish(in ShopTradeRequestedEvent request,in ShopTradeResult result)
        {
            Message=result.Success?(result.Replayed?"已成交请求的原收据，未重复扣款。":"交易已可靠保存。"):("交易未提交："+result.Failure);
            var r=result.Receipt;
            EventBus<ShopTradeResultEvent>.Publish(new ShopTradeResultEvent{InventoryId=request.InventoryId,RequestId=request.RequestId,
                Success=result.Success,Replayed=result.Replayed,Failure=result.Failure,Message=Message,Currency=_inventory.Profile.Currency,
                CurrencyDelta=r?.CurrencyDelta??0,ActualQuantity=r?.Quantity??0,WarehouseQuantity=r?.WarehouseQuantity??0,Materials=r?.Materials,
                ShopVersion=_inventory.Profile.Shop?.Version??0,Generation=_inventory.SavedGeneration});
        }
    }
}
