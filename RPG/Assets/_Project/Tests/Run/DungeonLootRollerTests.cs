using System;
using NUnit.Framework;

namespace Game.Run.Tests
{
    public sealed class DungeonLootRollerTests
    {

        private readonly System.Collections.Generic.List<UnityEngine.Object> _lootObjects = new System.Collections.Generic.List<UnityEngine.Object>();
        [TearDown] public void CleanupLootPlans()
        { foreach (var owned in _lootObjects) if (owned != null) UnityEngine.Object.DestroyImmediate(owned); _lootObjects.Clear(); }
        private ItemDefinition LootDefinition(string id, ItemKind kind)
        {
            var value = UnityEngine.ScriptableObject.CreateInstance<ItemDefinition>(); _lootObjects.Add(value);
            var serialized = new UnityEditor.SerializedObject(value);
            serialized.FindProperty("_contentId").stringValue = id; serialized.FindProperty("_kind").enumValueIndex = (int)kind;
            serialized.ApplyModifiedPropertiesWithoutUndo(); return value;
        }
        private DungeonLootTable MixedTable()
        {
            var table = UnityEngine.ScriptableObject.CreateInstance<DungeonLootTable>(); _lootObjects.Add(table);
            table.CoinChance = 1; table.MinCoins = 10; table.MaxCoins = 25;
            table.WandRolls = UnityEngine.ScriptableObject.CreateInstance<DungeonWandRollProfile>(); _lootObjects.Add(table.WandRolls);
            table.WandRolls.AlwaysCastCandidates = new[] { "plan.always-a", "plan.always-b" }; table.WandRolls.AlwaysCastProbability = 1;
            table.Items = new[]
            {
                new DungeonItemDrop { Chance = 1, Pool = new[] { LootDefinition("plan.spell", ItemKind.Spell) }, SpellCharges = 3, MaxSpellCharges = 9 },
                new DungeonItemDrop { Chance = 1, Pool = new[] { LootDefinition("plan.wand", ItemKind.Wand) } },
                new DungeonItemDrop { Chance = .35f, Pool = new[] { LootDefinition("plan.other", ItemKind.Consumable) } },
                new DungeonItemDrop { Chance = 1, Pool = new[] { LootDefinition("plan.bottle0", ItemKind.Container),
                    LootDefinition("plan.bottle1", ItemKind.Container), LootDefinition("plan.bottle2", ItemKind.Container), LootDefinition("plan.bottle3", ItemKind.Container) } }
            };
            return table;
        }
        private static object PlanValue(object value, string name)
        {
            var property = value.GetType().GetProperty(name); Assert.That(property, Is.Not.Null, name); return property.GetValue(value);
        }
        private sealed class LegacyItem
        { internal ItemDefinition Definition; internal string Key; internal UnityEngine.Vector3 Position; internal int Charges;
            internal bool Unlimited; internal int MaterialQuantity; internal WandStats? WandStats; }
        private static double LegacySample() => Math.Min(UnityEngine.Random.value, .999999999d);

