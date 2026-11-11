using System;
using System.Collections.Generic;
using Game.Core;
using Game.Skills;

namespace Game.Run
{
    public sealed partial class OnlineOwnedWorkspace
    {
        private bool _practiceContinuityRestored;
        public int ReplayCount => !_disposed ? _casts.Archive.Count : 0;
        public WandExecutionReplay GetReplayOldestFirst(int index) => !_disposed ? _casts.Archive.GetOldestFirst(index) : null;
        public bool TryGetLatestReplay(string wandId, out WandExecutionReplay replay)
        { replay = null; return !_disposed && _casts.Archive.TryGetLatest(wandId, out replay); }

        /// <summary>
        /// 只在研究Open/编辑/可靠退出时离散捕获。TryGetState与原研究台打开相同，至多物化初始牌堆，
        /// 不执行Machine、不补Mana、不推进随机种子。准备中的事务不能被读取为一半新一半旧的研究事实。
        /// </summary>
        public bool TryCaptureResearchState(out OnlineWandResearchStateSnapshot snapshot)
        {
            snapshot = null;
            if (_disposed || _profile.Items.IsResourceBusy || _casts.IsPending) return false;
            _wands.Synchronize();
            var values = new List<OnlineWandResearchEntrySnapshot>();
            foreach (var owned in _profile.Items)
            {
                if (!_profile.Items.Catalog.TryGetWandStats(owned.Item, out var stats)
                    || !OnlineWandResearchReadModel.Accessible(_profile.Items, owned.Location, PlayerId)) continue;
                if (!_casts.TryGetState(owned.Item.InstanceId, out var execution)
                    || !_wands.ReadOnly.TryGetState(owned.Item.InstanceId, out var resources)
                    || !_wands.ReadOnly.TryGetProgram(owned.Item.InstanceId, out var projection)) return false;
                var installed = new string[projection.Program.Count];
                for (int i = 0; i < installed.Length; i++) installed[i] = projection.Program.GetInstanceId(i) ?? "";
                var uses = new int[execution.Program.Count]; var eligible = new bool[uses.Length];
                for (int i = 0; i < uses.Length; i++)
                {
                    var instruction = execution.Program.Instructions[i];
                    if (instruction.IsIntrinsic) { uses[i] = -1; eligible[i] = true; continue; }
                    if (!_profile.Items.TryGet(instruction.InstanceId, out var card)) return false;
                    bool qualification = instruction.Kind == SpellKind.Modify || instruction.Kind == SpellKind.Multicast || instruction.Kind == SpellKind.Copy;
                    uses[i] = qualification || card.HasUnlimitedCharges ? -1 : card.RemainingCharges;
                    eligible[i] = !qualification || _eligibility.CanExecuteQualification(card, _isPeacefulPractice);
                }
                var inputs = new SpellExecutionInputs(execution.Mana, resources.MaxMana, uses, eligible, inputVersion: InventoryVersion);
                var contents = new string[execution.Program.Count]; int fixedAt = 0;
                for (int i = 0; i < contents.Length; i++)
                { var instruction = execution.Program.Instructions[i];
                    if (instruction.IsIntrinsic) contents[i] = stats.AlwaysCastContentIds[fixedAt++];
                    else { _profile.Items.TryGet(instruction.InstanceId, out var card); contents[i] = card.ContentId; } }
                values.Add(new OnlineWandResearchEntrySnapshot { WandId = owned.Item.InstanceId,
                    ProgramRevision = projection.ProgramRevision, Stats = OnlineWandResearchReadModel.CaptureStats(stats), InstalledIds = installed,
                    Program = CaptureProgram(execution.Program, stats, contents),
                    Mana = execution.Mana, Ready = resources.CooldownRemaining, Cast = execution.CastRemaining, Reload = execution.ReloadRemaining,
                    Draw = OnlineWandResearchReadModel.CaptureDraw(execution.Draw), Inputs = OnlineWandResearchReadModel.CaptureInputs(inputs) });
            }
            var library = new List<OnlineWandLibraryCardSnapshot>();
            foreach (var owned in _profile.Items)
                if (owned.Location.Kind != ItemLocationKind.WandSlot && _profile.Items.Catalog.TryGet(owned.Item.ContentId, out var definition)
                    && definition.Kind == ItemKind.Spell && OnlineWandResearchReadModel.Accessible(_profile.Items, owned.Location, PlayerId))
                    library.Add(new OnlineWandLibraryCardSnapshot { InstanceId = owned.Item.InstanceId, ContentId = owned.Item.ContentId });
            var history = new OnlineWandExecutionReplaySnapshot[_casts.Archive.Count];
            for (int i = 0; i < history.Length; i++) history[i] = CaptureReplay(_casts.Archive.GetOldestFirst(i));
            var candidate = new OnlineWandResearchStateSnapshot { SchemaVersion = OnlineWandResearchStateSnapshot.CurrentSchemaVersion,
                PlayerId = PlayerId, InventoryId = InventoryId, GameplayConfigHash = _gameplayHash,
                SelectedWandId = _wands.ReadOnly.SelectedWandId ?? "", InventoryVersion = InventoryVersion,
                ResourceVersion = Math.Max(1u, _wands.ReadOnly.ResourceVersion), SpellUseVersion = Math.Max(1u, _eligibility.Version),
                Wands = values.ToArray(), Library = library.ToArray(), Replays = history };
            if (!OnlineWandResearchStateCodec.Validate(candidate, out _)) return false;
            snapshot = candidate; return true;
        }

