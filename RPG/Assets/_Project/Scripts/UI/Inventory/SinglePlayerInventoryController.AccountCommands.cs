using System;
using Game.Core;
using Game.Run;

namespace Game.UI
{
    /// <summary>
    /// 原库存UI的账号命令适配：只提交移动意图，不执行本地事务，也不伪造副本Actor/Epoch。
    /// HTTP结果未知时保留原命令；同一物品再次落到相同目标或原Save入口只能重发这份固定意图。
    /// </summary>
    public sealed partial class SinglePlayerInventoryController
    {
        private string _dragAccountPlayer;
        private int _dragAccountScene;
        private bool _accountCommandPending, _accountCommandUnknown;
        private uint _accountCommittedVersion;
        private OnlineAccountInventoryCommandIntent _accountCommandIntent;
        private bool _accountNearbyBindingPending;

        private void BindAccountNearby()
        {
            if (!(PresentationSource is IAccountInventoryCommandContext account)) { _accountNearbyBindingPending = false; return; }
            if (_session == null || !_session.TryGetAccountGroundSource(out var source, out var nearby)
                || source.PlayerId != account.PlayerId || source.Version != account.ProfileVersion) return;
            _accountNearbyBindingPending = false;
            if (_nearby != nearby) { _nearby = nearby; if (_open) _pendingBackpackRefresh = true; }
        }
        private bool IsAccountGroundPickup(in InventoryItemSnapshot item, out OnlineAccountWorldItemSnapshot world)
        {
            world = null;
            return item.Location.Kind == ItemLocationKind.WorldItem && !item.EntryBoundBackpack
                && PresentationSource is IAccountInventoryCommandContext account && _session != null
                && _session.TryGetAccountGroundSource(out var ground, out _)
                && ground.PlayerId == account.PlayerId && ground.Version == account.ProfileVersion
                && ground.TryGetGroundItem(item.InstanceId, out world) && world.WorldOwnerId == item.Location.OwnerId
                && ground.IsGroundItemNearby(item.InstanceId, _session.ActorTransform, 3f);
        }