        [TestCase(7)] [TestCase(31)]
        public void MixedPlanPreservesLegacyRandomStreamAndOriginalEntryKeys(int seed)
        {
            var table = MixedTable(); var saved = UnityEngine.Random.state;
            try
            {
                // 迁移回归oracle保留旧Adapter原调用顺序，验证不是先过滤Container再独立抽一遍。
                UnityEngine.Random.InitState(seed);
                bool coin = DungeonLootRoller.ShouldDrop(table.CoinChance, LegacySample());
                int coins = coin ? DungeonLootRoller.Quantity(table.MinCoins, table.MaxCoins, LegacySample()) : 0;
                var expected = new System.Collections.Generic.List<LegacyItem>(); var origin = new UnityEngine.Vector3(2, 3, 4);
                for (int i = 0; i < table.Items.Length; i++)
                {
                    var entry = table.Items[i]; if (!DungeonLootRoller.ShouldDrop(entry.Chance, LegacySample())) continue;
                    var definition = entry.Pool[DungeonLootRoller.Quantity(0, entry.Pool.Length - 1, LegacySample())];
                    int count = DungeonLootRoller.Quantity(entry.MinCount, entry.MaxCount, LegacySample());
                    for (int j = 0; j < count; j++)
                    {
                        var offset = UnityEngine.Random.insideUnitCircle * .9f;
                        int charges = definition.Kind == ItemKind.Spell && entry.MaxSpellCharges > 0
                            ? DungeonLootRoller.Quantity(entry.SpellCharges, entry.MaxSpellCharges, LegacySample()) : entry.SpellCharges;
                        WandStats? stats = definition.Kind == ItemKind.Wand && table.WandRolls != null ? table.WandRolls.Roll() : (WandStats?)null;
                        expected.Add(new LegacyItem { Definition = definition, Key = "p7.run.1.2." + i + "." + j,
                            Position = origin + new UnityEngine.Vector3(offset.x, 0, offset.y), Charges = charges,
                            Unlimited = entry.UnlimitedSpell, MaterialQuantity = entry.MaterialQuantity, WandStats = stats });
                    }
                }
                float next = UnityEngine.Random.value;
                UnityEngine.Random.InitState(seed);
                var type = Type.GetType("Game.Run.DungeonLootPlanner, Game.Run"); Assert.That(type, Is.Not.Null, "缺少共享完整掉落plan采样。");
                var plan = type.GetMethod("Roll").Invoke(null, new object[] { table, "p7.run.1.2", origin, null });
                Assert.That(PlanValue(plan, "CoinDropped"), Is.EqualTo(coin)); Assert.That(PlanValue(plan, "CoinAmount"), Is.EqualTo(coins));
                var actual = new System.Collections.Generic.List<object>();
                foreach (var item in (System.Collections.IEnumerable)PlanValue(plan, "Items")) actual.Add(item);
                Assert.That(actual.Count, Is.EqualTo(expected.Count));
                for (int i = 0; i < actual.Count; i++)
                {
                    Assert.That(PlanValue(actual[i], "Definition"), Is.SameAs(expected[i].Definition));
                    Assert.That(PlanValue(actual[i], "Key"), Is.EqualTo(expected[i].Key));
                    Assert.That(PlanValue(actual[i], "Position"), Is.EqualTo(expected[i].Position));
                    Assert.That(PlanValue(actual[i], "Charges"), Is.EqualTo(expected[i].Charges));
                    Assert.That(PlanValue(actual[i], "Unlimited"), Is.EqualTo(expected[i].Unlimited));
                    Assert.That(PlanValue(actual[i], "MaterialQuantity"), Is.EqualTo(expected[i].MaterialQuantity));
                    var actualStats = (WandStats?)PlanValue(actual[i], "WandStats");
                    Assert.That(actualStats.HasValue, Is.EqualTo(expected[i].WandStats.HasValue));
                    if (actualStats.HasValue) AssertExactWandStats(actualStats.Value, expected[i].WandStats.Value);
                }
                Assert.That(UnityEngine.Random.value, Is.EqualTo(next), "完整混合表消耗的Unity随机流必须与旧Adapter一致。");
            }
            finally { UnityEngine.Random.state = saved; }
        }

        private static void AssertExactWandStats(WandStats actual, WandStats expected)
        {
            // WandStats构造会复制AlwaysCast到新的只读列表；默认struct Equals比较其引用会误报。
            // 逐数值精确比较并逐项核对数组，不加容差，也不减少随机次数或属性覆盖。
            Assert.That(actual.MaxMana, Is.EqualTo(expected.MaxMana));
            Assert.That(actual.ManaRegenPerSecond, Is.EqualTo(expected.ManaRegenPerSecond));
            Assert.That(actual.CastCooldown, Is.EqualTo(expected.CastCooldown));
            Assert.That(actual.BaseCastDelay, Is.EqualTo(expected.BaseCastDelay));
            Assert.That(actual.BaseReloadTime, Is.EqualTo(expected.BaseReloadTime));
            Assert.That(actual.BaseSpreadDegrees, Is.EqualTo(expected.BaseSpreadDegrees));
            Assert.That(actual.ProgramCapacity, Is.EqualTo(expected.ProgramCapacity));
            Assert.That(actual.BaseDraws, Is.EqualTo(expected.BaseDraws));
            Assert.That(actual.Shuffle, Is.EqualTo(expected.Shuffle));
            Assert.That(actual.AlwaysCastContentIds.Count, Is.EqualTo(expected.AlwaysCastContentIds.Count));
            for (int i = 0; i < expected.AlwaysCastContentIds.Count; i++)
                Assert.That(actual.AlwaysCastContentIds[i], Is.EqualTo(expected.AlwaysCastContentIds[i]));
        }

