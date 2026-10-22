using System;
using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class WandV2MigrationTests : InventoryTestFixture
    {
        private static float Float(in WandStats stats, string name)
        { var field = typeof(WandStats).GetField(name); Assert.That(field, Is.Not.Null, name); return (float)field.GetValue(stats); }
        private static WandStats V2()
        {
            var ctor = typeof(WandStats).GetConstructor(new[] { typeof(float), typeof(float), typeof(float), typeof(int), typeof(int), typeof(float), typeof(float), typeof(bool) });
            Assert.That(ctor, Is.Not.Null, "V2固定属性必须跟随实例而非UI缓存");
            return (WandStats)ctor.Invoke(new object[] { 222f, 17f, .12f, 6, 2, .73f, 4.5f, true });
        }
        private SinglePlayerProfile Profile(WandStats stats)
        {
            var store = Store(Define("wand", ItemKind.Wand));
            Assert.That(store.TryAdd(new RuntimeItemState("stable.wand", "wand", 0, rolledWandStats: stats), ItemLocation.Warehouse), Is.True);
            return new SinglePlayerProfile("player", store);
        }
        [Test] public void Schema13PreservesFiveValuesAndIdentityWhileAddingCompatibleDefaults()
        {
            var profile = Profile(new WandStats(222, 17, .42f, 6, 2)); var dto = ProfileSaveMapper.ToDto(profile, 1); dto.SchemaVersion = 13;
            foreach (string field in new[] { "WandReloadTime", "WandSpreadDegrees", "WandShuffle" })
            { var member = typeof(SinglePlayerItemDto).GetField(field); if (member != null) member.SetValue(dto.Items[0], member.FieldType == typeof(bool) ? (object)false : 0f); }
            Assert.That(ProfileSaveMapper.TryRestore(dto, profile.Items.Catalog, out var restored).Success, Is.True);
            restored.Items.TryGet("stable.wand", out var item); var stats = item.RolledWandStats.Value;
            Assert.That(stats.MaxMana, Is.EqualTo(222)); Assert.That(stats.ManaRegenPerSecond, Is.EqualTo(17));
            Assert.That(stats.CastCooldown, Is.EqualTo(.42f)); Assert.That(stats.ProgramCapacity, Is.EqualTo(6)); Assert.That(stats.BaseDraws, Is.EqualTo(2));
            Assert.That(Float(stats, "BaseCastDelay"), Is.EqualTo(.42f)); Assert.That(Float(stats, "BaseReloadTime"), Is.EqualTo(.42f));
            Assert.That(Float(stats, "BaseSpreadDegrees"), Is.Zero);
        }
        [Test] public void Schema14RoundTripAndRollbackSnapshotKeepAllInstanceProperties()
        {
            var profile = Profile(V2()); var dto = ProfileSaveMapper.ToDto(profile, 1);
            Assert.That(dto.SchemaVersion, Is.EqualTo(14));
            dto = JsonUtility.FromJson<SinglePlayerSaveDto>(JsonUtility.ToJson(dto));
            Assert.That(ProfileSaveMapper.TryRestore(dto, profile.Items.Catalog, out var restored).Success, Is.True);
            restored.Items.TryGet("stable.wand", out var item); Assert.That(item.RolledWandStats.Value.SameAs(V2()), Is.True);
            var rollback = ProfileAssetSnapshot.From(dto).ToDto();
            Assert.That(ProfileSaveMapper.TryRestore(rollback, profile.Items.Catalog, out var recovered).Success, Is.True);
            recovered.Items.TryGet("stable.wand", out var kept); Assert.That(kept.RolledWandStats.Value.SameAs(V2()), Is.True);
        }
        [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(-1f)]
        public void InvalidV2ReloadRejectsWholeCandidate(float value)
        {
            var profile = Profile(new WandStats(100, 10, .2f, 4, 1)); var dto = ProfileSaveMapper.ToDto(profile, 1); dto.SchemaVersion = 14;
            var field = typeof(SinglePlayerItemDto).GetField("WandReloadTime"); Assert.That(field, Is.Not.Null); field.SetValue(dto.Items[0], value);
            Assert.That(ProfileSaveMapper.TryRestore(dto, profile.Items.Catalog, out var rejected).Success, Is.False);
            Assert.That(rejected, Is.Null); Assert.That(profile.Items.TryGet("stable.wand", out _), Is.True);
        }
        [Test] public void FutureSchemaRejectsInsteadOfSilentlyLosingNewProperties()
        { var profile = Profile(new WandStats(100, 10, .2f, 4, 1)); var dto = ProfileSaveMapper.ToDto(profile, 1); dto.SchemaVersion = 15;
            Assert.That(ProfileSaveMapper.TryRestore(dto, profile.Items.Catalog, out _).Status, Is.EqualTo(ProfilePersistenceStatus.UnsupportedSchema)); }
    }
}
