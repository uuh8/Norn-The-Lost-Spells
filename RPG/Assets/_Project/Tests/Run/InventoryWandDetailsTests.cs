using System;
using System.Reflection;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    /// <summary>
    /// 详情只是当前Source的值投影，不创建roll、不采用另一份Session、不修改库存或资源。
    /// 新字段/重载用反射查找，使首次RED是缺行为的明确失败，而非整个Unity项目编译失败。
    /// </summary>
    public sealed class InventoryWandDetailsTests : InventoryTestFixture
    {
        private const string PopupPath = "Assets/_Project/Prefabs/UI/InventorySlots/ItemInfoPopup.prefab";
        private static WandStats Rolled(string[] traits = null, float mana = 237.25f)
            => new WandStats(mana, 17.5f, .125f, 32, 3, .625f, 2.75f, true, traits);

        private ItemOwnershipStore Catalog(params string[] traits)
        {
            var definitions = new ItemDefinition[traits.Length + 1];
            definitions[0] = Define("detail.wand", ItemKind.Wand);
            for (int i = 0; i < traits.Length; i++) definitions[i + 1] = Define(traits[i], ItemKind.Spell);
            return Store(definitions);
        }

        private static InventoryReadModel Read(ItemOwnershipStore store, string id, WandStats? stats)
        {
            Assert.That(store.TryAdd(new RuntimeItemState(id, "detail.wand", 0, rolledWandStats: stats), ItemLocation.Warehouse), Is.True);
            return new InventoryReadModel(new SinglePlayerProfile("player", store), "details");
        }

        private static WandStats Fixed(in InventoryItemSnapshot snapshot)
        {
            var field = typeof(InventoryItemSnapshot).GetField("FixedWandStats");
            Assert.That(field, Is.Not.Null, "缺少随当前实例导出的不可变固定属性，不能让UI按ContentId重取模板。");
            Assert.That(field.FieldType, Is.EqualTo(typeof(WandStats?)));
            Assert.That(field.IsInitOnly, Is.True);
            var value = field.GetValue(snapshot);
            Assert.That(value, Is.TypeOf<WandStats>(), "合法杖应有明确固定属性。");
            return (WandStats)value;
        }

        [Test]
        public void RolledSnapshotKeepsEveryFixedValueAndOrderedImmutableTraits()
        {
            var ids = new[] { "trait.z", "trait.a", "trait.m", "trait.b" };
            var stats = Rolled(ids); var store = Catalog(ids); var read = Read(store, "original", stats);
            ids[0] = "changed";
            Assert.That(read.TryGetView("original", out var item), Is.True);
            var captured = Fixed(item);
            Assert.That(captured.SameAs(stats), Is.True);
            Assert.That(captured.AlwaysCastContentIds, Is.EqualTo(new[] { "trait.z", "trait.a", "trait.m", "trait.b" }));
            Assert.That(item.Rules.WandProgramCapacity, Is.EqualTo(32));
            Assert.That(item.InstanceId, Is.EqualTo("original"));
            Assert.That(item.Location, Is.EqualTo(ItemLocation.Warehouse));
        }

        [Test]
        public void TemplateSnapshotUsesFrozenCatalogInsteadOfLaterAuthoringEdits()
        {
            var definition = Define("detail.wand", ItemKind.Wand); var store = Store(definition);
            Assert.That(store.Catalog.TryGetWandStats("detail.wand", out var expected), Is.True);
            var data = new SerializedObject(definition.WandDefinition);
            data.FindProperty("_maxMana").floatValue = 999; data.ApplyModifiedPropertiesWithoutUndo();
            var read = Read(store, "template", null);
            Assert.That(read.TryGetView("template", out var item), Is.True);
            Assert.That(Fixed(item).SameAs(expected), Is.True, "目录已冻结，不能在每次查看时重读可变SO。");
            Assert.That(store.TryGet("template", out var original), Is.True);
            Assert.That(original.RolledWandStats.HasValue, Is.False, "展示不能把模板旧物转换成随机实例。");
        }

        [Test]
        public void SameContentDifferentInstancesKeepTheirOwnFixedValues()
        {
            var store = Catalog(); var read = Read(store, "first", Rolled());
            Assert.That(store.TryAdd(new RuntimeItemState("second", "detail.wand", 0, rolledWandStats: Rolled(mana: 418.5f)), ItemLocation.Warehouse), Is.True);
            Assert.That(read.TryGetView("first", out var first), Is.True);
            Assert.That(read.TryGetView("second", out var second), Is.True);
            Assert.That(Fixed(first).MaxMana, Is.EqualTo(237.25f));
            Assert.That(Fixed(second).MaxMana, Is.EqualTo(418.5f));
        }

        [Test]
        public void PopupShowsAllFixedParametersAndTraitsWithoutSortingOrRoundingAwayFractions()
        {
            var ids = new[] { "trait.z", "trait.a", "trait.m", "trait.b" };
            var read = Read(Catalog(ids), "owned", Rolled(ids)); read.TryGetView("owned", out var item);
            var popup = Popup(new Vector2(900, 800)); Show(popup, item);
            string text = Body(popup).text;
            foreach (string value in new[] { "237.25", "17.5", "0.125", "0.625", "2.75", "32", "3" })
                Assert.That(text, Does.Contain(value));
            foreach (string label in new[] { "Mana", "恢复", "施法", "重载", "散布", "容量", "抽取", "乱序" })
                Assert.That(text, Does.Contain(label));
            Assert.That(text.IndexOf(ids[0], StringComparison.Ordinal), Is.LessThan(text.IndexOf(ids[1], StringComparison.Ordinal)));
            Assert.That(text.IndexOf(ids[1], StringComparison.Ordinal), Is.LessThan(text.IndexOf(ids[2], StringComparison.Ordinal)));
            Assert.That(text.IndexOf(ids[2], StringComparison.Ordinal), Is.LessThan(text.IndexOf(ids[3], StringComparison.Ordinal)));
            Assert.That(text, Does.Not.Contain("详细信息待开发"));
        }

        [Test]
        public void OriginalRightClickReadsCurrentSourceAndReopeningDoesNotReuseAnotherSource()
        {
            var firstStore = Catalog(); var firstRead = Read(firstStore, "same-id", Rolled());
            var secondStore = Catalog(); var secondRead = Read(secondStore, "same-id", Rolled(mana: 418.5f));
            uint firstVersion = firstStore.Version, secondVersion = secondStore.Version;
            var popup = Popup(new Vector2(900, 800));
            var host = Track(new GameObject("Details controller")); host.SetActive(false);
            var controller = host.AddComponent<SinglePlayerInventoryController>();
            Set(controller, "_panelRoot", popup.transform.parent.gameObject); Set(controller, "_infoPopup", popup);
            var firstSource = new Source(firstRead); controller.BindPresentationSource(firstSource);
            Set(controller, "_open", true); popup.transform.parent.gameObject.SetActive(true);
            controller.ShowItemInfo("same-id", Vector2.zero, null);
            Assert.That(Body(popup).text, Does.Contain("237.25"));
            var secondSource = new Source(secondRead); controller.BindPresentationSource(secondSource);
            Set(controller, "_open", true); popup.transform.parent.gameObject.SetActive(true);
            controller.ShowItemInfo("same-id", Vector2.zero, null);
            Assert.That(Body(popup).text, Does.Contain("418.5").And.Not.Contain("237.25"));
            Assert.That(controller.PresentationSource, Is.SameAs(secondSource));
            Assert.That(firstStore.Version, Is.EqualTo(firstVersion)); Assert.That(secondStore.Version, Is.EqualTo(secondVersion));
            Assert.That(firstRead.TryGetView("same-id", out var first), Is.True);
            Assert.That(first.Location, Is.EqualTo(ItemLocation.Warehouse)); Assert.That(first.RemainingCharges, Is.Zero);
            Assert.That(firstStore.TryGet("same-id", out var original), Is.True);
            Assert.That(original.RolledWandStats.Value.SameAs(Rolled()), Is.True);
        }

        [TestCase(360, 230)]
        [TestCase(240, 180)]
        public void LongDetailsStayInsideParentAndCanScrollToLastTrait(int width, int height)
        {
            var ids = new[] { "trait.z." + new string('z', 96), "trait.a." + new string('a', 96),
                "trait.m." + new string('m', 96), "trait.b." + new string('b', 96) };
            var read = Read(Catalog(ids), "owned", Rolled(ids)); read.TryGetView("owned", out var item);
            var popup = Popup(new Vector2(width, height)); Show(popup, item);
            Canvas.ForceUpdateCanvases();
            var frame = Get<RectTransform>(popup, "_frame"); var parent = (RectTransform)popup.transform.parent;
            var corners = new Vector3[4]; frame.GetWorldCorners(corners);
            foreach (var corner in corners)
            {
                var local = parent.InverseTransformPoint(corner);
                Assert.That(local.x, Is.InRange(parent.rect.xMin - 1, parent.rect.xMax + 1));
                Assert.That(local.y, Is.InRange(parent.rect.yMin - 1, parent.rect.yMax + 1));
            }
            var scroll = popup.GetComponentInChildren<ScrollRect>(true);
            Assert.That(scroll, Is.Not.Null, "正文超过可用高度时必须可滚动，不能只把Text裁掉。");
            Assert.That(scroll.vertical, Is.True); Assert.That(scroll.horizontal, Is.False);
            Assert.That(scroll.content.rect.height, Is.GreaterThan(scroll.viewport.rect.height));
            Assert.That(Body(popup).text, Does.Contain(ids[3]));
            scroll.verticalNormalizedPosition = 0; Canvas.ForceUpdateCanvases();
            Assert.That(scroll.verticalNormalizedPosition, Is.EqualTo(0).Within(.01f));
        }

        [Test]
        public void PopupUsesTheExistingSpellNameInsteadOfExposingItsInternalContentKey()
        {
            const string key = "internal.spell.poison.001";
            var store = Catalog(key);
            Assert.That(store.Catalog.TryGet(key, out var definition), Is.True);
            var data = new SerializedObject(definition);
            data.FindProperty("_displayName").stringValue = "毒球"; data.ApplyModifiedPropertiesWithoutUndo();
            var read = Read(store, "owned", Rolled(new[] { key })); read.TryGetView("owned", out var item);
            var popup = Popup(new Vector2(900, 800)); Show(popup, item);
            Assert.That(Body(popup).text, Does.Contain("毒球").And.Not.Contain(key));
        }

        [Test]
        public void NonWandAndLegacyStringEntryKeepTheOriginalPlaceholder()
        {
            var definition = Define("ordinary", ItemKind.Consumable); var store = Store(definition);
            Add(store, "ordinary-item", "ordinary");
            var read = new InventoryReadModel(new SinglePlayerProfile("player", store), "details");
            read.TryGetView("ordinary-item", out var item); var popup = Popup(new Vector2(900, 800));
            Show(popup, item); Assert.That(Body(popup).text, Is.EqualTo("详细信息待开发"));
            popup.Show("legacy name", Vector2.zero, null, Body(popup).font);
            Assert.That(Body(popup).text, Is.EqualTo("详细信息待开发"));
        }

        private InventoryInfoPopup Popup(Vector2 size)
        {
            var parent = Track(new GameObject("Temporary detail bounds", typeof(RectTransform)));
            ((RectTransform)parent.transform).sizeDelta = size;
            var prefab = AssetDatabase.LoadAssetAtPath<InventoryInfoPopup>(PopupPath);
            Assert.That(prefab, Is.Not.Null);
            return UnityEngine.Object.Instantiate(prefab, parent.transform, false);
        }

        private static Text Body(InventoryInfoPopup popup) => Get<Text>(popup, "_body");
        private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static void Show(InventoryInfoPopup popup, in InventoryItemSnapshot item)
        {
            var method = typeof(InventoryInfoPopup).GetMethod("Show", new[] { typeof(InventoryItemSnapshot).MakeByRefType(), typeof(Vector2), typeof(Camera), typeof(Font) });
            Assert.That(method, Is.Not.Null, "正式Popup缺少接收选中实例值快照的只读入口。");
            method.Invoke(popup, new object[] { item, new Vector2(Screen.width - 2, 2), null, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") });
        }

        private sealed class Source : IInventoryPresentationSource
        {
            public Source(IInventoryReadOnly read) { ReadOnly = read; }
            public IInventoryReadOnly ReadOnly { get; }
            public IToolbeltReadOnly ToolbeltReadOnly => null;
            public IWandLoadoutReadOnly WandsReadOnly => null;
            public ISpellUseReadOnly SpellUsesReadOnly => null;
            public IWalletReadOnly WalletReadOnly => null;
            public bool UseSpellProgrammingV2 => true;
            public bool CanAccessWarehouse => false;
            public string WorldSceneKey => "current";
            public string EquippedBackpackId => null;
            public int SpellStorageCapacity => 12;
            public string InitializationMessage => "readonly";
            public bool HasUnsavedChanges => false;
            public bool TryCaptureWandResearch(string id, out WandResearchSnapshot snapshot) { snapshot = default; return false; }
            public bool TryCopyContainer(string id, MaterialAmount[] buffer, out int count, out ulong total, out ulong capacity)
            { count = 0; total = capacity = 0; return false; }
        }
    }
}
