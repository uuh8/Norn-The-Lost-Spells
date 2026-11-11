using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>一次完整混合表抽样的只读结果；计划保留原entry/copy键与散布位置，提交失败不能重新Roll。</summary>
    public sealed class DungeonLootPlannedItem
    {
        public ItemDefinition Definition { get; }
        public string Key { get; }
        public Vector3 Position { get; }
        public int Charges { get; }
        public bool Unlimited { get; }
        public int MaterialQuantity { get; }
        public WandStats? WandStats { get; }
        internal DungeonLootPlannedItem(ItemDefinition definition, string key, Vector3 position, int charges,
            bool unlimited, int materialQuantity, WandStats? wandStats)
        { Definition = definition; Key = key; Position = position; Charges = charges; Unlimited = unlimited; MaterialQuantity = materialQuantity; WandStats = wandStats; }
    }

    public sealed class DungeonLootPlan
    {
        public bool CoinDropped { get; }
        public int CoinAmount { get; }
        public IReadOnlyList<DungeonLootPlannedItem> Items { get; }
        internal DungeonLootPlan(bool coin, int amount, List<DungeonLootPlannedItem> items)
        { CoinDropped = coin; CoinAmount = amount; Items = items.AsReadOnly(); }
    }

    /// <summary>
    /// 单机与DS共用原采样算法，复用原纯Roller和Unity随机流。不得先筛选Container表：
    /// coin、其它条目、次数、散布、有限卡次数与WandProfile都会影响后续随机流。
    /// </summary>
    public static class DungeonLootPlanner
    {
        private static double Sample() => Math.Min(UnityEngine.Random.value, .999999999d);
        public static DungeonLootPlan Roll(DungeonLootTable table, string key, Vector3 origin, Func<Vector3, Vector3> resolvePosition = null)
        {
            if (table == null || !table.IsValid || string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("完整掉落计划需要原有效表与稳定Run键。");
            bool coin = DungeonLootRoller.ShouldDrop(table.CoinChance, Sample());
            int amount = coin ? DungeonLootRoller.Quantity(table.MinCoins, table.MaxCoins, Sample()) : 0;
            var items = new List<DungeonLootPlannedItem>();
            for (int i = 0; i < table.Items.Length; i++)
            {
                var entry = table.Items[i];
                if (!DungeonLootRoller.ShouldDrop(entry.Chance, Sample())) continue;
                var definition = entry.Pool[DungeonLootRoller.Quantity(0, entry.Pool.Length - 1, Sample())];
                int count = DungeonLootRoller.Quantity(entry.MinCount, entry.MaxCount, Sample());
                for (int j = 0; j < count; j++)
                {
                    var offset = UnityEngine.Random.insideUnitCircle * .9f;
                    Vector3 position = origin + new Vector3(offset.x, 0, offset.y);
                    if (resolvePosition != null) position = resolvePosition(position);
                    int charges = definition.Kind == ItemKind.Spell && entry.MaxSpellCharges > 0
                        ? DungeonLootRoller.Quantity(entry.SpellCharges, entry.MaxSpellCharges, Sample()) : entry.SpellCharges;
                    WandStats? stats = definition.Kind == ItemKind.Wand && table.WandRolls != null ? table.WandRolls.Roll() : (WandStats?)null;
                    items.Add(new DungeonLootPlannedItem(definition, key + "." + i + "." + j, position, charges,
                        entry.UnlimitedSpell, entry.MaterialQuantity, stats));
                }
            }
            return new DungeonLootPlan(coin, amount, items);
        }

        /// <summary>
        /// 仅在启用P7新来源时证明旧Ground上限仍够用，不改变背包/堆叠规则或原Ground构造。
        /// Owned最多4096；容器只能在当前包的真实占据格或7工具位，不能藏在法术储存/法杖/嵌套包。
        /// 当前Catalog：maxBag=64、minCells=1、maxStack=1，4*(64+7)+12=296，小于原16384。
        /// </summary>
        public static long CalculateContainerLootBound(ItemCatalogIndex catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            long bagCells = 0, maxStack = 0; int minCells = int.MaxValue;
            foreach (var rules in catalog.Rules)
            {
                if (rules.Kind == ItemKind.Backpack)
                    bagCells = Math.Max(bagCells, (long)rules.BackpackWidth * rules.BackpackHeight);
                if (rules.Kind != ItemKind.Container) continue;
                if (rules.Shape.CellCount < 1 || rules.MaxStack < 1) throw new ArgumentException("容器规则不能证明有限占据。");
                minCells = Math.Min(minCells, rules.Shape.CellCount); maxStack = Math.Max(maxStack, rules.MaxStack);
            }
            if (minCells == int.MaxValue) throw new ArgumentException("P7容器来源缺少可信容器规则。");
            long owned = OnlineOwnedAssetSnapshot.MaximumItems;
            // 先按Owned上限裁剪乘数，避免极大但合法authoring尺寸的long乘法溢出。
            long groups = Math.Min(owned, bagCells / minCells);
            long carried = Math.Min(owned, groups * Math.Min(owned, maxStack) + HeldItemSlots.ToolCount);
            long total = OnlineActorLimits.PlayerCount * carried + OnlineDungeonContainerLoot.MaximumNpcPlans;
            if (total > OnlineGroundInventorySnapshot.MaximumItems)
                throw new ArgumentException("该Catalog无法证明P7新来源仍在原Ground容量内，拒绝配置；不扩容、不淘汰物品。");
            return total;
        }

        /// <summary>
        /// 完整Item来源只证明原12敌的48个稳定登记key；所有玩家可从SpellStorage携带物品，
        /// 不能把4×4096加来源的粗和当成正式配置失败。真实Ground仍按原16384总条目检查，
        /// 已满时新计划保持pending而不是扩容、删物或把已抽中结果丢掉。
        /// </summary>
        public static long CalculateItemLootBound(ItemCatalogIndex catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            return OnlineDungeonContainerLoot.MaximumLootItems;
        }
    }
}

