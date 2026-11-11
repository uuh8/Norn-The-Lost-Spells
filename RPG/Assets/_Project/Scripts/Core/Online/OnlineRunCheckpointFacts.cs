using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Core
{
    /// <summary>
    /// 本人退出安全点保存的累计损耗事实，不含库存或任何写入口。只有最终退出收据确认后，
    /// 实例才将它作为后续检查点的历史署名；不能把已退出玩家重新添加为资源Owner。
    /// 三个来源各最多8个MaterialKey，四Actor总计最多96行；明确零损耗用空数组，缺缓存不是零。
    /// </summary>
    [Serializable]
    public sealed class OnlineExitedMemberLossSnapshot
    {
        public const int MaximumMaterialsPerSource = 8;
        public string RunId = "", PlayerId = "";
        public ulong ActorId;
        public OnlineOwnedMaterialAmountSnapshot[] ContainerLost = Array.Empty<OnlineOwnedMaterialAmountSnapshot>();
        public OnlineOwnedMaterialAmountSnapshot[] SpellMaterialLost = Array.Empty<OnlineOwnedMaterialAmountSnapshot>();

        // 最终死亡销毁的是仍在本人库存中的材料，不冒充在途瓶/法术输出损耗。
        // 只有个人最终收据确认后才对完整检查点可见，每Actor仍最多8个材料Key。
        public OnlineOwnedMaterialAmountSnapshot[] FinalDeathLost = Array.Empty<OnlineOwnedMaterialAmountSnapshot>();

        /// <summary>跨Owner/实例捕获边界只交付独立值；不允许缺来源数组、重复Key或将缺缓存解释成零。</summary>
        public static bool TryCopy(OnlineExitedMemberLossSnapshot source, out OnlineExitedMemberLossSnapshot copy)
        {
            copy = null;
            if (source == null || string.IsNullOrWhiteSpace(source.RunId) || string.IsNullOrWhiteSpace(source.PlayerId)
                || source.ActorId < 1 || source.ActorId > 4
                || !CopyAmounts(source.ContainerLost, out var container) || !CopyAmounts(source.SpellMaterialLost, out var spell)
                || !CopyAmounts(source.FinalDeathLost, out var death)) return false;
            copy = new OnlineExitedMemberLossSnapshot { RunId = source.RunId, PlayerId = source.PlayerId, ActorId = source.ActorId,
                ContainerLost = container, SpellMaterialLost = spell, FinalDeathLost = death };
            return true;
        }
        private static bool CopyAmounts(OnlineOwnedMaterialAmountSnapshot[] source, out OnlineOwnedMaterialAmountSnapshot[] copy)
        {
            copy = null;
            if (source == null || source.Length > MaximumMaterialsPerSource) return false;
            var values = new OnlineOwnedMaterialAmountSnapshot[source.Length]; uint seen = 0;
            for (int i = 0; i < source.Length; i++)
            {
                var value = source[i];
                if (value == null || value.MaterialKey < 1 || value.MaterialKey > MaximumMaterialsPerSource || value.Quantity == 0) return false;
                uint bit = 1u << value.MaterialKey;
                if ((seen & bit) != 0) return false;
                seen |= bit;
                values[i] = new OnlineOwnedMaterialAmountSnapshot { MaterialKey = value.MaterialKey, Quantity = value.Quantity };
            }
            copy = values; return true;
        }
    }

    /// <summary>
    /// Run在同一主线程提交边界复制的中性事实；不含账号凭据、Shared DTO或任何可写服务引用。
    /// Epoch/Gameplay Tick/物理代理姿态由上层真实Registry补齐，不能在这里伪造连接身份。
    /// 这是保存证据，不声明能够重建施法调度器、材料Solver或决定ESC/死亡/撤离结算。
    /// </summary>
    [Serializable]
    public sealed class OnlineOwnedCheckpointSnapshot
    {
        public const int MaximumContainerTransits = 8;
        // 材料输出不是背包Item，不能误套4096物品槽。完整wire至少含两个32字符身份，
        // 每项必然超过64字节；此保守门仅排除不可能装入原64MiB正文的数量，不改变供料玩法。
        // 与Shared完整检查点字节预算保持同值，Core仍不反向依赖网络合同程序集。
        public const int MaximumMaterialOutputs = (64 * 1024 * 1024) / 64;
        public OnlineOwnedAssetSnapshot Assets;
        public uint ResourceVersion, SelectionVersion, SpellUseVersion, SelectedToolSlotPlusOne;
        public OnlineInventoryWandPresentationSnapshot[] Wands = Array.Empty<OnlineInventoryWandPresentationSnapshot>();
        public OnlineInventorySpellUsePresentationSnapshot[] SpellUses = Array.Empty<OnlineInventorySpellUsePresentationSnapshot>();
        public OnlineContainerTransitCheckpointSnapshot[] ContainerTransits = Array.Empty<OnlineContainerTransitCheckpointSnapshot>();
        public OnlineSpellMaterialCheckpointSnapshot[] MaterialOutputs = Array.Empty<OnlineSpellMaterialCheckpointSnapshot>();
        public OnlineOwnedMaterialAmountSnapshot[] ContainerLost = Array.Empty<OnlineOwnedMaterialAmountSnapshot>();
        public OnlineOwnedMaterialAmountSnapshot[] SpellMaterialLost = Array.Empty<OnlineOwnedMaterialAmountSnapshot>();
        public OnlineItemRetirementSnapshot[] Retired = Array.Empty<OnlineItemRetirementSnapshot>();
        public ulong RetirementSequence;
    }

    /// <summary>
    /// 原ContainerUseService的真实在途账本。Impacted=false时ImpactPosition没有当前代理位置语义；
    /// 完整发布者另取同Flight物理代理的Transform/Rigidbody，不能把这里的默认零向量当飞行位置。
    /// Item保留Location6、原ID/Revision和当前剩余组分，不返还携带位、不按模板重建材料。
    /// </summary>
    [Serializable]
    public sealed class OnlineContainerTransitCheckpointSnapshot
    {
        public string InventoryId = "", FlightId = "", WorldId = "", SourceId = "", ItemId = "";
        public OnlineOwnedItemSnapshot Item;
        public bool Impacted;
        public Vector3 ImpactPosition;
        public float Radius;
        public ulong Deposited;
    }

    /// <summary>库存已扣而尚未交给材料世界的独立数量Owner；不猜测原服务未保存的SpellId。</summary>
    [Serializable]
    public sealed class OnlineSpellMaterialCheckpointSnapshot
    {
        public string OutputId = "", WorldId = "";
        public OnlineOwnedMaterialAmountSnapshot[] Components = Array.Empty<OnlineOwnedMaterialAmountSnapshot>();
    }

    public enum OnlineItemRetirementReason : byte
    { None = 0, Consumed = 1, Merged = 2, Destroyed = 3 }

    /// <summary>
    /// 仅由已成功的资源事务写入的终止事实。源Store移除也可能是丢地/转交，不能按差集填本记录。
    /// Sequence属于整个Run的有界Journal；上层保存固定候选/收据水位后再筛选，不能无界积累或清墓碑腾位。
    /// </summary>
    [Serializable]
    public sealed class OnlineItemRetirementSnapshot
    {
        public ulong Sequence;
        public string InventoryId = "", InstanceId = "", ContentId = "";
        public uint Revision;
        public OnlineItemRetirementReason Reason;
    }

    /// <summary>容量预留与真实资源准备同生命周期；Commit不得失败或调用外部观察者，Cancel不能产生退役。</summary>
    public interface IOnlineItemRetirementReservation
    {
        void Commit();
        void Cancel();
    }

    /// <summary>
    /// DS为同一Run组装同一个Journal后绑定各Store。它只记录身份终止，不创建/转移物品或决定返还规则。
    /// 批次必须在删物/扣Mana之前预检容量；捕获不得借出内部数组或读到尚未提交的预留。
    /// </summary>
    public interface IOnlineItemRetirementJournal
    {
        int Count { get; }
        ulong Sequence { get; }
        bool TryReserve(OnlineItemRetirementSnapshot[] facts, out IOnlineItemRetirementReservation reservation);
        bool TryCapture(out OnlineItemRetirementSnapshot[] facts);
    }

    /// <summary>
    /// Run级有限身份Journal。32768沿既有完整检查点身份总预算，不代表无限掉落/无限探索；
    /// 总聚合还必须检查Owner/Ground/Transit与历史身份的联合预算。历史不回收、不覆写，
    /// 满额在真实资源提交之前拒绝；调用方不能因检查点未ACK就继续删除没有证据的实例。
    /// </summary>
    public sealed class OnlineRunItemRetirementJournal : IOnlineItemRetirementJournal
    {
        public const int MaximumRecords = 32768;
        private readonly OnlineItemRetirementSnapshot[] _records;
        private readonly HashSet<string> _known;
        private int _count, _reservedCount;
        private ulong _sequence;
        public int Count => _count;
        public ulong Sequence => _sequence;
        public int Capacity { get; }

        public OnlineRunItemRetirementJournal(int capacity = MaximumRecords)
        {
            if (capacity < 1 || capacity > MaximumRecords) throw new ArgumentOutOfRangeException(nameof(capacity));
            Capacity = capacity;
            _records = new OnlineItemRetirementSnapshot[capacity];
            _known = new HashSet<string>(capacity, StringComparer.Ordinal);
        }

        public bool TryReserve(OnlineItemRetirementSnapshot[] facts, out IOnlineItemRetirementReservation reservation)
        {
            reservation = null;
            if (facts == null || facts.Length > Capacity - _count - _reservedCount
                || (ulong)facts.Length > ulong.MaxValue - _sequence - (ulong)_reservedCount) return false;
            var ids = new HashSet<string>(facts.Length, StringComparer.Ordinal);
            var frozen = new OnlineItemRetirementSnapshot[facts.Length];
            for (int i = 0; i < facts.Length; i++)
            {
                var fact = facts[i];
                if (fact == null || fact.Sequence != 0 || string.IsNullOrWhiteSpace(fact.InventoryId)
                    || string.IsNullOrWhiteSpace(fact.InstanceId) || string.IsNullOrWhiteSpace(fact.ContentId)
                    || fact.Revision == 0 || fact.Reason < OnlineItemRetirementReason.Consumed
                    || fact.Reason > OnlineItemRetirementReason.Destroyed || _known.Contains(fact.InstanceId)
                    || !ids.Add(fact.InstanceId)) return false;
                frozen[i] = Copy(fact);
            }
            // 一整批通过后才占位；后续Commit只发布已经冻结的值，不再校验/扩容/调用观察者。
            for (int i = 0; i < frozen.Length; i++) _known.Add(frozen[i].InstanceId);
            _reservedCount += frozen.Length;
            reservation = new Reservation(this, frozen);
            return true;
        }

        public bool TryCapture(out OnlineItemRetirementSnapshot[] facts)
        {
            facts = null;
            if (_reservedCount != 0) return false;
            var captured = new OnlineItemRetirementSnapshot[_count];
            for (int i = 0; i < captured.Length; i++) captured[i] = Copy(_records[i]);
            facts = captured;
            return true;
        }

        private static OnlineItemRetirementSnapshot Copy(OnlineItemRetirementSnapshot value)
            => new OnlineItemRetirementSnapshot { Sequence = value.Sequence, InventoryId = value.InventoryId,
                InstanceId = value.InstanceId, ContentId = value.ContentId, Revision = value.Revision, Reason = value.Reason };

        private sealed class Reservation : IOnlineItemRetirementReservation
        {
            private readonly OnlineRunItemRetirementJournal _owner;
            private readonly OnlineItemRetirementSnapshot[] _facts;
            private bool _finished;
            internal Reservation(OnlineRunItemRetirementJournal owner, OnlineItemRetirementSnapshot[] facts)
            { _owner = owner; _facts = facts; }
            public void Commit()
            {
                if (_finished) return;
                _finished = true;
                for (int i = 0; i < _facts.Length; i++)
                {
                    var fact = _facts[i]; fact.Sequence = ++_owner._sequence;
                    _owner._records[_owner._count++] = fact;
                }
                _owner._reservedCount -= _facts.Length;
            }
            public void Cancel()
            {
                if (_finished) return;
                _finished = true;
                for (int i = 0; i < _facts.Length; i++) _owner._known.Remove(_facts[i].InstanceId);
                _owner._reservedCount -= _facts.Length;
            }
        }
    }
}
