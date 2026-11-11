using System;
using System.Reflection;
using Game.Core;
using NUnit.Framework;

namespace Game.Run.Tests
{
    /// <summary>
    /// 统一金币切片只验证可信冻结值→真实Owner→表现/检查点的值保真，不发金币、不跑世界，
    /// 不规定钱包落库时点。反射只发现新增合同字段，使RED可编译且不会降级到本地模板余额。
    /// </summary>
    public sealed class OnlineUnifiedGoldOwnerTests : InventoryTestFixture
    {
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private ItemCatalogIndex _catalog;

        [SetUp] public void Catalog()
        {
            _catalog = Store(Define("gold.test.bag", ItemKind.Backpack, bagWidth: 8, bagHeight: 8)).Catalog;
        }

        private static void SetGold(OnlineOwnedAssetSnapshot assets, long gold, uint version)
        {
            var goldField = typeof(OnlineOwnedAssetSnapshot).GetField("Gold");
            var versionField = typeof(OnlineOwnedAssetSnapshot).GetField("CurrencyVersion");
            Assert.That(goldField, Is.Not.Null, "Owned合同缺少真实统一金币，不能用Profile默认0代替。");
            Assert.That(versionField, Is.Not.Null, "Owned合同缺少独立DS货币修订。");
            goldField.SetValue(assets, gold); versionField.SetValue(assets, version);
        }

        private static T Value<T>(object value, string name)
        {
            var field = value.GetType().GetField(name);
            Assert.That(field, Is.Not.Null, "导出遗漏统一金币合同字段：" + name);
            return (T)field.GetValue(value);
        }

        private static T Property<T>(object value, string name)
        {
            var property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.That(property, Is.Not.Null, "Owner缺少只读金币事实：" + name);
            return (T)property.GetValue(value);
        }

        private static OnlineOwnedAssetSnapshot Assets(long gold = 10000, uint version = 7)
        {
            var value = new OnlineOwnedAssetSnapshot
            {
                SchemaVersion = 3, RunId = "run", PlayerId = "player", ActorId = 1,
                InventoryId = "inventory", InventoryVersion = 9, GameplayConfigHash = Hash,
                SelectedWandInstanceId = "", Items = Array.Empty<OnlineOwnedItemSnapshot>()
            };
            SetGold(value, gold, version); return value;
        }

        private OnlineOwnedWorkspace Owner(OnlineOwnedAssetSnapshot assets)
        {
            Assert.That(OnlineOwnedWorkspaceFactory.TryCreate(_catalog, assets, Hash, out var owner, out var error), Is.True, error);
            return owner;
        }

        private static OnlineInventoryPresentationSnapshot Snapshot(long gold = 10000, uint version = 7, ulong sequence = 1, uint epoch = 4)
        {
            return new OnlineInventoryPresentationSnapshot
            {
                SchemaVersion = OnlineInventoryPresentationSnapshot.CurrentSchemaVersion,
                ConnectionEpoch = epoch, SnapshotSequence = sequence, ServerTick = 40,
                ResourceVersion = 1, SelectionVersion = 1, SpellUseVersion = 1,
                Assets = Assets(gold, version), Wands = Array.Empty<OnlineInventoryWandPresentationSnapshot>(),
                SpellUses = Array.Empty<OnlineInventorySpellUsePresentationSnapshot>()
            };
        }

        private OnlineInventoryPresentationSource Source(OnlineInventoryPresentationSnapshot snapshot)
        {
            var identity = new OnlineInventoryPresentationIdentity("run", "player", 1, "inventory", Hash, snapshot.ConnectionEpoch, "world");
            Assert.That(OnlineInventoryPresentationSource.TryCreate(_catalog, identity, snapshot, out var source, out var error), Is.True, error);
            return source;
        }

        [Test] public void OwnedSchemaThreeExplicitlyDistinguishesMissingOldGold()
        {
            Assert.That(OnlineOwnedAssetSnapshot.CurrentSchemaVersion, Is.EqualTo(3u));
        }

        [TestCase(0L)] [TestCase(10000L)] [TestCase(long.MaxValue)]
        public void TrustedGoldImportsExactlyAndSurvivesPresentationAndCheckpoint(long gold)
        {
            var input = Assets(gold);
            using (var owner = Owner(input))
            {
                Assert.That(Property<long>(owner, "Gold"), Is.EqualTo(gold));
                Assert.That(Property<uint>(owner, "CurrencyVersion"), Is.EqualTo(7u));
                Assert.That(owner.ConfigureCheckpointJournal(new OnlineRunItemRetirementJournal()), Is.True);
                Assert.That(owner.TryCapturePresentation(4, 1, 40, out var view), Is.True);
                Assert.That(Value<long>(view.Assets, "Gold"), Is.EqualTo(gold));
                Assert.That(Value<uint>(view.Assets, "CurrencyVersion"), Is.EqualTo(7u));
                Assert.That(owner.TryCaptureCheckpoint(out var checkpoint), Is.True);
                Assert.That(Value<long>(checkpoint.Assets, "Gold"), Is.EqualTo(gold));
                Assert.That(Value<uint>(checkpoint.Assets, "CurrencyVersion"), Is.EqualTo(7u));
                SetGold(input, 123, 99); SetGold(checkpoint.Assets, 456, 100);
                Assert.That(owner.TryCaptureCheckpoint(out var again), Is.True);
                Assert.That(Value<long>(again.Assets, "Gold"), Is.EqualTo(gold), "入局DTO或导出DTO不能反写存活Owner。");
                Assert.That(Value<uint>(again.Assets, "CurrencyVersion"), Is.EqualTo(7u));
            }
        }

