using System.Collections.Generic;
using Game.Core;
using Game.Skills;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Run.Tests
{
    /// <summary>用实际 SO 配置和归属表验证空间约束，不触碰项目资产或当前场景。</summary>
    public class InventoryTestFixture
    {
        private readonly List<Object> _assets = new List<Object>();

        [TearDown]
        public void Cleanup()
        {
            for (int i = _assets.Count - 1; i >= 0; i--) Object.DestroyImmediate(_assets[i]);
            _assets.Clear();
        }

        protected T Track<T>(T value) where T : Object { _assets.Add(value); return value; }

        protected ItemDefinition Define(string id, ItemKind kind, int width = 1, int height = 1,
            int maxStack = 1, Vector2Int[] cells = null, int bagWidth = 1, int bagHeight = 1)
        {
            var value = Track(ScriptableObject.CreateInstance<ItemDefinition>());
            var data = new SerializedObject(value);
            data.FindProperty("_contentId").stringValue = id;
            data.FindProperty("_kind").intValue = (int)kind;
            data.FindProperty("_shapeWidth").intValue = width;
            data.FindProperty("_shapeHeight").intValue = height;
            data.FindProperty("_maxStack").intValue = maxStack;
            data.FindProperty("_backpackWidth").intValue = bagWidth;
            data.FindProperty("_backpackHeight").intValue = bagHeight;
            SerializedProperty shape = data.FindProperty("_occupiedCells");
            if (cells == null)
            {
                cells = new Vector2Int[width * height];
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++) cells[y * width + x] = new Vector2Int(x, y);
            }
            shape.arraySize = cells.Length;
            for (int i = 0; i < cells.Length; i++) shape.GetArrayElementAtIndex(i).vector2IntValue = cells[i];
            if (kind == ItemKind.Spell)
                data.FindProperty("_spellDefinition").objectReferenceValue = Track(ScriptableObject.CreateInstance<SpellDefinition>());
            if (kind == ItemKind.Wand)
            {
                var wand = Track(ScriptableObject.CreateInstance<WandDefinition>());
                var parameters = new SerializedObject(wand); parameters.FindProperty("_programCapacity").intValue = 1;
                parameters.ApplyModifiedPropertiesWithoutUndo(); data.FindProperty("_wandDefinition").objectReferenceValue = wand;
            }
            data.ApplyModifiedPropertiesWithoutUndo();
            return value;
        }

        protected ItemOwnershipStore Store(params ItemDefinition[] definitions)
        {
            var catalog = Track(ScriptableObject.CreateInstance<ItemCatalog>());
            var data = new SerializedObject(catalog);
            SerializedProperty entries = data.FindProperty("_definitions");
            entries.arraySize = definitions.Length;
            for (int i = 0; i < definitions.Length; i++) entries.GetArrayElementAtIndex(i).objectReferenceValue = definitions[i];
            data.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(catalog.TryCreateIndex(out ItemCatalogIndex index, out string error), Is.True, error);
            return new ItemOwnershipStore(index);
        }

        protected static void Add(ItemOwnershipStore store, string id, string content, int charges = 0)
        {
            Assert.That(store.TryAdd(new RuntimeItemState(id, content, charges), ItemLocation.Warehouse), Is.True);
        }

        protected static ItemLocation Bag(int x = 0, int y = 0, int rotation = 0, string owner = "bag") =>
            new ItemLocation(ItemLocationKind.Backpack, owner, x, y, rotation, -1);

        protected static void At(ItemOwnershipStore store, string id, ItemLocation expected)
        {
            Assert.That(store.TryGetLocation(id, out ItemLocation actual), Is.True);
            Assert.That(actual, Is.EqualTo(expected));
        }
    }

    public sealed class InventoryGridTests : InventoryTestFixture
    {
        private ItemOwnershipStore RectangleStore(int bagWidth = 4, int bagHeight = 3)
        {
            ItemOwnershipStore store = Store(Define("bag", ItemKind.Backpack, bagWidth: bagWidth, bagHeight: bagHeight),
                Define("bar", ItemKind.Container, 2, 1), Define("small", ItemKind.Consumable));
            Add(store, "bag", "bag"); Add(store, "a", "bar"); Add(store, "b", "small");
            return store;
        }

        [Test]
        public void FourRotations_ReturnOriginalCellsAndBoundingBox()
        {
            var shape = new InventoryShape(3, 2, new[] { new Vector2Int(0, 0), new Vector2Int(2, 1) });
            Assert.That(shape.GetCell(0, 1), Is.EqualTo(new Vector2Int(1, 0)));
            Assert.That(shape.GetCell(1, 1), Is.EqualTo(new Vector2Int(0, 2)));
            Assert.That(shape.GetWidth(1), Is.EqualTo(2));
            Assert.That(shape.GetHeight(1), Is.EqualTo(3));
            Assert.That(shape.GetCell(0, 4), Is.EqualTo(new Vector2Int(0, 0)));
            Assert.That(shape.GetCell(1, 4), Is.EqualTo(new Vector2Int(2, 1)));
            Assert.That(shape.GetWidth(4), Is.EqualTo(3));
            Assert.That(shape.GetHeight(4), Is.EqualTo(2));
        }

        [Test]
        public void Shape_CopiesAuthoringArrayAndRejectsDuplicateCells()
        {
            var cells = new[] { Vector2Int.zero };
            var shape = new InventoryShape(1, 1, cells);
            cells[0] = Vector2Int.one;
            Assert.That(shape.GetCell(0, 0), Is.EqualTo(Vector2Int.zero));
            Assert.Throws<System.ArgumentException>(() => new InventoryShape(1, 1, new[] { Vector2Int.zero, Vector2Int.zero }));
            Assert.Throws<System.ArgumentException>(() => new InventoryShape(1, 1, new[] { new Vector2Int(1, 0) }));
            Assert.Throws<System.ArgumentException>(() => new InventoryShape(1, 1, new Vector2Int[0]));
        }

        [Test]
        public void RotateRectangle_UsesSwappedDimensionsForBounds()
        {
            ItemOwnershipStore store = RectangleStore(2, 3);
            Assert.That(InventoryMoveOps.TryPlace(store, "a", "bag", new GridPlacement(1, 1, 1), store.Version).Success, Is.True);
            At(store, "a", Bag(1, 1, 1));
            uint version = store.Version;
            Assert.That(store.TryMove("a", Bag(1, 2, 1)).Reason, Is.EqualTo(InventoryFailureReason.OutOfBounds));
            At(store, "a", Bag(1, 1, 1));
            Assert.That(store.Version, Is.EqualTo(version));
        }

        [Test]
        public void MoveAcrossOwnOldCells_IsAllowed()
        {
            ItemOwnershipStore store = RectangleStore();
            Assert.That(store.TryMove("a", Bag()).Success, Is.True);
            Assert.That(store.TryMove("a", Bag(1)).Success, Is.True);
            At(store, "a", Bag(1));
        }

        [Test]
        public void LShape_HoleCanHoldAnotherItem()
        {
            ItemOwnershipStore store = Store(Define("bag", ItemKind.Backpack, bagWidth: 2, bagHeight: 2),
                Define("L", ItemKind.Container, 2, 2, cells: new[] { Vector2Int.zero, new Vector2Int(0, 1), Vector2Int.one }),
                Define("small", ItemKind.Consumable));
            Add(store, "bag", "bag"); Add(store, "a", "L"); Add(store, "b", "small");
            Assert.That(store.TryMove("a", Bag()).Success, Is.True);
            Assert.That(store.TryMove("b", Bag(1)).Success, Is.True);
            uint version = store.Version;
            Assert.That(store.TryMove("a", Bag(rotation: 1)).Reason, Is.EqualTo(InventoryFailureReason.Occupied));
            At(store, "a", Bag()); At(store, "b", Bag(1));
            Assert.That(store.Version, Is.EqualTo(version));
        }

        [Test]
        public void StalePlacement_IsRejectedEvenWhenDestinationIsFree()
        {
            ItemOwnershipStore store = RectangleStore();
            uint old = store.Version;
            Assert.That(store.TryMove("b", Bag(3)).Success, Is.True);
            Assert.That(InventoryMoveOps.TryPlace(store, "a", "bag", new GridPlacement(0, 0, 0), old).Reason,
                Is.EqualTo(InventoryFailureReason.StaleVersion));
            At(store, "a", ItemLocation.Warehouse);
        }

        [Test]
        public void InvalidShapeConfig_RejectsCatalogBeforeAnyOwnership()
        {
            ItemDefinition item = Define("bad", ItemKind.Container, cells: new[] { new Vector2Int(-1, 0) });
            Assert.Throws<AssertionException>(() => Store(item));
        }

        [Test]
        public void OutOfGrid_RejectsMoveAndRetainsSourceAndVersion()
        {
            ItemOwnershipStore store = Store(Define("bag", ItemKind.Backpack), Define("bottle", ItemKind.Container));
            Add(store, "bag", "bag"); Add(store, "a", "bottle");
            uint before = store.Version;
            Assert.That(store.TryMove("a", Bag(999, 999)).Reason, Is.EqualTo(InventoryFailureReason.OutOfBounds));
            At(store, "a", ItemLocation.Warehouse);
            Assert.That(store.Version, Is.EqualTo(before));
        }

        [Test]
        public void OccupiedCell_RejectsIndependentItemsWithoutLosingEither()
        {
            ItemOwnershipStore store = Store(Define("bag", ItemKind.Backpack), Define("bottle", ItemKind.Container));
            Add(store, "bag", "bag"); Add(store, "a", "bottle"); Add(store, "b", "bottle");
            Assert.That(store.TryMove("a", Bag()).Success, Is.True);
            uint before = store.Version;
            Assert.That(store.TryMove("b", Bag()).Success, Is.False);
            At(store, "a", Bag()); At(store, "b", ItemLocation.Warehouse);
            Assert.That(store.Count, Is.EqualTo(3));
            Assert.That(store.Version, Is.EqualTo(before));
        }
    }
}
