using System;
using System.Reflection;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.Networking;
using Mirror;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 只替换网络组合根，不启动Transport。真实Combat/Ground Owner与Scene对象验证重绑不会调用Gameplay Dispose；
    /// Reflection只填本例的稀疏事实/水位，并观察原回调所有权，不复制一份网络或玩法实现。
    /// </summary>
    public sealed class CombatNetworkingRebindTests
    {
        private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _worldRoot, _oldRoot, _newRoot, _liveOutput;
        private OnlineWorldBootstrap _world;
        private OnlineCombatWorld _combat;
        private OnlineGroundItemAuthority _ground;
        private RoomNetworkManager _oldManager, _newManager;
        private CombatNetworkChannel _oldChannel, _newChannel;
        private RoomInstanceBootstrap _ownerInstance;
        private NetworkManager _previousSingleton;

        [SetUp] public void PrepareOneGameplayOwnerAndTwoInactiveNetworkRoots()
        {
            Assert.That(NetworkServer.active || NetworkClient.active, Is.False, "不接管用户正在运行的连接。");
            _previousSingleton = NetworkManager.singleton;
            _worldRoot = new GameObject("Combat rebind preserved world"); _worldRoot.SetActive(false);
            _oldRoot = new GameObject("Combat rebind previous network"); _oldRoot.SetActive(false);
            _newRoot = new GameObject("Combat rebind next network"); _newRoot.SetActive(false);
            _world = _worldRoot.AddComponent<OnlineWorldBootstrap>();
            _oldManager = _oldRoot.AddComponent<RoomNetworkManager>(); _oldChannel = _oldRoot.AddComponent<CombatNetworkChannel>();
            _newManager = _newRoot.AddComponent<RoomNetworkManager>(); _newChannel = _newRoot.AddComponent<CombatNetworkChannel>();
            var definition = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
            var profile = Resources.Load<OnlineMovementProfile>("Online/MovementProfile");
            Assert.That(definition, Is.Not.Null); Assert.That(profile, Is.Not.Null);
            var simulation = new CombatSimulationWorld();
            Set(_world, "<Registry>k__BackingField", new NetworkActorRegistry(profile, _worldRoot.transform, definition, simulation));
            Set(_world, "_stopped", true); Set(_world, "_profile", profile); Set(_world, "_combatDefinition", definition);
            _combat = new OnlineCombatWorld(_world, definition, simulation, _oldChannel, _oldManager, null);
            Set(_world, "_combat", _combat); Set(_world, "_manager", _oldManager); Set(_world, "_boundManager", _oldManager);
            _ground = new OnlineGroundItemAuthority(definition, _worldRoot.scene); Set(_combat, "_groundItems", _ground);
            ((bool[])Get(_combat, "_homeEntryRestored"))[0] = true; Set(_combat, "_homeGroundRestored", true);
            ((uint[])Get(_combat, "_inventoryVersions"))[0] = 7; ((uint[])Get(_combat, "_bootstrappedEpoch"))[0] = 2;
            ((ulong[])Get(_combat, "_inventorySequences"))[0] = 90; Set(_combat, "_sequence", 50u);
            ((OnlineCastResourceSnapshot[])Get(_combat, "_resources"))[0] = new OnlineCastResourceSnapshot
                { WandInstanceId = "same-wand", MaxMana = 100, CurrentMana = 37, RemainingCharges = 9 };
            _liveOutput = new GameObject("Combat rebind original live output"); _liveOutput.transform.SetParent(_worldRoot.transform, false);
            var output = ((Array)Get(_combat, "_outputs")).GetValue(0); Set(output, "Id", 77UL); Set(output, "Root", _liveOutput);
            Set(_combat, "<ActiveOutputs>k__BackingField", 1);
        }

        [Test] public void ClientRebindPreservesLiveGameplayAndMovesEachNetworkCallbackExactlyOnce()
        {
            Assert.That(Rebind(null, out var error), Is.True, error);
            Assert.That(Get(_combat, "_groundItems"), Is.SameAs(_ground)); Assert.That(_ground.Capture(), Is.Not.Null);
            Assert.That(((bool[])Get(_combat, "_homeEntryRestored"))[0], Is.True);
            Assert.That((bool)Get(_combat, "_homeGroundRestored"), Is.True);
            Assert.That(_liveOutput != null, Is.True); Assert.That(_combat.ActiveOutputs, Is.EqualTo(1));
            Assert.That(_combat.TryGetResources(1, out var resources), Is.True); Assert.That(resources.CurrentMana, Is.EqualTo(37));
            Assert.That(resources.RemainingCharges, Is.EqualTo(9));
            Assert.That(((uint[])Get(_combat, "_inventoryVersions"))[0], Is.Zero);
            Assert.That(((uint[])Get(_combat, "_bootstrappedEpoch"))[0], Is.Zero);
            Assert.That(((ulong[])Get(_combat, "_inventorySequences"))[0], Is.EqualTo(90));
            Assert.That((uint)Get(_combat, "_sequence"), Is.EqualTo(50));
            AssertCallbacks(_oldChannel, 0); AssertCallbacks(_newChannel, 1);
            var oldInventory = _oldRoot.GetComponent<InventoryNetworkChannel>(); var nextInventory = _newRoot.GetComponent<InventoryNetworkChannel>();
            Assert.That(Get(oldInventory, "_worldOwner"), Is.Null); Assert.That(Get(nextInventory, "_worldOwner"), Is.SameAs(_combat));
            var senders = (InventorySnapshotSender[])Get(nextInventory, "_snapshotSenders"); senders[0] = new InventorySnapshotSender();
            Assert.That(senders[0].TryQueue(2, 91, new byte[] { 1, 2, 3 }), Is.True);
            Assert.That(Rebind(null, out error), Is.True, error);
            AssertCallbacks(_newChannel, 1); Assert.That(_liveOutput != null, Is.True);
            Assert.That(senders[0].BufferedCount, Is.EqualTo(1), "同一组网络重复绑定不能清掉可靠在途页。");
        }

        [Test] public void WorldBindNetworkKeepsTheSameCombatAndGroundOwnerInsteadOfConstructingAnotherGameplayWorld()
        {
            SetSingleton(_newManager);
            typeof(OnlineWorldBootstrap).GetMethod("BindNetwork", Private).Invoke(_world, null);
            Assert.That(_world.Combat, Is.SameAs(_combat));
            Assert.That(_ground.Capture(), Is.Not.Null); Assert.That(_liveOutput != null, Is.True);
            Assert.That(((bool[])Get(_combat, "_homeEntryRestored"))[0], Is.True);
            AssertCallbacks(_oldChannel, 0); AssertCallbacks(_newChannel, 1);
        }

        [Test] public void TemporaryManagerGapSuspendsOnlyAdaptersAndCanReattachTheSameChannels()
        {
            SetSingleton(null);
            typeof(OnlineWorldBootstrap).GetMethod("BindNetwork", Private).Invoke(_world, null);
            Assert.That(_world.Combat, Is.SameAs(_combat)); Assert.That(_ground.Capture(), Is.Not.Null);
            Assert.That(_liveOutput != null, Is.True); AssertCallbacks(_oldChannel, 0);
            SetSingleton(_oldManager);
            typeof(OnlineWorldBootstrap).GetMethod("BindNetwork", Private).Invoke(_world, null);
            Assert.That(_world.Combat, Is.SameAs(_combat)); Assert.That(_ground.Capture(), Is.Not.Null);
            AssertCallbacks(_oldChannel, 1); Assert.That(_oldChannel.enabled, Is.True);
            Assert.That(((bool[])Get(_combat, "_homeEntryRestored"))[0], Is.True);
        }

        [Test] public void DifferentDedicatedInstanceCannotReplaceTheOriginalEntryOrDisposeItsGameplayState()
        {
            // 本例唯一改变的是可信Instance对象；同Run字符串不能代替原Receipt/Inventory Owner。
            _combat.Dispose(); _combat = null; Object.DestroyImmediate(_liveOutput); _liveOutput = null;
            _ownerInstance = _oldRoot.AddComponent<RoomInstanceBootstrap>();
            var definition = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
            _combat = new OnlineCombatWorld(_world, definition, new CombatSimulationWorld(), _oldChannel, _oldManager, _ownerInstance);
            Set(_world, "_combat", _combat); Set(_world, "<IsAuthority>k__BackingField", true);
            _ground = new OnlineGroundItemAuthority(definition, _worldRoot.scene); Set(_combat, "_groundItems", _ground);
            var replacement = _newRoot.AddComponent<RoomInstanceBootstrap>();
            Assert.That(Rebind(replacement, out var error), Is.False);
            Assert.That(error, Is.Not.Empty); Assert.That(_combat.Faulted, Is.True);
            Assert.That(Get(_combat, "_instance"), Is.SameAs(_ownerInstance)); Assert.That(Get(_combat, "_manager"), Is.SameAs(_oldManager));
            Assert.That(_ground.Capture(), Is.Not.Null); AssertCallbacks(_oldChannel, 1); AssertCallbacks(_newChannel, 0);
        }

        [Test] public void SameDedicatedInstanceCanRebindWithoutLosingHomeRestorationOrGround()
        {
            _combat.Dispose(); _combat = null; Object.DestroyImmediate(_liveOutput); _liveOutput = null;
            _ownerInstance = _oldRoot.AddComponent<RoomInstanceBootstrap>();
            var definition = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
            _combat = new OnlineCombatWorld(_world, definition, new CombatSimulationWorld(), _oldChannel, _oldManager, _ownerInstance);
            Set(_world, "_combat", _combat); Set(_world, "<IsAuthority>k__BackingField", true);
            _ground = new OnlineGroundItemAuthority(definition, _worldRoot.scene); Set(_combat, "_groundItems", _ground);
            ((bool[])Get(_combat, "_homeEntryRestored"))[0] = true;
            Assert.That(Rebind(_ownerInstance, out var error), Is.True, error);
            Assert.That(Get(_combat, "_instance"), Is.SameAs(_ownerInstance)); Assert.That(_ground.Capture(), Is.Not.Null);
            Assert.That(((bool[])Get(_combat, "_homeEntryRestored"))[0], Is.True); Assert.That(_combat.Faulted, Is.False);
        }

        private bool Rebind(RoomInstanceBootstrap instance, out string error)
        {
            var method = typeof(OnlineCombatWorld).GetMethod("RebindNetworking"); Assert.That(method, Is.Not.Null,
                "网络组合根更新必须有独立Rebind，不能通过Dispose重建已经运行的Gameplay Owner。");
            var args = new object[] { _newManager, _newChannel, instance, null };
            bool accepted = (bool)method.Invoke(_combat, args); error = args[3] as string; return accepted;
        }
        private static void AssertCallbacks(CombatNetworkChannel channel, int expected)
        {
            foreach (string name in new[] { "IntentReceived", "FactReceived" })
            {
                var callback = Get(channel, name) as Delegate; int count = 0;
                if (callback != null) foreach (var handler in callback.GetInvocationList()) if (handler.Target is OnlineCombatWorld) count++;
                Assert.That(count, Is.EqualTo(expected), name);
            }
        }
        private static object Get(object value, string name) => value.GetType().GetField(name, Private).GetValue(value);
        private static void Set(object value, string name, object content) => value.GetType().GetField(name, Private).SetValue(value, content);
        private static void SetSingleton(NetworkManager manager)
            => typeof(NetworkManager).GetProperty("singleton", BindingFlags.Static | BindingFlags.Public).GetSetMethod(true).Invoke(null, new object[] { manager });

        [TearDown] public void ReleaseOnlyThisFixturesOwners()
        {
            SetSingleton(_previousSingleton);
            if (_world != null) Set(_world, "_stopped", true);
            if (_worldRoot != null) Object.DestroyImmediate(_worldRoot);
            _combat?.Dispose(); _combat = null;
            if (_newRoot != null) Object.DestroyImmediate(_newRoot); if (_oldRoot != null) Object.DestroyImmediate(_oldRoot);
        }
    }
}
