using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Run
{
    /// <summary>整数格点形状。复制 Authoring 数组一次，旋转/查询只计算值，不生成旋转数组。</summary>
    public sealed class InventoryShape
    {
        private readonly Vector2Int[] _cells;
        public int Width { get; }
        public int Height { get; }
        public int CellCount => _cells.Length;
        public InventoryShape(int width, int height, IReadOnlyList<Vector2Int> cells)
        {
            if (width <= 0 || height <= 0 || cells == null || cells.Count == 0)
                throw new ArgumentException("形状必须有正尺寸和至少一个实际占用格。");
            Width = width; Height = height;
            _cells = new Vector2Int[cells.Count];
            var unique = new HashSet<Vector2Int>();
            for (int i = 0; i < cells.Count; i++)
            {
                Vector2Int cell = cells[i];
                if (cell.x < 0 || cell.x >= width || cell.y < 0 || cell.y >= height || !unique.Add(cell))
                    throw new ArgumentException("局部占格不能越界或重复。");
                _cells[i] = cell;
            }
        }
        public int GetWidth(int quarterTurns) => (Normalize(quarterTurns) & 1) == 0 ? Width : Height;
        public int GetHeight(int quarterTurns) => (Normalize(quarterTurns) & 1) == 0 ? Height : Width;
        public Vector2Int GetCell(int index, int quarterTurns)
        {
            Vector2Int cell = _cells[index];
            int width = Width, height = Height;
            for (int i = 0, turns = Normalize(quarterTurns); i < turns; i++)
            {
                // 左上局部整数坐标旋转；尺寸交换使矩形也能正确旋转，空洞保持为空。
                cell = new Vector2Int(height - 1 - cell.y, cell.x);
                int previousWidth = width; width = height; height = previousWidth;
            }
            return cell;
        }
        private static int Normalize(int turns) => ((turns % 4) + 4) % 4;
    }
    public readonly struct GridPlacement
    {
        public readonly int X, Y, QuarterTurns;
        public GridPlacement(int x, int y, int quarterTurns) { X = x; Y = y; QuarterTurns = quarterTurns; }
        public ItemLocation InBackpack(string backpackId) => new ItemLocation(ItemLocationKind.Backpack, backpackId, X, Y, QuarterTurns, -1);
    }
    /// <summary>初始化时冻结的空间规则；Play Mode 编辑 SO 不会改变已经建好的库存占格。</summary>
    public readonly struct InventoryItemRules
    {
        public readonly ItemKind Kind;
        public readonly InventoryShape Shape;
        public readonly int MaxStack, BackpackWidth, BackpackHeight, WandProgramCapacity;
        public readonly ulong ContainerCapacity, MaterialItemCapacity;
        public readonly ushort ContainerMaterials;
        public readonly byte MaterialKey;
        public readonly ItemUseKind UseKind;
        public InventoryItemRules(ItemKind kind, InventoryShape shape, int maxStack, int width, int height, int programCapacity = 1,
            ulong containerCapacity = 1024, ushort containerMaterials = 410, byte materialKey = 5, ulong materialItemCapacity = 1024, ItemUseKind useKind = ItemUseKind.None)
        // 容器按实例显示储量，Runtime 也固定一格一份；旧 SO 的堆叠配置不能绕过此规则。
        { Kind = kind; Shape = shape; MaxStack = kind==ItemKind.Container?1:maxStack; BackpackWidth = width; BackpackHeight = height; WandProgramCapacity = programCapacity;
            ContainerCapacity = containerCapacity; ContainerMaterials = containerMaterials; MaterialKey = materialKey; MaterialItemCapacity = materialItemCapacity; UseKind = useKind; }
    }
}
