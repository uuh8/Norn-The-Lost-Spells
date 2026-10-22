using System;
using System.Collections.Generic;
using System.IO;
using Game.Run;
using Game.Skills;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;
namespace Game.EditorTools
{
    /// <summary>维护正式法术内容与商店/奖励接线；不创建玩家实例或解锁永久知识。</summary>
    public static class SpellContentSetup
    {
        public const string Folder="Assets/_Project/ScriptableObjects/Spell";
        private static readonly List<string> Changed=new List<string>();
        private static void Backup(Object asset)
        {
            string path=AssetDatabase.GetAssetPath(asset);if(string.IsNullOrEmpty(path))return;
            string backup="Temp/Spells/FinishBackup/"+path;Directory.CreateDirectory(Path.GetDirectoryName(backup));
            if(!File.Exists(backup)){File.Copy(path,backup);File.Copy(path+".meta",backup+".meta");}if(!Changed.Contains(path))Changed.Add(path);
        }
        private static void Set(Object asset,string name,string value)
        {var so=new SerializedObject(asset);so.FindProperty(name).stringValue=value;so.ApplyModifiedPropertiesWithoutUndo();}
        private static T Upsert<T>(T template,string category,string name) where T:ScriptableObject
        {
            // 文件名表达类型与用途，ContentId 另行维护；不能把存档中的 dot ID 当作资源文件名。
            if(name.Contains("."))throw new InvalidOperationException("资源名不能使用ContentId："+name);
            string path=Folder+"/"+category+"/"+name+".asset";
            var item=AssetDatabase.LoadAssetAtPath<T>(path);
            if(item==null){Directory.CreateDirectory(Path.GetDirectoryName(path));AssetDatabase.Refresh();item=Object.Instantiate(template);item.name=name;AssetDatabase.CreateAsset(item,path);}
            else Backup(item);
            return item;
        }
        public static void Build()
        {
            Directory.CreateDirectory(Folder);AssetDatabase.Refresh();Changed.Clear();
            var emitItem=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Spell/Items/Item_Spell_Emit_Fireball.asset");var wandTemplate=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Spell/Wands/Items/Item_Wand_StarterA.asset");
            if(emitItem==null||wandTemplate==null)throw new InvalidOperationException("缺少既有火球/法杖模板。");
            var cards=new List<ItemDefinition>();var spells=new List<SpellDefinition>();
            string[] ids={"add_mana","cast_faster","reload_faster","alpha","gamma","short_life","termination","double","damage"};
            string[] mechanics={"Modify_ManaRestore","Modify_CastFaster","Modify_ReloadFaster","Copy_Alpha","Copy_Gamma","Modify_ShortLifetime","Emit_Termination","Multicast_DoubleShot","Modify_DamageUp"};
            string[] names={"法力回流","快速施放","快速周转","α 借用","γ 借用","短寿命","终止触发弹"};
            string[] descriptions={"为本次构建恢复30法力。","减少连续两次施放之间的等待，需在根施放组生效；整轮装填和动画出手点独立。","减少整根法杖读完后的装填等待，不改变轮内连发间隔。","借回收区首张；否则本发首张、待抽首张。目标不移动、不重复扣普通费用。","借待抽末张；待抽为空则借本发末张。目标不移动、不重复扣普通费用。","本组弹体寿命减半，多张依次减半；终止触发弹可提前在空中释放子序列。","命中或正常寿命结束时释放后续一组；取消和离开地图不会释放。"};
            for(int i=0;i<ids.Length;i++)
            {
                SpellDefinition spell;
                if(i>=7)
                {
                    // 已有机制以旧SO为唯一规则来源；保留V2物品ID，避免已发放实例/商店库存失效。
                    string original=i==7?"Spell_Multicast_DoubleShot":"Spell_Modify_DamageUp";
                    spell=AssetDatabase.LoadAssetAtPath<SpellDefinition>("Assets/_Project/ScriptableObjects/Spell/Definitions/"+original+".asset");
                    if(spell==null)throw new InvalidOperationException("缺少既有法术："+original);
                    Backup(spell);
                    if(i==7)spell.ExtraDraws=1; // ExtraDraws是额外次数，原值2与“双重”的说明不符。
                    else spell.Description="将投射物伤害提高至1.5倍。触发弹携带的子序列独立计算修正。";
                    EditorUtility.SetDirty(spell);
                    // 旧物品 ID 仍解析到这份规则；已无引用的重复 SpellDefinition 不再创建。
                }
                else
                {
                // 已持有物品的引用才是既有内容身份；美术整理重命名SO后，不能按旧文件名重建副本。
                var authoredItem=AssetDatabase.LoadAssetAtPath<ItemDefinition>(Folder+"/Items/Item_Spell_"+mechanics[i]+".asset");
                if(authoredItem!=null && authoredItem.SpellDefinition!=null){spell=authoredItem.SpellDefinition;Backup(spell);}
                else spell=Upsert(emitItem.SpellDefinition,"Definitions",i==6?"Spell_Emit_TerminationFireball":"Spell_"+mechanics[i]);
                spell.Kind=i==6?SpellKind.Emit:i==3||i==4?SpellKind.Copy:SpellKind.Modify;
                spell.DisplayName=names[i];spell.Description=descriptions[i];spell.ManaCost=i==0?-30:i==3||i==4?20:5;
                spell.CastDelayDelta=i==1?-.12f:0;spell.ReloadDelta=i==2?-.3f:0;spell.CopyMode=i==4?SpellCopyMode.Gamma:SpellCopyMode.Alpha;
                spell.ModDamageAddFlat=0;spell.ModDamageMul=1;spell.ModSpeedMul=1;spell.ModLifetimeAddSeconds=0;spell.ModLifetimeMul=i==5?.5f:1;spell.ExtraDraws=0;
                spell.LifetimeSeconds=i==6?2:-1;spell.PayloadTrigger=i==6?PayloadTriggerMode.OnTermination:PayloadTriggerMode.None;
                spell.BaseDamage=i==6?5:0;spell.CastSfx=null;EditorUtility.SetDirty(spell);
                }
                spells.Add(spell);
                var item=Upsert(emitItem,"Items","Item_Spell_"+mechanics[i]+(i>=7?"Alias":""));Set(item,"_contentId","norn.v2."+ids[i]);Set(item,"_displayName",spell.DisplayName);
                var data=new SerializedObject(item);data.FindProperty("_spellDefinition").objectReferenceValue=spell;data.FindProperty("_icon").objectReferenceValue=spell.Icon;data.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(item);cards.Add(item);
            }
            // 旧库存与新库存共用Icon/名称；按实例持有的多张同类卡仍是合法财产，不能删除。
            foreach(string guid in AssetDatabase.FindAssets("t:ItemDefinition",new[]{"Assets/_Project/ScriptableObjects/Spell/Items"}))
            {
                var item=AssetDatabase.LoadAssetAtPath<ItemDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if(item.SpellDefinition!=spells[7]&&item.SpellDefinition!=spells[8])continue;
                Backup(item);Set(item,"_displayName",item.SpellDefinition.DisplayName);var data=new SerializedObject(item);
                data.FindProperty("_icon").objectReferenceValue=item.SpellDefinition.Icon;data.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(item);
            }
            var wands=new List<ItemDefinition>();string[] labels={"迅捷研习杖","重载研习杖","乱序研习杖"};
            string[] wandNames={"ResearchRapid","ResearchHeavy","ResearchShuffle"};
            for(int i=0;i<3;i++)
            {
                var stats=Upsert(wandTemplate.WandDefinition,"Wands/Stats","Wand_"+wandNames[i]);var data=new SerializedObject(stats);
                data.FindProperty("_programCapacity").intValue=i==0?16:32;data.FindProperty("_maxMana").floatValue=i==1?240:120;
                data.FindProperty("_manaRegenPerSecond").floatValue=i==1?25:15;data.FindProperty("_castCooldown").floatValue=i==0?.05f:i==1?.3f:.12f;
                data.FindProperty("_baseReloadTime").floatValue=i==1?.8f:.2f;data.FindProperty("_baseSpreadDegrees").floatValue=i==2?8:0;
                data.FindProperty("_shuffle").boolValue=i==2;data.FindProperty("_baseDraws").intValue=1;
                var fixedIds=data.FindProperty("_alwaysCastContentIds");fixedIds.arraySize=i==1?1:0;if(i==1)fixedIds.GetArrayElementAtIndex(0).stringValue="norn.v2.reload_faster";
                data.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(stats);
                var item=Upsert(wandTemplate,"Wands/Items","Item_Wand_"+wandNames[i]);Set(item,"_contentId","norn.v2.wand."+i);Set(item,"_displayName",labels[i]);
                data=new SerializedObject(item);data.FindProperty("_wandDefinition").objectReferenceValue=stats;data.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(item);wands.Add(item);
            }
            // 扩充既有目录，保留GUID和旧条目，跨Hub/Practice/存档恢复看到同一合法内容集合。
            foreach(string guid in AssetDatabase.FindAssets("t:ItemCatalog",new[]{"Assets/_Project/ScriptableObjects/"}))
            {
                var catalog=AssetDatabase.LoadAssetAtPath<ItemCatalog>(AssetDatabase.GUIDToAssetPath(guid));Backup(catalog);var data=new SerializedObject(catalog);var definitions=data.FindProperty("_definitions");
                foreach(var item in cards)Append(definitions,item);foreach(var item in wands)Append(definitions,item);data.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(catalog);
                // 两个物品ID保留，但新的随机奖励继续使用旧完整库的规范物品。
                // 其他显式选择保留；这里只有这两种已合并法术需要覆盖反向歧义。
                data.Update();var canonical=data.FindProperty("_canonicalSpellItems");
                foreach(string original in new[]{"Item_Spell_Multicast_DoubleShot","Item_Spell_Modify_DamageUp"})
                {
                    var chosen=AssetDatabase.LoadAssetAtPath<ItemDefinition>(Folder+"/Items/"+original+".asset");
                    if(chosen==null)throw new InvalidOperationException("缺少规范法术物品："+original);
                    int slot=-1;for(int i=0;i<canonical.arraySize;i++)
                    {var current=canonical.GetArrayElementAtIndex(i).objectReferenceValue as ItemDefinition;if(current!=null&&current.SpellDefinition==chosen.SpellDefinition){slot=i;break;}}
                    if(slot<0)slot=canonical.arraySize++;
                    canonical.GetArrayElementAtIndex(slot).objectReferenceValue=chosen;
                }
                data.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(catalog);
                if(!catalog.TryCreateIndex(out _,out var error))throw new InvalidOperationException(error);
            }
            var shop=AssetDatabase.LoadAssetAtPath<ShopDefinition>("Assets/_Project/ScriptableObjects/World/Hub/Trading/Shop.asset");Backup(shop);
            var offers=new List<ShopOffer>(shop.Offers);foreach(var item in cards)AddOffer(offers,item,35,8);foreach(var item in wands)AddOffer(offers,item,180,0);shop.Offers=offers.ToArray();EditorUtility.SetDirty(shop);
            var rewards=AssetDatabase.LoadAssetAtPath<DungeonRewardProfile>("Assets/_Project/ScriptableObjects/World/Dungeon/DungeonRewards.asset");Backup(rewards);
            var pool=new List<SpellDefinition>(rewards.Pool);
            foreach(var spell in spells)if(!pool.Contains(spell))pool.Add(spell);rewards.Pool=pool.ToArray();EditorUtility.SetDirty(rewards);
            for(int stage=1;stage<=4;stage++)
            {
                var table=AssetDatabase.LoadAssetAtPath<DungeonLootTable>("Assets/_Project/ScriptableObjects/World/Dungeon/Stage"+stage+"Loot.asset");Backup(table);
                // 同一候选池中旧/new ID代表同一规则时，仅保留一个候选，避免重复加权。
                foreach(var entry in table.Items)
                {
                    var seen=new HashSet<SpellDefinition>();var entries=new List<ItemDefinition>();
                    foreach(var item in entry.Pool)
                    {
                        var candidate=item.SpellDefinition==spells[7]?cards[7]:item.SpellDefinition==spells[8]?cards[8]:item;
                        if((candidate==cards[7]||candidate==cards[8])&&!seen.Add(candidate.SpellDefinition))continue;
                        entries.Add(candidate);
                    }
                    entry.Pool=entries.ToArray();
                }
                if(table.Items.Length>0){var entries=new List<ItemDefinition>(table.Items[0].Pool);foreach(var item in cards)if(!entries.Contains(item))entries.Add(item);table.Items[0].Pool=entries.ToArray();}
                EditorUtility.SetDirty(table);
                if(table.WandRolls!=null){Backup(table.WandRolls);table.WandRolls.Cooldown=new Vector2(.05f,.3f);table.WandRolls.Capacity=new Vector2Int(3,32);
                    table.WandRolls.AlwaysCastCandidates=new[]{"norn.v2.cast_faster","norn.v2.reload_faster"};table.WandRolls.AlwaysCastProbability=.12f;EditorUtility.SetDirty(table.WandRolls);}
            }
            AssetDatabase.SaveAssets();File.WriteAllLines("Temp/Spells/content-modified-assets.txt",Changed);
        }
        private static void Append(SerializedProperty array,Object item)
        {for(int i=0;i<array.arraySize;i++)if(array.GetArrayElementAtIndex(i).objectReferenceValue==item)return;int last=array.arraySize++;array.GetArrayElementAtIndex(last).objectReferenceValue=item;}
        private static void AddOffer(List<ShopOffer> offers,ItemDefinition item,long price,int charges)
        {foreach(var offer in offers)if(offer.ContentId==item.ContentId){offer.DisplayName=item.DisplayName;offer.Icon=item.Icon;return;}offers.Add(new ShopOffer{OfferId=item.ContentId,ContentId=item.ContentId,DisplayName=item.DisplayName,Icon=item.Icon,BuyPrice=price,SellPrice=price/3,Stock=4,InitialCharges=charges});}
    }
}
