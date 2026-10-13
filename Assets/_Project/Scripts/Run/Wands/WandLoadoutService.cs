using System;
using System.Collections.Generic;
using Game.Core;
using Game.Skills;
using Unity.Profiling;

namespace Game.Run
{
    /// <summary>唯一资源写持有者；携带缓存的热循环与离散库存投影分开，选择只换 ID。</summary>
    public sealed class WandLoadoutService
    {
        private sealed class Entry
        {
            public WandRuntimeState State;
            public WandStats Stats;
            public WandProgramProjection Program;
            public uint Revision;
            public int Slot = -1;
            public bool Carried;
        }
        private readonly SinglePlayerProfile _profile;
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<string, Receipt> _receipts = new Dictionary<string, Receipt>(StringComparer.Ordinal);
        private readonly Entry[] _slots = new Entry[3];
        private readonly List<Entry> _carried = new List<Entry>(3);
        private uint _inventoryVersion = uint.MaxValue;
        private string _selected;
        private uint _version;
        private readonly string _inventoryId;
        private static readonly ProfilerMarker TickMarker = new ProfilerMarker("Norn.WandResources.Tick");
        public IWandLoadoutReadOnly ReadOnly { get; }
        private struct Receipt { public WandResourceRequestedEvent Request; public WandResourceResultEvent Result; }
        public WandLoadoutService(SinglePlayerProfile profile, string inventoryId)
        { _profile = profile ?? throw new ArgumentNullException(nameof(profile)); _inventoryId = inventoryId;
            ReadOnly = new View(this); Synchronize(); }
        public void Synchronize()
        {
            ItemOwnershipStore store = _profile.Items;
            if (_inventoryVersion == store.Version) return;
            _inventoryVersion = store.Version; _carried.Clear();
            Array.Clear(_slots, 0, 3);
            foreach (Entry e in _entries.Values) { e.Carried = false; e.Slot = -1; }
            foreach (InventoryEntry item in store)
            {
                if (!store.Catalog.TryGetWandStats(item.Item.ContentId, out WandStats stats)) continue;
                string id = item.Item.InstanceId;
                if (!_entries.TryGetValue(id, out Entry e))
                {
                    e = new Entry { Stats = stats, State = new WandRuntimeState(id, stats.MaxMana, stats.ManaRegenPerSecond, stats.CastCooldown) };
                    _entries.Add(id, e);
                }
                InventoryEntry root = item;
                while (root.Location.HasParentInstance)
                {
                    store.TryGet(root.Location.OwnerId, out RuntimeItemState parent);
                    store.TryGetLocation(parent.InstanceId, out ItemLocation location);
                    root = new InventoryEntry(parent, location);
                }
                e.Carried = root.Location.OwnerId == _profile.ActorId &&
                    (root.Location.Kind == ItemLocationKind.EquippedBackpack || root.Location.Kind == ItemLocationKind.EquippedWand);
                // 装备槽固定三根，包内备用杖不限此数。缓存只在离散库存变化时扩容，
                // 不能截断前三根，否则DTO排列顺序甚至会让已装备杖漏掉Mana/Cooldown恢复。
                if (e.Carried) _carried.Add(e);
                if (item.Location.Kind == ItemLocationKind.EquippedWand && item.Location.OwnerId == _profile.ActorId)
                { e.Slot = item.Location.SlotIndex; _slots[e.Slot] = e; }
                var spells = new SpellDefinition[stats.ProgramCapacity]; var ids = new string[stats.ProgramCapacity];
                foreach (InventoryEntry card in store)
                    if (card.Location.Kind == ItemLocationKind.WandSlot && card.Location.OwnerId == id)
                    { int slot = card.Location.SlotIndex; store.Catalog.TryGet(card.Item.ContentId, out ItemDefinition definition);
                        spells[slot] = definition.SpellDefinition; ids[slot] = card.Item.InstanceId; }
                var next = new WandProgramProjection(spells, ids);
                if (e.Program == null || !e.Program.Matches(next)) { e.Program = next; e.Revision++; }
            }
            if (_selected == null || !_entries.TryGetValue(_selected, out Entry selected) || selected.Slot < 0)
            { _selected = null; for (int i = 0; i < 3; i++) if (_slots[i] != null) { _selected = _slots[i].State.InstanceId; break; } }
            _version++;
        }
        public bool TrySelect(int slot)
        { Synchronize(); if (slot < 0 || slot >= 3 || _slots[slot] == null) return false;
            string id = _slots[slot].State.InstanceId; if (_selected != id) { _selected = id; _version++; } return true; }
        public void Tick(float dt)
        {
            if (!WandRuntimeState.IsFinite(dt) || dt <= 0) return;
            using var sample = TickMarker.Auto();
            // 库存版本变化由离散事件同步；热路径按索引遍历已建缓存，不扩容，仓库资源冻结。
            bool changed = false;
            for (int i = 0; i < _carried.Count; i++)
            { WandRuntimeState s = _carried[i].State; float mana = s.CurrentMana, cd = s.CooldownRemaining; s.Tick(dt);
                changed |= mana != s.CurrentMana || cd != s.CooldownRemaining; }
            if (changed) _version++;
        }
        public WandResourceResultEvent Execute(in WandResourceRequestedEvent request)
        {
            Synchronize();
            var result = new WandResourceResultEvent { InventoryId = _inventoryId, SourceWandId = request.SourceWandId,
                CallerId = request.CallerId, RequestId = request.RequestId, Reason = OwnedWandFailureReason.InvalidRequest };
            if (request.InventoryId != _inventoryId || string.IsNullOrWhiteSpace(request.CallerId) || request.RequestId == 0) return result;
            if (_receipts.TryGetValue(request.CallerId, out Receipt old) && request.RequestId <= old.Request.RequestId)
            {
                // 同号不同内容拒绝；完全相同的重试返回原收据，旧号不能重新产生支付。
                if (request.RequestId == old.Request.RequestId && request.SourceWandId == old.Request.SourceWandId
                    && request.ManaCost.Equals(old.Request.ManaCost) && request.IsRootCast == old.Request.IsRootCast
                    && request.ProgramRevision == old.Request.ProgramRevision) return old.Result;
                return result;
            }
            if (request.SourceWandId == null || !_entries.TryGetValue(request.SourceWandId, out Entry e)) result.Reason = OwnedWandFailureReason.UnknownSource;
            else
            {
                if (!WandRuntimeState.IsFinite(request.ManaCost) || request.ManaCost < 0) result.Reason = OwnedWandFailureReason.InvalidRequest;
                else if (request.IsRootCast && !e.Carried) result.Reason = OwnedWandFailureReason.NotCarried;
                else if (request.IsRootCast && request.ProgramRevision != e.Revision) result.Reason = OwnedWandFailureReason.StaleProgram;
                else if (request.IsRootCast && e.State.CooldownRemaining > 0) result.Reason = OwnedWandFailureReason.Cooldown;
                else if (request.ManaCost > e.State.CurrentMana) result.Reason = OwnedWandFailureReason.InsufficientMana;
                else { result.Success = e.State.TrySpend(request.ManaCost, request.IsRootCast); result.Reason = OwnedWandFailureReason.None; _version++; }
                result.CurrentMana = e.State.CurrentMana; result.CooldownRemaining = e.State.CooldownRemaining;
            }
            _receipts[request.CallerId] = new Receipt { Request = request, Result = result }; return result;
        }
        // 私有包装对象防止只读接口被向下转换成带写方法的服务。
        private sealed class View : IWandLoadoutReadOnly
        {
            private readonly WandLoadoutService _owner;
            public View(WandLoadoutService owner) { _owner = owner; }
            public string InventoryId => _owner._inventoryId;
            public string SelectedWandId => _owner._selected;
            public uint ResourceVersion => _owner._version;
            public bool TryGetSlot(int slot, out WandStateSnapshot state)
            { state = default; if (slot < 0 || slot >= 3 || _owner._slots[slot] == null) return false;
                return TryGetState(_owner._slots[slot].State.InstanceId, out state); }
            public bool TryGetState(string id, out WandStateSnapshot state)
            { state = default; if (id == null || !_owner._entries.TryGetValue(id, out Entry e)) return false;
                state = new WandStateSnapshot(e.State, e.Slot, id == SelectedWandId, e.Carried); return true; }
            public bool TryGetProgram(string id, out WandProgramSnapshot program)
            { program = default; if (id == null || !_owner._entries.TryGetValue(id, out Entry e)) return false;
                program = new WandProgramSnapshot(id, e.Revision, e.Stats.BaseDraws, e.Program); return true; }
        }
    }
}
