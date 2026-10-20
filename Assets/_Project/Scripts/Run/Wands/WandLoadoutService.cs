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
            public WandExecutionState Execution;
            public string ExecutionReservation;
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
        public void Synchronize(bool force = false)
        {
            ItemOwnershipStore store = _profile.Items;
            // Adopt会更换Store；两个独立Store可能恰好同Version，不能据此跳过新资产投影。
            if (!force && _inventoryVersion == store.Version) return;
            _inventoryVersion = store.Version; _carried.Clear();
            Array.Clear(_slots, 0, 3);
            foreach (Entry e in _entries.Values) { e.Carried = false; e.Slot = -1; }
            foreach (InventoryEntry item in store)
            {
                if (!store.Catalog.TryGetWandStats(item.Item, out WandStats stats)) continue;
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
                if (e.Program == null || !e.Program.Matches(next))
                {
                    e.Program = next; e.Revision++;
                    if (e.Execution != null && TryCompile(e, out var compiled))
                    {
                        e.Execution.Reprogram(compiled);
                        e.State.CommitExecution(e.State.CurrentMana, Math.Max(e.State.CooldownRemaining, e.Execution.ReloadRemaining));
                    }
                }
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
            { Entry entry = _carried[i]; if (entry.ExecutionReservation != null) continue;
                WandRuntimeState s = entry.State; float mana = s.CurrentMana, cd = s.CooldownRemaining; s.Tick(dt); entry.Execution?.Tick(dt);
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
                if (e.ExecutionReservation != null) result.Reason = OwnedWandFailureReason.ResourceBusy;
                else if (!WandRuntimeState.IsFinite(request.ManaCost) || request.ManaCost < 0) result.Reason = OwnedWandFailureReason.InvalidRequest;
                else if (request.IsRootCast && !e.Carried) result.Reason = OwnedWandFailureReason.NotCarried;
                else if (request.IsRootCast && request.ProgramRevision != e.Revision) result.Reason = OwnedWandFailureReason.StaleProgram;
                else if (request.IsRootCast && e.State.CooldownRemaining > 0) result.Reason = OwnedWandFailureReason.Cooldown;
                else if (request.ManaCost > e.State.CurrentMana) result.Reason = OwnedWandFailureReason.InsufficientMana;
                else { result.Success = e.State.TrySpend(request.ManaCost, request.IsRootCast); result.Reason = OwnedWandFailureReason.None; _version++; }
                result.CurrentMana = e.State.CurrentMana; result.CooldownRemaining = e.State.CooldownRemaining;
            }
            _receipts[request.CallerId] = new Receipt { Request = request, Result = result }; return result;
        }
        private bool TryCompile(Entry entry, out SpellProgram program)
        {
            var ids = new string[entry.Program.Count];
            for (int i = 0; i < ids.Length; i++) ids[i] = entry.Program.GetInstanceId(i);
            program=null;var fixedCards=new SpellDefinition[entry.Stats.AlwaysCastContentIds.Count];
            for(int i=0;i<fixedCards.Length;i++)
            {if(!_profile.Items.Catalog.TryGet(entry.Stats.AlwaysCastContentIds[i],out var definition)||definition.Kind!=ItemKind.Spell)return false;fixedCards[i]=definition.SpellDefinition;}
            // 编译入口统一传播实例固定属性；UI和世界运行时不另拼时间/散射规则。
            return SpellProgramCompiler.TryCompile(entry.Program, new SpellProgramCompileInputs(entry.Stats.BaseDraws, ids,
                entry.Revision, entry.Stats.BaseCastDelay, entry.Stats.BaseReloadTime, entry.Stats.BaseSpreadDegrees,
                entry.Stats.Shuffle, entry.Stats.ManaRegenPerSecond,fixedCards), out program, out _);
        }
        internal bool TryGetExecution(string id, out WandExecutionSnapshot snapshot)
        {
            Synchronize(); snapshot = default;
            if (id == null || !_profile.Items.TryGet(id, out _) || !_entries.TryGetValue(id, out var entry)) return false;
            if (entry.Execution == null)
            { if (!TryCompile(entry, out var program)) return false; entry.Execution = new WandExecutionState(program, id); }
            if (entry.Execution.Program.Revision != entry.Revision) return false;
            var execution = entry.Execution;
            snapshot = new WandExecutionSnapshot(execution.Program, execution.Draw, entry.State.CurrentMana,
                execution.CastRemaining, execution.ReloadRemaining, _version); return true;
        }
        internal bool ReserveExecution(string id, string ticket)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.ExecutionReservation != null) return false;
            entry.ExecutionReservation = ticket; return true;
        }
        internal bool OwnsExecutionReservation(string id, string ticket) => _entries.TryGetValue(id, out var entry) && entry.ExecutionReservation == ticket;
        internal void ReleaseExecution(string id, string ticket)
        { if (OwnsExecutionReservation(id, ticket)) _entries[id].ExecutionReservation = null; }
        internal void CommitExecution(string id, string ticket, SpellBuildBuffer candidate, in SpellBuildSummary summary)
        {
            if (!OwnsExecutionReservation(id, ticket)) throw new InvalidOperationException("根执行失去法杖预约。");
            Entry entry = _entries[id];
            var spells = new SpellDefinition[entry.Program.Count]; var ids = new string[spells.Length];
            foreach (InventoryEntry card in _profile.Items)
                if (card.Location.Kind == ItemLocationKind.WandSlot && card.Location.OwnerId == id)
                { int slot = card.Location.SlotIndex; _profile.Items.Catalog.TryGet(card.Item.ContentId, out var definition);
                    spells[slot] = definition.SpellDefinition; ids[slot] = card.Item.InstanceId; }
            var projection = new WandProgramProjection(spells, ids); SpellProgram program = candidate.Graph.Program;
            if (!entry.Program.Matches(projection))
            { entry.Program = projection; entry.Revision++; program = SpellProgramCompiler.RetainInstances(program, ids, entry.Revision); }
            entry.Execution.Commit(program, candidate.NextState, summary);
            entry.State.CommitExecution(summary.ManaAfter, Math.Max(entry.Execution.CastRemaining, entry.Execution.ReloadRemaining));
            entry.ExecutionReservation = null; _version++;
        }
        internal WandSessionContinuity CaptureContinuity()
        {
            Synchronize(); var result = new WandSessionContinuity { Selected = _selected };
            foreach (var pair in _entries)
            {
                if (!_profile.Items.TryGet(pair.Key, out _)) continue;
                Entry entry = pair.Value; var ids = new string[entry.Program.Count];
                for (int i = 0; i < ids.Length; i++) ids[i] = entry.Program.GetInstanceId(i);
                result.Entries.Add(new WandSessionContinuity.Entry { Id = pair.Key, Stats = entry.Stats, Mana = entry.State.CurrentMana,
                    Ready = entry.State.CooldownRemaining, Revision = entry.Revision, Program = entry.Execution?.Program,
                    Draw = entry.Execution?.Draw, Cast = entry.Execution?.CastRemaining ?? 0, Reload = entry.Execution?.ReloadRemaining ?? 0, InstalledIds = ids });
            }
            return result;
        }
        internal void RestoreContinuity(WandSessionContinuity source)
        {
            if (source == null) return; Synchronize();
            foreach (var snapshot in source.Entries)
            {
                if (!_entries.TryGetValue(snapshot.Id, out var entry) || !entry.Stats.SameAs(snapshot.Stats)
                    || entry.Program.Count != snapshot.InstalledIds.Length) continue;
                bool same = true;
                for (int i = 0; i < entry.Program.Count; i++) same &= entry.Program.GetInstanceId(i) == snapshot.InstalledIds[i];
                if (!same) continue; // 只有同实例、同固定属性和同安装序列才能接续，不能把历史覆盖到新程序。
                entry.Revision = snapshot.Revision; entry.State.CommitExecution(snapshot.Mana, snapshot.Ready);
                if (snapshot.Program != null && snapshot.Draw != null)
                    entry.Execution = new WandExecutionState(snapshot.Program, snapshot.Id)
                    { Draw = snapshot.Draw, CastRemaining = snapshot.Cast, ReloadRemaining = snapshot.Reload };
            }
            if (source.Selected != null && _entries.TryGetValue(source.Selected, out var selected) && selected.Slot >= 0) _selected = source.Selected;
            _version++;
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