        private bool TryGetAccountDrag(string id, out InventoryItemSnapshot item)
        {
            item = default;
            var source = PresentationSource; var context = source as IAccountInventoryCommandContext;
            if (context == null || context.ProfileVersion == 0 || string.IsNullOrEmpty(context.PlayerId)
                || _read == null || !ReferenceEquals(_read, source.ReadOnly) || _read.Version != context.ProfileVersion
                || !_read.TryGetView(id, out item)
                || !IsOwnedAccountItem(_read, item, context.PlayerId) && !IsAccountGroundPickup(item, out _)) return false;
            if (IsAccountWarehouseItem(_read, item) && (!_warehouseMode || !CanAccessAccountWarehouse(source))) return false;
            if (!_accountCommandPending) return true;
            // Unknown只允许复述原物品/原目标；其他物品和Pending阶段不能覆盖未确认请求。
            return _accountCommandUnknown && CurrentAccountMatchesPending() && id == _accountCommandIntent.ItemId;
        }
        private static bool IsOwnedAccountItem(IInventoryReadOnly read, in InventoryItemSnapshot item, string player)
        {
            if (read == null || read.ActorId != player) return false;
            var root = item.Location;
            for (int depth = 0; root.HasParentInstance; depth++)
            {
                if (depth >= read.Count || !read.TryGetView(root.OwnerId, out var parent)) return false;
                root = parent.Location;
            }
            return root.Kind == ItemLocationKind.Warehouse || root.OwnerId == player
                && (root.Kind == ItemLocationKind.EquippedWand || root.Kind == ItemLocationKind.EquippedBackpack
                    || root.Kind == ItemLocationKind.EquippedTool || root.Kind == ItemLocationKind.SpellStorage);
        }
        private static bool IsAccountWarehouseItem(IInventoryReadOnly read, in InventoryItemSnapshot item)
        {
            var root = item.Location;
            for (int depth = 0; root.HasParentInstance; depth++)
            {
                if (read == null || depth >= read.Count || !read.TryGetView(root.OwnerId, out var parent)) return false;
                root = parent.Location;
            }
            return root.Kind == ItemLocationKind.Warehouse;
        }
        private bool CanAccessAccountWarehouse(IInventoryPresentationSource source)
            // Source包含完整仓库只是读模型能力；真实取放仍沿原Session的Hub阶段、区域与站点门。
            // 账号Hub不能借旧本地Profile补权限，也不能把Tab随身界面变成远程仓库。
            => source is IAccountInventoryCommandContext && source.ReadOnly != null && source.CanAccessWarehouse
                && _session != null && _session.CanAccessAccountWarehouse;
        private bool SharesAccountWorkspace(SinglePlayerInventoryController other)
            => other != null && other.gameObject.scene.handle == gameObject.scene.handle
                && PresentationSource is IAccountInventoryCommandContext current
                && other.PresentationSource is IAccountInventoryCommandContext next
                && current.PlayerId == next.PlayerId && current.ProfileVersion == next.ProfileVersion;
        private bool IsCapturedAccountDragCurrent(out IInventoryPresentationSource source, out IAccountInventoryCommandContext context)
        {
            source = PresentationSource; context = source as IAccountInventoryCommandContext;
            return _open && _dragging && context != null && ReferenceEquals(source, _dragOnlineSource)
                && context.PlayerId == _dragAccountPlayer && _dragAccountScene == gameObject.scene.handle
                && _dragVersion != 0 && source.ReadOnly != null && ReferenceEquals(source.ReadOnly, _read);
        }
        private void SubmitAccountPlace(in InventoryCommandEvent command)
        {
            if (!IsCapturedAccountDragCurrent(out var source, out var context)
                || command.ItemId != _dragSource.InstanceId || !_read.TryGetView(command.ItemId, out var item)
                || !IsOwnedAccountItem(_read, item, context.PlayerId) && !IsAccountGroundPickup(item, out _))
            { RejectAccountDrag(); return; }
            bool pickup = item.Location.Kind == ItemLocationKind.WorldItem;
            OnlineAccountWorldItemSnapshot ground = null;
            if (pickup && !IsAccountGroundPickup(item, out ground)) { RejectAccountDrag(); return; }
            ItemLocationKind target;
            if (command.Kind == InventoryCommandKind.EquipWand) target = ItemLocationKind.EquippedWand;
            else if (command.Kind == InventoryCommandKind.ReplaceBackpack) target = ItemLocationKind.EquippedBackpack;
            else if (command.Kind == InventoryCommandKind.Pickup)
                target = command.PickupDestination == InventoryPickupDestination.EquippedWand ? ItemLocationKind.EquippedWand
                    : command.PickupDestination == InventoryPickupDestination.EquippedBackpack ? ItemLocationKind.EquippedBackpack
                    : command.PickupDestination == InventoryPickupDestination.SpellStorage ? ItemLocationKind.SpellStorage
                    : command.PickupDestination == InventoryPickupDestination.WandSlot ? ItemLocationKind.WandSlot : ItemLocationKind.Backpack;
            else if (command.Kind == InventoryCommandKind.Place)
                target = command.Destination == InventoryDestination.Warehouse ? ItemLocationKind.Warehouse
                    : command.Destination == InventoryDestination.WandSlot ? ItemLocationKind.WandSlot
                    : command.Destination == InventoryDestination.SpellStorage ? ItemLocationKind.SpellStorage : ItemLocationKind.Backpack;
            else { RejectAccountDrag(); return; }
            string parent = ""; int slot = command.SlotIndex, x = 0, y = 0, turns = 0;
            bool whole = command.WholeStack;
            bool valid;
            if (target == ItemLocationKind.Warehouse)
            { valid = _warehouseMode && CanAccessAccountWarehouse(source); slot = -1; }
            else if (target == ItemLocationKind.Backpack)
            {
                parent = command.OwnerId; slot = -1; x = command.X; y = command.Y; turns = command.QuarterTurns;
                valid = item.Kind != ItemKind.Backpack && !string.IsNullOrEmpty(parent) && _read.TryGetView(parent, out var bag)
                    && bag.Kind == ItemKind.Backpack && IsOwnedAccountItem(_read, bag, context.PlayerId)
                    && x >= 0 && y >= 0 && turns >= 0 && turns <= 3;
            }
            else if (target == ItemLocationKind.WandSlot)
            {
                parent = command.OwnerId; whole = false;
                valid = item.Kind == ItemKind.Spell && !string.IsNullOrEmpty(parent) && source.WandsReadOnly != null
                    && source.WandsReadOnly.TryGetProgram(parent, out var program) && slot >= 0 && slot < program.Program.Count
                    && _read.TryGetView(parent, out var wand) && wand.Kind == ItemKind.Wand && IsOwnedAccountItem(_read, wand, context.PlayerId);
            }
            else if (target == ItemLocationKind.SpellStorage)
            { whole = false; valid = item.Kind == ItemKind.Spell && slot >= 0 && slot < source.SpellStorageCapacity; }
            else if (target == ItemLocationKind.EquippedWand)
            { whole = false; valid = item.Kind == ItemKind.Wand && slot >= 0 && slot < HeldItemSlots.WandCount; }
            else
            { whole = false; slot = 0; valid = item.Kind == ItemKind.Backpack; }
            // 按下时在仓库内并不足够：拖拽期间可以走出区域，源或目标父容器属于仓库时都在松手复核。
            bool touchesWarehouse = target == ItemLocationKind.Warehouse || IsAccountWarehouseItem(_read, item)
                || !string.IsNullOrEmpty(parent) && _read.TryGetView(parent, out var parentItem) && IsAccountWarehouseItem(_read, parentItem);
            if (touchesWarehouse && (!_warehouseMode || !CanAccessAccountWarehouse(source))) valid = false;
            if (!valid || pickup && target == ItemLocationKind.Warehouse) { RejectAccountDrag(); return; }
            PublishAccountMove(new OnlineAccountInventoryCommandIntent { Kind = pickup ? OnlineAccountInventoryCommandKind.Pickup : OnlineAccountInventoryCommandKind.Move,
                PlayerId = _dragAccountPlayer, SceneHandle = _dragAccountScene, ExpectedVersion = _dragVersion, ItemId = item.InstanceId,
                TargetLocationKind = (int)target, TargetParentInstanceId = parent, TargetSlot = slot,
                GridX = x, GridY = y, QuarterTurns = turns, WholeStack = pickup ? false : whole,
                WorldOwnerId = ground?.WorldOwnerId ?? "" });
        }
        private void SubmitAccountToolBinding(int slot)
        {
            if (!IsCapturedAccountDragCurrent(out var source, out var context) || slot < 0 || slot >= HeldItemSlots.ToolCount
                || !_read.TryGetView(_dragSource.InstanceId, out var item) || item.EntryBoundBackpack
                || item.Rules.UseKind == ItemUseKind.None
                || !IsOwnedAccountItem(_read, item, context.PlayerId) && !IsAccountGroundPickup(item, out _)
                || IsAccountWarehouseItem(_read, item) && (!_warehouseMode || !CanAccessAccountWarehouse(source)))
            { RejectAccountDrag(); return; }
            bool pickup = item.Location.Kind == ItemLocationKind.WorldItem;
            if (pickup && !IsAccountGroundPickup(item, out _)) { RejectAccountDrag(); return; }
            PublishAccountMove(new OnlineAccountInventoryCommandIntent { Kind = pickup ? OnlineAccountInventoryCommandKind.Pickup : OnlineAccountInventoryCommandKind.Move,
                PlayerId = _dragAccountPlayer, SceneHandle = _dragAccountScene, ExpectedVersion = _dragVersion,
                ItemId = item.InstanceId, TargetLocationKind = (int)ItemLocationKind.EquippedTool, TargetParentInstanceId = "", TargetSlot = slot,
                WorldOwnerId = pickup ? item.Location.OwnerId : "" });
        }
        private void SubmitAccountDrop()
        {
            if (!IsCapturedAccountDragCurrent(out var source, out var account)
                || !_read.TryGetView(_dragSource.InstanceId, out var item) || item.EntryBoundBackpack
                || !IsOwnedAccountItem(_read, item, account.PlayerId)
                || IsAccountWarehouseItem(_read, item) && (!_warehouseMode || !CanAccessAccountWarehouse(source)))
            { RejectAccountDrag(); return; }
            if (_accountCommandPending && _accountCommandUnknown && CurrentAccountMatchesPending()
                && _accountCommandIntent.Kind == OnlineAccountInventoryCommandKind.Drop && _accountCommandIntent.ItemId == item.InstanceId)
            { CancelDrag(); RetryAccountCommand(); return; }
            OnlineAccountWorldItemPose pose = null; string error = null;
            if (_session == null || !_session.TryPrepareAccountGroundDrop(item.InstanceId, out pose, out error))
            { CancelDrag(); SetStatus(error ?? "没有找到稳定地面，物品仍保留原位。"); return; }
            PublishAccountMove(new OnlineAccountInventoryCommandIntent { Kind = OnlineAccountInventoryCommandKind.Drop,
                PlayerId = _dragAccountPlayer, SceneHandle = _dragAccountScene, ExpectedVersion = _dragVersion,
                ItemId = item.InstanceId, TargetLocationKind = (int)ItemLocationKind.WorldItem, TargetParentInstanceId = "", TargetSlot = -1,
                WholeStack = _wholeStack, WorldOwnerId = "", WorldPose = pose.CopyValue() });
        }
        private void SubmitAccountOrganizeBackpack()
        {
            var source = PresentationSource;
            if (!_open || _dragging || _draft.Active || !(source is IAccountInventoryCommandContext account)
                || _read == null || !ReferenceEquals(source.ReadOnly, _read) || account.ProfileVersion != _read.Version
                || string.IsNullOrEmpty(source.EquippedBackpackId)
                || !_read.TryGetView(source.EquippedBackpackId, out var bag) || bag.Kind != ItemKind.Backpack
                || bag.Location.Kind != ItemLocationKind.EquippedBackpack || bag.Location.OwnerId != account.PlayerId)
            { SetStatus("当前没有可整理的已确认随身背包。"); return; }
            // 不在UI计算新布局或合并GMU。后台复用原整理事务，完整Profile安装前保留全部原实例与数量。
            PublishAccountMove(new OnlineAccountInventoryCommandIntent
            { Kind = OnlineAccountInventoryCommandKind.OrganizeBackpack, PlayerId = account.PlayerId,
                SceneHandle = gameObject.scene.handle, ExpectedVersion = account.ProfileVersion, ItemId = bag.InstanceId,
                TargetLocationKind = (int)ItemLocationKind.EquippedBackpack, TargetParentInstanceId = "", TargetSlot = 0 });
        }
        private void PublishAccountMove(OnlineAccountInventoryCommandIntent intent)
        {
            if (_accountCommandPending)
            {
                bool same = _accountCommandUnknown && CurrentAccountMatchesPending() && SameAccountMove(intent, _accountCommandIntent);
                CancelDrag(); CancelArrangement();
                if (same) RetryAccountCommand(); else SetStatus("上次操作尚未确认，请恢复原物品的相同目标或刷新重试。");
                return;
            }
            intent.CommandId = Guid.NewGuid().ToString("N");
            intent.WorldPose = intent.WorldPose?.CopyValue();
            _accountCommandIntent = intent; _accountCommandPending = true; _accountCommandUnknown = false; _accountCommittedVersion = 0;
            CancelDrag(); CancelArrangement(); SetStatus("已提交账号服务器，等待完整资产确认。");
            PublishFixedAccountIntent();
        }
        private static bool SameAccountMove(in OnlineAccountInventoryCommandIntent a, in OnlineAccountInventoryCommandIntent b)
            // 这里仅识别用户再次指向原实例/原目标。Profile可能已前移，当前位置派生的整堆标志也可能不同；
            // 重试始终发送b的原版本、原WholeStack和原ID，不把这次Ghost当作新的拆分/移动命令。
            => a.PlayerId == b.PlayerId && a.SceneHandle == b.SceneHandle && a.Kind == b.Kind && a.ItemId == b.ItemId
                && a.TargetLocationKind == b.TargetLocationKind && a.TargetParentInstanceId == b.TargetParentInstanceId
                && a.TargetSlot == b.TargetSlot && a.GridX == b.GridX && a.GridY == b.GridY
                && a.QuarterTurns == b.QuarterTurns && (a.WorldOwnerId ?? "") == (b.WorldOwnerId ?? "")
                && OnlineAccountWorldItemPose.Same(a.WorldPose, b.WorldPose);
        private bool CurrentAccountMatchesPending()
            => PresentationSource is IAccountInventoryCommandContext account && account.PlayerId == _accountCommandIntent.PlayerId
                && gameObject.scene.handle == _accountCommandIntent.SceneHandle && PresentationSource.ReadOnly != null;
        private void RetryAccountCommand()
        {
            if (!_accountCommandPending)
            {
                if (_nearby != null && _nearby.TryRetryPendingAccountPickup()) { SetStatus("正在确认上次拾取，仍使用原请求编号。"); return; }
                SetStatus("账号移动由服务器实时保存，无需写本地存档。"); return;
            }
            if (!CurrentAccountMatchesPending()) { SetStatus("请恢复原账号及据点，再确认上次账号操作。"); return; }
            _accountCommandUnknown = false;
            SetStatus("正在恢复上次账号操作；仍使用原请求编号与正文。");
            PublishFixedAccountIntent();
        }
        private void PublishFixedAccountIntent()
        {
            // 意图是值类型，姿态却是DTO引用；发布副本，避免其他观察者修改我们保留的未知请求正文。
            var intent=_accountCommandIntent;intent.WorldPose=intent.WorldPose?.CopyValue();
            EventBus<OnlineAccountInventoryCommandRequestedEvent>.Publish(new OnlineAccountInventoryCommandRequestedEvent
            {CallerId=GetInstanceID(),Intent=intent});
        }
        private void OnAccountInventoryResult(OnlineAccountInventoryCommandResultEvent result)
        {
            if (!_accountCommandPending || !CurrentAccountMatchesPending() || result.CallerId != GetInstanceID()
                || result.PlayerId != _accountCommandIntent.PlayerId || result.SceneHandle != _accountCommandIntent.SceneHandle
                || result.CommandId != _accountCommandIntent.CommandId) return;
            if (result.State == OnlineAccountInventoryCommandState.Committed)
            {
                // 业务原no-op不增版；真正HTTP/位置核对由账号组合层完成，本层仍要求完整Source达到该水位。
                if (result.CommittedVersion == 0 || result.CommittedVersion < _accountCommandIntent.ExpectedVersion) return;
                _accountCommittedVersion = result.CommittedVersion; CompleteAccountCommandStatusIfInstalled(); return;
            }
            if (_accountCommittedVersion != 0) return;
            if (result.State == OnlineAccountInventoryCommandState.Rejected)
            {
                _accountCommandPending = _accountCommandUnknown = false; _accountCommandIntent = default;
                _placingDropped=false;
                SetStatus(string.IsNullOrEmpty(result.Message) ? OnlineFailureText(result.Failure) : result.Message); return;
            }
            _accountCommandUnknown = result.State == OnlineAccountInventoryCommandState.Unknown;
            SetStatus(_accountCommandUnknown ? "上次操作结果未知；可将同一物品拖到原目标重试，或使用保存/刷新确认。"
                : "账号服务器正在确认原操作，请稍候。");
        }
        private void CompleteAccountCommandStatusIfInstalled()
        {
            if (!_accountCommandPending || _accountCommittedVersion == 0 || !CurrentAccountMatchesPending()
                || !(PresentationSource is IAccountInventoryCommandContext account) || account.ProfileVersion < _accountCommittedVersion
                || PresentationSource.ReadOnly.Version < _accountCommittedVersion) return;
            if (_accountCommandIntent.Kind == OnlineAccountInventoryCommandKind.Drop)
            {
                string id=_accountCommandIntent.ItemId;
                _nearby?.RefreshCandidates();Refresh();
                if (_read.TryGetView(id,out var dropped) && dropped.Location.Kind==ItemLocationKind.WorldItem)
                {
                    if(_placingDropped)_nearbyGrid?.MoveItem(id,dropped.Rules.Shape,_droppedPlacement);
                    else _nearbyGrid?.RevealItem(id);
                }
                _placingDropped=false;
            }
            _accountCommandPending = _accountCommandUnknown = false; _accountCommittedVersion = 0; _accountCommandIntent = default;
            SetStatus("账号库存已确认更新。");
        }
        private void RejectAccountDrag()
        { CancelDrag(); CancelArrangement(); _placingDropped=false; SetStatus("当前账号物品或目标无效，资产仍保留原位。"); }
    }
}
