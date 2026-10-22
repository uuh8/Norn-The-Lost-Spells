using System;
namespace Game.Run
{
    public static class SpellLootRoller
    {
        // 两个显式[0,1)样本分别判概率/权重，可测试边界，不每帧消费Random。
        public static bool TryRoll(SpellLootTable table, double chance, double weight, out SpellLootTable.Entry selected)
        {
            selected = default;
            if (table == null || table.Entries == null || table.DropChance < 0 || table.DropChance > 1 || float.IsNaN(table.DropChance)
                || chance < 0 || chance >= 1 || double.IsNaN(chance) || weight < 0 || weight >= 1 || double.IsNaN(weight)) return false;
            long total = 0;
            for (int i = 0; i < table.Entries.Length; i++)
            { var e = table.Entries[i]; if (e.Weight <= 0 || e.Charges < 0 || string.IsNullOrWhiteSpace(e.ContentId)) return false; total += e.Weight; }
            if (total <= 0 || chance >= table.DropChance) return false;
            double point = weight * total; long cumulative = 0;
            for (int i = 0; i < table.Entries.Length; i++)
            { cumulative += table.Entries[i].Weight; if (point < cumulative) { selected = table.Entries[i]; return true; } }
            return false;
        }
    }
}
