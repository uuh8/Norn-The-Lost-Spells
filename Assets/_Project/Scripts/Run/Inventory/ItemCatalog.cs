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

            index = new ItemCatalogIndex(definitions, rules, wands, _spellStorageCapacity);
            return true;
        }
    }

    /// <summary>只暴露查询，不返回可写字典/配置数组。固定指令继续共享 SO 引用。</summary>
    public sealed class ItemCatalogIndex
    {
        private readonly Dictionary<string, ItemDefinition> _definitions;
        private readonly Dictionary<string, InventoryItemRules> _rules;
        private readonly Dictionary<string, WandStats> _wands;

        public int SpellStorageCapacity { get; }
        internal ItemCatalogIndex(Dictionary<string, ItemDefinition> definitions, Dictionary<string, InventoryItemRules> rules, Dictionary<string, WandStats> wands, int spellStorageCapacity)
        {
            _definitions = definitions;
            _rules = rules;
            _wands = wands;
            SpellStorageCapacity = spellStorageCapacity;
        }

        public int Count => _definitions.Count;
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
            return new ItemCatalogIndex(_definitions,rules,_wands,SpellStorageCapacity);
        }
        public bool TryFindSpell(Game.Skills.SpellDefinition spell,out ItemDefinition item)
        {
            item=null;if(spell==null)return false;
            // 奖励是离散操作；按SO身份匹配，不把显示名当成稳定配置键。
            foreach(var entry in _definitions)if(entry.Value.Kind==ItemKind.Spell && entry.Value.SpellDefinition==spell)
            {if(item!=null)return false;item=entry.Value;}
            return item!=null;
        }
        public bool TryGetWandStats(string contentId, out WandStats stats)
        { stats = default; return contentId != null && _wands.TryGetValue(contentId, out stats); }

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
