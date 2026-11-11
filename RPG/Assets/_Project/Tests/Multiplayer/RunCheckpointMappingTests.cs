using Game.Core;
using NUnit.Framework;

namespace Game.Multiplayer.Tests
{
    public sealed class RunCheckpointMappingTests
    {
        [Test] public void ReverseMappingKeepsRealRollOrderChargesAndDoesNotLendSourceArrays()
        {
            var original = new OnlineOwnedAssetSnapshot { SchemaVersion = 1, RunId = "run", PlayerId = "player", ActorId = 2,
                InventoryId = "run/player", InventoryVersion = 9, GameplayConfigHash = "hash", SelectedWandInstanceId = "wand",
                Items = new[] { new OnlineOwnedItemSnapshot { InstanceId = "wand", ContentId = "wandtest", Revision = 7,
                    LocationKind = 3, Slot = 1, ParentInstanceId = "", UseTemplateWandStats = false,
                    WandStats = new OnlineOwnedWandStatsSnapshot { MaxMana = 123.25f, ProgramCapacity = 32, BaseDraws = 2,
                        AlwaysCastContentIds = new[] { "second", "first" } } },
                    new OnlineOwnedItemSnapshot { InstanceId = "card", ContentId = "cardtest", Revision = 3, LocationKind = 2,
                        ParentInstanceId = "wand", Slot = 31, RemainingCharges = 4, HasUnlimitedCharges = false } } };
            var shared = OnlineOwnedAssetMapping.ToShared(original); Assert.That(shared, Is.Not.Null);
            Assert.That(shared.InventoryVersion, Is.EqualTo(9)); Assert.That(shared.ActorId, Is.EqualTo(2));
            Assert.That(shared.Items[0].WandStats.MaxMana, Is.EqualTo(123.25f));
            Assert.That(shared.Items[0].WandStats.AlwaysCastContentIds, Is.EqualTo(new[] { "second", "first" }));
            Assert.That(shared.Items[1].ParentInstanceId, Is.EqualTo("wand")); Assert.That(shared.Items[1].Slot, Is.EqualTo(31));
            Assert.That(shared.Items[1].RemainingCharges, Is.EqualTo(4)); Assert.That(shared.Items[1].WandStats, Is.Null);
            shared.Items[0].WandStats.AlwaysCastContentIds[0] = "changed";
            Assert.That(original.Items[0].WandStats.AlwaysCastContentIds[0], Is.EqualTo("second"));
        }
        [Test] public void TransitAndMaterialFactsKeepTheirOriginalIdentityAndDeepCopyComponents()
        {
            var source = new OnlineOwnedItemSnapshot { InstanceId = "bottle", ContentId = "container", LocationKind = 6,
                ParentInstanceId = "flight", Slot = -1, Revision = 8,
                ContainerState = new OnlineOwnedContainerStateSnapshot { SchemaVersion = 1,
                    Components = new[] { new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 2, Quantity = 17 } } } };
            var copy = OnlineOwnedAssetMapping.ToSharedItem(source); Assert.That(copy, Is.Not.Null);
            Assert.That(copy.LocationKind, Is.EqualTo(6)); Assert.That(copy.ParentInstanceId, Is.EqualTo("flight"));
            Assert.That(copy.ContainerState.Components[0].Quantity, Is.EqualTo(17));
            Assert.That(copy.WandStats, Is.Null); Assert.That(copy.MaterialState, Is.Null); Assert.That(copy.AttachmentState, Is.Null);
            copy.ContainerState.Components[0].Quantity = 1; Assert.That(source.ContainerState.Components[0].Quantity, Is.EqualTo(17));
            Assert.That(OnlineOwnedAssetMapping.ToShared(null), Is.Null);
        }
    }
}
