using Game.Core;
using Unity.Profiling;
using UnityEngine;

namespace Game.Run
{
    /// <summary>商店的Unity边界；库存Session仍是唯一Profile持有者，本组件只调纯事务并采用可靠候选。</summary>
    public sealed class SinglePlayerShopSession : MonoBehaviour
    {
        [SerializeField] private SinglePlayerInventorySession _inventory;
        [SerializeField] private ShopDefinition _definition;
        private ShopConfiguration _configuration;
        private ShopReadModel _read;
        private readonly ShopTradeService _trades=new ShopTradeService();
        private bool _attempted;
        private static readonly ProfilerMarker UpdateMarker=new ProfilerMarker("Norn.Shop.SessionUpdate");
        public IShopReadOnly ReadOnly=>_read;
        public string Message { get; private set; }
        public bool CanTrade=>_inventory?.ReadOnly!=null && _inventory.Profile.PendingRaid==null && !_inventory.Profile.RaidActionsFrozen
            && (_inventory.RunCoordinator==null || _inventory.RunCoordinator.State.Phase==RaidPhase.HubReady)
            && (_inventory.HubInteraction==null || _inventory.HubInteraction.TryGetStation(out var station) && station.Kind==HubStationKind.Shop);
        public bool InitializeConfigured()=>Initialize(_inventory,_definition);
        private void OnEnable(){EventBus<ShopTradeRequestedEvent>.Subscribe(OnTrade);EventBus<RaidStateChangedEvent>.Subscribe(OnRaidState);}
        private void OnDisable(){EventBus<ShopTradeRequestedEvent>.Unsubscribe(OnTrade);EventBus<RaidStateChangedEvent>.Unsubscribe(OnRaidState);}
        private void Update()
        {
            // 标记包含就绪检查；初始化是一次性操作，稳定帧不创建候选或刷新供货。
            using(UpdateMarker.Auto())if(!_attempted && _inventory?.ReadOnly!=null)Initialize(_inventory,_definition);
        }
        public bool Initialize(SinglePlayerInventorySession inventory,ShopDefinition definition)
        {
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
            if(request.InventoryId!=_inventory?.ReadOnly?.InventoryId)return;
            if(_read==null || !CanTrade){Publish(request,new ShopTradeResult(ShopTradeFailure.NotInHub));return;}
            var result=_trades.TryTrade(_inventory.Profile,_inventory.Repository,_configuration,request,out var candidate);
            if(result.Success && !result.Replayed)_inventory.AdoptCommitted(candidate,true);
            Publish(request,result);
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