        /// <summary>
        /// 只供本次可信登录session的Home重新入局调用一次。完整候选先编译/校验，再恢复原资源服务和诊断Archive。
        /// 旧InventoryId/InventoryVersion只是历史证据，新DS仍使用新Entry域；同Wand ID、固定roll和安装闭包必须逐项相同。
        /// 不能用于重连、已经Tick/发过命令的工作区，也不能把历史Replay恢复为活Ticket或世界投射物。
        /// </summary>
        public bool TryRestorePracticeContinuity(OnlineWandResearchStateSnapshot snapshot, out string error)
        {
            error = null;
            if (_disposed || !_isPeacefulPractice || _practiceContinuityRestored || _hasTick || _inventoryCommandCount != 0
                || InventoryVersion != _entryInventoryVersion || _currencyVersion != _entryCurrencyVersion || _selectionVersion != 1
                || _attachmentRequestId != 0 || _containerRequestId != 0 || HasMemberExitFence
                || _profile.Items.IsResourceBusy || _casts.IsPending || _casts.ActiveReservations != 0 || _casts.Archive.Count != 0
                || snapshot == null || snapshot.PlayerId != PlayerId || snapshot.GameplayConfigHash != _gameplayHash)
                return OnlineWandResearchStateCodec.Fail("连续性只允许同账号/配置的全新和平Owner恢复一次。", out error);
            // 先验证每个保存条目自身的Catalog/ProgramIndex/牌堆，再交给原RestoreContinuity逐实例求交集。
            // Hub合法新增、售出或换程序不让整个Home永久卡住；只有未变的同实例/roll/安装序列接续旧消耗。
            if (!OnlineWandResearchReadModel.TryCreateContinuity(snapshot, _profile.Items.Catalog, out var candidate, out error)) return false;
            // 上方所有失败路径都未改资源；这里没有事件/支付/外部回调，原内部Restore只安装同值候选。
            _wands.RestoreContinuity(candidate.Continuity);
            _casts.Archive.Clear();
            for (int i = 0; i < candidate.Archive.Count; i++) _casts.Archive.Add(candidate.Archive.GetOldestFirst(i));
            _practiceContinuityRestored = true; return true;
        }

