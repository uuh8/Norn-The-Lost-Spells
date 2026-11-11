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
        private IInventoryPresentationSource _onlineSource;
        private bool _onlineReadOnly;
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
            {_proxies.Remove(key);PublishVisualBinding(false);}
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
            PublishVisualBinding(true);return true;
        }

        private void PublishVisualBinding(bool added)
        {
            var fact = new WorldItemVisualBoundEvent
            { Root = transform, InventoryId = InventoryId, RegisteredCount = _proxies.Count, Added = added };
            if (!_onlineReadOnly) { EventBus<WorldItemVisualBoundEvent>.Publish(fact); return; }
            // 线上注册/移除发生在权威账本提交之后；表现订阅异常不能撤销资产或使原收据丢失。
            try { EventBus<WorldItemVisualBoundEvent>.Publish(fact); }
            catch (Exception error) { GameLog.Error("线上地面表现通知失败：" + error.Message, "OnlineInventory"); }
        }

        /// <summary>
        /// Client只绑定已完整安装的线上投影；模型位置和注册Store均来自当前Source，不能由UI授予资产。
        /// Source安装新快照会换Store，重绑前先释放旧key，避免同一实例留下两份附近物注册。
        /// </summary>
        public bool BindOnline(IInventoryPresentationSource source, string itemId, string worldOwner, Transform actor)
        {
            if (string.IsNullOrEmpty(itemId) || string.IsNullOrEmpty(worldOwner)) return false;
            if (!(source is OnlineInventoryPresentationSource online) || !(source is IOnlineInventoryCommandContext context)
                || context.ActorId == 0 || context.ConnectionEpoch == 0 || actor == null || gameObject.scene != actor.gameObject.scene
                || online.PresentationItems == null || !online.PresentationItems.TryGetLocation(itemId, out var location)
                || location.Kind != ItemLocationKind.WorldItem || !string.Equals(location.OwnerId, worldOwner, StringComparison.Ordinal)
                || !online.TryGetGroundPose(itemId, out var position, out var rotation) || _store != null && !_onlineReadOnly) return false;
            ReleaseIdentity(); _store = null;
            _onlineSource = source; _onlineReadOnly = true; _collector = actor; _reach = 3; _collected = false;
            transform.SetPositionAndRotation(position, rotation);
            if (TryBind(online.PresentationItems, itemId, worldOwner, source.ReadOnly.InventoryId)) return true;
            _onlineSource = null; _onlineReadOnly = false; return false;
        }

        /// <summary>私人账号Hub只绑定已确认的地面记录；不借副本Actor/Epoch，也不开放本地TryMove。</summary>
        public bool BindAccount(AccountInventoryPresentationSource source, string itemId, Transform actor)
        {
            if (source == null || actor == null || gameObject.scene != actor.gameObject.scene
                || !source.TryGetGroundItem(itemId, out var world) || source.PresentationItems == null
                || !source.PresentationItems.TryGetLocation(itemId, out var location) || location.Kind != ItemLocationKind.WorldItem
                || location.OwnerId != world.WorldOwnerId || _store != null && !_onlineReadOnly) return false;
            ReleaseIdentity(); _store = null;
            _onlineSource = source; _onlineReadOnly = true; _collector = actor; _reach = 3; _collected = false;
            var pose = world.Pose;
            transform.SetPositionAndRotation(new Vector3(pose.PositionX, pose.PositionY, pose.PositionZ),
                new Quaternion(pose.RotationX, pose.RotationY, pose.RotationZ, pose.RotationW));
            if (TryBind(source.PresentationItems, itemId, world.WorldOwnerId, source.InventoryId)) return true;
            _onlineSource = null; _onlineReadOnly = false; return false;
        }

        // DS的共享模型也只是代理；即使收到旧单机EventBus命令，也不能越过Workspace事务直接TryMove。
        internal bool TryBindOnlineAuthority(ItemOwnershipStore store, string itemId, string worldOwner)
        {
            if (_store != null) return false;
            _onlineReadOnly = true;
            if (TryBind(store, itemId, worldOwner)) return true;
            _onlineReadOnly = false; return false;
        }

        internal void ReleaseOnlineBinding()
        {
            if (!_onlineReadOnly) return;
            ReleaseIdentity(); _store = null; _onlineSource = null; _collected = true;
        }

        /// <summary>附近F只提交当前Source的拾取意图；成功发送不等于资产成功，不提前隐藏模型或改变组分。</summary>
        public bool RequestOnlinePickup()
        {
            if (!_onlineReadOnly || !isActiveAndEnabled || _collector == null
                || !(_onlineSource is OnlineInventoryPresentationSource source)
                || !(_onlineSource is IOnlineInventoryCommandContext context) || context.ActorId == 0 || context.ConnectionEpoch == 0
                || gameObject.scene != _collector.gameObject.scene || !source.ReadOnly.TryGetView(ItemId, out var item)
                || item.Location.Kind != ItemLocationKind.WorldItem || !string.Equals(item.Location.OwnerId, WorldOwner, StringComparison.Ordinal)
                || !source.TryGetGroundPose(ItemId, out var position, out _) || (position - _collector.position).sqrMagnitude > 9) return false;
            EventBus<OnlineInventoryCommandRequestedEvent>.Publish(new OnlineInventoryCommandRequestedEvent
            {
                ActorId = context.ActorId, CallerId = GetInstanceID(), Intent = new OnlineInventoryCommandIntent
                {
                    CommandId = OnlineInventoryCommandIds.Next(), ConnectionEpoch = context.ConnectionEpoch,
                    // 容器/固体保留已验收的数量通道；普通Item/卡/法杖使用通用意图，完整实例仍由DS唯一转交。
                    ExpectedInventoryVersion = source.ReadOnly.Version, Kind = item.Kind == ItemKind.Container || item.Kind == ItemKind.Material
                        ? OnlineInventoryCommandKind.PickupContainer : OnlineInventoryCommandKind.PickupItem,
                    ItemId = ItemId, WorldOwnerId = item.Location.OwnerId, AutoPlace = true
                }
            });
            return true;
        }

        public InventoryOperationResult TryCollect(string backpackId, in GridPlacement placement, uint expectedVersion)
            => TryCollect(placement.InBackpack(backpackId), expectedVersion);

        public InventoryOperationResult TryCollect(in ItemLocation destination, uint expectedVersion)
        {
            if (_onlineReadOnly) return new InventoryOperationResult(false, InventoryFailureReason.InvalidOwnership,
                _onlineSource != null ? _onlineSource.ReadOnly.Version : _store != null ? _store.Version : 0);
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
                // 部分材料已补入旧堆时，地面余量仍属于原实例，不能提前销毁代理。
                if(_store.TryGetLocation(ItemId,out var remaining)&&remaining.Kind==ItemLocationKind.WorldItem)return result;
                // EventBus 是同步派发；先标记和停用，阻止重复拾取，OnDisable 同时解除订阅。
                _collected = true; ReleaseIdentity(); gameObject.SetActive(false);
                if (Application.isPlaying) Destroy(gameObject);
            }
            return result;
        }

        private void OnCommand(InventoryCommandEvent command)
        {
            if (_onlineReadOnly) return;
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
