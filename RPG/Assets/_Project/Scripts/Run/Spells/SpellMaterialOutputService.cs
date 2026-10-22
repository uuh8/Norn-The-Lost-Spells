using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;
using Unity.Profiling;

namespace Game.Run
{
    public readonly struct SpellMaterialOutputSnapshot
    {
        public readonly string OutputId, SpellId;
        public readonly SpellMaterialBudget Budget;
        public readonly SpellMaterialSupplyStatus Status;
        public readonly ulong Requested;
        internal SpellMaterialOutputSnapshot(string id, string card, SpellMaterialBudget budget, SpellMaterialSupplyStatus status, ulong requested)
        { OutputId = id; SpellId = card; Budget = budget; Status = status; Requested = requested; }
    }
    /// <summary>已扣库存量的唯一输出归属；向材料世界提交仍复用双端Journal，拒绝时持有原量。</summary>
    public sealed class SpellMaterialOutputService
    {
        private readonly SinglePlayerProfile _profile;
        private readonly string _inventoryId;
        private readonly Dictionary<string, Output> _outputs = new Dictionary<string, Output>(StringComparer.Ordinal);
        private readonly ulong[] _lost = new ulong[256];
        private readonly MaterialAmount[] _buffer = new MaterialAmount[8];
        private readonly MaterialDepositExchange _exchange = new MaterialDepositExchange();
        private bool _executing;
        private static readonly ProfilerMarker Marker = new ProfilerMarker("Norn.SpellMaterials.Deposit");
        public int ActiveOutputs => _outputs.Count;
        public ulong LostQuantity(byte key) => _lost[key];
        public SpellMaterialOutputService(SinglePlayerProfile profile, string inventoryId) { _profile = profile; _inventoryId = inventoryId; }
        internal PreparedLayer Prepare(SpellAttachmentState[] attachments, string[] cards, string worldId)
        {
            var batch = new PreparedLayer(this, attachments.Length);
            var sources = new Dictionary<string, SourceTake>(StringComparer.Ordinal);
            var share = new MaterialAmount[8]; var fractions = new ulong[8];
            for (int i = 0; i < attachments.Length; i++)
            {
                var config = attachments[i]; var status = SpellMaterialSupplyStatus.Disabled;
                SpellMaterialBudget budget = SpellMaterialBudget.Empty; Output output = null;
                if (config != null && config.Enabled)
                {
                    status = SpellMaterialSupplyStatus.InvalidSource;
                    if (!string.IsNullOrWhiteSpace(worldId) && IsCarried(config.ContainerId) && _profile.Items.TryGet(config.ContainerId, out var item) && item.Composition != null)
                    {
                        if (!sources.TryGetValue(config.ContainerId, out var take))
                        { take = new SourceTake(item.Composition); sources.Add(config.ContainerId, take); }
                        ulong quantity = MaterialAmountOps.SplitBudget(take.Remaining, take.Count, config.Dose, share, fractions, i);
                        status = quantity == 0 ? SpellMaterialSupplyStatus.Empty : quantity < config.Dose ? SpellMaterialSupplyStatus.Partial : SpellMaterialSupplyStatus.Ready;
                        if (quantity > 0)
                        {
                            budget = new SpellMaterialBudget(share, take.Count);
                            for (int j = 0; j < take.Count; j++)
                            { take.Remaining[j].Quantity -= share[j].Quantity; take.Taken[j].Quantity += share[j].Quantity; }
                            output = new Output(this, Guid.NewGuid().ToString("N"), worldId, budget); batch.Outputs.Add(output);
                        }
                    }
                }
                batch.Snapshots[i] = new SpellMaterialOutputSnapshot(output?.Id, cards[i], budget, status, config?.Dose ?? 0);
            }
            foreach (var take in sources.Values) if (take.Total > 0) batch.Sources.Add(take);
            return batch;
        }
        private bool IsCarried(string id) => _profile.Items.TryGetLocation(id, out var location) && location.Kind == ItemLocationKind.Backpack
            && _profile.Items.TryGetLocation(location.OwnerId, out var bag) && bag.Kind == ItemLocationKind.EquippedBackpack && bag.OwnerId == _profile.ActorId;
        public SpellMaterialOutputResultEvent Execute(in SpellMaterialOutputEvent request)
        {
            using var marker = Marker.Auto();
            var result = new SpellMaterialOutputResultEvent { InventoryId = _inventoryId, OutputId = request.OutputId, Failure = MaterialTransferFailure.WrongOwnership };
            if (request.InventoryId != _inventoryId || request.OutputId == null || !_outputs.TryGetValue(request.OutputId, out var output)) return result;
            if (_executing) { result.Failure = MaterialTransferFailure.ResourceBusy; return result; }
            if (request.Ended)
            {
                for (int i = 0; i < output.Budget.Count; i++) { var amount = output.Budget[i]; _lost[amount.MaterialKey] += amount.Quantity; }
                result.Quantity = output.Budget.Total; Resolve(output); result.Resolved = true; result.Ended = true; result.Failure = MaterialTransferFailure.None;
                GameLog.Warn($"附着输出世界归属结束：{request.OutputId}, {result.Quantity} GMU；未返还库存。", "SpellMaterials"); return result;
            }
            // 已提交输出结束只改自己的世界归属；不能被另一笔施法的库存锁挡成孤儿。
            if (_profile.Items.IsResourceBusy) { result.Failure = MaterialTransferFailure.ResourceBusy; return result; }
            if (!Finite(request.Position)) { result.Failure = MaterialTransferFailure.InvalidRequest; return result; }
            _executing = true;
            try
            {
                _exchange.WorldId = output.WorldId; _exchange.Position = request.Position; _exchange.Radius = .2f;
                _exchange.Count = output.Budget.CopyTo(_buffer); _exchange.Amounts = _buffer; _exchange.Source = output;
                _exchange.Handled = false; _exchange.Transferred = 0; _exchange.Failure = MaterialTransferFailure.None;
                EventBus<MaterialWorldDepositRequestedEvent>.Publish(new MaterialWorldDepositRequestedEvent { Exchange = _exchange });
                result.Failure = _exchange.Handled ? _exchange.Failure : MaterialTransferFailure.WorldUnavailable;
                result.Quantity = _exchange.Transferred; result.Resolved = output.Resolved; return result;
            }
            finally { output.Cancel(); _exchange.Source = null; _executing = false; }
        }
        public void EndAll()
        {
            // 关闭是离散边界；不能在Dictionary枚举中删除成员。
            var ids = new string[_outputs.Count]; _outputs.Keys.CopyTo(ids, 0);
            for (int i = 0; i < ids.Length; i++) EventBus<SpellMaterialOutputResultEvent>.Publish(Execute(new SpellMaterialOutputEvent
            { InventoryId = _inventoryId, OutputId = ids[i], Ended = true }));
        }
        private void Resolve(Output output) { output.Resolved = true; _outputs.Remove(output.Id); _profile.PendingMaterialOutputs--; }
        private static bool Finite(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
        internal sealed class PreparedLayer
        {
            private readonly SpellMaterialOutputService _owner;
            internal readonly SpellMaterialOutputSnapshot[] Snapshots;
            internal readonly List<SourceTake> Sources = new List<SourceTake>();
            internal readonly List<Output> Outputs = new List<Output>();
            internal bool HasMaterial => Outputs.Count > 0;
            internal PreparedLayer(SpellMaterialOutputService owner, int count) { _owner = owner; Snapshots = new SpellMaterialOutputSnapshot[count]; }
            internal void Commit()
            {
                // 当前仍持有Cast的库存锁；全部预算已经验证，提交期间不发布观察事件。
                for (int i = 0; i < Sources.Count; i++) Sources[i].Composition.RemoveReserved(Sources[i].Taken, Sources[i].Count);
                for (int i = 0; i < Outputs.Count; i++)
                { _owner._outputs.Add(Outputs[i].Id, Outputs[i]); _owner._profile.PendingMaterialOutputs++; }
            }
        }
        internal sealed class SourceTake
        {
            internal readonly ContainerComposition Composition;
            internal readonly MaterialAmount[] Remaining = new MaterialAmount[8], Taken = new MaterialAmount[8];
            internal readonly int Count;
            internal ulong Total { get { ulong q = 0; for (int i = 0; i < Count; i++) q += Taken[i].Quantity; return q; } }
            internal SourceTake(ContainerComposition composition)
            { Composition = composition; Count = composition.CopyTo(Remaining); for (int i = 0; i < Count; i++) Taken[i] = new MaterialAmount(Remaining[i].MaterialKey, 0); }
        }
        internal sealed class Output : IMaterialTransferSource
        {
            private readonly SpellMaterialOutputService _owner;
            internal readonly string Id, WorldId;
            internal readonly SpellMaterialBudget Budget;
            internal bool Resolved;
            private bool _reserved;
            internal Output(SpellMaterialOutputService owner, string id, string world, SpellMaterialBudget budget) { _owner = owner; Id = id; WorldId = world; Budget = budget; }
            public bool TryReserve(MaterialAmount[] amounts, int count, out MaterialTransferFailure failure)
            {
                failure = MaterialTransferFailure.InvalidRequest;
                if (Resolved || _reserved || count != Budget.Count || amounts == null || count > amounts.Length) return false;
                for (int i = 0; i < count; i++) if (amounts[i].MaterialKey != Budget[i].MaterialKey || amounts[i].Quantity != Budget[i].Quantity) return false;
                _reserved = true; failure = MaterialTransferFailure.None; return true;
            }
            public void Commit() { if (!_reserved || Resolved) throw new InvalidOperationException("附着输出未预约。"); _owner.Resolve(this); _reserved = false; }
            public void Cancel() { _reserved = false; }
        }
    }
}