        private static OnlineWandRecordedProgramSnapshot CaptureProgram(SpellProgram program, WandStats stats, IReadOnlyList<string> contents)
        {
            var cards = new OnlineWandRecordedCardSnapshot[program.Count];
            for (int i = 0; i < cards.Length; i++) cards[i] = new OnlineWandRecordedCardSnapshot
            { InstanceId = program.Instructions[i].InstanceId, ContentId = contents[i], SourceSlot = program.Instructions[i].SourceSlot };
            return new OnlineWandRecordedProgramSnapshot { ProgramRevision = program.Revision,
                Stats = OnlineWandResearchReadModel.CaptureStats(stats), Cards = cards };
        }
        private static OnlineWandExecutionReplaySnapshot CaptureReplay(WandExecutionReplay replay)
        {
            var graph = replay.Graph; var scopes = new OnlineRecordedSpellScopeSnapshot[graph.Scopes.Count];
            for (int i = 0; i < scopes.Length; i++)
            {
                var s = graph.Scopes[i]; var outputIds = new int[s.OutputIds.Count];
                for (int j = 0; j < outputIds.Length; j++) outputIds[j] = s.OutputIds[j];
                IReadOnlyList<SpellParameterContribution> original = outputIds.Length == 0 ? Array.Empty<SpellParameterContribution>()
                    : graph.Outputs[outputIds[0]].Parameters.Contributions;
                var contributions = new OnlineRecordedSpellContributionSnapshot[original.Count];
                for (int j = 0; j < original.Count; j++) contributions[j] = new OnlineRecordedSpellContributionSnapshot
                { CardIndex = original[j].CardIndex, StepId = original[j].StepId };
                scopes[i] = new OnlineRecordedSpellScopeSnapshot { Id = s.Id, ParentScopeId = s.ParentScopeId,
                    OwnerOutputId = s.OwnerOutputId, ClosedAtStep = s.ClosedAtStep, CastDelayDelta = s.CastDelayDelta,
                    FinalModifiers = SpellExecutionReadModelFactory.CaptureModifier(s.FinalModifiers), OutputIds = outputIds, Contributions = contributions };
            }
            var outputs = new OnlineRecordedSpellOutputSnapshot[graph.Outputs.Count];
            for (int i = 0; i < outputs.Length; i++)
            {
                var o = graph.Outputs[i]; outputs[i] = new OnlineRecordedSpellOutputSnapshot { Id = o.Id, ScopeId = o.ScopeId,
                    SourceCardIndex = o.SourceCardIndex, SourceInstanceId = o.SourceInstanceId, PayloadScopeId = o.PayloadScopeId,
                    RegisteredAtStep = o.RegisteredAtStep, Command = SpellExecutionReadModelFactory.CaptureCommand(o.Command) };
            }
            var trace = new OnlineRecordedSpellTraceStepSnapshot[replay.Trace.Steps.Count];
            for (int i = 0; i < trace.Length; i++)
            {
                var t = replay.Trace.Steps[i]; trace[i] = new OnlineRecordedSpellTraceStepSnapshot { StepId = t.StepId, Kind = (byte)t.Kind,
                    ScopeId = t.ScopeId, CardIndex = t.CardIndex, RequestId = t.RequestId, ParentRequestId = t.ParentRequestId, OutputId = t.OutputId,
                    CauseCardIndex = t.CauseCardIndex, TargetPosition = t.TargetPosition, InvocationMode = (byte)t.InvocationMode,
                    From = (byte)t.From, To = (byte)t.To, Error = (byte)t.Error, ManaBefore = t.ManaBefore, ManaAfter = t.ManaAfter,
                    UsesBefore = t.UsesBefore, UsesAfter = t.UsesAfter };
            }
            var paid = new OnlineWandUseReceiptSnapshot[replay.UsesPaid.Count];
            for (int i = 0; i < paid.Length; i++) paid[i] = new OnlineWandUseReceiptSnapshot { InstanceId = replay.UsesPaid[i].InstanceId, Count = replay.UsesPaid[i].Count };
            var layers = new byte[graph.Scopes.Count]; for (int i = 0; i < layers.Length; i++) layers[i] = (byte)replay.GetLayerStatus(i);
            var released = new bool[graph.Outputs.Count]; for (int i = 0; i < released.Length; i++) released[i] = replay.ReleasedOutputs[i];
            var sum = replay.Summary;
            return new OnlineWandExecutionReplaySnapshot { TicketId = replay.TicketId, SourceWandId = replay.SourceWandId, CallerId = replay.CallerId,
                Program = CaptureProgram(graph.Program, replay.RecordedStats, replay.ProgramContentIds),
                AfterProgram = CaptureProgram(replay.AfterProgram, replay.RecordedStats, replay.AfterProgramContentIds),
                Before = OnlineWandResearchReadModel.CaptureDraw(replay.Before), After = OnlineWandResearchReadModel.CaptureDraw(replay.After),
                Inputs = OnlineWandResearchReadModel.CaptureInputs(replay.Inputs),
                Summary = new OnlineSpellBuildSummarySnapshot { Outcome = (byte)sum.Outcome, Error = (byte)sum.Diagnostic.Error,
                    DiagnosticSourceSlot = sum.Diagnostic.SourceSlot, DiagnosticMessage = sum.Diagnostic.Message ?? "", InstructionSteps = sum.InstructionSteps,
                    ManaAfter = sum.ManaAfter, CastInterval = sum.CastInterval, ReloadInterval = sum.ReloadInterval, NeedsReload = sum.NeedsReload },
                InventoryVersionBefore = replay.InventoryVersionBefore, InventoryVersionAfter = replay.InventoryVersionAfter,
                UsesPaid = paid, WorldStatus = (byte)replay.WorldStatus, LayerStatuses = layers, ReleasedOutputs = released,
                Scopes = scopes, Outputs = outputs, Trace = trace, TraceTotalEvents = replay.Trace.TotalEvents,
                TraceTruncated = replay.Trace.Truncated, TraceWasCaptured = replay.Trace.WasCaptured };
        }
    }
}
