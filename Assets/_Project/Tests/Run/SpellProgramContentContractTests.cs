using NUnit.Framework;
using UnityEditor;
using Game.Skills;
namespace Game.Run.Tests
{
    public sealed class SpellProgramContentContractTests
    {
        [Test] public void EveryDungeonRewardResolvesToOneCanonicalInventoryItem()
        {
            var catalog=AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/ScriptableObjects/World/Hub/Inventory/Catalog.asset");
            var rewards=AssetDatabase.LoadAssetAtPath<DungeonRewardProfile>("Assets/_Project/ScriptableObjects/World/Dungeon/DungeonRewards.asset");
            Assert.That(catalog.TryCreateIndex(out var index,out var error),Is.True,error);
            foreach(var spell in rewards.Pool)Assert.That(index.TryFindSpell(spell,out _),Is.True,spell.name+" 缺少可确定的奖励物品映射");
        }
        [TestCase("double", "Spell_Multicast_DoubleShot")]
        [TestCase("damage", "Spell_Modify_DamageUp")]
        public void ExistingEffectsReuseOriginalSpellAndIcon(string suffix, string originalName)
        {
            var original=AssetDatabase.LoadAssetAtPath<SpellDefinition>("Assets/_Project/ScriptableObjects/Spell/Definitions/"+originalName+".asset");
            var catalog=AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/ScriptableObjects/World/Hub/Inventory/Catalog.asset");
            Assert.That(catalog.TryCreateIndex(out var index,out _),Is.True);
            Assert.That(index.TryGet("norn.v2."+suffix,out var item),Is.True,"旧存档 ID 必须仍可恢复。");
            // 对象身份比同名/同图标更强：背包和研究台必须执行同一份规则。
            Assert.That(item.SpellDefinition,Is.SameAs(original));
            Assert.That(item.Icon,Is.SameAs(original.Icon));
            Assert.That(item.DisplayName,Is.EqualTo(original.DisplayName));
            var shop=AssetDatabase.LoadAssetAtPath<ShopDefinition>("Assets/_Project/ScriptableObjects/World/Hub/Trading/Shop.asset");
            int offers=0;foreach(var offer in shop.Offers)if(offer.ContentId==item.ContentId)
            {offers++;Assert.That(offer.Icon,Is.SameAs(original.Icon));Assert.That(offer.DisplayName,Is.EqualTo(original.DisplayName));}
            Assert.That(offers,Is.EqualTo(1));
            var rewards=AssetDatabase.LoadAssetAtPath<DungeonRewardProfile>("Assets/_Project/ScriptableObjects/World/Dungeon/DungeonRewards.asset");
            int count=0;
            foreach(var spell in rewards.Pool)if(spell==original)count++;
            Assert.That(count,Is.EqualTo(1));
            Assert.That(AssetDatabase.IsValidFolder("Assets/_Project/ScriptableObjects/Spell/Compatibility"),Is.False,"已无引用的重复规则不应继续占正式目录。");
            for(int stage=1;stage<=4;stage++)
            {
                var table=AssetDatabase.LoadAssetAtPath<DungeonLootTable>("Assets/_Project/ScriptableObjects/World/Dungeon/Stage"+stage+"Loot.asset");
                foreach(var entry in table.Items)
                {
                    count=0;foreach(var candidate in entry.Pool)if(candidate.SpellDefinition==original)count++;
                    Assert.That(count,Is.LessThanOrEqualTo(1),"同一候选池不能给重复定义额外掉落权重。");
                }
            }
        }

