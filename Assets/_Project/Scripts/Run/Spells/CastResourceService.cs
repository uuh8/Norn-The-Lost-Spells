using System;
using System.Collections.Generic;
using Game.Core;
using Game.Combat;
using Game.Skills;

namespace Game.Run
{
    /// <summary>原杖 Mana、本次有限次数和未来执行预算的唯一协调者；离散操作不进入每帧热路径。</summary>
    public sealed class CastResourceService
    {
        private readonly SinglePlayerProfile _profile;
        private readonly WandLoadoutService _wands;
        private readonly RaidSpellEligibility _eligibility;
        private readonly string _inventoryId, _paymentCaller;
        private ulong _paymentSequence;
        private readonly Dictionary<string, CastResourceReservation> _tickets = new Dictionary<string, CastResourceReservation>(StringComparer.Ordinal);
        private readonly Dictionary<string, Receipt> _receipts = new Dictionary<string, Receipt>(StringComparer.Ordinal);
        private CastResourceReservation _pending;
        private int _pendingLayer;
        private ItemOwnershipStore.PreparedInventory _prepared;
        private SpellMaterialOutputService.PreparedLayer _materialLayer;
        public SpellMaterialOutputService MaterialOutputs { get; }
        private struct Receipt { internal CastResourceRequestedEvent Request; internal CastResourceResultEvent Result; }
        public bool IsPending => _pending != null;
        public ICastResourceReadOnly ReadOnly { get; }
        public CastResourceService(SinglePlayerProfile profile, WandLoadoutService wands, RaidSpellEligibility eligibility, string inventoryId,
            SpellMaterialOutputService materialOutputs = null)
        { _profile = profile; _wands = wands; _eligibility = eligibility; _inventoryId = inventoryId;
            _paymentCaller = "cast." + Guid.NewGuid().ToString("N"); ReadOnly = new View(this);
            MaterialOutputs = materialOutputs ?? new SpellMaterialOutputService(profile, inventoryId); }
        public CastResourceResultEvent Execute(in CastResourceRequestedEvent request)
        {
            var result = new CastResourceResultEvent { InventoryId = _inventoryId, SourceWandId = request.SourceWandId,
                CallerId = request.CallerId, RequestId = request.RequestId, TicketId = request.TicketId, Operation = request.Operation, Reason = OwnedWandFailureReason.InvalidRequest };
            if (request.InventoryId != _inventoryId || string.IsNullOrWhiteSpace(request.CallerId) || request.RequestId == 0) return result;
            if (_receipts.TryGetValue(request.CallerId, out Receipt receipt) && request.RequestId <= receipt.Request.RequestId)
                return SameRequest(request, receipt.Request) ? receipt.Result : result;
            switch (request.Operation)
            {
                case CastResourceOperation.PrepareRoot: result.Reason = PrepareRoot(request, out string ticket); result.TicketId = ticket; break;
                case CastResourceOperation.PrepareLayer: result.Reason = PrepareLayer(request); break;
                case CastResourceOperation.Complete: result.Reason = Finish(request, true); break;
                case CastResourceOperation.Cancel: result.Reason = Finish(request, false); break;
                case CastResourceOperation.Abandon: result.Reason = Abandon(request); break;
                case CastResourceOperation.AbandonCaller: CancelCaller(request.CallerId); result.Reason = OwnedWandFailureReason.None; break;
            }
            result.Success = result.Reason == OwnedWandFailureReason.None;
            _receipts[request.CallerId] = new Receipt { Request = request, Result = result }; return result;
        }
        private static bool SameRequest(in CastResourceRequestedEvent a, in CastResourceRequestedEvent b) =>
            a.RequestId == b.RequestId && a.SourceWandId == b.SourceWandId && a.TicketId == b.TicketId
            && a.LayerIndex == b.LayerIndex && a.ProgramRevision == b.ProgramRevision && a.Operation == b.Operation && a.MaterialWorldId == b.MaterialWorldId;
        private OwnedWandFailureReason PrepareRoot(in CastResourceRequestedEvent request, out string ticket)
        {
            ticket = null;
            if (request.LayerIndex != 0 || request.TicketId != null) return OwnedWandFailureReason.InvalidRequest;
            if (IsPending) return OwnedWandFailureReason.ResourceBusy;
            _wands.Synchronize(); var read = _wands.ReadOnly;
            if (!read.TryGetState(request.SourceWandId, out WandStateSnapshot state)
                || !read.TryGetProgram(request.SourceWandId, out WandProgramSnapshot program)) return OwnedWandFailureReason.UnknownSource;
            if (!state.Carried) return OwnedWandFailureReason.NotCarried;
            if (program.ProgramRevision != request.ProgramRevision) return OwnedWandFailureReason.StaleProgram;
            if (state.CooldownRemaining > 0) return OwnedWandFailureReason.Cooldown;
            if (!CastExecutionPlan.TryBuild(program.Program, program.BaseDraws, out CastExecutionPlan plan) || !ValidatePrefabs(plan)) return OwnedWandFailureReason.InvalidProgram;
            if (plan.GetLayer(0).ManaCost > state.CurrentMana) return OwnedWandFailureReason.InsufficientMana;
            var uses = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int l = 0; l < plan.LayerCount; l++)
            {
                CastExecutionLayer layer = plan.GetLayer(l);
                for (int i = 0; i < layer.UseCount; i++)
                {
                    string id = program.Program.GetInstanceId(layer.GetSlot(i));
                    if (!_profile.Items.TryGet(id, out RuntimeItemState item)) return OwnedWandFailureReason.InvalidProgram;
                    SpellKind kind = layer.GetKind(i);
                    if (kind == SpellKind.Modify || kind == SpellKind.Multicast)
                    {
                        // 整备中无限卡仍能试验；有限资格卡必须显式进入 Raid 才能使用。
                        if (!item.HasUnlimitedCharges && !_eligibility.IsEligible(id)) return OwnedWandFailureReason.MissingEligibility;
                        continue;
                    }
                    uses.TryGetValue(id, out int count); uses[id] = count + 1;
                }
            }
            if (!_profile.Items.TryPrepareUses(uses, out _prepared)) return OwnedWandFailureReason.InsufficientUses;
            ticket = Guid.NewGuid().ToString("N");
            var reservation = new CastResourceReservation(ticket, request.SourceWandId, request.CallerId, request.ProgramRevision, plan);
            reservation.MaterialWorldId = request.MaterialWorldId;
            reservation.Attachments = new SpellAttachmentState[plan.LayerCount][]; reservation.Cards = new string[plan.LayerCount][];
            for (int l = 0; l < plan.LayerCount; l++)
            {
                var layer = plan.GetLayer(l); reservation.Attachments[l] = new SpellAttachmentState[layer.CommandCount]; reservation.Cards[l] = new string[layer.CommandCount];
                for (int i = 0; i < layer.CommandCount; i++)
                {
                    string card = program.Program.GetInstanceId(layer.GetOutputSlot(i)); reservation.Cards[l][i] = card;
                    _profile.Items.TryGet(card, out var instance); reservation.Attachments[l][i] = instance?.Attachment;
                }
            }
            _materialLayer = MaterialOutputs.Prepare(reservation.Attachments[0], reservation.Cards[0], reservation.MaterialWorldId);
            reservation.Attempted[0] = true; _tickets.Add(ticket, reservation); _pending = reservation; _pendingLayer = 0;
            return OwnedWandFailureReason.None;
        }
        private static bool ValidatePrefabs(CastExecutionPlan plan)
        {
            for (int l = 0; l < plan.LayerCount; l++)
                for (int i = 0; i < plan.GetLayer(l).CommandCount; i++)
                {
                    EmitCommand command = plan.GetLayer(l).GetCommand(i);
                    if (command.ProjectilePrefab == null || command.SpawnMode > SpellSpawnMode.StaticAtPoint || !ValidNumbers(command)) return false;
                    if (command.SpawnMode == SpellSpawnMode.StaticAtPoint)
                    { if (command.ProjectilePrefab.GetComponentInChildren<ProjectileShield>(true) == null || command.HasPayload) return false; }
                    else if (command.ProjectilePrefab.GetComponent<ProjectileBase>() == null) return false;
                }
            return true;
        }
        private static bool ValidNumbers(in EmitCommand cmd)
        {
            // Prefab 合法不代表 Rigidbody 配置合法。最终快照必须在产生任何对象前检查，NaN 不能进入 Physics。
            return WandRuntimeState.IsFinite(cmd.Speed) && cmd.Speed >= 0 && WandRuntimeState.IsFinite(cmd.Damage)
                && WandRuntimeState.IsFinite(cmd.ExplosionDamage) && WandRuntimeState.IsFinite(cmd.FireFieldDamagePerTick)
                && WandRuntimeState.IsFinite(cmd.FireFieldTickInterval) && WandRuntimeState.IsFinite(cmd.FireFieldDuration)
                && WandRuntimeState.IsFinite(cmd.SpreadDegrees) && WandRuntimeState.IsFinite(cmd.HomingRadius)
                && WandRuntimeState.IsFinite(cmd.HomingDuration) && WandRuntimeState.IsFinite(cmd.HomingTurnRateDegrees)
                && WandRuntimeState.IsFinite(cmd.OrbitRadius) && WandRuntimeState.IsFinite(cmd.OrbitAngularSpeedDegrees)
                && WandRuntimeState.IsFinite(cmd.OrbitPhaseOffsetDegrees) && WandRuntimeState.IsFinite(cmd.OrbitPlaneTiltDegrees)
                && WandRuntimeState.IsFinite(cmd.SkyfallHeight) && WandRuntimeState.IsFinite(cmd.SkyfallBackOffset)
                && WandRuntimeState.IsFinite(cmd.LandingSiteDuration) && cmd.LandingSiteDuration >= 0
                && WandRuntimeState.IsFinite(cmd.PayloadDelaySeconds) && cmd.PayloadDelaySeconds >= 0
                && cmd.PayloadTrigger <= PayloadTriggerMode.AfterDelay && cmd.DamageType <= DamageType.True
                && cmd.MotionMode <= ProjectileMotionMode.Orbit;
        }
        private bool TryTicket(in CastResourceRequestedEvent request, out CastResourceReservation ticket)
        {
            ticket = null;
            return request.TicketId != null && _tickets.TryGetValue(request.TicketId, out ticket)
                && ticket.SourceWandId == request.SourceWandId && ticket.CallerId == request.CallerId
                && ticket.ProgramRevision == request.ProgramRevision && request.LayerIndex >= 0 && request.LayerIndex < ticket.Plan.LayerCount;
        }
        private OwnedWandFailureReason PrepareLayer(in CastResourceRequestedEvent request)
        {
            if (IsPending) return OwnedWandFailureReason.ResourceBusy;
            if (!TryTicket(request, out CastResourceReservation ticket) || request.LayerIndex == 0 || !ticket.RootCommitted)
                return OwnedWandFailureReason.BudgetConsumed;
            int layer = request.LayerIndex;
            if (ticket.Attempted[layer] || !ticket.Attempted[ticket.Plan.GetLayer(layer).Parent]) return OwnedWandFailureReason.BudgetConsumed;
            // 失败尝试也消耗预算，不能等 Mana 回满后用同一个回调重复重试。
            ticket.Attempted[layer] = true;
            if (!_wands.ReadOnly.TryGetState(ticket.SourceWandId, out WandStateSnapshot state)) { CancelDescendants(ticket, layer); Collect(ticket); return OwnedWandFailureReason.UnknownSource; }
            if (ticket.Plan.GetLayer(layer).ManaCost > state.CurrentMana) { CancelDescendants(ticket, layer); Collect(ticket); return OwnedWandFailureReason.InsufficientMana; }
            if (!_profile.Items.TryPrepareUses(new Dictionary<string, int>(), out _prepared)) { CancelDescendants(ticket, layer); Collect(ticket); return OwnedWandFailureReason.ResourceBusy; }
            _materialLayer = MaterialOutputs.Prepare(ticket.Attachments[layer], ticket.Cards[layer], ticket.MaterialWorldId);
            _pending = ticket; _pendingLayer = layer; return OwnedWandFailureReason.None;
        }
        private OwnedWandFailureReason Finish(in CastResourceRequestedEvent request, bool commit)
        {
            if (!TryTicket(request, out CastResourceReservation ticket) || !ReferenceEquals(_pending, ticket) || request.LayerIndex != _pendingLayer)
                return OwnedWandFailureReason.InvalidRequest;
            bool paid = false; OwnedWandFailureReason reason = OwnedWandFailureReason.None;
            if (commit)
            {
                var payment = _wands.Execute(new WandResourceRequestedEvent { InventoryId = _inventoryId, SourceWandId = ticket.SourceWandId,
                    CallerId = _paymentCaller, RequestId = ++_paymentSequence, ProgramRevision = ticket.ProgramRevision,
                    IsRootCast = _pendingLayer == 0, ManaCost = ticket.Plan.GetLayer(_pendingLayer).ManaCost });
                paid = payment.Success; reason = payment.Reason;
            }
            bool materialChanged = paid && _materialLayer != null && _materialLayer.HasMaterial;
            if (paid) _materialLayer?.Commit();
            _profile.Items.CompletePrepared(_prepared, paid, materialChanged); _prepared = null; _pending = null; _materialLayer = null;
            if (_pendingLayer == 0)
            {
                if (!paid) _tickets.Remove(ticket.TicketId); else { ticket.RootCommitted = true; Collect(ticket); }
            }
            else { if (!paid) CancelDescendants(ticket, _pendingLayer); Collect(ticket); }
            return commit ? reason : OwnedWandFailureReason.None;
        }
        private OwnedWandFailureReason Abandon(in CastResourceRequestedEvent request)
        {
            if (!TryTicket(request, out CastResourceReservation ticket) || request.LayerIndex == 0) return OwnedWandFailureReason.BudgetConsumed;
            // 父投射物销毁时只取消未触发的子树，已经执行的层所产生的新父对象继续持有自己的预算。
            if (!ticket.Attempted[request.LayerIndex]) { ticket.Attempted[request.LayerIndex] = true; CancelDescendants(ticket, request.LayerIndex); }
            if (!ReferenceEquals(_pending, ticket)) Collect(ticket); return OwnedWandFailureReason.None;
        }
        private static void CancelDescendants(CastResourceReservation ticket, int parent)
        {
            CastExecutionLayer layer = ticket.Plan.GetLayer(parent);
            for (int i = 0; i < layer.CommandCount; i++)
            { int child = layer.GetChild(i); if (child < 0 || ticket.Attempted[child]) continue;
                ticket.Attempted[child] = true; CancelDescendants(ticket, child); }
        }
        private void Collect(CastResourceReservation ticket)
        {
            for (int i = 0; i < ticket.Attempted.Length; i++) if (!ticket.Attempted[i]) return;
            _tickets.Remove(ticket.TicketId);
        }
        public void CancelAll()
        {
            if (_prepared != null) _profile.Items.CompletePrepared(_prepared, false);
            _prepared = null; _pending = null; _materialLayer = null; _tickets.Clear();
        }
        private void CancelCaller(string caller)
        {
            // 施法者可能先于 Session 消失：它停掉的延迟 Coroutine 没有父投射物可发 Finished。
            if (_pending != null && _pending.CallerId == caller)
            { _profile.Items.CompletePrepared(_prepared, false); _prepared = null; _pending = null; _materialLayer = null; }
            var cancelled = new List<string>();
            foreach (CastResourceReservation ticket in _tickets.Values) if (ticket.CallerId == caller) cancelled.Add(ticket.TicketId);
            for (int i = 0; i < cancelled.Count; i++) _tickets.Remove(cancelled[i]);
        }
        private sealed class View : ICastResourceReadOnly
        {
            private readonly CastResourceService _owner;
            public View(CastResourceService owner) { _owner = owner; }
            public int ActiveReservations => _owner._tickets.Count;
            public bool TryGetMaterialOutput(string ticket, int layerIndex, int command, out SpellMaterialOutputSnapshot output)
            {
                output = default;
                if (_owner._pending == null || _owner._pending.TicketId != ticket || _owner._pendingLayer != layerIndex
                    || _owner._materialLayer == null || command < 0 || command >= _owner._materialLayer.Snapshots.Length) return false;
                output = _owner._materialLayer.Snapshots[command]; return true;
            }
            public bool TryGetLayer(string ticket, int layerIndex, out CastExecutionLayer layer)
            {
                layer = null; if (ticket == null || !_owner._tickets.TryGetValue(ticket, out CastResourceReservation reservation)
                    || layerIndex < 0 || layerIndex >= reservation.Plan.LayerCount) return false;
                layer = reservation.Plan.GetLayer(layerIndex); return true;
            }
        }
    }
}
