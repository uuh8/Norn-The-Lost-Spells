using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>世界资产的场景代理，只迁移已有身份。这里不创建/复制法术或材料内容。</summary>
    public sealed class WorldItemPickup : MonoBehaviour
    {
        // 注册在 Bind、拾取成功或 Destroy 时释放；普通停用仍保留身份，避免两个所有者。
        // 元组为值键，强引用在场景对象销毁时释放；每局结束必须销毁所有代理（M4 世界清理）。
        private static readonly Dictionary<(ItemOwnershipStore, string), WorldItemPickup> _proxies =
            new Dictionary<(ItemOwnershipStore, string), WorldItemPickup>();
        private ItemOwnershipStore _store;
        public string ItemId { get; private set; }
        public string WorldOwner { get; private set; }
        public string InventoryId { get; private set; }
        private bool _collected;
        private bool _subscribed;
        private Transform _collector;
        private float _reach;
        public static int RegisteredCount=>_proxies.Count;
        public static int CopyNearby(string inventoryId,Transform player,float reach,WorldItemPickup[] buffer)
        {
            if(player==null || inventoryId==null)return 0;int count=0;
            foreach(var proxy in _proxies.Values)
            {
                if(proxy==null || proxy._collected || !proxy.isActiveAndEnabled || proxy.InventoryId!=inventoryId || proxy.gameObject.scene!=player.gameObject.scene)continue;
                float d=(proxy.transform.position-player.position).sqrMagnitude;if(d>reach*reach)continue;
                if(count>=buffer.Length)throw new InvalidOperationException("候选缓冲必须在注册边界预留全部代理容量。");
                int index=count++;while(index>0)
                {var prior=buffer[index-1];float pd=(prior.transform.position-player.position).sqrMagnitude;
                    if(pd<d || pd==d && string.CompareOrdinal(prior.ItemId,proxy.ItemId)<=0)break;buffer[index]=prior;index--;}
                buffer[index]=proxy;
            }
            return count;
        }
        public void ConfigureReach(Transform collector,float reach){_collector=collector;_reach=reach;}
        public static WorldItemPickup FindNearby(string inventoryId,string itemId,Transform player,float reach)
        {
            if(player==null || inventoryId==null)return null;WorldItemPickup nearest=null;float distance=reach*reach;
            foreach(var proxy in _proxies.Values)
            {
                if(proxy==null || proxy._collected || !proxy.isActiveAndEnabled || proxy.InventoryId!=inventoryId
                    || itemId!=null && proxy.ItemId!=itemId || proxy.gameObject.scene!=player.gameObject.scene)continue;
                float d=(proxy.transform.position-player.position).sqrMagnitude;
                if(d<distance || d==distance && (nearest==null || string.CompareOrdinal(proxy.ItemId,nearest.ItemId)<0)){nearest=proxy;distance=d;}
            }
            return nearest;
        }
        private void OnEnable()
        {
            if (_subscribed) return;
            EventBus<InventoryCommandEvent>.Subscribe(OnCommand); _subscribed = true;
        }
        private void OnDisable()
        {
            if (!_subscribed) return;
            EventBus<InventoryCommandEvent>.Unsubscribe(OnCommand); _subscribed = false;
        }
        private void OnDestroy()
        {
            OnDisable();
            ReleaseIdentity();
        }

        private void ReleaseIdentity()
        {
            if (_store == null) return;
            var key = (_store, ItemId);
            if (_proxies.TryGetValue(key, out WorldItemPickup current) && ReferenceEquals(current, this))
            {_proxies.Remove(key);EventBus<WorldItemVisualBoundEvent>.Publish(new WorldItemVisualBoundEvent{Root=transform,InventoryId=InventoryId,RegisteredCount=_proxies.Count});}
        }

        public static bool CanBind(ItemOwnershipStore store, string itemId) =>
            !_proxies.TryGetValue((store, itemId), out WorldItemPickup proxy) || proxy == null;

        public bool TryBind(ItemOwnershipStore store, string itemId, string worldOwner, string inventoryId = null)
        {
            if (_store != null || store == null || !store.TryGetLocation(itemId, out ItemLocation location)
                || location.Kind != ItemLocationKind.WorldItem || !string.Equals(location.OwnerId, worldOwner, StringComparison.Ordinal)) return false;
            var key = (store, itemId);
            if (_proxies.TryGetValue(key, out WorldItemPickup existing) && existing != null) return false;
            _proxies[key] = this;
            _store = store; ItemId = itemId; WorldOwner = worldOwner; InventoryId = inventoryId;
            EventBus<WorldItemVisualBoundEvent>.Publish(new WorldItemVisualBoundEvent{Root=transform,InventoryId=inventoryId,RegisteredCount=_proxies.Count,Added=true});return true;
        }

        public InventoryOperationResult TryCollect(string backpackId, in GridPlacement placement, uint expectedVersion)
            => TryCollect(placement.InBackpack(backpackId), expectedVersion);

        public InventoryOperationResult TryCollect(in ItemLocation destination, uint expectedVersion)
        {
            if (_store == null) return new InventoryOperationResult(false, InventoryFailureReason.InvalidOwnership, 0);
            // UI拖拽和自动拾取共用此Gate；列表显示曾经在附近，不等于执行时仍可远程拿走。
            if(_collector!=null && (gameObject.scene!=_collector.gameObject.scene || (transform.position-_collector.position).sqrMagnitude>_reach*_reach))
                return _store.Result(false,InventoryFailureReason.InvalidOwnership);
            if (_collected || !_store.TryGetLocation(ItemId, out ItemLocation source)
                || source.Kind != ItemLocationKind.WorldItem || !string.Equals(source.OwnerId, WorldOwner, StringComparison.Ordinal))
                return _store.Result(false, InventoryFailureReason.InvalidOwnership);
            if (destination.Kind != ItemLocationKind.Backpack && destination.Kind != ItemLocationKind.EquippedWand
                && destination.Kind != ItemLocationKind.EquippedBackpack && destination.Kind != ItemLocationKind.WandSlot && destination.Kind != ItemLocationKind.SpellStorage)
                return _store.Result(false, InventoryFailureReason.InvalidOwnership);
            // 背包外壳不能塞入背包；拾取可进入空装备位，仍验证占位与全部子资产。
            // 不提供直接送仓库的箱庭拾取路径，撤离结算在 M4 实现。
            InventoryOperationResult result = _store.TryMove(ItemId, destination, expectedVersion);
            if (result.Success)
            {
                // EventBus 是同步派发；先标记和停用，阻止重复拾取，OnDisable 同时解除订阅。
                _collected = true; ReleaseIdentity(); gameObject.SetActive(false);
                if (Application.isPlaying) Destroy(gameObject);
            }
            return result;
        }

        private void OnCommand(InventoryCommandEvent command)
        {
            if (_store == null || _collected || !string.Equals(command.InventoryId, InventoryId, StringComparison.Ordinal)
                || command.Kind != InventoryCommandKind.Pickup || !string.Equals(command.ItemId, ItemId, StringComparison.Ordinal)
                || !string.Equals(command.WorldOwner, WorldOwner, StringComparison.Ordinal)) return;
            uint before = _store.Version;
            InventoryOperationResult result;
            switch (command.PickupDestination)
            {
                case InventoryPickupDestination.SpellStorage:
                    result = TryCollect(new ItemLocation(ItemLocationKind.SpellStorage, command.OwnerId, 0, 0, 0, command.SlotIndex), command.ExpectedVersion); break;
                case InventoryPickupDestination.Backpack:
                    result = TryCollect(command.OwnerId, new GridPlacement(command.X, command.Y, command.QuarterTurns), command.ExpectedVersion); break;
                case InventoryPickupDestination.EquippedWand:
                    result = TryCollect(new ItemLocation(ItemLocationKind.EquippedWand, command.OwnerId, 0, 0, 0, command.SlotIndex), command.ExpectedVersion); break;
                case InventoryPickupDestination.EquippedBackpack:
                    result = TryCollect(new ItemLocation(ItemLocationKind.EquippedBackpack, command.OwnerId, 0, 0, 0, 0), command.ExpectedVersion); break;
                case InventoryPickupDestination.WandSlot:
                    result = TryCollect(new ItemLocation(ItemLocationKind.WandSlot, command.OwnerId, 0, 0, 0, command.SlotIndex), command.ExpectedVersion); break;
                default:
                    result = _store.Result(false, InventoryFailureReason.InvalidState); break;
            }
            EventBus<InventoryCommandResultEvent>.Publish(new InventoryCommandResultEvent
            {
                RequestId = command.RequestId, ItemId = command.ItemId, InventoryId = InventoryId,
                Success = result.Success, Reason = result.Reason, Version = result.Version
            });
            if (result.Success && before != result.Version) EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = InventoryId, Version = result.Version });
        }
    }
}
