using System;
using Game.NetworkProtocol;

namespace Game.Multiplayer
{
    /// <summary>DS领取的入局身份快照，只读查询；不复用单机InventorySession，不提供Client改资产入口。</summary>
    public sealed class OnlineInventorySession
    {
        private readonly InstanceEntryMember[] _members;
        private readonly bool[] _exited = new bool[4];
        // 冻结闭包还包括包内备用杖的安装卡；另留仓库16/旧平面包8槽的有界整备预算。
        private const int MaxWholeProfileItems = OnlineOwnedAssetSnapshot.MaximumItems + 24;
        public int MemberCount => _members.Length;
        public int ActiveMemberCount { get { int count = 0; for (int i = 0; i < _members.Length; i++) if (!_exited[(int)_members[i].ActorId - 1]) count++; return count; } }
        public OnlineInventorySession(InstanceReadyReply ready, string manifestHash)
        {
            if (ready == null || ready.Members == null || (ready.SinglePlayer ? ready.Members.Length != 1 : ready.Members.Length < 2 || ready.Members.Length > 4))
                throw new ArgumentException("单人须可信业务标记且恰好1人；合作仍为2–4人。");
            _members = ready.Members;
            for (int i = 0; i < _members.Length; i++)
            {
                var member = _members[i];
                if (member == null || member.Profile == null || member.ActorId < 1 || member.ActorId > 4 || member.Profile.Items == null || member.Profile.Items.Length > MaxWholeProfileItems || member.Profile.ContentManifestHash != manifestHash) throw new ArgumentException("入局资产快照不符。");
                // 同一RR事务冻结的Profile与执行闭包必须是同一真实余额；不取本地档或缺字段的默认0。
                var owned = member.Profile.CombatAssets;
                if (member.Profile.Gold < 0 || owned == null || owned.SchemaVersion != OnlineOwnedAssetSnapshot.CurrentSchemaVersion
                    || owned.Gold != member.Profile.Gold || owned.CurrencyVersion == 0)
                    throw new ArgumentException("入局金币与可信资产版本不一致。");
                for (int j = 0; j < i; j++) if (_members[j].ActorId == member.ActorId || _members[j].Profile.PlayerId == member.Profile.PlayerId) throw new ArgumentException("重复入局身份。");
            }
        }
        public bool Contains(ulong actor, string player)
        { for (int i = 0; i < _members.Length; i++) if (_members[i].ActorId == actor && !_exited[(int)actor - 1] && _members[i].Profile.PlayerId == player) return true; return false; }
        /// <summary>仅最终业务收据才能结束本人资格；固定Actor槽不压缩，旧Entry不能重建已经结算的库存。</summary>
        public bool MarkMemberExited(ulong actor, string player)
        {
            if (actor < 1 || actor > 4) return false;
            for (int i = 0; i < _members.Length; i++)
                if (_members[i].ActorId == actor && _members[i].Profile.PlayerId == player)
                { _exited[(int)actor - 1] = true; return true; }
            return false;
        }
        public bool IsMemberExited(ulong actor) => actor >= 1 && actor <= 4 && _exited[(int)actor - 1];
        /// <summary>只借出可信冻结身份；完整捕获器须立即深拷贝，不能将Entry冒充曾入场成员的当前值。</summary>
        public bool TryGetFrozenMember(int index, out InstanceEntryMember member)
        { member = null; if ((uint)index >= (uint)_members.Length) return false; member = _members[index]; return true; }
        /// <summary>只交付冻结的入局值；不重读Client、本地Profile或注册种子。</summary>
        public bool TryGetCombatAssets(ulong actor, out OnlineOwnedAssetSnapshot assets)
        {
            assets = null;
            for (int i = 0; i < _members.Length; i++) if (_members[i].ActorId == actor && !_exited[(int)actor - 1])
            { assets = _members[i].Profile.CombatAssets; return assets != null; }
            return false;
        }
    }
}
