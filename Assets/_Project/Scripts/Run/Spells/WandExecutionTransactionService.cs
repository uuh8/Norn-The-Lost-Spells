using System;
using System.Collections.Generic;
using Game.Core;
using Game.Combat;
using Game.Skills;

namespace Game.Run
{
    /// <summary>显式V2接缝；在独立服务Sandbox验证后才接正式角色请求。</summary>
    public sealed class WandExecutionTransactionService
    {
        private readonly SinglePlayerProfile _profile;
        private readonly WandLoadoutService _wands;
        private readonly RaidSpellEligibility _eligibility;
        private readonly string _inventoryId;
        private readonly bool _allowPeacefulExperiments;
        private readonly SpellProgramMachine _machine = new SpellProgramMachine();
        private readonly Dictionary<string, Ticket> _tickets = new Dictionary<string, Ticket>(StringComparer.Ordinal);
        // 会话不无限积累每发收据。窗口外旧请求仍拒绝，绝不重新支付；窗口内重试返回原结果。
        public const int ReceiptWindow = 256;
        private readonly Dictionary<(string Caller, ulong Request), Receipt> _receipts = new Dictionary<(string, ulong), Receipt>();
        private readonly Queue<(string Caller, ulong Request)> _receiptOrder = new Queue<(string, ulong)>(ReceiptWindow);
        private readonly Dictionary<string, ulong> _latestRequests = new Dictionary<string, ulong>(StringComparer.Ordinal);
        private Ticket _pending;
        private int _pendingLayer;
        private ItemOwnershipStore.PreparedInventory _prepared;
        private SpellMaterialOutputService.PreparedLayer _materialLayer;
        private struct Receipt { internal CastResourceRequestedEvent Request; internal CastResourceResultEvent Result; }
        private sealed class Ticket
        {
            internal string Id, Wand, Caller, World;
            internal uint Revision, InventoryVersion;
            internal WandExecutionSnapshot Before;
            internal SpellExecutionInputs Inputs;
            internal SpellBuildBuffer Candidate;
            internal CastExecutionPlan RuntimePlan;
            internal SpellBuildSummary Summary;
            internal Dictionary<string, int> Uses;
            internal SpellAttachmentState[][] Attachments;
            internal string[][] Cards;
            internal WandLayerResourceStatus[] Layers;
            internal WandExecutionReplay Replay;
            internal bool ExpectWorldObservations;
        }
        public WandExecutionReplayArchive Archive { get; }
        public SpellMaterialOutputService MaterialOutputs { get; }
        public ICastResourceReadOnly ReadOnly { get; }
        public bool IsPending => _pending != null;
        public int ActiveReservations => _tickets.Count;
        public SpellExecutionTrace LastRejectedTrace { get; private set; }
        public WandExecutionTransactionService(SinglePlayerProfile profile, WandLoadoutService wands,
            RaidSpellEligibility eligibility, string inventoryId, WandExecutionReplayArchive archive,
            SpellMaterialOutputService materials = null, bool allowPeacefulExperiments = false)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _wands = wands ?? throw new ArgumentNullException(nameof(wands)); _eligibility = eligibility;
            _inventoryId = inventoryId; Archive = archive ?? throw new ArgumentNullException(nameof(archive));
            _allowPeacefulExperiments = allowPeacefulExperiments;
            MaterialOutputs = materials ?? new SpellMaterialOutputService(profile, inventoryId);
            ReadOnly = new View(this);
        }
        public bool TryGetState(string id, out WandExecutionSnapshot state) => _wands.TryGetExecution(id, out state);
        public CastResourceResultEvent Execute(in CastResourceRequestedEvent request)
        {
            var result = new CastResourceResultEvent { InventoryId = _inventoryId, SourceWandId = request.SourceWandId,
                CallerId = request.CallerId, RequestId = request.RequestId, TicketId = request.TicketId, Operation = request.Operation,
                Reason = OwnedWandFailureReason.InvalidRequest };
            if (request.InventoryId != _inventoryId || string.IsNullOrWhiteSpace(request.CallerId) || request.RequestId == 0) return result;
            var key = (request.CallerId, request.RequestId);
            if (_receipts.TryGetValue(key, out var receipt))
                return SameRequest(request, receipt.Request) ? receipt.Result : result;
            if (_latestRequests.TryGetValue(request.CallerId, out ulong last) && request.RequestId <= last) return result;
            switch (request.Operation)
            {
                case CastResourceOperation.PrepareRoot: result.Reason = PrepareRoot(request, out string id); result.TicketId = id; break;
                case CastResourceOperation.PrepareLayer: result.Reason = PrepareLayer(request); break;
                case CastResourceOperation.Complete: result.Reason = Finish(request, true); break;
                case CastResourceOperation.Cancel: result.Reason = Finish(request, false); break;
                case CastResourceOperation.Abandon: result.Reason = Abandon(request); break;
                case CastResourceOperation.AbandonCaller: CancelCaller(request.CallerId); result.Reason = OwnedWandFailureReason.None; break;
            }
            result.Success = result.Reason == OwnedWandFailureReason.None;
            _latestRequests[request.CallerId] = request.RequestId;
            if (_receiptOrder.Count == ReceiptWindow) _receipts.Remove(_receiptOrder.Dequeue());
            _receipts.Add(key, new Receipt { Request = request, Result = result }); _receiptOrder.Enqueue(key); return result;
        }
        private static bool SameRequest(in CastResourceRequestedEvent a, in CastResourceRequestedEvent b) =>
            a.RequestId == b.RequestId && a.SourceWandId == b.SourceWandId && a.TicketId == b.TicketId && a.Operation == b.Operation
            && a.ProgramRevision == b.ProgramRevision && a.ExecutionRevision == b.ExecutionRevision && a.LayerIndex == b.LayerIndex
            && a.InventoryVersion == b.InventoryVersion && a.ResourceVersion == b.ResourceVersion && a.MaterialWorldId == b.MaterialWorldId;
        private OwnedWandFailureReason PrepareRoot(in CastResourceRequestedEvent request, out string ticketId)
        {
            ticketId = null; LastRejectedTrace = null;
            if (request.LayerIndex != 0) return OwnedWandFailureReason.InvalidRequest;
            if (IsPending || _profile.Items.IsResourceBusy) return OwnedWandFailureReason.ResourceBusy;
            if (!TryGetState(request.SourceWandId, out var state) || !_wands.ReadOnly.TryGetState(request.SourceWandId, out var resources))
                return OwnedWandFailureReason.InvalidProgram;
            if (!resources.Carried) return OwnedWandFailureReason.NotCarried;
            if (state.Program.Revision != request.ProgramRevision) return OwnedWandFailureReason.StaleProgram;
            if (state.Draw.ExecutionRevision != request.ExecutionRevision) return OwnedWandFailureReason.StaleExecution;
            if (state.ResourceVersion != request.ResourceVersion || _profile.Items.Version != request.InventoryVersion)
                return OwnedWandFailureReason.StaleResources;
            if (resources.CooldownRemaining > 0) return OwnedWandFailureReason.Cooldown;
            int count = state.Program.Count; var uses = new int[count]; var eligible = new bool[count];
            for (int i = 0; i < count; i++)
            {
                var instruction = state.Program.Instructions[i];
                if(instruction.IsIntrinsic){uses[i]=-1;eligible[i]=true;continue;}
                if (!_profile.Items.TryGet(instruction.InstanceId, out var item)
                    || !_profile.Items.TryGetLocation(item.InstanceId, out var location) || location.Kind != ItemLocationKind.WandSlot
                    || location.OwnerId != request.SourceWandId || location.SlotIndex != instruction.SourceSlot)
                    return OwnedWandFailureReason.InvalidProgram;
                bool qualification = instruction.Kind == SpellKind.Modify || instruction.Kind == SpellKind.Multicast || instruction.Kind == SpellKind.Copy;
                uses[i] = qualification || item.HasUnlimitedCharges ? -1 : item.RemainingCharges;
                eligible[i] = !qualification || item.HasUnlimitedCharges || _eligibility != null && _eligibility.CanExecuteQualification(item, _allowPeacefulExperiments);
            }
            var inputs = new SpellExecutionInputs(state.Mana, resources.MaxMana, uses, eligible, inputVersion: request.InventoryVersion);
            var candidate = new SpellBuildBuffer();
            if (!_machine.TryBuild(state.Program, state.Draw, inputs, candidate, out var summary))
            {
                LastRejectedTrace = candidate.Trace;
                return summary.Diagnostic.Error == SpellProgramError.TooComplex ? OwnedWandFailureReason.TooComplex
                    : summary.Diagnostic.Error == SpellProgramError.EmptyProgram ? OwnedWandFailureReason.EmptyProgram : OwnedWandFailureReason.InvalidProgram;
            }
            if (!ValidateGraph(candidate.Graph)) return OwnedWandFailureReason.InvalidProgram;
            var payment = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++) if (uses[i] >= 0 && uses[i] > candidate.RemainingUsesAfter[i])
                payment.Add(state.Program.Instructions[i].InstanceId, uses[i] - candidate.RemainingUsesAfter[i]);
            string id = Guid.NewGuid().ToString("N");
            if (!_wands.ReserveExecution(request.SourceWandId, id)) return OwnedWandFailureReason.ResourceBusy;
            if (!_profile.Items.TryPrepareUses(payment, out _prepared))
            { _wands.ReleaseExecution(request.SourceWandId, id); return OwnedWandFailureReason.ResourceBusy; }
            var ticket = new Ticket { Id = id, Wand = request.SourceWandId, Caller = request.CallerId, Revision = request.ProgramRevision,
                World = request.MaterialWorldId, Before = state, Inputs = inputs, Candidate = candidate, Summary = summary,
                Uses = payment, InventoryVersion = request.InventoryVersion, Layers = new WandLayerResourceStatus[candidate.Graph.Scopes.Count],
                RuntimePlan = CastExecutionPlan.FromGraph(candidate.Graph),ExpectWorldObservations=request.UseV2 };
            CaptureAttachments(ticket);
            ticket.Layers[0] = WandLayerResourceStatus.Prepared; _tickets.Add(id, ticket);
            _pending = ticket; _pendingLayer = 0;
            _materialLayer = MaterialOutputs.Prepare(ticket.Attachments[0], ticket.Cards[0], ticket.World);
            ticketId = id; return OwnedWandFailureReason.None;
        }
        private static bool ValidateGraph(ShotExecutionGraph graph)
        {
            for (int i = 0; i < graph.Outputs.Count; i++)
            {
                var output = graph.Outputs[i]; var command = output.Command;
                if (command.ProjectilePrefab == null || !CastResourceService.ValidNumbers(command)) return false;
                if (command.SpawnMode == SpellSpawnMode.StaticAtPoint)
                { if (command.ProjectilePrefab.GetComponentInChildren<ProjectileShield>(true) == null || output.PayloadScopeId >= 0) return false; }
                else if (command.SpawnMode > SpellSpawnMode.StaticAtPoint || command.ProjectilePrefab.GetComponent<ProjectileBase>() == null) return false;
            }
            return true;
        }
        private void CaptureAttachments(Ticket ticket)
        {
            var graph = ticket.Candidate.Graph;
            ticket.Attachments = new SpellAttachmentState[graph.Scopes.Count][]; ticket.Cards = new string[graph.Scopes.Count][];
            for (int layer = 0; layer < graph.Scopes.Count; layer++)
            {
                var outputs = graph.Scopes[layer].OutputIds; ticket.Attachments[layer] = new SpellAttachmentState[outputs.Count];
                ticket.Cards[layer] = new string[outputs.Count];
                for (int i = 0; i < outputs.Count; i++)
                {
                    string card = graph.Outputs[outputs[i]].SourceInstanceId; ticket.Cards[layer][i] = card;
                    _profile.Items.TryGet(card, out var item); ticket.Attachments[layer][i] = item?.Attachment;
                }
            }
        }
        private bool TryTicket(in CastResourceRequestedEvent request, out Ticket ticket)
        {
            ticket = null;
            return request.TicketId != null && _tickets.TryGetValue(request.TicketId, out ticket)
                && request.SourceWandId == ticket.Wand && request.CallerId == ticket.Caller && request.ProgramRevision == ticket.Revision
                && request.LayerIndex >= 0 && request.LayerIndex < ticket.Layers.Length;
        }
        private OwnedWandFailureReason PrepareLayer(in CastResourceRequestedEvent request)
        {
            if (IsPending || _profile.Items.IsResourceBusy) return OwnedWandFailureReason.ResourceBusy;
            if (!TryTicket(request, out var ticket) || ticket.Replay == null || request.LayerIndex == 0) return OwnedWandFailureReason.BudgetConsumed;
            int layer = request.LayerIndex; int parent = ticket.Candidate.Graph.Scopes[layer].ParentScopeId;
            if (ticket.Layers[layer] != WandLayerResourceStatus.Unattempted || ticket.Layers[parent] != WandLayerResourceStatus.Committed)
                return OwnedWandFailureReason.BudgetConsumed;
            // 子层只预约自己的材料；不再查询当前法杖/卡实例，也不重复支付Mana。
            if (!_profile.Items.TryPrepareUses(new Dictionary<string, int>(), out _prepared)) return OwnedWandFailureReason.ResourceBusy;
            ticket.Layers[layer] = WandLayerResourceStatus.Prepared; _pending = ticket; _pendingLayer = layer;
            _materialLayer = MaterialOutputs.Prepare(ticket.Attachments[layer], ticket.Cards[layer], ticket.World);
            return OwnedWandFailureReason.None;
        }
        private OwnedWandFailureReason Finish(in CastResourceRequestedEvent request, bool commit)
        {
            if (!TryTicket(request, out var ticket) || !ReferenceEquals(_pending, ticket) || request.LayerIndex != _pendingLayer)
                return OwnedWandFailureReason.InvalidRequest;
            bool root = _pendingLayer == 0;
            OwnedWandFailureReason failure = OwnedWandFailureReason.None;
            if (commit && !_profile.Items.OwnsPreparation(_prepared))
            { commit = false; failure = OwnedWandFailureReason.StaleResources; }
            if (commit && root && !_wands.OwnsExecutionReservation(ticket.Wand, ticket.Id))
            { commit = false; failure = OwnedWandFailureReason.StaleExecution; }
            if (commit)
            {
                // 持有库存与本杖预约时提交，不向EventBus发布任何中间状态。
                bool materialChanged = _materialLayer.HasMaterial; _materialLayer.Commit();
                _profile.Items.CompletePrepared(_prepared, true, materialChanged);
                ticket.Layers[_pendingLayer] = WandLayerResourceStatus.Committed;
                if (root)
                {
                    _wands.CommitExecution(ticket.Wand, ticket.Id, ticket.Candidate, ticket.Summary);
                    _wands.TryGetExecution(ticket.Wand, out var after);
                    ticket.Replay = new WandExecutionReplay(ticket.Id, ticket.Wand, ticket.Caller, ticket.Candidate,
                        ticket.Before.Draw, after.Draw, ticket.Inputs, ticket.Summary, ticket.InventoryVersion, _profile.Items.Version, ticket.Uses, ticket.Layers);
                    Archive.Add(ticket.Replay);
                }
            }
            else
            {
                _profile.Items.CompletePrepared(_prepared, false);
                CancelDescendants(ticket, _pendingLayer); ticket.Layers[_pendingLayer] = WandLayerResourceStatus.Cancelled;
                if (root) _wands.ReleaseExecution(ticket.Wand, ticket.Id);
            }
            _prepared = null; _pending = null; _materialLayer = null; Collect(ticket);
            return failure;
        }
        private static void CancelDescendants(Ticket ticket, int parent)
        {
            var graph = ticket.Candidate.Graph;
            for (int i = 1; i < graph.Scopes.Count; i++)
                if (graph.Scopes[i].ParentScopeId == parent && ticket.Layers[i] == WandLayerResourceStatus.Unattempted)
                { ticket.Layers[i] = WandLayerResourceStatus.Cancelled; CancelDescendants(ticket, i); }
        }
        private OwnedWandFailureReason Abandon(in CastResourceRequestedEvent request)
        {
            if (!TryTicket(request, out var ticket) || request.LayerIndex == 0) return OwnedWandFailureReason.BudgetConsumed;
            int layer = request.LayerIndex;
            if (ticket.Layers[layer] == WandLayerResourceStatus.Unattempted)
            { ticket.Layers[layer] = WandLayerResourceStatus.Cancelled; CancelDescendants(ticket, layer); }
            Collect(ticket); return OwnedWandFailureReason.None;
        }
        private void Collect(Ticket ticket)
        {
            for (int i = 0; i < ticket.Layers.Length; i++)
                if (ticket.Layers[i] == WandLayerResourceStatus.Unattempted || ticket.Layers[i] == WandLayerResourceStatus.Prepared) return;
            if(ticket.ExpectWorldObservations && ticket.Replay!=null)
                for(int i=0;i<ticket.Candidate.Graph.Outputs.Count;i++)
                    if(ticket.Layers[ticket.Candidate.Graph.Outputs[i].ScopeId]==WandLayerResourceStatus.Committed && !ticket.Replay.ReleasedOutputs[i])return;
            _tickets.Remove(ticket.Id);
        }
        public bool Observe(in SpellExecutionObservedEvent observation)
        {
            if(observation.InventoryId!=_inventoryId || observation.TicketId==null)return false;
            _tickets.TryGetValue(observation.TicketId,out var ticket);
            WandExecutionReplay replay=ticket?.Replay;
            if(replay==null)Archive.TryGetTicket(observation.TicketId,out replay);
            if(replay==null||replay.WorldStatus==WandWorldExecutionStatus.Cancelled||replay.CallerId!=observation.CallerId||replay.SourceWandId!=observation.SourceWandId
                ||replay.Graph.Program.Revision!=observation.ProgramRevision||observation.Kind!=SpellExecutionObservationKind.OutputReleased)return false;
            bool accepted=replay.Observe(observation.LayerId,observation.OutputId,observation.SimulationTime);
            if(accepted && ticket!=null)Collect(ticket);return accepted;
        }
        public bool TryGetGraph(string ticket, out ShotExecutionGraph graph)
        { graph = null; if (ticket == null || !_tickets.TryGetValue(ticket, out var reservation)) return false;
            graph = reservation.Candidate.Graph; return true; }
        public bool TryGetMaterialOutput(string ticket, int layer, int command, out SpellMaterialOutputSnapshot output)
        {
            output = default;
            if (_pending == null || _pending.Id != ticket || _pendingLayer != layer || command < 0 || command >= _materialLayer.Snapshots.Length) return false;
            output = _materialLayer.Snapshots[command]; return true;
        }
        private void CancelCaller(string caller)
        {
            var owned = new List<Ticket>();
            foreach (var ticket in _tickets.Values) if (ticket.Caller == caller) owned.Add(ticket);
            for (int i = 0; i < owned.Count; i++) CancelTicket(owned[i]);
        }
        private void CancelTicket(Ticket ticket)
        {
            if(ticket.Replay!=null && ticket.Replay.WorldStatus==WandWorldExecutionStatus.NotObserved)ticket.Replay.WorldStatus=WandWorldExecutionStatus.Cancelled;
            if (ReferenceEquals(_pending, ticket))
            { _profile.Items.CompletePrepared(_prepared, false); _prepared = null; _pending = null; _materialLayer = null; }
            _wands.ReleaseExecution(ticket.Wand, ticket.Id);
            for (int i = 0; i < ticket.Layers.Length; i++)
                if (ticket.Layers[i] == WandLayerResourceStatus.Unattempted || ticket.Layers[i] == WandLayerResourceStatus.Prepared)
                    ticket.Layers[i] = WandLayerResourceStatus.Cancelled;
            _tickets.Remove(ticket.Id);
        }
        public void CancelAll()
        {
            var tickets = new List<Ticket>(_tickets.Values);
            for (int i = 0; i < tickets.Count; i++) CancelTicket(tickets[i]);
        }
        private sealed class View : ICastResourceReadOnly
        {
            private readonly WandExecutionTransactionService _owner;
            internal View(WandExecutionTransactionService owner) { _owner = owner; }
            public int ActiveReservations => _owner.ActiveReservations;
            public bool TryGetLayer(string ticket, int layerIndex, out CastExecutionLayer layer)
            {
                layer = null;
                if (ticket == null || !_owner._tickets.TryGetValue(ticket, out var reservation)
                    || layerIndex < 0 || layerIndex >= reservation.RuntimePlan.LayerCount) return false;
                layer = reservation.RuntimePlan.GetLayer(layerIndex); return true;
            }
            public bool TryGetMaterialOutput(string ticket, int layerIndex, int command, out SpellMaterialOutputSnapshot output)
                => _owner.TryGetMaterialOutput(ticket, layerIndex, command, out output);
        }
    }
}