        [Test] public void TerminationHasADifferentTriggerConditionFromExistingFireballs()
        {
            var termination=AssetDatabase.LoadAssetAtPath<SpellDefinition>("Assets/_Project/ScriptableObjects/Spell/Definitions/Spell_Emit_Termination.asset");
            var hit=AssetDatabase.LoadAssetAtPath<SpellDefinition>("Assets/_Project/ScriptableObjects/Spell/Definitions/Spell_Emit_TriggerFireball.asset");
            var timer=AssetDatabase.LoadAssetAtPath<SpellDefinition>("Assets/_Project/ScriptableObjects/Spell/Definitions/Spell_Emit_TimedFireball.asset");
            Assert.That(termination.PayloadTrigger,Is.EqualTo(PayloadTriggerMode.OnTermination));
            Assert.That(hit.PayloadTrigger,Is.EqualTo(PayloadTriggerMode.OnImpact));
            Assert.That(timer.PayloadTrigger,Is.EqualTo(PayloadTriggerMode.AfterDelay));
        }

        [Test] public void OriginalDoubleDrawsExactlyTwoAndDamageRetainsOriginalMultiplier()
        {
            var multi=AssetDatabase.LoadAssetAtPath<SpellDefinition>("Assets/_Project/ScriptableObjects/Spell/Definitions/Spell_Multicast_DoubleShot.asset");
            var damage=AssetDatabase.LoadAssetAtPath<SpellDefinition>("Assets/_Project/ScriptableObjects/Spell/Definitions/Spell_Modify_DamageUp.asset");
            var fire=AssetDatabase.LoadAssetAtPath<SpellDefinition>("Assets/_Project/ScriptableObjects/Spell/Definitions/Spell_Emit_Fireball.asset");
            Assert.That(multi.ExtraDraws,Is.EqualTo(1));
            Assert.That(damage.ModDamageAddFlat,Is.Zero);Assert.That(damage.ModDamageMul,Is.EqualTo(1.5f));
            Assert.That(SpellProgramCompiler.TryCompile(new[]{multi,fire,damage,fire,fire},new SpellProgramCompileInputs(1),out var program,out var diagnostic),Is.True,diagnostic.Message);
            var buffer=new SpellBuildBuffer();
            Assert.That(new SpellProgramMachine().TryBuild(program,WandDrawSnapshot.CreateInitial(program),new SpellExecutionInputs(1000,1000),buffer,out var summary),Is.True,summary.Diagnostic.Message);
            Assert.That(buffer.Graph.RootOutputCount,Is.EqualTo(2));
            foreach(var output in buffer.Graph.Outputs)Assert.That(output.Command.Damage,Is.EqualTo(fire.BaseDamage*1.5f));
        }

        [Test]public void GeneratedCardsWandsCatalogAndFourRewardsAreLegal()
        {
            string[] names={"add_mana","cast_faster","reload_faster","alpha","gamma","short_life","termination","double","damage"};
            var catalog=AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/ScriptableObjects/World/Hub/Inventory/Catalog.asset");
            Assert.That(catalog.TryCreateIndex(out var index,out var error),Is.True,error);
            foreach(string name in names)
            {
                Assert.That(index.TryGet("norn.v2."+name,out var item),Is.True);Assert.That(item,Is.Not.Null);
                Assert.That(item.ContentId,Is.EqualTo("norn.v2."+name));Assert.That(item.Icon,Is.Not.Null);Assert.That(item.SpellDefinition.Description,Is.Not.Empty);
                Assert.That(SpellProgramCompiler.TryCompile(new[]{item.SpellDefinition},new SpellProgramCompileInputs(1),out _,out var diagnostic),Is.True,diagnostic.Message);Assert.That(index.TryGet(item.ContentId,out _),Is.True);
            }
            for(int i=0;i<3;i++){Assert.That(index.TryGet("norn.v2.wand."+i,out var item),Is.True);Assert.That(item.WandDefinition.TryFreeze(out var stats),Is.True);Assert.That(stats.ProgramCapacity,Is.GreaterThanOrEqualTo(16));}
            for(int i=1;i<=4;i++){var table=AssetDatabase.LoadAssetAtPath<DungeonLootTable>("Assets/_Project/ScriptableObjects/World/Dungeon/Stage"+i+"Loot.asset");Assert.That(table.IsValid,Is.True);foreach(var drop in table.Items)foreach(var item in drop.Pool)Assert.That(index.TryGet(item.ContentId,out _),Is.True);}
        }
    }
}
