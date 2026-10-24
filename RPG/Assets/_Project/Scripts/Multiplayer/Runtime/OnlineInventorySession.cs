using System;
using Game.NetworkProtocol;

namespace Game.Multiplayer
{
    /// <summary>DS领取的入局身份快照，只读查询；不复用单机InventorySession，不提供Client改资产入口。</summary>
    public sealed class OnlineInventorySession
    {
        private readonly InstanceEntryMember[] _members;
        // 仓库16 + 背包8 + 3把装备法杖及每把最多32张安装卡；执行投影本身仍最多99项。
        private const int MaxWholeProfileItems = 24 + 3 * (1 + 32);
        public int MemberCount => _members.Length;
        public OnlineInventorySession(InstanceReadyReply ready, string manifestHash)
        {
            if (ready == null || ready.Members == null || ready.Members.Length < 2 || ready.Members.Length > 4) throw new ArgumentException("入局成员数量不符。");
            _members = ready.Members;
            for (int i = 0; i < _members.Length; i++)
            {
                var member = _members[i];
                if (member == null || member.Profile == null || member.ActorId < 1 || member.ActorId > 4 || member.Profile.Items == null || member.Profile.Items.Length > MaxWholeProfileItems || member.Profile.ContentManifestHash != manifestHash) throw new ArgumentException("入局资产快照不符。");
                for (int j = 0; j < i; j++) if (_members[j].ActorId == member.ActorId || _members[j].Profile.PlayerId == member.Profile.PlayerId) throw new ArgumentException("重复入局身份。");
            }
        }
        public bool Contains(ulong actor, string player)
        { for (int i = 0; i < _members.Length; i++) if (_members[i].ActorId == actor && _members[i].Profile.PlayerId == player) return true; return false; }
        /// <summary>只交付冻结的入局值；不重读Client、本地Profile或注册种子。</summary>
        public bool TryGetCombatAssets(ulong actor, out OnlineOwnedAssetSnapshot assets)
        {
            assets = null;
            for (int i = 0; i < _members.Length; i++) if (_members[i].ActorId == actor)
            { assets = _members[i].Profile.CombatAssets; return assets != null; }
            return false;
        }
    }
}
