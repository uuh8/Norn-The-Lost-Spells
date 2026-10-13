using System;
using UnityEngine;
using Game.Core;

namespace Game.Run
{
    /// <summary>只读新档发放模板。只有确实缺少主档和备份时使用，不能覆盖读取失败的旧资产。</summary>
    [CreateAssetMenu(menuName = "Game/Run/Inventory/Starting Inventory", fileName = "StartingInventory")]
    public sealed class SinglePlayerStartingInventory : ScriptableObject
    {
        [Serializable]
        private sealed class Seed
        {
            public ItemDefinition Definition;
            public int RemainingCharges;
            public bool HasUnlimitedCharges;
            public ItemLocationKind Location = ItemLocationKind.Warehouse;
            public int SlotIndex;
            public int OwnerSeedIndex = -1;
            [Tooltip("仅新档的初始容器内容；已有实例与存档不受模板修改影响。")]
            public MaterialAmount[] Materials = Array.Empty<MaterialAmount>();
        }
        [SerializeField] private string _actorId = "player";
        [SerializeField, Min(0), Tooltip("仅新档/每次新的 Scene Preview 发放。已有存档金币不会被此数值覆盖。")]
        private long _initialCurrency;
        [SerializeField] private Seed[] _items = Array.Empty<Seed>();

        public bool TryCreate(ItemCatalogIndex catalog, out SinglePlayerProfile profile, out string error)
        {
            profile = null; error = null;
            if (_initialCurrency < 0) { error = "初始金币不能为负数。"; return false; }
            if (string.IsNullOrWhiteSpace(_actorId) || _items == null) { error = "初始资产配置缺少玩家身份/数组。"; return false; }
            var store = new ItemOwnershipStore(catalog);
            // 测试/新档只读模板按父项在前构造，不改变已有实例 ID 或存档 schema。
            var instances = new RuntimeItemState[_items.Length];
            for (int i = 0; i < _items.Length; i++)
            {
                Seed seed = _items[i];
                if (seed == null || seed.Definition == null || seed.RemainingCharges < 0) { error = "初始资产缺少定义或次数非法。"; return false; }
                ItemLocation location;
                if (seed.Location == ItemLocationKind.Warehouse) location = ItemLocation.Warehouse;
                else if (seed.Location == ItemLocationKind.EquippedWand || seed.Location == ItemLocationKind.EquippedBackpack)
                    location = new ItemLocation(seed.Location, _actorId, 0, 0, 0, seed.SlotIndex);
                else if (seed.Location == ItemLocationKind.WandSlot && seed.OwnerSeedIndex >= 0 && seed.OwnerSeedIndex < i)
                    location = new ItemLocation(ItemLocationKind.WandSlot, instances[seed.OwnerSeedIndex].InstanceId, 0, 0, 0, seed.SlotIndex);
                else { error = "初始模板嵌套项必须引用之前的法杖父项。"; return false; }
                RuntimeItemState item = RuntimeItemState.Create(seed.Definition.ContentId, seed.RemainingCharges, seed.HasUnlimitedCharges);
                if (seed.Materials != null && seed.Materials.Length > 0)
                {
                    if (!catalog.TryGetRules(seed.Definition.ContentId, out var rules) || rules.Kind != ItemKind.Container)
                    { error = "只有容器初始项可以配置材料内容。"; return false; }
                    var composition = new ContainerComposition(rules.ContainerCapacity, rules.ContainerMaterials);
                    if (!composition.CanAdd(seed.Materials, seed.Materials.Length))
                    { error = "初始容器内容不兼容、重复或超过容量。"; return false; }
                    composition.AddReserved(seed.Materials, seed.Materials.Length);
                    item = new RuntimeItemState(item.InstanceId, item.ContentId, item.RemainingCharges, item.HasUnlimitedCharges, composition: composition);
                }
                if (!store.TryAdd(item, location)) { error = "初始资产身份/装备/配置验证失败。"; return false; }
                instances[i] = item;
            }
            profile = new SinglePlayerProfile(_actorId, store, currency: _initialCurrency); return true;
        }
    }
}
