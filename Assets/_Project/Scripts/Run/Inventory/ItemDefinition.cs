using System;
using Game.Skills;
using UnityEngine;

namespace Game.Run
{
    public enum ItemKind : byte
    {
        Spell = 0,
        Wand = 1,
        Backpack = 2,
        Container = 3,
        Material = 4,
        Consumable = 5,
        Alchemy = 6
    }

    // 类别回答“是什么”，使用能力回答“能执行什么”。后续道具增加行为时扩展此能力，不改快捷位逻辑。
    public enum ItemUseKind : byte { None = 0, Container = 1 }
    public enum ItemCategory : byte { General=0,Herb=1,Food=2,Container=3 }

    /// <summary>
    /// 物品种类的共享 Authoring Data。当前次数和位置属于具体实例，不能写进这个 SO。
    /// 私有序列化字段仍可在 Inspector 配置，公开只读属性防止 UI 改写所有副本的基础配置。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Run/Inventory/Item Definition", fileName = "ItemDefinition")]
    public sealed class ItemDefinition : ScriptableObject
    {
        [SerializeField, Tooltip("跨保存稳定且唯一的配置键，例如 spell.water；不要使用显示名称或 GetInstanceID。")]
        private string _contentId = "";
        [SerializeField] private ItemKind _kind;
        [SerializeField, Tooltip("快捷使用能力；未实现具体行为的素材保持None。旧容器自动沿用Container能力。")]
        private ItemUseKind _useKind;
        [SerializeField] private string _displayName = "";
        [SerializeField] private Sprite _icon;
        [SerializeField,Tooltip("纯美术Prefab；供图标/容器表现使用。可拾取包装填Pickup Prefab。")] private GameObject _worldModel;
        [SerializeField,Tooltip("可拾取包装Prefab，运行时继承它的Scale、模型及材质；复制体禁用投放组件，不重复发放。")] private GameObject _pickupPrefab;
        [SerializeField] private ItemCategory _category;
        [SerializeField, Tooltip("法术物品引用现有只读指令；其他物品保持为空。")]
        private SpellDefinition _spellDefinition;
        [SerializeField, Min(1)] private int _shapeWidth = 1;
        [SerializeField, Min(1)] private int _shapeHeight = 1;
        [SerializeField, Tooltip("局部占用格；未列出的空洞允许放其他物品。")]
        private Vector2Int[] _occupiedCells = { Vector2Int.zero };
        [SerializeField, Min(1)] private int _maxStack = 1;
        [SerializeField, Min(1)] private int _backpackWidth = 1;
        [SerializeField, Min(1)] private int _backpackHeight = 1;
        [SerializeField] private WandDefinition _wandDefinition;
        // 只供 Editor 迁移旧资产读取；Runtime 容量统一来自固定属性，避免两处配置互相冲突。
        [SerializeField, HideInInspector]
        private int _wandProgramCapacity = 1;
        [SerializeField, Min(1)] private int _containerCapacityGmu = 1024;
        [SerializeField, Tooltip("bit N 允许稳定材料键 N。默认水/毒/粘液/两种气体；排除 Fire 与固体。")]
        private int _containerMaterialsMask = (1 << 1) | (1 << 3) | (1 << 4) | (1 << 7) | (1 << 8);
        [SerializeField, Tooltip("直接采集固体物品的稳定材料键；其他物品不使用。")]
        private byte _materialKey = 5;
        [SerializeField, Min(1)] private int _materialItemCapacityGmu = 1024;

        public string ContentId => _contentId;
        public ItemKind Kind => _kind;
        public ItemUseKind UseKind => _useKind == ItemUseKind.None && _kind == ItemKind.Container ? ItemUseKind.Container : _useKind;
        public string DisplayName => _displayName;
        public Sprite Icon => _icon;
        public GameObject WorldModel=>_worldModel;
        public GameObject PickupPrefab=>_pickupPrefab;
        public ItemCategory Category=>_category;
        public SpellDefinition SpellDefinition => _spellDefinition;
        public WandDefinition WandDefinition => _wandDefinition;

        internal bool TryCreateRules(out InventoryItemRules rules, out string error)
        {
            rules = default; error = null;
            if (UseKind > ItemUseKind.Container || UseKind == ItemUseKind.Container && _kind != ItemKind.Container)
            { error = "使用能力与物品数据不兼容；未实现的道具不能借用容器行为。"; return false; }
            int capacity = 1;
            if (_kind == ItemKind.Wand)
            {
                if (_wandDefinition == null || !_wandDefinition.TryFreeze(out WandStats stats))
                { error = "法杖缺少合法固定属性。"; return false; }
                capacity = stats.ProgramCapacity;
            }
            if (_maxStack < 1 || _backpackWidth < 1 || _backpackHeight < 1
                || ((_kind == ItemKind.Wand || _kind == ItemKind.Backpack) && _maxStack != 1))
            { error = "堆叠/背包尺寸必须为正；法杖和背包外壳不堆叠。"; return false; }
            try
            {
                if (_containerCapacityGmu <= 0 || (_containerMaterialsMask & 0x1fe) == 0 || (_containerMaterialsMask & ~0x1fe) != 0
                    || _materialItemCapacityGmu <= 0 || !Game.Core.MaterialAmount.IsKnown(_materialKey))
                { error = "容器/材料物品数量或稳定键配置非法。"; return false; }
                rules = new InventoryItemRules(_kind, new InventoryShape(_shapeWidth, _shapeHeight, _occupiedCells),
                    _maxStack, _backpackWidth, _backpackHeight, capacity, (ulong)_containerCapacityGmu,
                    (ushort)_containerMaterialsMask, _materialKey, (ulong)_materialItemCapacityGmu, UseKind);
                return true;
            }
            catch (ArgumentException exception) { error = exception.Message; return false; }
        }
    }
}
