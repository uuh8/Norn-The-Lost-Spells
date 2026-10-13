using Game.Core;
using NUnit.Framework;
using UnityEditor;

namespace Game.Run.Tests
{
    public sealed class ContainerCapacityMigrationTests : InventoryTestFixture
    {
        [Test] public void Old150CapacityMigratesTo15000WithoutCreatingMaterialOrChangingCoins()
        {
            var definition = Define("bottle", ItemKind.Container);
            var fields = new SerializedObject(definition); fields.FindProperty("_containerCapacityGmu").longValue = 150; fields.ApplyModifiedPropertiesWithoutUndo();
            var old = Store(definition); var mix = new ContainerComposition(150, 410); mix.AddReserved(new[] { new MaterialAmount(1, 100) }, 1);
            Assert.That(old.TryAdd(new RuntimeItemState("a", "bottle", 0, composition: mix), ItemLocation.Warehouse), Is.True);
            var dto = ProfileSaveMapper.ToDto(new SinglePlayerProfile("player", old, currency: 77), 0); dto.SchemaVersion = 10;
            fields.FindProperty("_containerCapacityGmu").longValue = 15000; fields.ApplyModifiedPropertiesWithoutUndo();
            var index = Store(definition).Catalog;
            Assert.That(ProfileSaveMapper.TryRestore(dto, index, out var restored).Success, Is.True);
            Assert.That(restored.Items.TryGet("a", out var item), Is.True); Assert.That(item.Composition.Total, Is.EqualTo(100));
            Assert.That(item.Composition.Capacity, Is.EqualTo(15000)); Assert.That(restored.Currency, Is.EqualTo(77));
            dto.Items[0].ContainerCapacity = 149;
            Assert.That(ProfileSaveMapper.TryRestore(dto, index, out _).Success, Is.False, "不能把任意损坏容量当作迁移");
        }
    }
}