        [Test]
        public void NpcMixedPlanRetriesEveryOriginalItemWithoutRerollingOrBlockingOtherItems()
        {
            var table = MixedTable(); var saved = UnityEngine.Random.state;
            // 测试夹具令四条都命中，以直接验证完整提交；生产SO概率和原随机调用顺序不改。
            table.Items[2].Chance = 1; table.Items[0].UnlimitedSpell = true;
            int attempts = 0; bool accept = false, acceptSpell = false;
            var observed = new System.Collections.Generic.Dictionary<string, DungeonLootPlannedItem>();
            Func<DungeonLootPlannedItem, bool> register = item =>
            {
                attempts++;
                Assert.That(item.Key, Does.StartWith("p7.retry.1.2."));
                Assert.That(item.Position.y, Is.EqualTo(4f));
                Assert.That((item.Position - new UnityEngine.Vector3(2, 4, 6)).sqrMagnitude, Is.LessThanOrEqualTo(.81f));
                if (observed.TryGetValue(item.Key, out var original)) Assert.That(item, Is.SameAs(original), "重试必须保留同一个冻结结果与次数/随机法杖状态。");
                else observed.Add(item.Key, item);
                return accept && (item.Definition.Kind != ItemKind.Spell || acceptSpell);
            };
            var constructor = typeof(OnlineDungeonContainerLoot).GetConstructor(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
                new[] { typeof(string), typeof(Func<DungeonLootPlannedItem, bool>) }, null);
            Assert.That(constructor, Is.Not.Null);
            using var queue = (OnlineDungeonContainerLoot)constructor.Invoke(new object[] { "retry", register });
            try
            {
                UnityEngine.Random.InitState(7);
                Assert.That(queue.TryRollOnce(Game.Core.OnlineActorLimits.FirstNpcActorId, 1, 2, table,
                    new UnityEngine.Vector3(2, 4, 6)), Is.True);
                Assert.That(attempts, Is.EqualTo(4)); Assert.That(queue.PendingCount, Is.EqualTo(4));
                Assert.That(observed["p7.retry.1.2.0.0"].Unlimited, Is.True);
                Assert.That(observed["p7.retry.1.2.1.0"].WandStats.HasValue, Is.True);
                var afterPlan = UnityEngine.Random.state; float next = UnityEngine.Random.value;
                UnityEngine.Random.state = afterPlan;
                Assert.That(queue.TryRollOnce(Game.Core.OnlineActorLimits.FirstNpcActorId, 1, 2, table,
                    new UnityEngine.Vector3(2, 4, 6)), Is.True);
                // Frozen time contributes no retry debt; the next live interval still owes the full original .15s.
                queue.Step(99, false); queue.Step(.14f, true); Assert.That(attempts, Is.EqualTo(4));
                accept = true; queue.Step(.02f, true);
                Assert.That(attempts, Is.EqualTo(8)); Assert.That(queue.PendingCount, Is.EqualTo(1), "卡片登记失败不能阻止其后的杖/食品/空瓶成功。");
                acceptSpell = true; queue.Step(.16f, true);
                Assert.That(attempts, Is.EqualTo(9)); Assert.That(queue.PendingCount, Is.Zero);
                queue.Step(100, true); Assert.That(attempts, Is.EqualTo(9));
                Assert.That(UnityEngine.Random.value, Is.EqualTo(next), "Retry and duplicate death must not consume RNG.");
            }
            finally { UnityEngine.Random.state = saved; }
        }

        [TestCase(0d,0d,false)]
        [TestCase(1d,.999999d,true)]
        [TestCase(.25d,.25d,false)]
        [TestCase(.25d,.249999d,true)]
        public void ChanceHasStrictBoundary(double chance,double sample,bool expected)
        {
            // 先通过反射检查契约，使尚未实现的规则得到真正RED而非编译错误。
            var type=Type.GetType("Game.Run.DungeonLootRoller, Game.Run");
            Assert.That(type,Is.Not.Null,"地牢掉落应有可确定测试的纯抽样规则。");
            Assert.That(type.GetMethod("ShouldDrop").Invoke(null,new object[]{chance,sample}),Is.EqualTo(expected));
        }
        [TestCase(10,25,0d,10)]
        [TestCase(10,25,.999999d,25)]
        [TestCase(7,7,.5d,7)]
        public void QuantityIncludesBothConfiguredEndpoints(int min,int max,double sample,int expected)
        {
            var type=Type.GetType("Game.Run.DungeonLootRoller, Game.Run");Assert.That(type,Is.Not.Null);
            Assert.That(type.GetMethod("Quantity").Invoke(null,new object[]{min,max,sample}),Is.EqualTo(expected));
        }
    }
}
