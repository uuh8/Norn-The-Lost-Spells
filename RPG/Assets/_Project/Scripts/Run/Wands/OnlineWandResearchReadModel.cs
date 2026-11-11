using System;
using System.Collections.Generic;
using Game.Core;
using Game.Skills;

namespace Game.Run
{
    /// <summary>
    /// 一份完整研究候选，只有全部程序、历史和闭包通过后才交给Owner/Source一次安装。
    /// Current绑定当前Store；历史允许引用已耗尽卡，但只能引用同Hash Catalog中存在的定义。
    /// </summary>
    internal sealed class OnlineWandResearchReadModel
    {
        internal readonly Dictionary<string, WandResearchSnapshot> Wands = new Dictionary<string, WandResearchSnapshot>(StringComparer.Ordinal);
        internal readonly WandExecutionReplayArchive Archive = new WandExecutionReplayArchive();
        internal readonly WandSessionContinuity Continuity = new WandSessionContinuity();

        internal static bool TryCreate(OnlineWandResearchStateSnapshot value, ItemCatalogIndex catalog, ItemOwnershipStore store,
            IReadOnlyDictionary<string, SpellUseState> spellUses, bool peaceful, out OnlineWandResearchReadModel result, out string error)
        {
            result = null;
            if (!OnlineWandResearchStateCodec.Validate(value, out error)) return false;
            var candidate = new OnlineWandResearchReadModel(); var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var owned in store)
                if (catalog.TryGetWandStats(owned.Item, out _) && Accessible(store, owned.Location, value.PlayerId)) expected.Add(owned.Item.InstanceId);
            if (expected.Count != value.Wands.Length) return Fail("研究必须完整覆盖当前全部真实携带法杖。", out error);
            if (!TryLibrary(value, catalog, store, out var library, out error)) return false;
            foreach (var entry in value.Wands)
            {
                if (!expected.Remove(entry.WandId) || !store.TryGet(entry.WandId, out var item)
                    || !catalog.TryGetWandStats(item, out var actualStats) || !ReadStats(entry.Stats).SameAs(actualStats))
                    return Fail("研究法杖身份或固定roll与当前Store不同。", out error);
                var definitions = new SpellDefinition[actualStats.ProgramCapacity]; var ids = new string[definitions.Length];
                foreach (var card in store)
                    if (card.Location.Kind == ItemLocationKind.WandSlot && card.Location.OwnerId == entry.WandId)
                    { int slot = card.Location.SlotIndex; catalog.TryGet(card.Item.ContentId, out var definition); definitions[slot] = definition.SpellDefinition; ids[slot] = card.Item.InstanceId; }
                for (int i = 0; i < ids.Length; i++)
                    if ((ids[i] ?? "") != entry.InstalledIds[i]) return Fail("研究安装序列不是当前同一实例闭包。", out error);
                if (!Compile(definitions, ids, actualStats, entry.ProgramRevision, catalog, out var program, out error)) return false;
                if (!ValidateInputs(program, entry.Inputs, store, spellUses, peaceful, out error)
                    || !ValidateDraw(program, entry.Draw, entry.Inputs.RemainingUses, out error)) return false;
                var draw = ReadDraw(entry.Draw); var inputs = ReadInputs(entry.Inputs);
                candidate.Wands.Add(entry.WandId, new WandResearchSnapshot(new WandExecutionSnapshot(program, draw, entry.Mana,
                    entry.Cast, entry.Reload, value.ResourceVersion), inputs, library, false));
                candidate.Continuity.Entries.Add(new WandSessionContinuity.Entry { Id = entry.WandId, Stats = actualStats,
                    Revision = entry.ProgramRevision, Mana = entry.Mana, Ready = entry.Ready, Cast = entry.Cast, Reload = entry.Reload,
                    Program = program, Draw = draw, InstalledIds = ids });
            }
            if (value.SelectedWandId.Length != 0 && (!store.TryGetLocation(value.SelectedWandId, out var selected)
                || selected.Kind != ItemLocationKind.EquippedWand || selected.OwnerId != value.PlayerId))
                return Fail("连续性选择必须仍是本人真实装备杖。", out error);
            candidate.Continuity.Selected = value.SelectedWandId.Length == 0 ? null : value.SelectedWandId;
            foreach (var recorded in value.Replays)
            {
                if (!TryReplay(recorded, catalog, out var replay, out error)) return false;
                candidate.Archive.Add(replay);
            }
            result = candidate; return true;
        }

