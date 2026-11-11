using System;
using Game.Networking;
using Shared = Game.NetworkProtocol;

namespace Game.Multiplayer
{
    /// <summary>一个固定正文的控制面pending；Receipt/完整Head均确认前，不释放原候选或推进退役水位。</summary>
    public sealed class RunCheckpointPending
    {
        private string _body, _checkpoint, _instance, _run, _hash;
        private uint _epoch;
        private ulong _sequence, _retirement, _lastPrepared;
        private bool _terminal;
        public bool HasPending => _body != null;
        public string Body => _body;
        public string CheckpointId => _checkpoint;
        public ulong LastConfirmedSequence { get; private set; }
        public string LastConfirmedCheckpointId { get; private set; }
        public string LastConfirmedPayloadHash { get; private set; }
        public bool LastConfirmedTerminal { get; private set; }
        public bool IsTerminal => HasPending && _terminal;
        public ulong LastAcknowledgedRetirementSequence { get; private set; }
        public bool TryPrepare(CapturedRunCheckpoint captured, out string error)
        {
            error = "RunCheckpointPendingConflict";
            var value = captured != null ? captured.Value : null;
            if (HasPending || value == null || value.Sequence == 0 || value.Sequence <= _lastPrepared
                || !Guid.TryParseExact(value.CheckpointId, "N", out _) || !Guid.TryParseExact(value.InstanceId, "N", out _)
                || !Guid.TryParseExact(value.RunId, "N", out _) || value.RoomEpoch == 0
                || captured.RetirementSequence < LastAcknowledgedRetirementSequence
                || !TryCanonicalBody(value, false, captured.TerminalCapture, out var body)) return false;
            // 只保留固定正文与基本身份，不借出原DTO供后续游戏帧修改。未知HTTP结果重发的是同一string。
            _body = body; _checkpoint = value.CheckpointId; _instance = value.InstanceId; _run = value.RunId;
            _epoch = value.RoomEpoch; _sequence = _lastPrepared = value.Sequence; _retirement = captured.RetirementSequence;
            _terminal = captured.TerminalCapture;
            _hash = null; error = null; return true;
        }
        public bool ObserveReceipt(Shared.OnlineRunAssetCheckpointReceipt receipt)
        {
            if (!Matches(receipt) || !Hash(receipt.PayloadHash) || _hash != null && _hash != receipt.PayloadHash) return false;
            _hash = receipt.PayloadHash; return true;
        }
        public bool ConfirmReadback(Shared.OnlineRunAssetCheckpointHeadReply reply)
        {
            if (!HasPending || _hash == null || reply == null || !reply.IsComplete || !Matches(reply.Receipt)
                || reply.Receipt.PayloadHash != _hash || reply.Checkpoint == null
                || reply.Checkpoint.SchemaVersion != Shared.OnlineRunAssetCheckpoint.CompleteSchemaVersion
                || !TryCanonicalBody(reply.Checkpoint, true, _terminal, out var body) || body != _body) return false;
            // ACK的是这份已经保存且按完整值读回的候选，不是查询时世界里更新后的journal.Sequence。
            LastConfirmedSequence = _sequence; LastAcknowledgedRetirementSequence = _retirement;
            LastConfirmedCheckpointId = _checkpoint; LastConfirmedPayloadHash = _hash; LastConfirmedTerminal = _terminal;
            Clear(); return true;
        }
        public bool ResolveRejectedPost(long postStatus, long queryStatus)
        {
            // 连接中断/超时+404不能证明先前事务不会提交；这种情况保留原ID/正文继续查询/重试。
            // 只有明确4xx拒绝、随后同凭据查询404且尚未见任何持久收据，才允许另建新候选。
            if (!HasPending || _hash != null || postStatus < 400 || postStatus >= 500 || postStatus == 408
                || postStatus == 429 || queryStatus != 404) return false;
            Clear(); return true;
        }
        private bool Matches(Shared.OnlineRunAssetCheckpointReceipt receipt)
            => HasPending && receipt != null && receipt.CheckpointId == _checkpoint && receipt.InstanceId == _instance
                && receipt.RunId == _run && receipt.RoomEpoch == _epoch && receipt.Sequence == _sequence;
        private static bool Hash(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }
        private void Clear() { _body = _checkpoint = _instance = _run = _hash = null; _sequence = _retirement = 0; _epoch = 0; _terminal = false; }

