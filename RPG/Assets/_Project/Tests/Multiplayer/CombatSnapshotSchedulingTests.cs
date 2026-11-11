using System;
using System.Collections.Generic;
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
    /// 驱动真实 Combat.Pump 并观察 NewFact 的序号推进，不另写一份发送调度逻辑。
    /// 基础调度没有接收连接；重装备专例使用内存连接观察Mirror入批，没有端口或Transport发送。
    /// 网络生命周期仍由主控串行执行，所有临时身份/Transport只在本专例内拥有。
    /// </summary>
    public sealed class CombatSnapshotSchedulingTests
    {
        private GameObject _worldRoot, _networkRoot;
        private OnlineWorldBootstrap _world;
        private OnlineCombatWorld _combat;
        private bool _previousServerActive;
        private static readonly PropertyInfo ServerActive = typeof(NetworkServer).GetProperty("active", BindingFlags.Static | BindingFlags.Public);
        private static readonly FieldInfo FactSequence = typeof(OnlineCombatWorld).GetField("_sequence", BindingFlags.Instance | BindingFlags.NonPublic);

        [SetUp]
        public void SetUp()
        {
            _previousServerActive = NetworkServer.active;
            Assert.That(_previousServerActive, Is.False, "隔离调度回归不能抢占已经运行的 Server。");
            try
            {
                var profile = Resources.Load<OnlineMovementProfile>("Online/MovementProfile");
                var definition = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
                Assert.That(profile, Is.Not.Null); Assert.That(definition, Is.Not.Null);
                Assert.That(FactSequence, Is.Not.Null);
                _worldRoot = new GameObject("N4 combat snapshot scheduling fixture"); _worldRoot.SetActive(false);
                _networkRoot = new GameObject("N4 inactive combat snapshot channel"); _networkRoot.SetActive(false);
                _world = _worldRoot.AddComponent<OnlineWorldBootstrap>();
                var previousManager = NetworkManager.singleton;
                var manager = _networkRoot.AddComponent<RoomNetworkManager>();
                var channel = _networkRoot.AddComponent<CombatNetworkChannel>();
                Assert.That(NetworkManager.singleton, Is.SameAs(previousManager));
                // inactive Owner 不运行 Manager.Awake；只补齐其中的空 Session 状态，不能调用 base.Awake 抢单例。
                // ActorConnection 会读取这些席位来判断是否存在接收者；所有 ConnectionId 保持 -1，不建立连接。
                var sessions = (ConnectionSession[])typeof(RoomNetworkManager)
                    .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
                for (int i = 0; i < sessions.Length; i++) sessions[i] = new ConnectionSession(10, 4);
                var simulation = new CombatSimulationWorld();
                var registry = new NetworkActorRegistry(profile, _worldRoot.transform, definition, simulation);
                // 恰好一个有 Health 的生产 Actor，无 Cast/连接/输出：每次 State 批次恰好生成一份真实 Kind7。
                // 因此 NewFact 序号增长就是批次数，不会把 OwnerSetup、出生或每个 Actor 的包数误算成 Hz。
                registry.EnsurePlayer(0, PlayerExecutionMode.Authority);
                Set(_world, "<Registry>k__BackingField", registry);
                Set(_world, "<IsAuthority>k__BackingField", true);
                Set(_world, "<WorldReady>k__BackingField", true);
                Set(_world, "_manager", manager);
                _combat = new OnlineCombatWorld(_world, definition, simulation, channel, manager, null);
                Set(_world, "_combat", _combat);
                manager.SetAuthorityReady(true);
                ServerActive.GetSetMethod(true).Invoke(null, new object[] { true });
                Assert.That(_world.CanAdvance, Is.True, "Pump 必须真正进入权威生成路径，不能把早退误当调度结果。");
            }
            catch
            {
                Cleanup();
                throw;
            }
        }

        [TearDown]
        public void Cleanup()
        {
            // 即使断言或 Pump 抛异常，也先恢复 Mirror 静态门，避免后面的测试误认为有 Server 正在运行。
            ServerActive.GetSetMethod(true).Invoke(null, new object[] { _previousServerActive });
            if (_world != null) Set(_world, "_stopped", true);
            if (_worldRoot != null) Object.DestroyImmediate(_worldRoot);
            _combat?.Dispose(); _combat = null;
            if (_networkRoot != null) Object.DestroyImmediate(_networkRoot);
            _world = null; _worldRoot = _networkRoot = null;
        }

        [TestCase(50)]
        [TestCase(60)]
        [TestCase(144)]
        public void RealPump_StableRenderFramesPreserveFifteenHz(int renderFps)
        {
            int batches = 0;
            // 覆盖 [0,20) 秒。首帧可立即生成；允许一个边界样本，不能把 50FPS 下 12.5Hz 当作 15Hz。
            for (int frame = 0; frame < renderFps * 20; frame++) batches += PumpAndCount(frame / (double)renderFps);
            Assert.That(batches, Is.EqualTo(300).Within(1),
                "真实 Combat.Pump 应保留晚帧余数，使资源/HP/弹体状态长期保持声明的 15Hz。");
        }

        [Test]
        public void RealPump_HitchKeepsDeadlinePhaseAndNeverGeneratesCatchupBurst()
        {
            Assert.That(PumpAndCount(0), Is.EqualTo(1));
            // 跳过许多周期只生成最新状态；同一时刻重复 Pump 也没有第二份资格。
            Assert.That(PumpAndCount(1.03), Is.EqualTo(1));
            Assert.That(PumpAndCount(1.03), Is.Zero);
            Assert.That(PumpAndCount(1.066666666666667), Is.EqualTo(1),
                "恢复后仍沿原 15Hz deadline，不应从 1.03 秒重新等待完整周期。");
            Assert.That(PumpAndCount(1.066666666666667), Is.Zero);
        }

        [Test]
        public void RealPump_ReequippingTheSameWandAfterEmptyHandsResendsSetupSoundWithoutReplayingOutputBirths()
        {
            const int connectionId = 65123;
            const string wandId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", cardId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            const string bagId = "cccccccccccccccccccccccccccccccc";
            Assert.That(NetworkServer.connections.ContainsKey(connectionId), Is.False);
            var previousTransport = Transport.active;
            var manager = _networkRoot.GetComponent<RoomNetworkManager>();
            var channel = _networkRoot.GetComponent<CombatNetworkChannel>();
            var movement = _networkRoot.GetComponent<MovementNetworkChannel>();
            var inventory = _networkRoot.GetComponent<InventoryNetworkChannel>();
            var definition = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
            var slot = _world.Registry[0];
            // 本专例进入真实库存Codec，玩家/Run须使用正式32位小写hex身份，不能沿用纯领域测试短标签。
            var identity = new JoinConnectionIdentity(new string('d', 32), new string('e', 32), new string('f', 32), 1, 1, 1, "p7");
            var connection = new NetworkConnectionToClient(connectionId) { isAuthenticated = true, authenticationData = identity };
            var facts = new List<CapturedFact>(); var codec = new CombatCodec();
            Action<NetworkDiagnostics.MessageInfo> capture = info =>
            {
                if (!(info.message is CombatFactEnvelope envelope)) return;
                // SendFact复用payload缓冲；在真实Mirror发送通知中立即解码并复制必要值，不能保存借用数组。
                Assert.That(codec.TryDecodeFact(envelope.Payload, out var fact), Is.True);
                facts.Add(new CapturedFact { Kind = fact.Kind, Wand = fact.WandInstanceId,
                    Revision = fact.ProgramRevision, Sound = fact.SoundKey, Epoch = fact.ConnectionEpoch });
            };
            NetworkDiagnostics.OutMessageEvent += capture;
            try
            {
                // 只为Mirror入批提供包长；不启动Transport、不监听端口，也不执行Connection.Update发出数据。
                Transport.active = _networkRoot.AddComponent<BatchOnlyTransport>();
                NetworkServer.connections.Add(connectionId, connection);
                var sessions = (ConnectionSession[])typeof(RoomNetworkManager).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
                sessions[0].Attach(connectionId);
                ((uint[])typeof(MovementNetworkChannel).GetField("_readyEpochs", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(movement))[0] = 1;
                typeof(CombatNetworkChannel).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(channel, null);
                typeof(InventoryNetworkChannel).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(inventory, null);
                Set(_world, "<ServerTick>k__BackingField", 1u);
                var wizard = (WizardController)slot.Player;
                if (wizard.SpellCaster == null)
                    typeof(WizardController).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(wizard, null);
                if (slot.Health.CurrentHp <= 0)
                    typeof(HealthComponent).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(slot.Health, null);
                slot.Cast = wizard.gameObject.AddComponent<OnlineCastDriver>();
                var assets = new OnlineOwnedAssetSnapshot
                {
                    SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, ActorId = 1, PlayerId = identity.PlayerId,
                    RunId = identity.RunId, InventoryId = identity.RunId + "/" + identity.PlayerId,
                    InventoryVersion = 1, SelectedWandInstanceId = wandId, GameplayConfigHash = definition.GameplayConfigHash,
                    Items = new[]
                    {
                        new OnlineOwnedItemSnapshot { InstanceId = wandId, ContentId = "wandtest.wand.a", Revision = 1, LocationKind = 3, Slot = 0, UseTemplateWandStats = true },
                        new OnlineOwnedItemSnapshot { InstanceId = cardId, ContentId = "wandtest.fire", Revision = 1, LocationKind = 2, ParentInstanceId = wandId, Slot = 0, RemainingCharges = 3 },
                        new OnlineOwnedItemSnapshot { InstanceId = bagId, ContentId = "wandtest.bag", Revision = 1, LocationKind = 4 }
                    }
                };
                var simulation = (CombatSimulationWorld)typeof(OnlineCombatWorld).GetField("_simulation", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_combat);
                Assert.That(slot.Cast.InitializeAuthority(assets, definition, simulation, out var error), Is.True, error);
                slot.Cast.SetConnection(1, true); slot.Epoch = 1; slot.SimulationReady = true;
                Assert.That(slot.Cast.TryCaptureInventory(1, 1, 1, out var ownerSnapshot), Is.True,
                    "先证明真实Owner闭包可导出，不能让Pump库存发布故障抢先覆盖Setup缓存的目标RED。");
                Assert.That(InventorySnapshotInbox.Matches(identity, ownerSnapshot, definition.GameplayConfigHash), Is.True);
                Assert.That(new InventorySnapshotCodec().TryEncode(ownerSnapshot, out _), Is.True,
                    "领域初始化成功仍须满足真实Wire身份与实例闭包，不能预填发送缓存绕过发布。");
                Assert.That(slot.Cast.TryGetAuthorityResources(out var original), Is.True);
                Assert.That(original.PredictionSoundKey, Is.Not.Zero, "真实单Emit火球程序必须提供可观察的预测音效来源。");

                // 保留一个真实生产Prefab输出，才能发现误清bootstrap Epoch造成的重连式Birth重播。
                var prefab = definition.OutputPrefab(1); Assert.That(prefab, Is.Not.Null);
                var output = Object.Instantiate(prefab, _worldRoot.transform);
                EventBus<CombatOutputSpawnedEvent>.Publish(new CombatOutputSpawnedEvent
                { ActorId = 1, CommandId = 91, Output = output, DefinitionPrefab = prefab });
                _combat.Pump(0);
                Assert.That(_combat.Faulted, Is.False);
                Assert.That(facts.FindAll(value => value.Kind == 1).Count, Is.EqualTo(1));
                int originalBirths = facts.FindAll(value => value.Kind == 4).Count;
                Assert.That(originalBirths, Is.GreaterThan(0));

                var move = new OnlineInventoryCommandIntent
                {
                    CommandId = 101, ConnectionEpoch = 1, ExpectedInventoryVersion = slot.Cast.OwnedInventoryVersion,
                    Kind = OnlineInventoryCommandKind.MoveOwned, ItemId = wandId, TargetLocationKind = 1,
                    TargetBackpackId = bagId, TargetSlot = 0
                };
                var moved = slot.Cast.ExecuteInventoryCommand(move); Assert.That(moved.Success, Is.True, moved.Failure.ToString());
                Assert.That(slot.Cast.TryGetAuthorityResources(out _), Is.False);
                _combat.Pump(.01);
                Assert.That(_combat.Faulted, Is.False);
                var equip = new OnlineInventoryCommandIntent
                {
                    CommandId = 102, ConnectionEpoch = 1, ExpectedInventoryVersion = slot.Cast.OwnedInventoryVersion,
                    Kind = OnlineInventoryCommandKind.EquipWand, ItemId = wandId, TargetLocationKind = 3, TargetSlot = 0
                };
                var equipped = slot.Cast.ExecuteInventoryCommand(equip); Assert.That(equipped.Success, Is.True, equipped.Failure.ToString());
                Assert.That(slot.Cast.TryGetAuthorityResources(out var restored), Is.True);
                Assert.That(restored.ProgramRevision, Is.EqualTo(original.ProgramRevision), "仅移动真实杖树，不编辑程序来制造重新发送。");
                _combat.Pump(.02);
                Assert.That(_combat.Faulted, Is.False);
                var setups = facts.FindAll(value => value.Kind == 1);
                Assert.That(setups.Count, Is.EqualTo(2), "同Epoch空手后重新装备同杖/同程序也必须重发Setup音效元数据。");
                Assert.That(setups[1].Wand, Is.EqualTo(wandId)); Assert.That(setups[1].Revision, Is.EqualTo(original.ProgramRevision));
                Assert.That(setups[1].Sound, Is.EqualTo(original.PredictionSoundKey)); Assert.That(setups[1].Epoch, Is.EqualTo(1u));
                Assert.That(facts.FindAll(value => value.Kind == 4).Count, Is.EqualTo(originalBirths), "重新装备不是重连，不能重播现存输出Birth。");
            }
            finally
            {
                NetworkDiagnostics.OutMessageEvent -= capture;
                // inactive组件不会保证发送OnDisable；显式卸载本测试注册的handler，不触碰其他连接。
                typeof(InventoryNetworkChannel).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(inventory, null);
                NetworkServer.connections.Remove(connectionId); Transport.active = previousTransport;
            }
        }

        private struct CapturedFact
        {
            public uint Kind, Revision, Sound, Epoch;
            public string Wand;
        }

        /// <summary>发送边界的固定容量替身；任何实际网络操作都让测试立即失败，只有Mirror入批可用。</summary>
        private sealed class BatchOnlyTransport : Transport
        {
            public override bool Available() => true;
            public override bool ClientConnected() => false;
            public override void ClientConnect(string address) => throw new NotSupportedException();
            public override void ClientSend(ArraySegment<byte> segment, int channelId = Channels.Reliable) => throw new NotSupportedException();
            public override void ClientDisconnect() => throw new NotSupportedException();
            public override Uri ServerUri() => throw new NotSupportedException();
            public override bool ServerActive() => false;
            public override void ServerStart() => throw new NotSupportedException();
            public override void ServerSend(int connectionId, ArraySegment<byte> segment, int channelId = Channels.Reliable) => throw new NotSupportedException();
            public override void ServerDisconnect(int connectionId) => throw new NotSupportedException();
            public override string ServerGetClientAddress(int connectionId) => "fixture";
            public override void ServerStop() => throw new NotSupportedException();
            public override int GetMaxPacketSize(int channelId = Channels.Reliable) => 65536;
            public override int GetBatchThreshold(int channelId = Channels.Reliable) => 1400;
            public override void Shutdown() { }
        }

        private int PumpAndCount(double now)
        {
            uint before = (uint)FactSequence.GetValue(_combat);
            _combat.Pump(now);
            uint delta = (uint)FactSequence.GetValue(_combat) - before;
            Assert.That(delta, Is.LessThanOrEqualTo(1u), "一帧最多生成一批当前状态，不补发并不存在的历史快照。");
            return (int)delta;
        }

        private static void Set(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }
}
