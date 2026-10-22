using System;
using System.Reflection;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    /// <summary>仓库分组是表现投影；多份不同状态不能变成一个可写资产。</summary>
    public sealed class WarehouseCatalogViewTests : InventoryTestFixture
    {
        private Component View(out RectTransform viewport, out RectTransform content, out InventoryItemView prefab)
        {
            Type type = typeof(SinglePlayerInventoryController).Assembly.GetType("Game.UI.WarehouseCatalogGridView");
            Assert.That(type, Is.Not.Null, "缺少仓库分类网格，现有目录没有空格及同类汇总");
            var root = Track(new GameObject("Test warehouse viewport", typeof(RectTransform), typeof(Image), typeof(ScrollRect)));
            viewport = root.GetComponent<RectTransform>(); viewport.sizeDelta = new Vector2(480, 320);
            var grid = new GameObject("Content", typeof(RectTransform), typeof(Image)); grid.transform.SetParent(root.transform, false);
            content = grid.GetComponent<RectTransform>(); content.anchorMin = content.anchorMax = content.pivot = new Vector2(0, 1);
            var scroll = root.GetComponent<ScrollRect>(); scroll.viewport = viewport; scroll.content = content;
            var template = Track(new GameObject("Item template", typeof(RectTransform), typeof(Image), typeof(InventoryItemView)));
            prefab = template.GetComponent<InventoryItemView>();
            return grid.AddComponent(type);
        }
        private static object Call(Component view, string method, params object[] arguments) =>
            view.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance).Invoke(view, arguments);
        private static int Number(Component view, string property) => (int)view.GetType().GetProperty(property).GetValue(view);
        private static InventoryItemSnapshot[] Items(ItemOwnershipStore store)
        {
            var read = new InventoryReadModel(new SinglePlayerProfile("player", store), "test");
            var items = new InventoryItemSnapshot[read.Count]; read.CopyItemsTo(items); return items;
        }
        [Test]
        public void FoodAndHerbsAreMaterialsAndOnlyUsableContainersAreTools()
        {
            var view = View(out _, out _, out var prefab);
            var store = Store(Define("pumpkin", ItemKind.Consumable), Define("herb", ItemKind.Alchemy),
                Define("bottle", ItemKind.Container), Define("bag", ItemKind.Backpack));
            Add(store, "p", "pumpkin"); Add(store, "h", "herb"); Add(store, "c", "bottle"); Add(store, "b", "bag");
            var items = Items(store); Call(view, "Refresh", null, prefab, items, items.Length);
            Call(view, "SetFilter", "", 3);
            Assert.That(Number(view, "GroupCount"), Is.EqualTo(2), "材料目录应包含食物与草药");
            Call(view, "SetFilter", "", 9);
            Assert.That(Number(view, "GroupCount"), Is.EqualTo(1), "道具目录只显示有使用能力的容器");
        }
        [Test]
        public void SameContentHasOneSquareWhileDifferentStatesKeepTheirIdentity()
        {
            var view = View(out _, out _, out var prefab);
            var store = Store(Define("spell", ItemKind.Spell), Define("bottle", ItemKind.Container, 4, 4));
            Add(store, "s1", "spell", 3); Add(store, "s2", "spell", 8); Add(store, "b1", "bottle");
            uint before = store.Version; var items = Items(store);
            Call(view, "Refresh", null, prefab, items, items.Length);
            Assert.That(Number(view, "GroupCount"), Is.EqualTo(2));
            Assert.That(Number(view, "InstanceCount"), Is.EqualTo(3));
            store.TryGet("s1", out var first); store.TryGet("s2", out var second);
            Assert.That(first.RemainingCharges, Is.EqualTo(3)); Assert.That(second.RemainingCharges, Is.EqualTo(8));
            Assert.That(store.Version, Is.EqualTo(before));
        }
        [Test]
        public void SameKindContainersUseSeparateWarehouseSquares()
        {
            var view=View(out _,out _,out var prefab);
            var store=Store(Define("bottle",ItemKind.Container));
            Add(store,"empty","bottle");Add(store,"filled","bottle");
            store.TryGet("filled",out var filled);
            filled.Composition.AddReserved(new[]{new Game.Core.MaterialAmount(1,10)},1);
            var items=Items(store);Call(view,"Refresh",null,prefab,items,items.Length);
            Assert.That(Number(view,"GroupCount"),Is.EqualTo(2));
            Assert.That(Number(view,"InstanceCount"),Is.EqualTo(2));
            var captions = view.GetComponentsInChildren<Text>();
            Assert.That(captions.Length, Is.EqualTo(2));
            foreach (var label in captions) Assert.That(label.text, Does.Not.Contain("×1"));
            Assert.That(Array.Exists(captions, label => label.text.EndsWith("0/1024")), Is.True);
            Assert.That(Array.Exists(captions, label => label.text.EndsWith("10/1024")), Is.True);
        }
        [Test]
        public void EmptyWarehouseStillDisplaysBackgroundSquares()
        {
            var view = View(out _, out _, out var prefab);
            Call(view, "Refresh", null, prefab, Array.Empty<InventoryItemSnapshot>(), 0);
            Assert.That(Number(view, "GroupCount"), Is.Zero);
            Assert.That(Number(view, "VisibleCellCount"), Is.GreaterThanOrEqualTo(15));
        }
        [Test]
        public void ManyKindsExtendContentWithoutCreatingEveryRowOfViews()
        {
            var view = View(out var viewport, out var content, out var prefab);
            var definitions = new ItemDefinition[120];
            for (int i = 0; i < definitions.Length; i++) definitions[i] = Define("kind" + i, ItemKind.Container);
            var store = Store(definitions);
            for (int i = 0; i < definitions.Length; i++) Add(store, "item" + i, "kind" + i);
            var items = Items(store); Call(view, "Refresh", null, prefab, items, items.Length);
            Assert.That(Number(view, "GroupCount"), Is.EqualTo(120));
            Assert.That(content.rect.height, Is.GreaterThan(viewport.rect.height * 3));
            Assert.That(Number(view, "AllocatedItemViewCount"), Is.LessThan(60));
            Assert.That(Number(view, "VisibleCellCount"), Is.LessThan(60));
        }
        [Test]
        public void FilterAndOrganizeOnlyChangePresentation()
        {
            var view = View(out _, out _, out var prefab);
            var store = Store(Define("spell", ItemKind.Spell), Define("bottle", ItemKind.Container));
            Add(store, "s1", "spell", 3); Add(store, "b1", "bottle"); uint before = store.Version;
            var items = Items(store); Call(view, "Refresh", null, prefab, items, items.Length);
            Call(view, "SetFilter", "spell", 0); Assert.That(Number(view, "GroupCount"), Is.EqualTo(1));
            Call(view, "Organize"); Call(view, "SetFilter", "", 0);
            Assert.That(Number(view, "GroupCount"), Is.EqualTo(2));
            Assert.That(store.Count, Is.EqualTo(2)); Assert.That(store.Version, Is.EqualTo(before));
        }
    }
}