        [TestCase(-1L, 7u)] [TestCase(0L, 0u)]
        public void NegativeOrUnversionedGoldCannotCreateOwnerOrClientSource(long gold, uint version)
        {
            var input = Assets(gold, version);
            Assert.That(OnlineOwnedWorkspaceFactory.TryCreate(_catalog, input, Hash, out var owner, out var error), Is.False);
            Assert.That(owner, Is.Null); Assert.That(error, Is.Not.Empty);
            var snapshot = Snapshot(gold, version);
            var identity = new OnlineInventoryPresentationIdentity("run", "player", 1, "inventory", Hash, 4, "world");
            Assert.That(OnlineInventoryPresentationSource.TryCreate(_catalog, identity, snapshot, out var source, out error), Is.False);
            Assert.That(source, Is.Null); Assert.That(error, Is.Not.Empty);
        }

        [TestCase(0u)] [TestCase(1u)] [TestCase(2u)]
        public void OldSchemaCannotGrantDefaultGold(uint schema)
        {
            var input = Assets(); input.SchemaVersion = schema;
            Assert.That(OnlineOwnedWorkspaceFactory.TryCreate(_catalog, input, Hash, out var owner, out _), Is.False);
            Assert.That(owner, Is.Null);
            var snapshot = Snapshot(); snapshot.Assets.SchemaVersion = schema;
            var identity = new OnlineInventoryPresentationIdentity("run", "player", 1, "inventory", Hash, 4, "world");
            Assert.That(OnlineInventoryPresentationSource.TryCreate(_catalog, identity, snapshot, out var source, out _), Is.False);
            Assert.That(source, Is.Null);
        }

        [TestCase(0L)] [TestCase(10000L)] [TestCase(long.MaxValue)]
        public void ClientWalletDisplaysCurrentUnifiedBalanceAndIndependentNonzeroRevision(long gold)
        {
            using (var source = Source(Snapshot(gold)))
            {
                Assert.That(source.WalletReadOnly, Is.Not.Null, "金币必须进入原UI的正式钱包读口。");
                Assert.That(source.WalletReadOnly, Is.SameAs(source));
                Assert.That(source.WalletReadOnly.Currency, Is.EqualTo(gold));
                Assert.That(source.WalletReadOnly.Version, Is.EqualTo(7u));
            }
        }

        [TestCase(6u, 10000L)] [TestCase(7u, 9999L)] [TestCase(7u, 10001L)]
        public void ClientRejectsRegressedRevisionOrChangedBalanceAtSameRevisionAtomically(uint version, long gold)
        {
            using (var source = Source(Snapshot()))
            {
                Assert.That(source.WalletReadOnly, Is.Not.Null);
                Assert.That(source.TryInstall(Snapshot(gold, version, 2), out var error), Is.False);
                Assert.That(error, Is.Not.Empty);
                Assert.That(source.SnapshotSequence, Is.EqualTo(1ul));
                Assert.That(source.WalletReadOnly.Currency, Is.EqualTo(10000L));
                Assert.That(source.WalletReadOnly.Version, Is.EqualTo(7u));
            }
        }

        [Test] public void NewerGoldRevisionCanRepresentALegitimateDecreaseWithoutInventingCurrencyRules()
        {
            using (var source = Source(Snapshot()))
            {
                Assert.That(source.WalletReadOnly, Is.Not.Null); var wallet = source.WalletReadOnly;
                Assert.That(source.TryInstall(Snapshot(9000, 8, 2), out var error), Is.True, error);
                Assert.That(source.WalletReadOnly, Is.SameAs(wallet));
                Assert.That(wallet.Currency, Is.EqualTo(9000L)); Assert.That(wallet.Version, Is.EqualTo(8u));
            }
        }

        [Test] public void DisposedSourceDoesNotExposeUnknownWalletAsZeroBalance()
        {
            var source = Source(Snapshot(12347));
            Assert.That(source.WalletReadOnly.Currency, Is.EqualTo(12347));
            source.Dispose();
            Assert.That(source.WalletReadOnly, Is.Null, "失效Source应撤销钱包读能力，不能显示伪造的0余额。");
        }

        [Test] public void ReconnectReadsSameLivingWorkspaceAndNeverImportsAnotherStartingBalance()
        {
            using (var owner = Owner(Assets(27, 2)))
            {
                Assert.That(owner.TryCapturePresentation(4, 1, 40, out var first), Is.True);
                Assert.That(owner.TryCapturePresentation(8, 2, 41, out var reconnect), Is.True);
                Assert.That(Value<long>(reconnect.Assets, "Gold"), Is.EqualTo(27L));
                Assert.That(Value<uint>(reconnect.Assets, "CurrencyVersion"), Is.EqualTo(2u));
                using (var source = Source(reconnect))
                {
                    Assert.That(source.WalletReadOnly, Is.Not.Null);
                    Assert.That(source.WalletReadOnly.Currency, Is.EqualTo(27L));
                    Assert.That(source.WalletReadOnly.Version, Is.EqualTo(2u));
                }
                Assert.That(Value<long>(first.Assets, "Gold"), Is.EqualTo(27L));
            }
        }
    }
}
