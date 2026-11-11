using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    /// <summary>正式Coin领域行为RED；只用内存Catalog/真实Owned，不接服务、不运行物理场景或伪造Item金币。</summary>
    public sealed class OnlineCoinWorldTests : InventoryTestFixture
    {
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private readonly List<OnlineOwnedWorkspace> _owners = new List<OnlineOwnedWorkspace>();
        private ItemCatalogIndex _catalog;
        [SetUp] public void BuildCatalog() => _catalog = Store(Define("coin.test.bag", ItemKind.Backpack)).Catalog;
        [TearDown] public void DisposeOwners() { foreach (var owner in _owners) owner.Dispose(); _owners.Clear(); }

        private OnlineOwnedWorkspace Owner(ulong actor = 1, long gold = 10000, uint currencyVersion = 1, string run = "run")
        {
            var source = new OnlineOwnedAssetSnapshot
            {
                SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, RunId = run, PlayerId = "player" + actor,
                ActorId = actor, InventoryId = run + "/player" + actor, InventoryVersion = 1,
                GameplayConfigHash = Hash, Gold = gold, CurrencyVersion = currencyVersion, Items = Array.Empty<OnlineOwnedItemSnapshot>()
            };
            Assert.That(OnlineOwnedWorkspaceFactory.TryCreate(_catalog, source, Hash, out var owner, out var error), Is.True, error);
            _owners.Add(owner); return owner;
        }

        private static Type WorldType()
        {
            var type = typeof(OnlineOwnedWorkspace).Assembly.GetType("Game.Run.OnlineCoinWorld");
            Assert.That(type, Is.Not.Null, "缺少正式有界Coin领域；不能把金币伪装成Item或GMU。"); return type;
        }
        private static object World(Func<bool> gate = null) => Activator.CreateInstance(WorldType(), "run", gate ?? (() => true));
        private static object Call(object target, string method, params object[] args)
        {
            var entry = target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance);
            Assert.That(entry, Is.Not.Null, "Coin领域缺少正式调用：" + method); return entry.Invoke(target, args);
        }
        private static T Read<T>(object target, string name)
        {
            var property = target.GetType().GetProperty(name);
            if (property != null) return (T)property.GetValue(target);
            var field = target.GetType().GetField(name);
            Assert.That(field, Is.Not.Null, "Coin值记录缺字段：" + name); return (T)field.GetValue(target);
        }
        private static object Record(object world, ulong id)
        {
            var method = world.GetType().GetMethod("TryGet"); Assert.That(method, Is.Not.Null);
            object[] args = { id, null }; Assert.That((bool)method.Invoke(world, args), Is.True); return args[1];
        }
        private static bool Register(object world, ulong id = 5, long amount = 17, Vector3 position = default)
            => (bool)Call(world, "TryRegister", id, amount, position);
        private static bool Collect(object world, OnlineOwnedWorkspace owner, ulong id = 5, ulong actor = 1)
            => (bool)Call(world, "TryCollect", id, actor, owner);

        [Test] public void SameCoinRegistrationIsIdempotentButChangedFactsConflict()
        {
            var world = World(); Assert.That(Register(world), Is.True); uint version = Read<uint>(world, "Version");
            Assert.That(Register(world), Is.True); Assert.That(Read<int>(world, "Count"), Is.EqualTo(1));
            Assert.That(Read<uint>(world, "Version"), Is.EqualTo(version));
            Assert.That(Register(world, amount: 18), Is.False);
            Assert.That(Register(world, position: new Vector3(.0000001f, 0, 0)), Is.False, "异值不得用Vector近似相等隐藏。");
            Assert.That(Read<long>(Record(world, 5), "Amount"), Is.EqualTo(17L));
        }

        [Test] public void OneWinnerCreditsExactlyOnceAndTombstoneCannotRespawn()
        {
            var world = World(); var first = Owner(); var second = Owner(2);
            Assert.That(Register(world), Is.True); Assert.That(Collect(world, first), Is.True);
            Assert.That(first.Gold, Is.EqualTo(10017L)); Assert.That(first.CurrencyVersion, Is.EqualTo(2u));
            Assert.That(Collect(world, first), Is.False); Assert.That(Collect(world, second, actor: 2), Is.False);
            Assert.That(second.Gold, Is.EqualTo(10000L)); Assert.That(second.CurrencyVersion, Is.EqualTo(1u));
            Assert.That(Register(world), Is.True); Assert.That(Read<bool>(Record(world, 5), "Collected"), Is.True);
            Assert.That(Read<ulong>(Record(world, 5), "CollectorActorId"), Is.EqualTo(1ul));
            Assert.That(Read<int>(world, "Count"), Is.EqualTo(1));
        }

        [TestCase(0ul)] [TestCase(4ul)] [TestCase(17ul)]
        public void InvalidNpcIdentityNeverConsumesWorldMetadata(ulong id)
        {
            var world = World(); Assert.That(Register(world, id), Is.False);
            Assert.That(Read<int>(world, "Count"), Is.Zero); Assert.That(Read<uint>(world, "Version"), Is.EqualTo(1u));
        }

        [TestCase(0L)] [TestCase(-1L)]
        public void NonpositiveAmountNeverCreatesCoin(long amount)
        { var world = World(); Assert.That(Register(world, amount: amount), Is.False); Assert.That(Read<int>(world, "Count"), Is.Zero); }

        [Test] public void FrozenWorldOrClosedOwnerGateDoesNotConsumeCoinOrGold()
        {
            bool open = true; var world = World(() => open); var owner = Owner();
            Assert.That(Register(world), Is.True); uint before = Read<uint>(world, "Version"); open = false;
            Assert.That(Collect(world, owner), Is.False); Assert.That(Register(world, 6), Is.False);
            Assert.That(owner.Gold, Is.EqualTo(10000L)); Assert.That(owner.CurrencyVersion, Is.EqualTo(1u));
            Assert.That(Read<uint>(world, "Version"), Is.EqualTo(before)); open = true;
            owner.ConfigureAuthorityWriteGate(() => false);
            Assert.That(Collect(world, owner), Is.False); Assert.That(Read<bool>(Record(world, 5), "Collected"), Is.False);
        }

        [TestCase(long.MaxValue, 1u)] [TestCase(10000L, uint.MaxValue)]
        public void GoldOrCurrencyRevisionOverflowFailsWithoutConsumingCoin(long balance, uint revision)
        {
            var world = World(); var full = Owner(gold: balance, currencyVersion: revision);
            Assert.That(Register(world), Is.True); uint version = Read<uint>(world, "Version");
            Assert.That(Collect(world, full), Is.False);
            Assert.That(full.Gold, Is.EqualTo(balance)); Assert.That(full.CurrencyVersion, Is.EqualTo(revision));
            Assert.That(Read<uint>(world, "Version"), Is.EqualTo(version));
            Assert.That(Read<bool>(Record(world, 5), "Collected"), Is.False);
            Assert.That(Collect(world, Owner(2), actor: 2), Is.True, "失败者不能吃掉别人仍可拾取的同一个金币。");
        }

        [Test] public void WrongActorForeignRunNullAndDisposedOwnerCannotCollect()
        {
            var world = World(); var owner = Owner(); Assert.That(Register(world), Is.True);
            Assert.That(Collect(world, owner, actor: 2), Is.False);
            Assert.That(Collect(world, Owner(run: "foreign")), Is.False);
            Assert.That(Collect(world, null), Is.False);
            owner.Dispose(); Assert.That(Collect(world, owner), Is.False);
            Assert.That(Read<bool>(Record(world, 5), "Collected"), Is.False);
        }

        [Test] public void TwelveRegisteredIdentitiesRemainBoundedIncludingAllCollectedTombstones()
        {
            var world = World(); var owner = Owner();
            for (ulong id = 5; id <= 16; id++) { Assert.That(Register(world, id, 1), Is.True); Assert.That(Collect(world, owner, id), Is.True); }
            Assert.That(Read<int>(world, "Count"), Is.EqualTo(12)); Assert.That(owner.Gold, Is.EqualTo(10012L));
            Assert.That(Register(world, 17, 1), Is.False);
            Assert.That(Register(world, 5, 1), Is.True); Assert.That(Collect(world, owner, 5), Is.False);
            var recordType = WorldType().Assembly.GetType("Game.Run.OnlineCoinRecord"); Assert.That(recordType, Is.Not.Null);
            var target = Array.CreateInstance(recordType, 12);
            Assert.That((int)Call(world, "CopyTo", target), Is.EqualTo(12));
            for (int i = 0; i < target.Length; i++) Assert.That(Read<bool>(target.GetValue(i), "Collected"), Is.True);
            ((IDisposable)world).Dispose(); Assert.That(Register(world, 5, 1), Is.False);
            Assert.That(owner.Gold, Is.EqualTo(10012L)); Assert.That(Read<int>(world, "Count"), Is.EqualTo(12));
        }

        [Test] public void LootRetryConsumesOnlyTheOriginalCompleteCoinRollAtDeathPosition()
        {
            var previous = UnityEngine.Random.state;
            try
            {
                var table = Track(ScriptableObject.CreateInstance<DungeonLootTable>());
                table.CoinChance = 1; table.MinCoins = 13; table.MaxCoins = 17;
                var death = new Vector3(7, 8, 9);
                UnityEngine.Random.InitState(153);
                var expected = DungeonLootPlanner.Roll(table, "expected", death); var afterOneRoll = UnityEngine.Random.state;
                UnityEngine.Random.InitState(153);
                bool open = false; var world = World(() => open);
                var ctor = typeof(OnlineDungeonContainerLoot).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(string), typeof(Func<DungeonLootPlannedItem, bool>) }, null);
                Assert.That(ctor, Is.Not.Null);
                using (var loot = (OnlineDungeonContainerLoot)ctor.Invoke(new object[] { "run", (Func<DungeonLootPlannedItem, bool>)(_ => true) }))
                {
                    Assert.That((bool)Call(loot, "ConfigureCoins", world), Is.True);
                    Assert.That(loot.TryRollOnce(5, 0, 0, table, death), Is.True);
                    Assert.That(loot.PendingCount, Is.EqualTo(1)); Assert.That(Read<int>(world, "Count"), Is.Zero);
                    Assert.That(UnityEngine.Random.state, Is.EqualTo(afterOneRoll));
                    Assert.That(loot.TryRollOnce(5, 0, 0, table, death), Is.True);
                    open = true; loot.Step(.2f, true);
                    Assert.That(loot.PendingCount, Is.Zero); Assert.That(Read<int>(world, "Count"), Is.EqualTo(1));
                    Assert.That(Read<long>(Record(world, 5), "Amount"), Is.EqualTo((long)expected.CoinAmount));
                    Assert.That(Read<Vector3>(Record(world, 5), "Position"), Is.EqualTo(death));
                    Assert.That(UnityEngine.Random.state, Is.EqualTo(afterOneRoll), "重试/重复死亡不得重新抽Coin或改变完整表随机流。");
                }
            }
            finally { UnityEngine.Random.state = previous; }
        }

        [Test] public void ReplicaCoinOnlyPresentsCollectionAndKeepsOriginalTriggerGeometry()
        {
            var root = Track(new GameObject("Replica coin fixture"));
            var trigger = root.AddComponent<SphereCollider>(); trigger.isTrigger = true; trigger.radius = .75f; trigger.center = Vector3.up * .2f;
            var pickup = root.AddComponent<WorldCurrencyPickup>();
            typeof(WorldCurrencyPickup).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(pickup, null);
            Call(pickup, "ConfigureReplica", 17L);
            var actor = Track(new GameObject("Replica candidate actor"));
            Assert.That(pickup.TryCollect(actor.transform), Is.False); Assert.That(pickup.Collected, Is.False);
            Call(pickup, "PresentCollected"); Assert.That(pickup.Collected, Is.True); Assert.That(trigger.enabled, Is.False);
            Call(pickup, "PresentCollected");
            Assert.That(trigger.radius, Is.EqualTo(.75f)); Assert.That(trigger.center, Is.EqualTo(Vector3.up * .2f));
            Assert.That(root.transform.localScale, Is.EqualTo(Vector3.one));
            Assert.That(typeof(WorldCurrencyPickup).GetMethod("Configure", new[] { typeof(SinglePlayerInventorySession), typeof(long) }), Is.Not.Null);
        }

        private sealed class Collector : IWorldCurrencyCollector
        {
            internal Func<ulong, Transform, bool> Collect;
            public bool TryCollectWorldCurrency(ulong id, Transform actor) => Collect != null && Collect(id, actor);
        }
        private GameObject CoinPrefab()
        {
            var root = Track(new GameObject("Coin source fixture")); root.SetActive(false);
            var trigger = root.AddComponent<SphereCollider>(); trigger.isTrigger = true; trigger.radius = .75f;
            root.AddComponent<WorldCurrencyPickup>(); return root;
        }

        [Test] public void RuntimeOriginalPickupPortCreditsOneAttachedOwnerAndCapturesBothActiveAndTombstoneFacts()
        {
            var prefab = CoinPrefab(); var actor = Track(new GameObject("coin actor")); bool open = true;
            var first = Owner(); var second = Owner(2);
            using var runtime = new OnlineGroundItemWorld(_catalog, actor.scene);
            var port = new Collector { Collect = (id, touched) => touched == actor.transform && runtime.TryCollectCoin(id, 1) };
            Assert.That(runtime.ConfigureCoins("run", prefab, () => open, port), Is.True);
            Assert.That(runtime.Attach(first) && runtime.Attach(second), Is.True);
            var point = new Vector3(2, 3, 4);
            Assert.That(runtime.Coins.TryRegister(5, 17, point), Is.True); runtime.SynchronizeCoins();
            var active = runtime.Capture(); Assert.That(active.Version, Is.EqualTo(2u));
            Assert.That(active.Coins.Length, Is.EqualTo(1)); Assert.That(active.Coins[0].Position, Is.EqualTo(point));
            active.Coins[0].Amount = 999; Assert.That(runtime.Capture().Coins[0].Amount, Is.EqualTo(17L));
            var models = (WorldCurrencyPickup[])typeof(OnlineGroundItemWorld).GetField("_coinModels", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(runtime);
            var pickup = models[0]; Assert.That(pickup, Is.Not.Null);
            Assert.That(pickup.GetComponent<SphereCollider>().radius, Is.EqualTo(.75f));
            open = false; Assert.That(pickup.TryCollect(actor.transform), Is.False); Assert.That(first.Gold, Is.EqualTo(10000L));
            open = true; Assert.That(pickup.TryCollect(actor.transform), Is.True); Assert.That(pickup.Collected, Is.True);
            Assert.That(first.Gold, Is.EqualTo(10017L)); Assert.That(first.CurrencyVersion, Is.EqualTo(2u));
            Assert.That(pickup.TryCollect(actor.transform), Is.False); Assert.That(runtime.TryCollectCoin(5, 2), Is.False);
            Assert.That(second.Gold, Is.EqualTo(10000L)); Assert.That(runtime.Capture().Version, Is.EqualTo(3u));
            Assert.That(runtime.Capture().Coins, Is.Empty);
            var facts = new OnlineCoinCheckpointRecord[12]; Assert.That(runtime.CopyCoinCheckpointsTo(facts), Is.EqualTo(1));
            Assert.That(facts[0].Id, Is.EqualTo(5ul)); Assert.That(facts[0].Amount, Is.EqualTo(17L));
            Assert.That(facts[0].Collected, Is.True); Assert.That(facts[0].CollectorActorId, Is.EqualTo(1ul));
            Assert.That(facts[0].Position, Is.EqualTo(point));
        }

        [Test] public void CombinedGroundVersionExhaustionFailsBeforeOwnerCreditAndNeverWraps()
        {
            var prefab = CoinPrefab(); var owner = Owner();
            using var runtime = new OnlineGroundItemWorld(_catalog, prefab.scene);
            Assert.That(runtime.ConfigureCoins("run", prefab, () => true, new Collector()), Is.True);
            Assert.That(runtime.Attach(owner), Is.True); Assert.That(runtime.Coins.TryRegister(5, 17, Vector3.zero), Is.True);
            var store = typeof(OnlineGroundItemWorld).GetField("_ground", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(runtime);
            store.GetType().GetProperty("Version").SetValue(store, uint.MaxValue - 1);
            Assert.That(runtime.Version, Is.EqualTo(uint.MaxValue)); Assert.That(runtime.TryCollectCoin(5, 1), Is.False);
            Assert.That(owner.Gold, Is.EqualTo(10000L)); Assert.That(owner.CurrencyVersion, Is.EqualTo(1u));
            Assert.That(runtime.Coins.TryGet(5, out var coin), Is.True); Assert.That(coin.Collected, Is.False);
            Assert.That(runtime.Faulted, Is.True); Assert.That(runtime.Capture(), Is.Null);
        }

        [Test] public void ClientPresenterUsesOriginalPrefabAndConfirmedRemovalAnimatesWithoutWalletWrite()
        {
            var prefab = CoinPrefab(); var actor = Track(new GameObject("coin replica actor")); var owner = Owner();
            Assert.That(owner.TryCapturePresentation(2, 1, 1, out var snapshot), Is.True);
            snapshot.Ground = new OnlineGroundInventorySnapshot { Version = 1, Coins = new[] { new OnlineCoinSnapshot { Id = 5, Amount = 17, Position = Vector3.up } } };
            var identity = new OnlineInventoryPresentationIdentity("run", "player1", 1, "run/player1", Hash, 2, "CoinFixture");
            Assert.That(OnlineInventoryPresentationSource.TryCreate(_catalog, identity, snapshot, out var source, out var error), Is.True, error);
            using (source)
            using (var presentation = new OnlineGroundItemPresentation(source, actor.transform, prefab))
            {
                Assert.That(presentation.Sync(), Is.True);
                var models = (WorldCurrencyPickup[])typeof(OnlineGroundItemPresentation).GetField("_coinModels", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(presentation);
                var pickup = models[0]; Assert.That(pickup, Is.Not.Null); Assert.That(pickup.Amount, Is.EqualTo(17L));
                Assert.That(pickup.TryCollect(actor.transform), Is.False); Assert.That(source.Currency, Is.EqualTo(10000L));
                Assert.That(owner.TryCapturePresentation(2, 2, 2, out var removed), Is.True);
                removed.Ground = new OnlineGroundInventorySnapshot { Version = 2 };
                Assert.That(source.TryInstall(removed, out error), Is.True, error); Assert.That(presentation.Sync(), Is.True);
                Assert.That(pickup.Collected, Is.True); Assert.That(pickup.GetComponent<SphereCollider>().enabled, Is.False);
                Assert.That(source.Currency, Is.EqualTo(10000L)); Assert.That(owner.Gold, Is.EqualTo(10000L));
            }
        }
    }
}
