using System;
using System.Collections.Generic;
using Game.Core;
using Game.Skills;

namespace Game.Run
{
    /// <summary>
    /// 共享地面只保存原Transfer的真实连续性。它不创建/移动物品、不授Owner、不发世界输出。
    /// Root先恢复未发布的完整地面树，再逐Record调用此桥；失败时原GroundStore与账号现场都不变。
    /// </summary>
    internal static class OnlineWandResearchContinuityBridge
    {
        internal static bool TryCaptureGround(ItemOwnershipStore ground, OnlineOwnedWorkspace.GroundTransferState transfer, uint inventoryVersion,
            out OnlineWandResearchEntrySnapshot[] wands, out OnlineInventorySpellUsePresentationSnapshot[] uses, out string error)
        {
            wands = null; uses = null; error = null;
            if (ground == null || transfer == null || inventoryVersion == 0) return Fail("缺少真实地面树或连续性来源。", out error);
            var result = new List<OnlineWandResearchEntrySnapshot>(); var qualifications = new List<OnlineInventorySpellUsePresentationSnapshot>();
            foreach (var pair in transfer.Revisions)
            {
                if (!ground.TryGet(pair.Key, out var item) || !ground.Catalog.TryGet(item.ContentId, out var definition))
                    return Fail("地面连续性实例不在原Store。", out error);
                if (definition.Kind == ItemKind.Spell) qualifications.Add(new OnlineInventorySpellUsePresentationSnapshot
                    { InstanceId = item.InstanceId, Eligible = transfer.Qualified.Contains(item.InstanceId) });
                if (!ground.Catalog.TryGetWandStats(item, out var stats)) continue;
                WandSessionContinuity.Entry saved = null;
                if (transfer.Wands != null) foreach (var candidate in transfer.Wands.Entries) if (candidate.Id == item.InstanceId) { saved = candidate; break; }
                var definitions = new SpellDefinition[stats.ProgramCapacity]; var ids = new string[definitions.Length];
                foreach (var card in ground)
                    if (card.Location.Kind == ItemLocationKind.WandSlot && card.Location.OwnerId == item.InstanceId)
                    { ground.Catalog.TryGet(card.Item.ContentId, out var cardDefinition); definitions[card.Location.SlotIndex] = cardDefinition.SpellDefinition; ids[card.Location.SlotIndex] = card.Item.InstanceId; }
                if (saved != null)
                {
                    if (!saved.Stats.SameAs(stats) || saved.InstalledIds.Length != ids.Length) return Fail("地面固定roll或安装序列变动。", out error);
                    for (int i = 0; i < ids.Length; i++) if (ids[i] != saved.InstalledIds[i]) return Fail("地面安装实例变动。", out error);
                }
                uint revision = saved?.Revision ?? 1;
                if (!OnlineWandResearchReadModel.Compile(definitions, ids, stats, revision, ground.Catalog, out var compiled, out error)) return false;
                var program = saved?.Program ?? compiled;
                // 原来尚未查询过的杖没有Execution对象：在DS用原FNV种子/初始牌堆物化，绝不覆盖已有Draw。
                var draw = saved?.Draw ?? new WandExecutionState(program, item.InstanceId).Draw;
                var counts = new int[program.Count]; var eligible = new bool[program.Count]; var cards = new OnlineWandRecordedCardSnapshot[program.Count]; int fixedAt = 0;
                for (int i = 0; i < program.Count; i++)
                {
                    var instruction = program.Instructions[i]; string content;
                    if (instruction.IsIntrinsic) { counts[i] = -1; eligible[i] = true; content = stats.AlwaysCastContentIds[fixedAt++]; }
                    else
                    {
                        if (!ground.TryGet(instruction.InstanceId, out var card)) return Fail("地面程序卡缺失。", out error);
                        content = card.ContentId;
                        bool qualification = instruction.Kind == SpellKind.Modify || instruction.Kind == SpellKind.Multicast || instruction.Kind == SpellKind.Copy;
                        counts[i] = qualification || card.HasUnlimitedCharges ? -1 : card.RemainingCharges;
                        eligible[i] = !qualification || card.HasUnlimitedCharges || transfer.Qualified.Contains(card.InstanceId) || card.RemainingCharges > 0;
                    }
                    cards[i] = new OnlineWandRecordedCardSnapshot { InstanceId = instruction.InstanceId, ContentId = content, SourceSlot = instruction.SourceSlot };
                }
                float mana = saved?.Mana ?? stats.MaxMana;
                var installed = new string[ids.Length]; for (int i = 0; i < ids.Length; i++) installed[i] = ids[i] ?? "";
                result.Add(new OnlineWandResearchEntrySnapshot { WandId = item.InstanceId, ProgramRevision = revision,
                    Stats = OnlineWandResearchReadModel.CaptureStats(stats), InstalledIds = installed,
                    Program = new OnlineWandRecordedProgramSnapshot { ProgramRevision = program.Revision, Stats = OnlineWandResearchReadModel.CaptureStats(stats), Cards = cards },
                    Mana = mana, Ready = saved?.Ready ?? 0, Cast = saved?.Cast ?? 0, Reload = saved?.Reload ?? 0,
                    Draw = OnlineWandResearchReadModel.CaptureDraw(draw),
                    Inputs = OnlineWandResearchReadModel.CaptureInputs(new SpellExecutionInputs(mana, stats.MaxMana, counts, eligible, inputVersion: inventoryVersion)) });
            }
            wands = result.ToArray(); uses = qualifications.ToArray(); return true;
        }

