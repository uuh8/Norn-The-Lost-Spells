using System;
using System.Reflection;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    /// <summary>仓库分组是表现投影；多份不同状态不能变成一个可写资产。</summary>
    public sealed class WarehouseCatalogViewTests : InventoryTestFixture
    {
        private SinglePlayerInventoryController _controller;
        private Component View(out RectTransform viewport, out RectTransform content, out InventoryItemView prefab)
        {
            // 真实Refresh需要Controller选择作者SlotPrefab；只创建inactive读源，不订事件或初始化本地资产。
            var owner = Track(new GameObject("Inactive warehouse controller")); owner.SetActive(false);
            _controller = owner.AddComponent<SinglePlayerInventoryController>();
            Type type = typeof(SinglePlayerInventoryController).Assembly.GetType("Game.UI.WarehouseCatalogGridView");
            Assert.That(type, Is.Not.Null, "缺少仓库分类网格，现有目录没有空格及同类汇总");
            var root = Track(new GameObject("Test warehouse viewport", typeof(RectTransform), typeof(Image), typeof(ScrollRect)));
            viewport = root.GetComponent<RectTransform>(); viewport.sizeDelta = new Vector2(480, 320);
            var grid = new GameObject("Content", typeof(RectTransform), typeof(Image)); grid.transform.SetParent(root.transform, false);
            content = grid.GetComponent<RectTransform>(); content.anchorMin = content.anchorMax = content.pivot = new Vector2(0, 1);
            var scroll = root.GetComponent<ScrollRect>(); scroll.viewport = viewport; scroll.content = content;
            var template = Track(new GameObject("Item template", typeof(RectTransform), typeof(Image), typeof(InventoryItemView)));
            prefab = template.GetComponent<InventoryItemView>();
            // 显式给本用例的作者模板，避免Resources默认美术模板改变纯目录分组/池化测试的层级。
            var styles = Track(ScriptableObject.CreateInstance<InventorySlotPrefabs>()); styles.WarehouseItem = prefab;
            typeof(SinglePlayerInventoryController).GetField("_slotPrefabs", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_controller, styles);
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
            var items = Items(store); Call(view, "Refresh", _controller, prefab, items, items.Length);
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
            Call(view, "Refresh", _controller, prefab, items, items.Length);
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
            var definition = Define("bottle", ItemKind.Container);
            // 使用有效Icon验证玩家实际看到的容量层，避免无Sprite时两份填充都被隐藏而假通过。
            var texture = Track(new Texture2D(2, 2));
            var icon = Track(Sprite.Create(texture, new Rect(0, 0, 2, 2), new Vector2(.5f, .5f)));
            var data = new SerializedObject(definition);
            data.FindProperty("_displayName").stringValue = "测试容器";
            data.FindProperty("_icon").objectReferenceValue = icon;
            data.FindProperty("_containerCapacityGmu").intValue = 1024;
            data.ApplyModifiedPropertiesWithoutUndo();
            var store=Store(definition);
            Add(store,"empty","bottle");Add(store,"filled","bottle");
            store.TryGet("filled",out var filled);
            filled.Composition.AddReserved(new[]{new Game.Core.MaterialAmount(1,10)},1);
            uint version = store.Version;
            var items=Items(store);Call(view,"Refresh",_controller,prefab,items,items.Length);
            Assert.That(Number(view,"GroupCount"),Is.EqualTo(2));
            Assert.That(Number(view,"InstanceCount"),Is.EqualTo(2));
            var captions = view.GetComponentsInChildren<Text>();
            Assert.That(captions.Length, Is.EqualTo(2));
            foreach (var label in captions)
            {
                Assert.That(label.text, Does.Not.Contain("×1"));
                Assert.That(label.text, Is.EqualTo(definition.DisplayName), "容量已由Icon填充表达，不再挤入小格文字。");
            }
            var squares = view.GetComponentsInChildren<InventoryItemView>();
            Assert.That(squares.Length, Is.EqualTo(2));
            Assert.That(new[] { squares[0].Snapshot.InstanceId, squares[1].Snapshot.InstanceId },
                Is.EquivalentTo(new[] { "empty", "filled" }), "同类容器必须各自保留真实实例身份。");
            foreach (var square in squares)
            {
                bool hasMaterial = square.Snapshot.InstanceId == "filled";
                Assert.That(square.Snapshot.ContainerTotal, Is.EqualTo(hasMaterial ? 10UL : 0UL));
                var fill = Array.Find(square.GetComponentsInChildren<Image>(true), image => image.name == ContainerIconFill.ChildName);
                Assert.That(fill, Is.Not.Null);
                Assert.That(fill.sprite, Is.SameAs(icon));
                Assert.That(fill.type, Is.EqualTo(Image.Type.Filled));
                Assert.That(fill.fillMethod, Is.EqualTo(Image.FillMethod.Vertical));
                Assert.That(fill.fillOrigin, Is.EqualTo((int)Image.OriginVertical.Bottom));
                Assert.That(fill.fillAmount, Is.EqualTo(hasMaterial ? 10f / 1024f : 0f).Within(.000001f));
                Assert.That(fill.enabled, Is.EqualTo(hasMaterial), "空瓶保留原图，已装材料的那份单独显示底部填充。");
            }
            Assert.That(store.Version, Is.EqualTo(version));
            Assert.That(filled.Composition.Total, Is.EqualTo(10UL), "目录投影不能合并或改写真实组分。");
        }

        [TestCase(12,12,"24")]
        [TestCase(3,8,"11")]
        public void GroupedSpellCaptionTotalsChargesWithoutMergingInstanceState(int first,int second,string total)
        {
            var view=View(out _,out _,out var prefab);var store=Store(Define("spell",ItemKind.Spell));
            Add(store,"spell.a","spell",first);Add(store,"spell.b","spell",second);
            uint version=store.Version;var items=Items(store);Call(view,"Refresh",_controller,prefab,items,items.Length);
            Assert.That(Number(view,"GroupCount"),Is.EqualTo(1));
            var square=view.GetComponentInChildren<InventoryItemView>();
            var count=(Text)typeof(InventoryItemView).GetField("_spellCount",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(square);
            Assert.That(count.text,Is.EqualTo(total),"仓库角标表示整个ContentId组的总余次，不是代表实例余次。");
            Assert.That(store.TryGet("spell.a",out var a),Is.True);Assert.That(a.RemainingCharges,Is.EqualTo(first));
            Assert.That(store.TryGet("spell.b",out var b),Is.True);Assert.That(b.RemainingCharges,Is.EqualTo(second));
            Assert.That(store.Count,Is.EqualTo(2));Assert.That(store.Version,Is.EqualTo(version));
        }

        [Test]
        public void GroupedOrdinaryItemsDisplayOnlyOneTotalWithoutMemberFraction()
        {
            var view=View(out _,out _,out var prefab);var store=Store(Define("herb",ItemKind.Alchemy));
            Add(store,"herb.a","herb");Add(store,"herb.b","herb");
            var items=Items(store);Call(view,"Refresh",_controller,prefab,items,items.Length);
            var square=view.GetComponentInChildren<InventoryItemView>();
            var label=(Text)typeof(InventoryItemView).GetField("_label",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(square);
            Assert.That(label.text,Is.EqualTo("herb\n2"),"保留原物品名称，数字行只表达一次总数；代表实例身份仍由Snapshot和拖动携带。");
            Assert.That(label.text,Does.Not.Contain("×"));Assert.That(label.text,Does.Not.Contain("1/2"));
            Assert.That(Call(view,"GetMemberCount",0),Is.EqualTo(2));
            Assert.That(store.Count,Is.EqualTo(2));
        }

        [Test]
        public void UnlimitedWarehouseSpellGroupDisplaysInfinityWithoutChangingFiniteMembers()
        {
            var view=View(out _,out _,out var prefab);var store=Store(Define("spell",ItemKind.Spell));
            Add(store,"finite","spell",12);
            Assert.That(store.TryAdd(new RuntimeItemState("unlimited","spell",0,true),ItemLocation.Warehouse),Is.True);
            var items=Items(store);Call(view,"Refresh",_controller,prefab,items,items.Length);
            var square=view.GetComponentInChildren<InventoryItemView>();
            var count=(Text)typeof(InventoryItemView).GetField("_spellCount",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(square);
            Assert.That(count.text,Is.EqualTo("∞"));
            Assert.That(store.TryGet("finite",out var finite),Is.True);Assert.That(finite.RemainingCharges,Is.EqualTo(12));
        }

        [Test]
        public void SelectingAnotherWarehouseRepresentativeKeepsTheGroupTotalAndItsOwnCharges()
        {
            var view=View(out _,out _,out var prefab);var store=Store(Define("spell",ItemKind.Spell));
            Add(store,"spell.a","spell",3);Add(store,"spell.b","spell",8);
            typeof(SinglePlayerInventoryController).GetField("_selectedId",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(_controller,"spell.b");
            var items=Items(store);Call(view,"Refresh",_controller,prefab,items,items.Length);
            var square=view.GetComponentInChildren<InventoryItemView>();
            Assert.That(square.Snapshot.InstanceId,Is.EqualTo("spell.b"));
            Assert.That(square.Snapshot.RemainingCharges,Is.EqualTo(8),"被拖动的仍是这份8次实例，合计不能污染Snapshot。");
            var count=(Text)typeof(InventoryItemView).GetField("_spellCount",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(square);
            Assert.That(count.text,Is.EqualTo("11"));
        }

        [Test]
        public void QualificationGroupsDoNotPresentEntryChargesAsProjectileUses()
        {
            var view=View(out _,out _,out var prefab);var definition=Define("modifier",ItemKind.Spell);
            definition.SpellDefinition.Kind=Game.Skills.SpellKind.Modify;
            var store=Store(definition);Add(store,"modifier.a","modifier",12);Add(store,"modifier.b","modifier",12);
            var items=Items(store);Call(view,"Refresh",_controller,prefab,items,items.Length);
            var square=view.GetComponentInChildren<InventoryItemView>();
            var count=(Text)typeof(InventoryItemView).GetField("_spellCount",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(square);
            Assert.That(count.gameObject.activeSelf,Is.False);
            Assert.That(store.TryGet("modifier.a",out var item),Is.True);Assert.That(item.RemainingCharges,Is.EqualTo(12));
        }
        [Test]
        public void EmptyWarehouseStillDisplaysBackgroundSquares()
        {
            var view = View(out _, out _, out var prefab);
            Call(view, "Refresh", _controller, prefab, Array.Empty<InventoryItemSnapshot>(), 0);
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
            var items = Items(store); Call(view, "Refresh", _controller, prefab, items, items.Length);
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
            var items = Items(store); Call(view, "Refresh", _controller, prefab, items, items.Length);
            Call(view, "SetFilter", "spell", 0); Assert.That(Number(view, "GroupCount"), Is.EqualTo(1));
            Call(view, "Organize"); Call(view, "SetFilter", "", 0);
            Assert.That(Number(view, "GroupCount"), Is.EqualTo(2));
            Assert.That(store.Count, Is.EqualTo(2)); Assert.That(store.Version, Is.EqualTo(before));
        }
    }
}