        private static bool TryCanonicalBody(Shared.OnlineRunAssetCheckpoint value, bool received, bool terminal, out string body)
        {
            body = null;
            if (value == null || value.SchemaVersion != Shared.OnlineRunAssetCheckpoint.CompleteSchemaVersion
                || value.Members == null || value.Members.Length > 4 || (terminal ? value.Members.Length != 0 : value.Members.Length < 1)
                || value.GroundItems == null || value.RetiredItems == null || value.World == null || value.World.CanRestoreAuthorityRules
                || value.World.Chunks == null || value.Flights == null || value.MaterialOutputs == null || value.LostMaterials == null
                || !Shared.OnlineRunAssetCheckpoint.HasValidCoinFacts(value.Coins)) return false;
            foreach (var member in value.Members)
            {
                if (member == null || member.Assets == null || member.Assets.Items == null || member.Wands == null || member.SpellUses == null) return false;
                // 新完整能力明确包含可信统一币值；不能把旧Owned/缺修订补成0后推进保存水位。
                if (member.Assets.SchemaVersion != Shared.OnlineOwnedAssetSnapshot.CurrentSchemaVersion
                    || member.Assets.Gold < 0 || member.Assets.CurrencyVersion == 0) return false;
                if (received) member.Assets = OnlineOwnedAssetMapping.ToShared(OnlineOwnedAssetMapping.ToCore(member.Assets));
                if (!Items(member.Assets.Items)) return false;
                for (int i = 0; i < member.Wands.Length; i++) if (member.Wands[i] == null) return false;
                for (int i = 0; i < member.SpellUses.Length; i++) if (member.SpellUses[i] == null) return false;
                Array.Sort(member.Wands, (a, b) => StringComparer.Ordinal.Compare(a.InstanceId, b.InstanceId));
                Array.Sort(member.SpellUses, (a, b) => StringComparer.Ordinal.Compare(a.InstanceId, b.InstanceId));
            }
            foreach (var ground in value.GroundItems)
            {
                if (ground == null || ground.Item == null || ground.Children == null) return false;
                if (received)
                {
                    ground.Item = NormalizeItem(ground.Item);
                    for (int i = 0; i < ground.Children.Length; i++) ground.Children[i] = NormalizeItem(ground.Children[i]);
                }
                if (!Items(ground.Children) || !Parts(ground.Item.ContainerState != null ? ground.Item.ContainerState.Components : null)) return false;
            }
            foreach (var flight in value.Flights)
            {
                if (flight == null || flight.Item == null) return false;
                if (received)
                {
                    flight.Item = NormalizeItem(flight.Item);
                    if (flight.Impacted && !flight.HasPhysicalProxy) flight.Velocity = null;
                    if (flight.Impacted && !flight.HasPhysicalProxy) flight.Rotation = null;
                }
                if (!Parts(flight.Item.ContainerState != null ? flight.Item.ContainerState.Components : null)) return false;
            }
            foreach (var output in value.MaterialOutputs) if (output == null || output.Remaining == null || !Parts(output.Remaining)) return false;
            foreach (var lost in value.LostMaterials) if (lost == null) return false;
            foreach (var retired in value.RetiredItems) if (retired == null) return false;
            foreach (var chunk in value.World.Chunks) if (chunk == null || chunk.Words == null) return false;
            // 规范数组顺序只用于按值比对；不改Mana/数量/固定属性，不按此排序重新解释程序槽位。
            Array.Sort(value.Members, (a, b) => StringComparer.Ordinal.Compare(a.PlayerId, b.PlayerId));
            Array.Sort(value.GroundItems, (a, b) => StringComparer.Ordinal.Compare(a.Item.InstanceId, b.Item.InstanceId));
            Array.Sort(value.Flights, (a, b) => StringComparer.Ordinal.Compare(a.FlightId, b.FlightId));
            Array.Sort(value.RetiredItems, (a, b) => StringComparer.Ordinal.Compare(a.InstanceId, b.InstanceId));
            Array.Sort(value.Coins, (a, b) => a.Id.CompareTo(b.Id));
            Array.Sort(value.MaterialOutputs, (a, b) => StringComparer.Ordinal.Compare(a.OutputId, b.OutputId));
            Array.Sort(value.LostMaterials, (a, b) => {
                int v = StringComparer.Ordinal.Compare(a.PlayerId, b.PlayerId); if (v != 0) return v;
                v = StringComparer.Ordinal.Compare(a.Source, b.Source); return v != 0 ? v : a.MaterialKey.CompareTo(b.MaterialKey); });
            Array.Sort(value.World.Chunks, (a, b) => {
                int v = a.Domain.CompareTo(b.Domain); if (v != 0) return v; v = a.X.CompareTo(b.X); if (v != 0) return v;
                v = a.Y.CompareTo(b.Y); return v != 0 ? v : a.Z.CompareTo(b.Z); });
            return InstanceCheckpointJson.TrySerialize(value, out body, out _);
        }
        private static Shared.OnlineOwnedItemSnapshot NormalizeItem(Shared.OnlineOwnedItemSnapshot item)
        {
            if (item == null) return null;
            var wrapper = new Shared.OnlineOwnedAssetSnapshot { Items = new[] { item } };
            return OnlineOwnedAssetMapping.ToSharedItem(OnlineOwnedAssetMapping.ToCore(wrapper).Items[0]);
        }
        private static bool Items(Shared.OnlineOwnedItemSnapshot[] items)
        {
            foreach (var item in items) if (item == null || !Parts(item.ContainerState != null ? item.ContainerState.Components : null)) return false;
            Array.Sort(items, (a, b) => StringComparer.Ordinal.Compare(a.InstanceId, b.InstanceId)); return true;
        }
        private static bool Parts(Shared.OnlineOwnedMaterialAmountSnapshot[] values)
        {
            if (values == null) return true;
            foreach (var part in values) if (part == null) return false;
            Array.Sort(values, (a, b) => a.MaterialKey.CompareTo(b.MaterialKey)); return true;
        }
    }
}