        internal static bool TryRestoreGround(OnlineWandResearchStateSnapshot snapshot, ItemOwnershipStore ground,
            OnlineOwnedWorkspace.GroundTransferState transfer, out string error)
        {
            error = null;
            if (ground == null || transfer == null) return Fail("地面候选未建立。", out error);
            OnlineWandResearchEntrySnapshot[] entries;
            var qualification = new HashSet<string>(StringComparer.Ordinal);
            if (snapshot == null)
            {
                // 新登录没有旧session正文，明确使用原新session初态；不能把它报告为恢复了上次Mana/Draw。
                if (!TryCaptureGround(ground, transfer, Math.Max(1u, ground.Version), out entries, out _, out error)) return false;
            }
            else
            {
                if (!OnlineWandResearchStateCodec.Validate(snapshot, out error)) return false;
                var selected = new List<OnlineWandResearchEntrySnapshot>();
                foreach (var entry in snapshot.GroundWands)
                {
                    if (!ground.TryGet(entry.WandId, out var item) || !ground.Catalog.TryGetWandStats(item, out _))
                        return Fail("保存的地面杖不在完整地面候选。", out error);
                    if (transfer.Revisions.ContainsKey(entry.WandId)) selected.Add(entry);
                }
                int expected = 0;
                foreach (var pair in transfer.Revisions) if (ground.TryGet(pair.Key, out var item) && ground.Catalog.TryGetWandStats(item, out _)) expected++;
                if (selected.Count != expected) return Fail("旧session地面杖缺少连续性，拒绝静默满蓝。", out error);
                foreach (var use in snapshot.GroundSpellUses)
                {
                    if (!ground.TryGet(use.InstanceId, out var item) || !ground.Catalog.TryGet(item.ContentId, out var definition) || definition.Kind != ItemKind.Spell)
                        return Fail("地面资格不属于真实候选卡。", out error);
                    if (transfer.Revisions.ContainsKey(use.InstanceId) && use.Eligible) qualification.Add(use.InstanceId);
                }
                foreach (var pair in transfer.Revisions)
                    if (ground.TryGet(pair.Key, out var item) && ground.Catalog.TryGet(item.ContentId, out var definition) && definition.Kind == ItemKind.Spell)
                    {
                        bool present = false; foreach (var use in snapshot.GroundSpellUses) if (use.InstanceId == pair.Key) { present = true; break; }
                        if (!present) return Fail("地面卡缺少原资格事实。", out error);
                    }
                entries = selected.ToArray();
            }
            var continuity = new WandSessionContinuity();
            foreach (var entry in entries)
            {
                if (!OnlineWandResearchReadModel.TryContinuityEntry(entry, ground.Catalog, out var restored, out error)
                    || !ground.TryGet(entry.WandId, out var wand) || !ground.Catalog.TryGetWandStats(wand, out var actual)
                    || !actual.SameAs(restored.Stats)) return Fail("地面杖固定roll或程序非法。", out error);
                var installed = new string[actual.ProgramCapacity];
                foreach (var card in ground) if (card.Location.Kind == ItemLocationKind.WandSlot && card.Location.OwnerId == entry.WandId)
                    installed[card.Location.SlotIndex] = card.Item.InstanceId;
                for (int i = 0; i < installed.Length; i++) if (installed[i] != restored.InstalledIds[i]) return Fail("地面恢复安装序列不匹配。", out error);
                for (int i = 0; i < restored.Program.Count; i++)
                {
                    var instruction = restored.Program.Instructions[i]; if (instruction.IsIntrinsic) continue;
                    if (!ground.TryGet(instruction.InstanceId, out var card) || card.ContentId != entry.Program.Cards[i].ContentId) return Fail("地面程序ContentId不匹配。", out error);
                    bool q = instruction.Kind == SpellKind.Modify || instruction.Kind == SpellKind.Multicast || instruction.Kind == SpellKind.Copy;
                    int count = q || card.HasUnlimitedCharges ? -1 : card.RemainingCharges;
                    bool eligible = !q || card.HasUnlimitedCharges || qualification.Contains(card.InstanceId) || card.RemainingCharges > 0;
                    if (entry.Inputs.RemainingUses[i] != count || entry.Inputs.Eligibility[i] != eligible) return Fail("地面次数/资格不匹配。", out error);
                }
                continuity.Entries.Add(restored);
            }
            transfer.Wands = continuity; transfer.Qualified.Clear(); foreach (string id in qualification) transfer.Qualified.Add(id);
            return true;
        }
        private static bool Fail(string message, out string error) => OnlineWandResearchStateCodec.Fail(message, out error);
    }
}