        private static bool TryLibrary(OnlineWandResearchStateSnapshot value, ItemCatalogIndex catalog, ItemOwnershipStore store,
            out SpellProgram program, out string error)
        {
            program = null; error = null; var accessible = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var owned in store)
                if (owned.Location.Kind != ItemLocationKind.WandSlot && catalog.TryGet(owned.Item.ContentId, out var definition)
                    && definition.Kind == ItemKind.Spell && Accessible(store, owned.Location, value.PlayerId))
                    accessible.Add(owned.Item.InstanceId, owned.Item.ContentId);
            if (accessible.Count != value.Library.Length) return Fail("研究散卡库不是当前可安装实例的完整闭包。", out error);
            var definitions = new SpellDefinition[value.Library.Length]; var ids = new string[definitions.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                var card = value.Library[i];
                if (!accessible.TryGetValue(card.InstanceId, out var content) || content != card.ContentId || !catalog.TryGet(content, out var definition))
                    return Fail("研究散卡库的ContentId与当前实例不同。", out error);
                definitions[i] = definition.SpellDefinition; ids[i] = card.InstanceId;
            }
            if (!SpellProgramCompiler.TryCompile(definitions, new SpellProgramCompileInputs(1, ids), out program, out _))
                return Fail("研究散卡库不能通过原编译器。", out error);
            return true;
        }
        internal static bool Accessible(ItemOwnershipStore store, ItemLocation location, string player)
        {
            int depth = 0;
            while (location.HasParentInstance)
                if (++depth > store.Count || !store.TryGetLocation(location.OwnerId, out location)) return false;
            return location.OwnerId == player && (location.Kind == ItemLocationKind.EquippedWand
                || location.Kind == ItemLocationKind.EquippedBackpack || location.Kind == ItemLocationKind.SpellStorage);
        }
        private static bool ValidateInputs(SpellProgram program, OnlineSpellExecutionInputsSnapshot input, ItemOwnershipStore store,
            IReadOnlyDictionary<string, SpellUseState> uses, bool peaceful, out string error)
        {
            error = null;
            if (program.Count != input.RemainingUses.Length) return Fail("研究输入长度与ProgramIndex不同。", out error);
            for (int i = 0; i < program.Count; i++)
            {
                var instruction = program.Instructions[i];
                if (instruction.IsIntrinsic)
                { if (input.RemainingUses[i] != -1 || !input.Eligibility[i]) return Fail("固定卡不可伪造次数或资格。", out error); continue; }
                if (!store.TryGet(instruction.InstanceId, out var item) || !uses.TryGetValue(item.InstanceId, out var state))
                    return Fail("研究输入缺少当前卡的真实资源事实。", out error);
                bool qualification = instruction.Kind == SpellKind.Modify || instruction.Kind == SpellKind.Multicast || instruction.Kind == SpellKind.Copy;
                int count = qualification || item.HasUnlimitedCharges ? -1 : item.RemainingCharges;
                bool eligible = !qualification || item.HasUnlimitedCharges || state.Eligible || peaceful && item.RemainingCharges > 0;
                if (input.RemainingUses[i] != count || input.Eligibility[i] != eligible)
                    return Fail("研究输入不能改变当前真实次数或资格。", out error);
            }
            return true;
        }
        internal static bool ValidateDraw(SpellProgram program, OnlineWandDrawSnapshot draw, IReadOnlyList<int> uses, out string error)
        {
            error = null; var seen = new bool[program.Count];
            foreach (int index in draw.Deck) { if (index < 0 || index >= seen.Length || seen[index]) return Fail("Deck索引重复或越界。", out error); seen[index] = true; }
            foreach (int index in draw.Discard) { if (index < 0 || index >= seen.Length || seen[index]) return Fail("Discard索引重复或越界。", out error); seen[index] = true; }
            for (int i = 0; i < seen.Length; i++)
                if (uses[i] < -1 || (program.Instructions[i].IsIntrinsic ? seen[i] : !seen[i] && uses[i] != 0))
                    return Fail("牌堆不是原非固定指令集合，不能补牌或漏掉剩余卡。", out error);
            return true;
        }
        internal static bool Compile(SpellDefinition[] definitions, string[] ids, WandStats stats, long revision, ItemCatalogIndex catalog,
            out SpellProgram program, out string error)
        {
            program = null; error = null; var fixedCards = new SpellDefinition[stats.AlwaysCastContentIds.Count];
            for (int i = 0; i < fixedCards.Length; i++)
            {
                if (!catalog.TryGet(stats.AlwaysCastContentIds[i], out var definition) || definition.Kind != ItemKind.Spell)
                    return Fail("固定法术ContentId不在受控Catalog。", out error);
                fixedCards[i] = definition.SpellDefinition;
            }
            if (!SpellProgramCompiler.TryCompile(definitions, new SpellProgramCompileInputs(stats.BaseDraws, ids, revision,
                stats.BaseCastDelay, stats.BaseReloadTime, stats.BaseSpreadDegrees, stats.Shuffle, stats.ManaRegenPerSecond, fixedCards), out program, out _))
                return Fail("原程序无法按固定roll和实例槽重新编译。", out error);
            return true;
        }
        internal static bool HistoricalProgram(OnlineWandRecordedProgramSnapshot value, ItemCatalogIndex catalog,
            out SpellProgram program, out string error)
        {
            var stats = ReadStats(value.Stats); program = null; error = null;
            if (!stats.IsValid || !catalog.ValidateWandTraits(stats)) return Fail("历史法杖固定roll非法。", out error);
            var definitions = new SpellDefinition[stats.ProgramCapacity]; var ids = new string[definitions.Length];
            foreach (var card in value.Cards)
            {
                if (!catalog.TryGet(card.ContentId, out var definition) || definition.Kind != ItemKind.Spell)
                    return Fail("历史卡不在同Hash受控Catalog。", out error);
                if (card.SourceSlot < 0) continue;
                definitions[card.SourceSlot] = definition.SpellDefinition; ids[card.SourceSlot] = card.InstanceId;
            }
            if (!Compile(definitions, ids, stats, value.ProgramRevision, catalog, out program, out error)) return false;
            if (program.Count != value.Cards.Length) return Fail("历史程序数量发生变化。", out error);
            for (int i = 0; i < program.Count; i++)
                if (program.Instructions[i].InstanceId != value.Cards[i].InstanceId || program.Instructions[i].SourceSlot != value.Cards[i].SourceSlot)
                    return Fail("历史ProgramIndex或SourceSlot被重排。", out error);
            return true;
        }
        internal static bool TryReplay(OnlineWandExecutionReplaySnapshot value, ItemCatalogIndex catalog, out WandExecutionReplay replay, out string error)
        {
            replay = null;
            if (!HistoricalProgram(value.Program, catalog, out var before, out error)
                || !HistoricalProgram(value.AfterProgram, catalog, out var after, out error)) return false;
            var stats = ReadStats(value.Program.Stats);
            if (!stats.SameAs(ReadStats(value.AfterProgram.Stats)) || value.Inputs.MaxMana != stats.MaxMana
                || !ValidateDraw(before, value.Before, value.Inputs.RemainingUses, out error)) return Fail("历史Before或固定roll不一致。", out error);
            var paid = new Dictionary<string, int>(StringComparer.Ordinal);
            var receipts = new WandUseReceipt[value.UsesPaid.Length];
            for (int i = 0; i < receipts.Length; i++)
            { var p = value.UsesPaid[i]; paid.Add(p.InstanceId, p.Count); receipts[i] = new WandUseReceipt(p.InstanceId, p.Count); }
            var beforeById = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < before.Count; i++) beforeById.Add(before.Instructions[i].InstanceId, i);
            foreach (var payment in paid)
                if (!beforeById.TryGetValue(payment.Key, out int index) || before.Instructions[index].IsIntrinsic
                    || value.Inputs.RemainingUses[index] < payment.Value) return Fail("历史收费没有对应有限次数原卡。", out error);
            var afterUses = new int[after.Count]; var retained = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < after.Count; i++)
            {
                var card = after.Instructions[i];
                if (!beforeById.TryGetValue(card.InstanceId, out int index) || value.AfterProgram.Cards[i].ContentId != value.Program.Cards[index].ContentId
                    || card.SourceSlot != before.Instructions[index].SourceSlot) return Fail("After程序不是同一原实例的保真保留。", out error);
                int use = value.Inputs.RemainingUses[index]; paid.TryGetValue(card.InstanceId, out int count);
                if (count > 0 && use == count) return Fail("已耗尽卡不应仍留在After程序。", out error);
                afterUses[i] = use < 0 ? -1 : use - count; retained.Add(card.InstanceId);
            }
            for (int i = 0; i < before.Count; i++)
                if (!retained.Contains(before.Instructions[i].InstanceId) && (!paid.TryGetValue(before.Instructions[i].InstanceId, out int count)
                    || count != value.Inputs.RemainingUses[i])) return Fail("After程序删除了未耗尽卡。", out error);
            long expectedCycle = (long)value.Before.CycleIndex + (value.Summary.NeedsReload ? 1 : 0);
            if (value.After.CycleIndex != expectedCycle || after.Revision < before.Revision
                || !ValidateDraw(after, value.After, afterUses, out error)) return Fail("历史After牌堆或循环水位非法。", out error);
            if (!SpellExecutionReadModelFactory.TryCreate(before, value, out var graph, out var trace, out var summary, out error)) return false;
            if ((graph.RootOutputCount > 0) != (summary.Outcome == SpellBuildOutcome.Emitted)) return Fail("历史Summary与原Root输出不符。", out error);
            var layers = new WandLayerResourceStatus[value.LayerStatuses.Length];
            for (int i = 0; i < layers.Length; i++)
            { layers[i] = (WandLayerResourceStatus)value.LayerStatuses[i]; if (layers[i] == WandLayerResourceStatus.Prepared) return Fail("不能保存准备中的历史资源层。", out error); }
            if (layers[0] != WandLayerResourceStatus.Committed) return Fail("Replay必须来自真实Root提交。", out error);
            if (trace.WasCaptured && !trace.Truncated && !TraceEndsAt(before, after, ReadDraw(value.Before), trace, value.After))
                return Fail("Trace集合移动与真实After牌堆不一致。", out error);
            var contentIds = new string[before.Count]; var afterIds = new string[after.Count];
            for (int i = 0; i < contentIds.Length; i++) contentIds[i] = value.Program.Cards[i].ContentId;
            for (int i = 0; i < afterIds.Length; i++) afterIds[i] = value.AfterProgram.Cards[i].ContentId;
            replay = new WandExecutionReplay(value.TicketId, value.SourceWandId, value.CallerId, graph, trace,
                ReadDraw(value.Before), ReadDraw(value.After), ReadInputs(value.Inputs), summary,
                value.InventoryVersionBefore, value.InventoryVersionAfter, receipts, layers, value.ReleasedOutputs,
                (WandWorldExecutionStatus)value.WorldStatus, stats, contentIds, after, afterIds);
            return true;
        }
        private static bool TraceEndsAt(SpellProgram before, SpellProgram after, WandDrawSnapshot draw, SpellExecutionTrace trace, OnlineWandDrawSnapshot expected)
        {
            var piles = new SpellTracePileReplay(); if (!piles.Seek(draw, trace, int.MaxValue) || piles.Hand.Count != 0) return false;
            var mapping = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < after.Count; i++) mapping.Add(after.Instructions[i].InstanceId, i);
            return SamePile(piles.Deck, expected.Deck) && SamePile(piles.Discard, expected.Discard);
            bool SamePile(IReadOnlyList<int> oldIndices, int[] wanted)
            {
                int at = 0;
                foreach (int index in oldIndices)
                    if (mapping.TryGetValue(before.Instructions[index].InstanceId, out int current))
                    { if (at >= wanted.Length || wanted[at++] != current) return false; }
                return at == wanted.Length;
            }
        }
        internal static bool TryContinuityEntry(OnlineWandResearchEntrySnapshot value, ItemCatalogIndex catalog,
            out WandSessionContinuity.Entry entry, out string error)
        {
            entry = null; error = null;
            if (!HistoricalProgram(value.Program, catalog, out var program, out error)
                || !ReadStats(value.Stats).SameAs(ReadStats(value.Program.Stats))
                || !ValidateDraw(program, value.Draw, value.Inputs.RemainingUses, out error)) return false;
            var ids = new string[value.InstalledIds.Length];
            foreach (var instruction in program.Instructions)
            {
                if (instruction.IsIntrinsic) continue;
                if (value.InstalledIds[instruction.SourceSlot] != instruction.InstanceId) return Fail("保存条目安装序列与原程序不同。", out error);
                ids[instruction.SourceSlot] = instruction.InstanceId;
            }
            for (int i = 0; i < ids.Length; i++) if ((ids[i] ?? "") != value.InstalledIds[i]) return Fail("保存条目夹带未编译的安装卡。", out error);
            entry = new WandSessionContinuity.Entry { Id = value.WandId, Stats = ReadStats(value.Stats), Revision = value.ProgramRevision,
                Mana = value.Mana, Ready = value.Ready, Cast = value.Cast, Reload = value.Reload, InstalledIds = ids,
                Program = program, Draw = ReadDraw(value.Draw) }; return true;
        }
        internal static bool TryCreateContinuity(OnlineWandResearchStateSnapshot value, ItemCatalogIndex catalog,
            out OnlineWandResearchReadModel result, out string error)
        {
            result = null;
            if (!OnlineWandResearchStateCodec.Validate(value, out error)) return false;
            var candidate = new OnlineWandResearchReadModel();
            foreach (var entry in value.Wands)
            {
                if (!TryContinuityEntry(entry, catalog, out var restored, out error)) return false;
                candidate.Continuity.Entries.Add(restored);
            }
            foreach (var entry in value.GroundWands)
                if (!TryContinuityEntry(entry, catalog, out _, out error)) return false;
            foreach (var replay in value.Replays)
            { if (!TryReplay(replay, catalog, out var record, out error)) return false; candidate.Archive.Add(record); }
            candidate.Continuity.Selected = value.SelectedWandId.Length == 0 ? null : value.SelectedWandId;
            result = candidate; return true;
        }
        internal static WandStats ReadStats(OnlineOwnedWandStatsSnapshot s) => new WandStats(s.MaxMana, s.ManaRegenPerSecond,
            s.BaseCastDelay, s.ProgramCapacity, s.BaseDraws, s.BaseReloadTime, s.BaseSpreadDegrees, s.Shuffle, s.AlwaysCastContentIds);
        internal static OnlineOwnedWandStatsSnapshot CaptureStats(in WandStats s)
        {
            var fixedIds = new string[s.AlwaysCastContentIds.Count]; for (int i = 0; i < fixedIds.Length; i++) fixedIds[i] = s.AlwaysCastContentIds[i];
            return new OnlineOwnedWandStatsSnapshot { MaxMana = s.MaxMana, ManaRegenPerSecond = s.ManaRegenPerSecond,
                BaseCastDelay = s.BaseCastDelay, BaseReloadTime = s.BaseReloadTime, BaseSpreadDegrees = s.BaseSpreadDegrees,
                ProgramCapacity = s.ProgramCapacity, BaseDraws = s.BaseDraws, Shuffle = s.Shuffle, AlwaysCastContentIds = fixedIds };
        }
        internal static WandDrawSnapshot ReadDraw(OnlineWandDrawSnapshot d) => new WandDrawSnapshot(d.Deck, d.Discard,
            d.ProgramRevision, d.ExecutionRevision, d.CycleIndex, d.RandomState, d.CycleReloadDelta);
        internal static OnlineWandDrawSnapshot CaptureDraw(WandDrawSnapshot d) => new OnlineWandDrawSnapshot
        { ProgramRevision = d.ProgramRevision, ExecutionRevision = d.ExecutionRevision, CycleIndex = d.CycleIndex,
            RandomState = d.RandomState, CycleReloadDelta = d.CycleReloadDelta, Deck = Copy(d.Deck), Discard = Copy(d.Discard) };
        internal static SpellExecutionInputs ReadInputs(OnlineSpellExecutionInputsSnapshot i) => new SpellExecutionInputs(i.AvailableMana,
            i.MaxMana, i.RemainingUses, i.Eligibility, i.HpRatio, i.SelfWet, i.SelfBurning, i.InputVersion);
        internal static OnlineSpellExecutionInputsSnapshot CaptureInputs(in SpellExecutionInputs i)
        {
            var eligible = new bool[i.Eligibility.Count]; for (int at = 0; at < eligible.Length; at++) eligible[at] = i.Eligibility[at];
            return new OnlineSpellExecutionInputsSnapshot { AvailableMana = i.AvailableMana, MaxMana = i.MaxMana, HpRatio = i.HpRatio,
                SelfWet = i.SelfWet, SelfBurning = i.SelfBurning, InputVersion = i.InputVersion, RemainingUses = Copy(i.RemainingUses), Eligibility = eligible };
        }
        private static int[] Copy(IReadOnlyList<int> values)
        { var result = new int[values.Count]; for (int i = 0; i < result.Length; i++) result[i] = values[i]; return result; }
        private static bool Fail(string message, out string error) => OnlineWandResearchStateCodec.Fail(message, out error);
    }
}
