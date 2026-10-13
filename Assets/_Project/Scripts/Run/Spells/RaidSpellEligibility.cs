using System;
using System.Collections.Generic;
using Game.Core;
using Game.Skills;

namespace Game.Run
{
    /// <summary>本局资格按实例记录；一次候选提交同时完成安装和扣资格，最后 1→0 仍可用。</summary>
    public sealed class RaidSpellEligibility : IInventoryCommitPolicy
    {
        private readonly SinglePlayerProfile _profile;
        private readonly HashSet<string> _eligible = new HashSet<string>(StringComparer.Ordinal);
        public string RaidId { get; private set; }
        public uint Version { get; private set; }
        public ISpellUseReadOnly ReadOnly { get; }
        public RaidSpellEligibility(SinglePlayerProfile profile)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            if (profile.Items.CommitPolicy != null) throw new InvalidOperationException("库存已有资格策略。");
            profile.Items.CommitPolicy = this; ReadOnly = new View(this);
        }
        public bool TryBegin(string raidId, out InventoryFailureReason reason)
        {
            reason = InventoryFailureReason.InvalidState;
            if (string.IsNullOrWhiteSpace(raidId) || _profile.Items.IsResourceBusy) return false;
            if (RaidId != null) { reason = RaidId == raidId ? InventoryFailureReason.None : reason; return RaidId == raidId; }
            RaidId = raidId; InventoryOperationResult result = _profile.Items.ReconcilePolicy();
            if (!result.Success) RaidId = null; else Version++;
            reason = result.Reason; return result.Success;
        }
        public bool TryEnd(out InventoryFailureReason reason)
        {
            reason = InventoryFailureReason.ResourceBusy; if (_profile.Items.IsResourceBusy) return false;
            RaidId = null; _eligible.Clear(); Version++; reason = InventoryFailureReason.None; return true;
        }
        public bool IsEligible(string id) => RaidId != null && id != null && _eligible.Contains(id);
        public bool TryResumePractice(string raidId,IReadOnlyList<string> qualified)
        {
            if(RaidId!=null || string.IsNullOrWhiteSpace(raidId) || _profile.Items.IsResourceBusy)return false;
            // 资格是“一轮练习”的状态，不是Scene状态；恢复先登记旧ID，再Reconcile，仅新安装的有限卡扣一次。
            RaidId=raidId;
            for(int i=0;i<qualified.Count;i++)if(_profile.Items.TryGet(qualified[i],out _))_eligible.Add(qualified[i]);
            Version++;return _profile.Items.ReconcilePolicy().Success;
        }
        internal void CopyPracticeQualifications(List<string> destination)
        {destination.Clear();foreach(var id in _eligible)destination.Add(id);}
        internal void Resume(PendingRaidRecord pending)
        { RaidId = pending.RaidId; for (int i = 0; i < pending.QualifiedIds.Length; i++) _eligible.Add(pending.QualifiedIds[i]); Version++; }
        public static bool IsQualification(SpellDefinition spell) => spell != null && (spell.Kind == SpellKind.Modify || spell.Kind == SpellKind.Multicast);
        InventoryFailureReason IInventoryCommitPolicy.Prepare(Dictionary<string, InventoryEntry> candidate, out object preparation, out bool changed)
        {
            preparation = null; changed = false; if (RaidId == null) return InventoryFailureReason.None;
            if(_profile.RaidActionsFrozen)return InventoryFailureReason.ResourceBusy;
            if (_profile.PendingRaid != null)
            {
                // Hub资产留在同一权威表，但正式Raid提交策略锁住其身份/位置，拖拽或直接Store入口均不能借用。
                foreach (var current in _profile.Items) if (_profile.PendingRaid.IsHubAsset(current.Item.InstanceId))
                    if (!candidate.TryGetValue(current.Item.InstanceId, out var next) || next.Location != current.Location
                        || !ReferenceEquals(next.Item, current.Item)) return InventoryFailureReason.InvalidOwnership;
                foreach (var next in candidate.Values) if (!_profile.PendingRaid.IsHubAsset(next.Item.InstanceId)
                    && (next.Location.Kind == ItemLocationKind.Warehouse || next.Location.HasParentInstance
                        && _profile.PendingRaid.IsHubAsset(next.Location.OwnerId))) return InventoryFailureReason.InvalidOwnership;
            }
            var acquired = new List<string>();
            foreach (InventoryEntry entry in candidate.Values)
            {
                if (entry.Location.Kind != ItemLocationKind.WandSlot || _eligible.Contains(entry.Item.InstanceId)) continue;
                _profile.Items.Catalog.TryGet(entry.Item.ContentId, out ItemDefinition definition);
                if (!IsQualification(definition.SpellDefinition) || !IsCarried(entry.Location.OwnerId, candidate)) continue;
                if (!entry.Item.HasUnlimitedCharges && entry.Item.RemainingCharges <= 0) return InventoryFailureReason.InsufficientUses;
                acquired.Add(entry.Item.InstanceId);
            }
            // 先扫描全部成员再改候选表，Dictionary 枚举中不能替换元素。
            for (int i = 0; i < acquired.Count; i++)
            {
                string id = acquired[i]; InventoryEntry entry = candidate[id]; RuntimeItemState item = entry.Item;
                if (!item.HasUnlimitedCharges) candidate[id] = new InventoryEntry(new RuntimeItemState(id, item.ContentId,
                    item.RemainingCharges - 1, false, item.StackStateKey, item.Composition, item.Material, item.Attachment), entry.Location);
            }
            preparation = acquired; changed = acquired.Count > 0; return InventoryFailureReason.None;
        }
        void IInventoryCommitPolicy.Commit(object preparation)
        { if (preparation is List<string> acquired && acquired.Count > 0) { for (int i = 0; i < acquired.Count; i++) _eligible.Add(acquired[i]); Version++; } }
        private bool IsCarried(string wand, Dictionary<string, InventoryEntry> candidate)
        {
            if (!candidate.TryGetValue(wand, out InventoryEntry root)) return false;
            while (root.Location.HasParentInstance) if (!candidate.TryGetValue(root.Location.OwnerId, out root)) return false;
            return root.Location.OwnerId == _profile.ActorId && (root.Location.Kind == ItemLocationKind.EquippedWand || root.Location.Kind == ItemLocationKind.EquippedBackpack);
        }
        private sealed class View : ISpellUseReadOnly
        {
            private readonly RaidSpellEligibility _owner;
            public View(RaidSpellEligibility owner) { _owner = owner; }
            public string RaidId => _owner.RaidId;
            public uint Version => _owner.Version;
            public bool TryGetState(string id, out SpellUseState state)
            {
                state = default; if (!_owner._profile.Items.TryGet(id, out RuntimeItemState item)
                    || !_owner._profile.Items.Catalog.TryGet(item.ContentId, out ItemDefinition definition) || definition.Kind != ItemKind.Spell) return false;
                state = new SpellUseState(item.RemainingCharges, item.HasUnlimitedCharges, IsQualification(definition.SpellDefinition), _owner.IsEligible(id)); return true;
            }
        }
    }
}
