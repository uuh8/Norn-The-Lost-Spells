using System;
using Game.Core;

namespace Game.Run
{
    /// <summary>七个独立装备位置与十选一手持状态；装备/替换必须先通过完整归属事务。</summary>
    public sealed class ToolbeltService : IToolbeltReadOnly
    {
        private readonly SinglePlayerProfile _profile;
        public string InventoryId { get; }
        public uint ConfigurationVersion => _profile.ToolConfigurationVersion;
        public uint SelectionVersion { get; private set; }
        public int SelectedSlot { get; private set; } = -1;
        public string SelectedItemId { get; private set; }
        public ToolbeltFailure LastFailure { get; private set; }
        public ToolbeltService(SinglePlayerProfile profile, string inventoryId)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            InventoryId = inventoryId; NormalizeProfile(profile); SelectFallback(false);
        }
        internal static bool IsUsable(SinglePlayerProfile profile, string id)
        {
            var store = profile.Items;
            return id != null && store.TryGet(id, out var item) && store.TryGetLocation(id, out var location)
                && store.Catalog.TryGetRules(item.ContentId, out var rules) && rules.UseKind != ItemUseKind.None
                && (location.Kind == ItemLocationKind.EquippedTool && location.OwnerId == profile.ActorId
                    || location.Kind == ItemLocationKind.Backpack && store.TryGetLocation(location.OwnerId, out var bag)
                    && bag.Kind == ItemLocationKind.EquippedBackpack && bag.OwnerId == profile.ActorId);
        }
        internal static void NormalizeProfile(SinglePlayerProfile profile)
        {
            string[] input = profile.ToolBindings;
            // 迁移损坏的快捷引用只清配置，真实物品仍由Mapper原有完整验证负责。
            var clean = new string[HeldItemSlots.ToolCount];
            for (int i = 0; input != null && i < input.Length && i < HeldItemSlots.ToolCount; i++)
            {
                string id = input[i]; if (!IsUsable(profile, id)) continue;
                bool duplicate = false; for (int j = 0; j < i; j++) if (clean[j] == id) duplicate = true;
                if (!duplicate)
                {
                    // 旧档只存引用；释放原占格后装备，不创建新瓶子或复制组分。
                    if (profile.Items.TryMove(id, ToolLocation(profile, i)).Success) clean[i] = id;
                }
            }
            foreach (var entry in profile.Items)
                if (entry.Location.Kind == ItemLocationKind.EquippedTool && entry.Location.OwnerId == profile.ActorId)
                    clean[entry.Location.SlotIndex] = entry.Item.InstanceId;
            profile.ToolBindings = clean;
        }
        public bool TryGetBinding(int toolSlot, out string instanceId)
        {
            instanceId = toolSlot >= 0 && toolSlot < HeldItemSlots.ToolCount ? _profile.ToolBindings[toolSlot] : null;
            return instanceId != null && _profile.Items.TryGetLocation(instanceId, out var location)
                && location == ToolLocation(_profile, toolSlot);
        }
        public bool TryBind(int toolSlot, string instanceId, uint expectedVersion)
        {
            LastFailure = ToolbeltFailure.None;
            if (expectedVersion != ConfigurationVersion) { LastFailure = ToolbeltFailure.StaleVersion; return false; }
            if (toolSlot < 0 || toolSlot >= HeldItemSlots.ToolCount) { LastFailure = ToolbeltFailure.InvalidSlot; return false; }
            if (_profile.RaidActionsFrozen || _profile.Items.IsResourceBusy) { LastFailure = ToolbeltFailure.Busy; return false; }
            if (string.IsNullOrEmpty(instanceId)) instanceId = null;
            if (instanceId != null && !IsUsable(_profile, instanceId))
            { LastFailure = ToolbeltFailure.NotCarried; return false; }
            string[] bindings = _profile.ToolBindings;
            if (bindings[toolSlot] == instanceId) return true;
            int previous = -1;
            for (int i = 0; instanceId != null && i < HeldItemSlots.ToolCount; i++) if (bindings[i] == instanceId) previous = i;
            var store = _profile.Items;
            var changes = new System.Collections.Generic.List<InventoryEntry>(2);
            string replaced = bindings[toolSlot];
            if (instanceId != null && store.TryGet(instanceId, out var incoming))
                changes.Add(new InventoryEntry(incoming, ToolLocation(_profile, toolSlot)));
            bool committed;
            if (replaced != null && store.TryGet(replaced, out var outgoing))
            {
                if (previous >= 0)
                {
                    changes.Add(new InventoryEntry(outgoing, ToolLocation(_profile, previous)));
                    committed = store.TryApply(changes, store.Version).Success;
                }
                else committed = TryReturnToBackpack(outgoing, changes);
            }
            else committed = changes.Count == 0 || store.TryApply(changes, store.Version).Success;
            if (!committed) { LastFailure = ToolbeltFailure.BackpackFull; return false; }
            if (previous >= 0) bindings[previous] = replaced;
            bindings[toolSlot] = instanceId; _profile.ToolConfigurationVersion++;
            Synchronize(); Publish(); return true;
        }
        private static ItemLocation ToolLocation(SinglePlayerProfile profile, int slot)
            => new ItemLocation(ItemLocationKind.EquippedTool, profile.ActorId, 0, 0, 0, slot);
        private bool TryReturnToBackpack(RuntimeItemState item, System.Collections.Generic.List<InventoryEntry> changes)
        {
            var store = _profile.Items;
            foreach (var entry in store)
            {
                if (entry.Location.Kind != ItemLocationKind.EquippedBackpack || entry.Location.OwnerId != _profile.ActorId) continue;
                store.Catalog.TryGetRules(entry.Item.ContentId, out var bag);
                int index = changes.Count; changes.Add(default);
                // 同一次候选验证包含入装备与旧道具回包；满包失败绝不先卸下或丢失物品。
                for (int turn = 0; turn < 4; turn++)
                    for (int y = 0; y < bag.BackpackHeight; y++)
                        for (int x = 0; x < bag.BackpackWidth; x++)
                        {
                            changes[index] = new InventoryEntry(item, new ItemLocation(ItemLocationKind.Backpack, entry.Item.InstanceId, x, y, turn, -1));
                            if (store.TryApply(changes, store.Version).Success) return true;
                        }
                return false;
            }
            return false;
        }
        public bool TrySelect(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= HeldItemSlots.TotalCount || _profile.RaidActionsFrozen) return false;
            string id = Resolve(slotIndex); if (id == null) return false;
            SetSelection(slotIndex, id, true); return true;
        }
        public void Synchronize()
        {
            bool changed = false;
            for (int i = 0; i < HeldItemSlots.ToolCount; i++)
                if (_profile.ToolBindings[i] != null && !TryGetBinding(i, out _))
                { _profile.ToolBindings[i] = null; changed = true; }
            if (changed) _profile.ToolConfigurationVersion++;
            string id = Resolve(SelectedSlot);
            if (id == null) SelectFallback(true); else SetSelection(SelectedSlot, id, true);
            if (changed) Publish();
        }
        private string Resolve(int slot)
        {
            if (slot >= HeldItemSlots.WandCount && slot < HeldItemSlots.TotalCount) return TryGetBinding(slot - HeldItemSlots.WandCount, out var id) ? id : null;
            if (slot < 0 || slot >= 3) return null;
            foreach (InventoryEntry entry in _profile.Items)
                if (entry.Location.Kind == ItemLocationKind.EquippedWand && entry.Location.OwnerId == _profile.ActorId
                    && entry.Location.SlotIndex == slot) return entry.Item.InstanceId;
            return null;
        }
        private void SelectFallback(bool publish)
        {
            for (int i = 0; i < 3; i++) { string id = Resolve(i); if (id != null) { SetSelection(i, id, publish); return; } }
            SetSelection(-1, null, publish);
        }
        private void SetSelection(int slot, string id, bool publish)
        {
            if (SelectedSlot == slot && SelectedItemId == id) return;
            SelectedSlot = slot; SelectedItemId = id; SelectionVersion++;
            if (publish) Publish();
        }
        private void Publish() => EventBus<ToolbeltChangedEvent>.Publish(new ToolbeltChangedEvent
        { InventoryId = InventoryId, ItemId = SelectedItemId, SelectedSlot = SelectedSlot,
            ConfigurationVersion = ConfigurationVersion, SelectionVersion = SelectionVersion });
    }
}
