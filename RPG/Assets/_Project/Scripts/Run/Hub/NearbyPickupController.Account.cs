using System;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 原附近/F入口的账号适配。只读Session单一地面Source，逐条提交固定意图；
    /// HTTP结果未知不会隐藏代理、改本地Store或把队列切到另一个物品。
    /// </summary>
    public sealed partial class NearbyPickupController
    {
        private bool _account, _accountUnknown, _accountReceived;
        private AccountInventoryPresentationSource _accountSource;
        private string _accountPlayer;
        private int _accountScene;
        private bool _accountPendingMaterial;
        private ulong _accountPendingMaterialQuantity;
        private OnlineAccountInventoryCommandIntent _accountPending;
        private OnlineAccountInventoryCommandResultEvent _accountReceipt;
        private string[] _accountBatch;
        private int _accountBatchIndex;
        private string _accountBatchItem;
        private int _accountBatchAttempts;

        public void ConfigureAccount(AccountInventoryPresentationSource source, Transform actor)
        {
            if (_account && ReferenceEquals(_accountSource,source) && _player==actor && !_lifecycleReleased) return;
            bool same = _account && source != null && _accountPlayer == source.PlayerId && _player == actor
                && actor != null && _accountScene == actor.gameObject.scene.handle;
            if (!same) ClearAccountRequests();
            ClearOnlineRequests(); _online=false; _onlineSource=null;
            _account=true; _accountSource=source; _player=actor; _accountPlayer=source?.PlayerId;
            _accountScene=actor!=null?actor.gameObject.scene.handle:0;
            _forceListNotification=true;
            if (isActiveAndEnabled) EnsureSubscriptions();
            RefreshCandidates();
        }
        private bool AccountContextAlive => this != null && !_lifecycleReleased && _account && isActiveAndEnabled
            && _player != null && _player.gameObject.scene == gameObject.scene && _accountSource?.ReadOnly != null
            && _accountSource.PlayerId == _accountPlayer && _accountSource.Version != 0
            && OnlineAccountInventoryHubScope.TryGetSummaryForScene(_accountScene, out var summary)
            && summary.PlayerId == _accountPlayer && summary.Version == _accountSource.Version
            && !summary.AssetsLocked && string.IsNullOrEmpty(summary.ActiveRunId);
        private bool IsAccountGround(string id, WorldItemPickup proxy)
            => proxy != null && _accountSource != null && CurrentRead.TryGetView(id,out var item)
                && item.Location.Kind==ItemLocationKind.WorldItem && item.Location.OwnerId==proxy.WorldOwner
                && _accountSource.IsGroundItemNearby(id,_player,3f);

        private bool TryBeginAccountPickup(string id)
        {
            if (!CanUse || string.IsNullOrEmpty(id)) return false;
            if (!string.IsNullOrEmpty(_accountPending.CommandId))
                return _accountUnknown && _accountPending.ItemId == id && TryRetryPendingAccountPickup();
            if (!_accountSource.TryPrepareGroundPickup(id, out var intent, out _))
            {
                // 当前物品仍在合法范围但没有自动目标时，沿原UI进入手动摆放；不伪造一次HTTP成功。
                if(_accountSource.IsGroundItemNearby(id,_player,3f) && CurrentRead.TryGetView(id,out _))
                    PublishOnlinePickupResult(id,false,OnlineInventoryCommandFailure.NoCapacity);
                return false;
            }
            _accountPendingMaterial=CurrentRead.TryGetView(id,out var item) && item.Kind==ItemKind.Material;
            _accountPendingMaterialQuantity=_accountPendingMaterial?item.MaterialQuantity:0;
            intent.CommandId=Guid.NewGuid().ToString("N");
            _accountPending=intent;_accountUnknown=_accountReceived=false;_accountReceipt=default;
            PublishAccountPickup();return true;
        }
        /// <summary>原保存/刷新入口的显式恢复，只复述当前未知命令，不生成另一编号或重新选格。</summary>
        public bool TryRetryPendingAccountPickup()
        {
            if (!AccountContextAlive || !_accountUnknown || string.IsNullOrEmpty(_accountPending.CommandId)) return false;
            _accountUnknown=false;_accountReceived=false;PublishAccountPickup();return true;
        }
        private void PublishAccountPickup()
            => EventBus<OnlineAccountInventoryCommandRequestedEvent>.Publish(new OnlineAccountInventoryCommandRequestedEvent
            {CallerId=GetInstanceID(),Intent=_accountPending});
        private void OnAccountReceipt(OnlineAccountInventoryCommandResultEvent result)
        {
            if (!_account || !AccountContextAlive || string.IsNullOrEmpty(_accountPending.CommandId)
                || result.CallerId!=GetInstanceID() || result.PlayerId!=_accountPending.PlayerId
                || result.SceneHandle!=_accountPending.SceneHandle || result.CommandId!=_accountPending.CommandId) return;
            if (result.State==OnlineAccountInventoryCommandState.Pending) return;
            if (result.State==OnlineAccountInventoryCommandState.Unknown)
            {_accountUnknown=true;return;}
            _accountReceipt=result;_accountReceived=true;_accountUnknown=false;
        }
        private void ProgressAccountPickup()
        {
            if (!AccountContextAlive) return;
            if (!string.IsNullOrEmpty(_accountPending.CommandId))
            {
                if (!_accountReceived) return;
                bool success=_accountReceipt.State==OnlineAccountInventoryCommandState.Committed;
                if (success && (_accountReceipt.CommittedVersion<_accountPending.ExpectedVersion
                    || CurrentRead.Version<_accountReceipt.CommittedVersion)) return;
                string id=_accountPending.ItemId;
                if (success && !AccountPickupWasInstalled(id)) return;
                var failure=_accountReceipt.Failure;
                bool materialRemains=success && _accountPendingMaterial && CurrentRead.TryGetView(id,out var remaining)
                    && remaining.Location.Kind==ItemLocationKind.WorldItem;
                _accountPending=default;_accountReceipt=default;_accountReceived=_accountUnknown=false;
                _accountPendingMaterial=false;_accountPendingMaterialQuantity=0;
                if(_accountBatch!=null && !materialRemains){_accountBatchIndex++;_accountBatchItem=null;}
                PublishOnlinePickupResult(id,success,failure);
                RefreshCandidates();
                if(failure==OnlineInventoryCommandFailure.NotReady || failure==OnlineInventoryCommandFailure.StaleVersion)
                {_accountBatch=null;_accountBatchIndex=0;}
            }
            if(_accountBatch!=null && string.IsNullOrEmpty(_accountPending.CommandId) && CanUse)StartNextAccountPickup();
        }
        private bool AccountPickupWasInstalled(string id)
        {
            // 拾取可将材料并入原堆并退休旧ID。只有业务已确认的完整Profile才到这里；
            // 仍在原落地点的同ID不能算拾到，消失或归本人的闭包才可推进下一条。
            if(!CurrentRead.TryGetView(id,out var item))return _accountPendingMaterial;
            if(item.Location.Kind==ItemLocationKind.WorldItem)
                return _accountPendingMaterial && item.Location.OwnerId==_accountPending.WorldOwnerId
                    && item.MaterialQuantity<_accountPendingMaterialQuantity;
            var root=item.Location;
            for(int depth=0;root.HasParentInstance;depth++)
            {if(depth>=CurrentRead.Count || !CurrentRead.TryGetView(root.OwnerId,out var parent))return false;root=parent.Location;}
            return root.OwnerId==_accountPlayer && (root.Kind==ItemLocationKind.EquippedBackpack
                || root.Kind==ItemLocationKind.EquippedWand || root.Kind==ItemLocationKind.EquippedTool || root.Kind==ItemLocationKind.SpellStorage);
        }
        private void StartNextAccountPickup()
        {
            while(_accountBatch!=null && _accountBatchIndex<_accountBatch.Length)
            {
                string id=_accountBatch[_accountBatchIndex];
                if(_accountBatchItem!=id){_accountBatchItem=id;_accountBatchAttempts=CurrentRead.Count+1;}
                // 保留原全部拾取对材料部分并堆后的继续尝试；每条都等待新Profile，且次数沿原Count+1上界。
                if(_accountBatchAttempts-->0 && TryBeginAccountPickup(id))return;
                _accountBatchIndex++;_accountBatchItem=null;
            }
            _accountBatch=null;_accountBatchIndex=0;_accountBatchItem=null;_accountBatchAttempts=0;
        }
        private void ClearAccountRequests()
        {_accountPending=default;_accountReceipt=default;_accountReceived=_accountUnknown=false;_accountBatch=null;_accountBatchIndex=0;
            _accountPendingMaterial=false;_accountPendingMaterialQuantity=0;_accountBatchItem=null;_accountBatchAttempts=0;}
    }
}
