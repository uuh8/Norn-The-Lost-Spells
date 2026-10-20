using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 物品配置目录。初始化只发生在开局/恢复等离散操作，成功后发布完整索引；
    /// 某条配置非法时不把已经处理的半份目录交给库存，否则恢复可能静默吞掉未知物品。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Run/Inventory/Item Catalog", fileName = "ItemCatalog")]
    public sealed class ItemCatalog : ScriptableObject
    {
        [SerializeField] private ItemDefinition[] _definitions = Array.Empty<ItemDefinition>();
        [SerializeField, Tooltip("多个旧物品ID共享同一法术时，明确指定新奖励使用的物品；不删除旧ID。未指定的法术仍须唯一。")]
        private ItemDefinition[] _canonicalSpellItems = Array.Empty<ItemDefinition>();
        [SerializeField, Min(1), Tooltip("玩家独立法术专区容量；与普通包和法杖程序分别计算。首版12格。")]
        private int _spellStorageCapacity = 12;

        public bool TryCreateIndex(out ItemCatalogIndex index, out string error)
        {
            index = null;
            error = null;
            if (_definitions == null || _spellStorageCapacity < 1 || _spellStorageCapacity > 128)
            {
                error = "物品目录未配置定义数组。";
                return false;
            }

            var definitions = new Dictionary<string, ItemDefinition>(_definitions.Length, StringComparer.Ordinal);
            var rules = new Dictionary<string, InventoryItemRules>(_definitions.Length, StringComparer.Ordinal);
            var wands = new Dictionary<string, WandStats>(StringComparer.Ordinal);
            for (int i = 0; i < _definitions.Length; i++)
            {
                ItemDefinition definition = _definitions[i];
                if (definition == null || string.IsNullOrWhiteSpace(definition.ContentId))
                {
                    error = $"物品目录第 {i} 项缺少定义或稳定 ContentId。";
                    return false;
                }
                if (definition.Kind < ItemKind.Spell || definition.Kind > ItemKind.Alchemy)
                {
                    error = $"物品 '{definition.ContentId}' 的种类无效。";
                    return false;
                }
                if (definition.Kind == ItemKind.Spell && definition.SpellDefinition == null)
                {
                    error = $"法术物品 '{definition.ContentId}' 缺少 SpellDefinition。";
                    return false;
                }
                if (definition.Kind != ItemKind.Spell && definition.SpellDefinition != null)
                {
                    error = $"非施法物品 '{definition.ContentId}' 不应持有 SpellDefinition。";
                    return false;
                }
                if (definitions.ContainsKey(definition.ContentId))
                {
                    error = $"物品目录存在重复 ContentId：'{definition.ContentId}'。";
                    return false;
                }
                definitions.Add(definition.ContentId, definition);
                if (!definition.TryCreateRules(out InventoryItemRules itemRules, out string rulesError))
                { error = $"物品 '{definition.ContentId}' 的空间配置非法：{rulesError}"; return false; }
                rules.Add(definition.ContentId, itemRules);
                if (definition.Kind == ItemKind.Wand && definition.WandDefinition.TryFreeze(out WandStats stats)) wands.Add(definition.ContentId, stats);
            }

            // 正向ID用于存档，反向法术映射用于发奖励。显式选择解决共享规则后的歧义，
            // 不能依赖Dictionary遍历顺序，也不能为兼容旧实例再复制一份法术规则。
            var canonical=new Dictionary<Game.Skills.SpellDefinition,ItemDefinition>();
            foreach(var item in _canonicalSpellItems??Array.Empty<ItemDefinition>())
            {
                if(item==null||item.Kind!=ItemKind.Spell||item.SpellDefinition==null||string.IsNullOrWhiteSpace(item.ContentId)
                    ||!definitions.TryGetValue(item.ContentId,out var registered)||registered!=item
                    ||canonical.ContainsKey(item.SpellDefinition))
                {error="规范法术物品必须属于当前目录，且每种法术只能指定一次。";return false;}
                canonical.Add(item.SpellDefinition,item);
            }
            index = new ItemCatalogIndex(definitions, rules, wands, _spellStorageCapacity,canonical);
            foreach(var wand in wands)if(!index.ValidateWandTraits(wand.Value)){index=null;error="固定法术引用未知ContentId："+wand.Key;return false;}
            return true;
        }
    }

    /// <summary>只暴露查询，不返回可写字典/配置数组。固定指令继续共享 SO 引用。</summary>
    public sealed class ItemCatalogIndex
    {
        private readonly Dictionary<string, ItemDefinition> _definitions;
        private readonly Dictionary<string, InventoryItemRules> _rules;
        private readonly Dictionary<string, WandStats> _wands;
        private readonly Dictionary<Game.Skills.SpellDefinition,ItemDefinition> _canonicalSpellItems;

        public int SpellStorageCapacity { get; }
        internal ItemCatalogIndex(Dictionary<string, ItemDefinition> definitions, Dictionary<string, InventoryItemRules> rules, Dictionary<string, WandStats> wands, int spellStorageCapacity,
            Dictionary<Game.Skills.SpellDefinition,ItemDefinition> canonicalSpellItems=null)
        {
            _definitions = definitions;
            _rules = rules;
            _wands = wands;
            _canonicalSpellItems = canonicalSpellItems;
            SpellStorageCapacity = spellStorageCapacity;
        }

        public int Count => _definitions.Count;
        // 材料身份属于数据规则；采集不能把所有固体硬编码成冰。
        public bool TryFindMaterialContent(byte key, out string contentId)
        {
            foreach (var entry in _rules)
                if (entry.Value.Kind == ItemKind.Material && entry.Value.MaterialKey == key)
                { contentId = entry.Key; return true; }
            contentId = null; return false;
        }
        internal ItemCatalogIndex WithLegacySingleCellWands()
        {
            // 只用于旧Schema恢复前的完整校验，不能修改生产SO或已经冻结的当前目录。
            var rules = new Dictionary<string, InventoryItemRules>(_rules, StringComparer.Ordinal);
            foreach (var entry in _rules)
            {
                var r = entry.Value;
                if (r.Kind != ItemKind.Wand || r.Shape.Width != 3 || r.Shape.Height != 1 || r.Shape.CellCount != 3) continue;
                rules[entry.Key] = new InventoryItemRules(r.Kind, new InventoryShape(1,1,new[] { Vector2Int.zero }),
                    r.MaxStack,r.BackpackWidth,r.BackpackHeight,r.WandProgramCapacity,r.ContainerCapacity,r.ContainerMaterials,r.MaterialKey,r.MaterialItemCapacity,r.UseKind);
            }
            return new ItemCatalogIndex(_definitions,rules,_wands,SpellStorageCapacity,_canonicalSpellItems);
        }
        public bool TryFindSpell(Game.Skills.SpellDefinition spell,out ItemDefinition item)
        {
            item=null;if(spell==null)return false;
            if(_canonicalSpellItems!=null&&_canonicalSpellItems.TryGetValue(spell,out item))return true;
            // 奖励是离散操作；按SO身份匹配，不把显示名当成稳定配置键。
            foreach(var entry in _definitions)if(entry.Value.Kind==ItemKind.Spell && entry.Value.SpellDefinition==spell)
            {if(item!=null){item=null;return false;}item=entry.Value;}
            return item!=null;
        }
        public bool TryGetWandStats(string contentId, out WandStats stats)
        { stats = default; return contentId != null && _wands.TryGetValue(contentId, out stats); }
        public bool TryGetWandStats(RuntimeItemState item,out WandStats stats)
        {
            stats=default;if(item==null||!TryGetWandStats(item.ContentId,out stats))return false;
            if(item.RolledWandStats.HasValue)stats=item.RolledWandStats.Value;return stats.IsValid&&ValidateWandTraits(stats);
        }
        internal bool ValidateWandTraits(in WandStats stats)
        {for(int i=0;i<stats.AlwaysCastContentIds.Count;i++)if(!TryGet(stats.AlwaysCastContentIds[i],out var definition)||definition.Kind!=ItemKind.Spell||definition.SpellDefinition==null)return false;return true;}
        public bool TryGetRules(RuntimeItemState item,out InventoryItemRules rules)
        {
            rules=default;if(item==null||!TryGetRules(item.ContentId,out rules))return false;
            if(item.RolledWandStats.HasValue&&rules.Kind==ItemKind.Wand)
            {
                // 只覆写实例程序容量；模型占格、用途等仍来自作者冻结目录。
                var r=rules;rules=new InventoryItemRules(r.Kind,r.Shape,r.MaxStack,r.BackpackWidth,r.BackpackHeight,item.RolledWandStats.Value.ProgramCapacity,
                    r.ContainerCapacity,r.ContainerMaterials,r.MaterialKey,r.MaterialItemCapacity,r.UseKind);
            }
            return true;
        }

        public bool TryGet(string contentId, out ItemDefinition definition)
        {
            definition = null;
            return !string.IsNullOrWhiteSpace(contentId) && _definitions.TryGetValue(contentId, out definition);
        }
        public bool TryGetRules(string contentId, out InventoryItemRules rules)
        {
            rules = default;
            return !string.IsNullOrWhiteSpace(contentId) && _rules.TryGetValue(contentId, out rules);
        }
    }
}
