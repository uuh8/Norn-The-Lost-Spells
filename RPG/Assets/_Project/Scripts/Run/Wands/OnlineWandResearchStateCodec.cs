using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Game.Core;
using Game.Skills;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 离散研究/可靠个人退出使用的有界canonical JSON。这里不接触磁盘、HTTP或账号，也不授予资源。
    /// 先检查UTF8、深度与数组预算再调用Unity JSON，避免未知大数组先被反序列化分配；再做领域结构校验。
    /// 仅接受本Codec产生的canonical正文：重编码逐字比较同时拒绝未知字段、重复字段、缺字段及隐式默认值。
    /// 这是冷路径，反射检查所有primitive数值刻意不进入Update；真正Catalog/归属校验仍由安装层负责。
    /// </summary>
    public static class OnlineWandResearchStateCodec
    {
        public static bool TryEncode(OnlineWandResearchStateSnapshot snapshot, out string json, out string error)
        {
            json = null;
            if (!Validate(snapshot, out error)) return false;
            string candidate = JsonUtility.ToJson(snapshot);
            if (Encoding.UTF8.GetByteCount(candidate) > OnlineWandResearchStateSnapshot.MaximumUtf8Bytes)
                return Fail("研究连续性超过24MiB预算，保留现场。", out error);
            json = candidate; return true;
        }
        public static bool TryDecode(string json, out OnlineWandResearchStateSnapshot snapshot, out string error)
        {
            snapshot = null; error = null;
            if (string.IsNullOrEmpty(json) || json.Length > OnlineWandResearchStateSnapshot.MaximumUtf8Bytes
                || Encoding.UTF8.GetByteCount(json) > OnlineWandResearchStateSnapshot.MaximumUtf8Bytes || !BoundedJson(json))
                return Fail("研究正文为空、超预算或嵌套结构非法。", out error);
            try
            {
                var candidate = JsonUtility.FromJson<OnlineWandResearchStateSnapshot>(json);
                if (!Validate(candidate, out error)) return false;
                if (!string.Equals(JsonUtility.ToJson(candidate), json, StringComparison.Ordinal))
                    return Fail("研究正文不是受控canonical JSON。", out error);
                snapshot = candidate; return true;
            }
            catch (ArgumentException) { return Fail("研究JSON无法解码。", out error); }
        }

        internal static bool Validate(OnlineWandResearchStateSnapshot value, out string error)
        {
            error = null;
            if (value == null || value.SchemaVersion != OnlineWandResearchStateSnapshot.CurrentSchemaVersion
                || !Text(value.PlayerId) || !Text(value.InventoryId) || !Hash(value.GameplayConfigHash)
                || value.SelectedWandId == null || value.InventoryVersion == 0 || value.ResourceVersion == 0 || value.SpellUseVersion == 0
                || value.Wands == null || value.Wands.Length > OnlineOwnedAssetSnapshot.MaximumItems
                || value.Library == null || value.Library.Length > SpellProgramLimits.MaxInstructions
                || value.Replays == null || value.Replays.Length > WandExecutionReplayArchive.Capacity
                || value.GroundWands == null || value.GroundWands.Length > OnlineGroundInventorySnapshot.MaximumItems
                || value.GroundSpellUses == null || value.GroundSpellUses.Length > OnlineGroundInventorySnapshot.MaximumItems || !PrimitiveTree(value, 0))
                return Fail("研究身份、数值或数组预算非法。", out error);
            var wands = new HashSet<string>(StringComparer.Ordinal); var installed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in value.Wands)
            {
                if (entry == null || !Text(entry.WandId) || !wands.Add(entry.WandId) || entry.ProgramRevision == 0
                    || !Stats(entry.Stats) || !Program(entry.Program) || entry.Program.ProgramRevision != entry.ProgramRevision
                    || !OnlineWandResearchReadModel.ReadStats(entry.Stats).SameAs(OnlineWandResearchReadModel.ReadStats(entry.Program.Stats))
                    || entry.InstalledIds == null || entry.InstalledIds.Length != entry.Stats.ProgramCapacity
                    || entry.Mana < 0 || entry.Mana > entry.Stats.MaxMana || entry.Ready < 0 || entry.Cast < 0 || entry.Reload < 0
                    || !Draw(entry.Draw, entry.ProgramRevision) || !Inputs(entry.Inputs)
                    || entry.Inputs.AvailableMana != entry.Mana || entry.Inputs.MaxMana != entry.Stats.MaxMana
                    || entry.Inputs.InputVersion != value.InventoryVersion) return Fail("当前法杖连续性不完整。", out error);
                int count = entry.Stats.AlwaysCastContentIds.Length;
                foreach (string id in entry.InstalledIds)
                {
                    if (id == null) return Fail("空槽必须显式编码为空字符串。", out error);
                    if (id.Length == 0) continue;
                    if (!Text(id) || !installed.Add(id)) return Fail("安装实例重复或非法。", out error);
                    count++;
                }
                if (entry.Inputs.RemainingUses.Length != count || !Piles(entry.Draw, count)
                    || !PileMembership(entry.Draw, entry.Inputs.RemainingUses, count - entry.Stats.AlwaysCastContentIds.Length))
                    return Fail("牌堆和当前ProgramIndex不一致。", out error);
            }
            if (value.SelectedWandId.Length != 0 && !wands.Contains(value.SelectedWandId)) return Fail("选中杖不在连续性闭包中。", out error);
            var library = new HashSet<string>(StringComparer.Ordinal);
            foreach (var card in value.Library)
                if (card == null || !Text(card.InstanceId) || !Text(card.ContentId) || !library.Add(card.InstanceId) || installed.Contains(card.InstanceId))
                    return Fail("研究散卡库重复、缺失或与安装实例重叠。", out error);
            var ownerInstalled = new HashSet<string>(installed, StringComparer.Ordinal);
            foreach (var entry in value.GroundWands)
            {
                if (!StandaloneEntry(entry) || !wands.Add(entry.WandId)) return Fail("地面杖连续性非法或与Owner重叠。", out error);
                foreach (string id in entry.InstalledIds) if (id.Length != 0 && (!installed.Add(id) || library.Contains(id)))
                    return Fail("地面安装卡与Owner/其他地面杖重叠。", out error);
            }
            var groundUses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var use in value.GroundSpellUses)
                if (use == null || !Text(use.InstanceId) || !groundUses.Add(use.InstanceId) || ownerInstalled.Contains(use.InstanceId) || library.Contains(use.InstanceId))
                    return Fail("地面资格实例重复或与Owner分区重叠。", out error);
            foreach (var entry in value.GroundWands) foreach (string id in entry.InstalledIds)
                if (id.Length != 0 && !groundUses.Contains(id)) return Fail("地面安装卡缺少资格事实。", out error);
            var tickets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var replay in value.Replays)
                if (!Replay(replay) || !tickets.Add(replay.TicketId)) return Fail("历史执行的数值、索引或原预算非法。", out error);
            return true;
        }
        private static bool StandaloneEntry(OnlineWandResearchEntrySnapshot entry)
        {
            if (entry == null || !Text(entry.WandId) || entry.ProgramRevision == 0 || !Stats(entry.Stats)
                || !Program(entry.Program) || entry.Program.ProgramRevision != entry.ProgramRevision
                || !OnlineWandResearchReadModel.ReadStats(entry.Stats).SameAs(OnlineWandResearchReadModel.ReadStats(entry.Program.Stats))
                || entry.InstalledIds == null || entry.InstalledIds.Length != entry.Stats.ProgramCapacity
                || !Inputs(entry.Inputs) || entry.Inputs.AvailableMana != entry.Mana || entry.Inputs.MaxMana != entry.Stats.MaxMana
                || entry.Mana < 0 || entry.Mana > entry.Stats.MaxMana || entry.Cast < 0 || entry.Reload < 0 || entry.Ready < 0
                || !Draw(entry.Draw, entry.ProgramRevision) || !Piles(entry.Draw, entry.Program.Cards.Length)
                || entry.Inputs.RemainingUses.Length != entry.Program.Cards.Length) return false;
            foreach (string id in entry.InstalledIds) if (id == null) return false;
            return PileMembership(entry.Draw, entry.Inputs.RemainingUses, entry.Program.Cards.Length - entry.Stats.AlwaysCastContentIds.Length);
        }

        private static bool Replay(OnlineWandExecutionReplaySnapshot r)
        {
            if (r == null || !Text(r.TicketId) || !Text(r.SourceWandId) || !Text(r.CallerId)
                || !Program(r.Program) || !Program(r.AfterProgram) || !Inputs(r.Inputs)
                || r.Inputs.RemainingUses.Length != r.Program.Cards.Length
                || !Draw(r.Before, r.Program.ProgramRevision) || !Draw(r.After, r.AfterProgram.ProgramRevision)
                || !Piles(r.Before, r.Program.Cards.Length) || !Piles(r.After, r.AfterProgram.Cards.Length)
                || !PileMembership(r.Before, r.Inputs.RemainingUses, r.Program.Cards.Length - r.Program.Stats.AlwaysCastContentIds.Length)
                || r.Before.ExecutionRevision == long.MaxValue || r.After.ExecutionRevision != r.Before.ExecutionRevision + 1
                || r.InventoryVersionBefore == 0 || r.InventoryVersionAfter < r.InventoryVersionBefore
                || r.Inputs.InputVersion != r.InventoryVersionBefore || r.WorldStatus > 2
                || r.Summary == null || r.Summary.Outcome < 1 || r.Summary.Outcome > 2 || r.Summary.Error != 0
                || r.Summary.ManaAfter < 0 || r.Summary.ManaAfter > r.Inputs.MaxMana
                || r.Summary.CastInterval < SpellProgramLimits.MinimumInterval || r.Summary.ReloadInterval < SpellProgramLimits.MinimumInterval
                || r.Summary.InstructionSteps < 0 || r.Summary.InstructionSteps > SpellProgramLimits.MaxSteps
                || r.Scopes == null || r.Scopes.Length < 1 || r.Scopes.Length > SpellProgramLimits.MaxOutputs + 1
                || r.Outputs == null || r.Outputs.Length > SpellProgramLimits.MaxOutputs
                || r.LayerStatuses == null || r.LayerStatuses.Length != r.Scopes.Length
                || r.ReleasedOutputs == null || r.ReleasedOutputs.Length != r.Outputs.Length
                || r.UsesPaid == null || r.UsesPaid.Length > r.Program.Cards.Length
                || r.Trace == null || r.Trace.Length > SpellProgramLimits.MaxTraceEvents || r.TraceTotalEvents < 1 || r.TraceTotalEvents < r.Trace.Length
                || !r.TraceWasCaptured && r.Trace.Length != 0
                || r.TraceWasCaptured && !r.TraceTruncated && r.TraceTotalEvents != r.Trace.Length) return false;
            for (int i = 0; i < r.Scopes.Length; i++)
            {
                var s = r.Scopes[i];
                if (s == null || s.Id != i || s.FinalModifiers == null || s.FinalModifiers.MotionMode > 2
                    || s.ParentScopeId < -1 || s.ParentScopeId >= i || (i == 0) != (s.ParentScopeId == -1)
                    || !Index(s.OwnerOutputId, r.Outputs.Length) || i == 0 && s.OwnerOutputId != -1
                    || i != 0 && s.OwnerOutputId < 0 || s.ClosedAtStep < -1 || s.ClosedAtStep >= r.TraceTotalEvents
                    || s.OutputIds == null || s.OutputIds.Length > r.Outputs.Length
                    || s.Contributions == null || s.Contributions.Length > SpellProgramLimits.MaxSteps) return false;
                foreach (int id in s.OutputIds) if (id < 0 || id >= r.Outputs.Length) return false;
                foreach (var c in s.Contributions)
                    if (c == null || c.CardIndex < 0 || c.CardIndex >= r.Program.Cards.Length || c.StepId < 0 || c.StepId >= r.TraceTotalEvents) return false;
                if (r.LayerStatuses[i] > (byte)WandLayerResourceStatus.Cancelled) return false;
            }
            for (int i = 0; i < r.Outputs.Length; i++)
            {
                var o = r.Outputs[i];
                if (o == null || o.Id != i || o.ScopeId < 0 || o.ScopeId >= r.Scopes.Length
                    || o.SourceCardIndex < 0 || o.SourceCardIndex >= r.Program.Cards.Length
                    || o.SourceInstanceId != r.Program.Cards[o.SourceCardIndex].InstanceId
                    || !Index(o.PayloadScopeId, r.Scopes.Length) || o.RegisteredAtStep < 0 || o.RegisteredAtStep >= r.TraceTotalEvents
                    || o.Command == null || o.Command.SpawnMode > 2 || o.Command.MotionMode > 2 || o.Command.PayloadTrigger > 3
                    || r.ReleasedOutputs[i] && r.LayerStatuses[o.ScopeId] != (byte)WandLayerResourceStatus.Committed) return false;
            }
            var paid = new HashSet<string>(StringComparer.Ordinal);
            foreach (var receipt in r.UsesPaid) if (receipt == null || !Text(receipt.InstanceId) || !paid.Add(receipt.InstanceId)
                || receipt.Count < 1 || receipt.Count > SpellProgramLimits.MaxSteps) return false;
            for (int i = 0; i < r.Trace.Length; i++)
            {
                var t = r.Trace[i];
                if (t == null || t.StepId != i || t.Kind > (byte)SpellTraceKind.IntrinsicActionInvoked
                    || t.InvocationMode > 2 || t.From > 3 || t.To > 3 || t.Error > (byte)SpellProgramError.NumericOverflow
                    || !Index(t.CardIndex, r.Program.Cards.Length) || !Index(t.CauseCardIndex, r.Program.Cards.Length)
                    || !Index(t.ScopeId, r.Scopes.Length) || !Index(t.OutputId, r.Outputs.Length)
                    || t.RequestId < -1 || t.RequestId > SpellProgramLimits.MaxTraceEvents
                    || t.ParentRequestId < -1 || t.ParentRequestId > SpellProgramLimits.MaxTraceEvents
                    || t.TargetPosition < -1 || t.TargetPosition > SpellProgramLimits.MaxInstructions
                    || t.UsesBefore < -1 || t.UsesAfter < -1 || t.ManaBefore < 0 || t.ManaAfter < 0
                    || t.ManaBefore > r.Inputs.MaxMana || t.ManaAfter > r.Inputs.MaxMana) return false;
            }
            if (r.TraceWasCaptured && !r.TraceTruncated && (r.Trace.Length == 0
                || r.Trace[r.Trace.Length - 1].Kind != (byte)SpellTraceKind.BuildCompleted
                || r.Trace[r.Trace.Length - 1].ManaAfter != r.Summary.ManaAfter)) return false;
            return true;
        }
        private static bool Program(OnlineWandRecordedProgramSnapshot p)
        {
            if (p == null || p.ProgramRevision < 1 || p.ProgramRevision > uint.MaxValue || !Stats(p.Stats)
                || p.Cards == null || p.Cards.Length > SpellProgramLimits.MaxInstructions) return false;
            var ids = new HashSet<string>(StringComparer.Ordinal); var slots = new HashSet<int>(); int fixedCount = 0, previousSlot = -1;
            foreach (var c in p.Cards)
            {
                if (c == null || !Text(c.InstanceId) || !Text(c.ContentId) || !ids.Add(c.InstanceId)
                    || c.SourceSlot < -1 || c.SourceSlot >= p.Stats.ProgramCapacity) return false;
                if (c.SourceSlot < 0)
                { if (fixedCount >= p.Stats.AlwaysCastContentIds.Length || c.InstanceId != "@always:" + fixedCount
                        || c.ContentId != p.Stats.AlwaysCastContentIds[fixedCount++]) return false; }
                else { if (fixedCount != 0 || !slots.Add(c.SourceSlot) || c.SourceSlot <= previousSlot) return false; previousSlot = c.SourceSlot; }
            }
            return fixedCount == p.Stats.AlwaysCastContentIds.Length;
        }
        private static bool Stats(OnlineOwnedWandStatsSnapshot s)
            => s != null && s.MaxMana > 0 && s.ManaRegenPerSecond >= 0 && s.BaseCastDelay >= 0 && s.BaseReloadTime >= 0
                && s.BaseSpreadDegrees >= 0 && s.ProgramCapacity >= 1 && s.ProgramCapacity <= 32 && s.BaseDraws >= 1
                && s.BaseDraws <= s.ProgramCapacity && s.AlwaysCastContentIds != null && s.AlwaysCastContentIds.Length <= 4;
        private static bool Inputs(OnlineSpellExecutionInputsSnapshot i)
        {
            if (!(i != null && i.AvailableMana >= 0 && i.MaxMana >= i.AvailableMana && i.MaxMana > 0 && i.HpRatio >= 0 && i.HpRatio <= 1
                && i.InputVersion > 0 && i.InputVersion <= uint.MaxValue && i.RemainingUses != null && i.Eligibility != null
                && i.RemainingUses.Length <= SpellProgramLimits.MaxInstructions && i.RemainingUses.Length == i.Eligibility.Length)) return false;
            foreach (int uses in i.RemainingUses) if (uses < -1) return false;
            return true;
        }
        private static bool Draw(OnlineWandDrawSnapshot d, long revision)
            => d != null && d.ProgramRevision == revision && d.ExecutionRevision >= 0 && d.CycleIndex >= 1 && d.RandomState != 0
                && d.Deck != null && d.Discard != null && d.Deck.Length <= SpellProgramLimits.MaxInstructions && d.Discard.Length <= SpellProgramLimits.MaxInstructions;
        private static bool Piles(OnlineWandDrawSnapshot draw, int count)
        {
            var seen = new HashSet<int>();
            foreach (int id in draw.Deck) if (id < 0 || id >= count || !seen.Add(id)) return false;
            foreach (int id in draw.Discard) if (id < 0 || id >= count || !seen.Add(id)) return false;
            return true;
        }
        private static bool PileMembership(OnlineWandDrawSnapshot draw, int[] uses, int ordinaryCount)
        {
            var seen = new bool[uses.Length];
            foreach (int index in draw.Deck) seen[index] = true;
            foreach (int index in draw.Discard) seen[index] = true;
            for (int i = 0; i < seen.Length; i++)
                if (i >= ordinaryCount ? seen[i] || uses[i] != -1 : !seen[i] && uses[i] != 0) return false;
            return true;
        }
        internal static bool Fail(string message, out string error) { error = message; return false; }
        private static bool Index(int value, int count) => value >= -1 && value < count;
        private static bool Text(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;
        private static bool Hash(string value)
        { if (value == null || value.Length != 64) return false; foreach (char c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false; return true; }
        private static bool PrimitiveTree(object value, int depth)
        {
            if (value == null || depth > 12) return value == null;
            if (value is float number) return float.IsFinite(number);
            if (value is string text) return text.Length <= 512;
            Type type = value.GetType(); if (type.IsPrimitive) return true;
            if (value is Array array)
            { if (array.Length > SpellProgramLimits.MaxTraceEvents) return false; foreach (object item in array) if (!PrimitiveTree(item, depth + 1)) return false; return true; }
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
                if (!PrimitiveTree(field.GetValue(value), depth + 1)) return false;
            return true;
        }
        private static bool BoundedJson(string json)
        {
            var kinds = new char[12]; var counts = new int[12]; int depth = 0; bool quoted = false, escaped = false;
            foreach (char c in json)
            {
                if (quoted) { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') quoted = false; continue; }
                if (c == '"') { quoted = true; continue; }
                if (c == '{' || c == '[') { if (depth == kinds.Length) return false; kinds[depth] = c; counts[depth++] = 1; }
                else if (c == '}' || c == ']')
                { if (depth == 0 || kinds[--depth] != (c == '}' ? '{' : '[')) return false; }
                else if (c == ',' && depth != 0 && kinds[depth - 1] == '[' && ++counts[depth - 1] > SpellProgramLimits.MaxTraceEvents) return false;
            }
            return !quoted && depth == 0;
        }
    }
}
