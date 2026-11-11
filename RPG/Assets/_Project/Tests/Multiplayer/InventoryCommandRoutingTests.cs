using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game.Core;
using Game.Networking;
using Mirror;
using NUnit.Framework;
using UnityEngine;

namespace Game.Multiplayer.Tests
{
    /// <summary>测试真实消息边界和本机收据关联；Caller不是网络身份，旧代次不能消费新请求。</summary>
    public sealed class InventoryCommandRoutingTests
    {
        private Type Require(string name)
        {
            var type = typeof(OnlineWorldBootstrap).Assembly.GetType("Game.Multiplayer." + name);
            Assert.That(type, Is.Not.Null, "缺少有界库存命令路由：" + name); return type;
        }

        [Test] public void CommandsAndReceiptsHaveDistinctMirrorIds()
        {
            var ids = new Dictionary<ushort, Type>();
            foreach (var type in typeof(NetworkManager).Assembly.GetTypes())
                if (type.IsValueType && !type.ContainsGenericParameters && typeof(NetworkMessage).IsAssignableFrom(type))
                    ids[type.FullName.GetStableHashCode16()] = type;
            foreach (var type in new[] { typeof(JoinTicketRequestEnvelope), typeof(JoinTicketReplyEnvelope), typeof(RoomProtocolEnvelope),
                typeof(CombatFactEnvelope), typeof(CombatIntentEnvelope), typeof(MovementInputEnvelope), typeof(MovementStateEnvelope),
                Require("InventorySnapshotEnvelope"), Require("InventoryCommandEnvelope"), Require("InventoryReceiptEnvelope") })
            {
                ushort id = type.FullName.GetStableHashCode16();
                Assert.That(ids.TryGetValue(id, out var previous), Is.False, type.FullName + " collides with " + previous?.FullName);
                ids.Add(id, type);
            }
        }

        [Test]
        public void SolidReleaseUsesTheExistingBoundedDiscreteBudgetWithoutTakingCollectOrPourTokens()
        {
            var budget = new InventoryCommandOperationBudget();
            for (int i = 0; i < 16; i++) Assert.That(budget.TryTake(1, 1, OnlineInventoryCommandKind.ReleaseSolid, 0), Is.True);
            Assert.That(budget.TryTake(1, 1, OnlineInventoryCommandKind.ReleaseSolid, 0), Is.False);
            Assert.That(budget.TryTake(1, 1, OnlineInventoryCommandKind.ThrowContainer, 0), Is.False);
            Assert.That(budget.TryTake(1, 1, OnlineInventoryCommandKind.CollectMaterial, 0), Is.True);
            Assert.That(budget.TryTake(1, 1, OnlineInventoryCommandKind.PourContainer, 0), Is.True);
        }

        [TestCase(OnlineInventoryCommandKind.MoveOwned)]
        [TestCase(OnlineInventoryCommandKind.EquipWand)]
        [TestCase(OnlineInventoryCommandKind.DropItem)]
        [TestCase(OnlineInventoryCommandKind.PickupItem)]
        public void GeneralInventoryCommandsUseTheExistingBoundedDiscreteBucket(OnlineInventoryCommandKind kind)
        {
            var budget = new InventoryCommandOperationBudget();
            for (int i = 0; i < 16; i++) Assert.That(budget.TryTake(1, 1, kind, 100), Is.True);
            Assert.That(budget.TryTake(1, 1, kind, 100), Is.False);
            Assert.That(budget.TryTake(1, 1, OnlineInventoryCommandKind.PlaceCard, 100), Is.False);
            Assert.That(budget.TryTake(1, 1, OnlineInventoryCommandKind.CollectMaterial, 100), Is.True);
            Assert.That(budget.TryTake(1, 1, kind, 100.125), Is.True);
            Assert.That(budget.TryTake(2, 1, kind, 100.25), Is.False);
        }

        [TestCase("Command")][TestCase("Receipt")]
        public void CommandEnvelopeRejectsOversizeOrTruncatedBytes(string kind)
        {
            var read = Require("InventoryCommandEnvelopeSerialization").GetMethod("ReadInventory" + kind + "Envelope");
            Assert.That(read, Is.Not.Null);
            using var writer = NetworkWriterPool.Get(); writer.WriteUInt(513);
            var invalid = Assert.Throws<TargetInvocationException>(() => read.Invoke(null, new object[] { new NetworkReader(writer.ToArraySegment()) }));
            Assert.That(invalid.InnerException, Is.TypeOf<InvalidDataException>());
            writer.Reset(); writer.WriteUInt(2); writer.WriteByte(1);
            invalid = Assert.Throws<TargetInvocationException>(() => read.Invoke(null, new object[] { new NetworkReader(writer.ToArraySegment()) }));
            Assert.That(invalid.InnerException, Is.TypeOf<InvalidDataException>());
        }

        [Test] public void PendingReceiptKeepsCallerAndConsumesOnlyOnce()
        {
            var type = Require("InventoryCommandPendingInbox"); var inbox = Activator.CreateInstance(type);
            Assert.That(type.GetMethod("TryAdd").Invoke(inbox, new object[] { 5ul, 2u, 17 }), Is.True);
            var args = new object[] { 5ul, 2u, 0 };
            Assert.That(type.GetMethod("TryTake").Invoke(inbox, args), Is.True); Assert.That(args[2], Is.EqualTo(17));
            Assert.That(type.GetMethod("TryTake").Invoke(inbox, args), Is.False);
        }

        [Test] public void WrongEpochAndDuplicateIdCannotHijackCaller()
        {
            var type = Require("InventoryCommandPendingInbox"); var inbox = Activator.CreateInstance(type);
            Assert.That(type.GetMethod("TryAdd").Invoke(inbox, new object[] { 5ul, 2u, 17 }), Is.True);
            Assert.That(type.GetMethod("TryAdd").Invoke(inbox, new object[] { 5ul, 2u, 42 }), Is.False);
            Assert.That(type.GetMethod("TryTake").Invoke(inbox, new object[] { 5ul, 1u, 0 }), Is.False);
            var args = new object[] { 5ul, 2u, 0 }; Assert.That(type.GetMethod("TryTake").Invoke(inbox, args), Is.True);
            Assert.That(args[2], Is.EqualTo(17));
        }

        [Test] public void PendingBudgetDoesNotEvictUnacknowledgedCommandsAndClearReleasesIt()
        {
            var type = Require("InventoryCommandPendingInbox"); var inbox = Activator.CreateInstance(type);
            var add = type.GetMethod("TryAdd");
            for (ulong i = 1; i <= 128; i++) Assert.That(add.Invoke(inbox, new object[] { i, 2u, (int)i }), Is.True);
            Assert.That(add.Invoke(inbox, new object[] { 129ul, 2u, 129 }), Is.False);
            Assert.That(type.GetMethod("TryTake").Invoke(inbox, new object[] { 1ul, 2u, 0 }), Is.True);
            Assert.That(add.Invoke(inbox, new object[] { 129ul, 2u, 129 }), Is.True);
            type.GetMethod("Clear").Invoke(inbox, null);
            Assert.That(type.GetMethod("TryTake").Invoke(inbox, new object[] { 129ul, 2u, 0 }), Is.False);
            Assert.That(add.Invoke(inbox, new object[] { 130ul, 3u, 130 }), Is.True);
        }

        [Test] public void IngressBudgetRejectsFloodBeforeAllocationAndRefillsOnlyToBurstLimit()
        {
            var type = Require("InventoryCommandRateBudget"); var budget = Activator.CreateInstance(type, new object[] { 64d, 32d });
            var take = type.GetMethod("TryTake");
            for (int i = 0; i < 64; i++) Assert.That(take.Invoke(budget, new object[] { 10, 2u, 100d }), Is.True);
            for (int i = 0; i < 1000; i++) Assert.That(take.Invoke(budget, new object[] { 10, 2u, 100d }), Is.False);
            Assert.That(take.Invoke(budget, new object[] { 10, 2u, 100d + 1d / 32 }), Is.True);
            Assert.That(take.Invoke(budget, new object[] { 10, 2u, 100d + 1d / 32 }), Is.False);
            for (int i = 0; i < 64; i++) Assert.That(take.Invoke(budget, new object[] { 10, 2u, 10000d }), Is.True);
            Assert.That(take.Invoke(budget, new object[] { 10, 2u, 10000d }), Is.False);
        }

        [Test] public void IngressBudgetDoesNotReattachOldEpochOrAcceptInvalidTime()
        {
            var type = Require("InventoryCommandRateBudget"); var budget = Activator.CreateInstance(type, new object[] { 1d, 1d });
            var take = type.GetMethod("TryTake");
            Assert.That(take.Invoke(budget, new object[] { 10, 2u, 100d }), Is.True);
            Assert.That(take.Invoke(budget, new object[] { 10, 1u, 101d }), Is.False);
            Assert.That(take.Invoke(budget, new object[] { 10, 2u, double.NaN }), Is.False);
            Assert.That(take.Invoke(budget, new object[] { 10, 2u, 99d }), Is.False);
            Assert.That(take.Invoke(budget, new object[] { 11, 3u, 101d }), Is.True);
        }

        // 这里先声明尚未实现的策略API，使RED表现为缺少能力的断言，而不是Unity编译错误。
        // 真正的Channel测试在下方走Codec/认证连接/OnCommand，不用测试替身冒充生产接线。
        private object NewOperationBudget() => Activator.CreateInstance(Require("InventoryCommandOperationBudget"));
        private static bool TakeOperation(object budget, OnlineInventoryCommandKind kind, double now,
            int connection = 10, uint epoch = 2)
        {
            var take = budget.GetType().GetMethod("TryTake", new[]
                { typeof(int), typeof(uint), typeof(OnlineInventoryCommandKind), typeof(double) });
            Assert.That(take, Is.Not.Null, "按已解码命令种类选择预算，不能复用原离散8/s桶。");
            return (bool)take.Invoke(budget, new object[] { connection, epoch, kind, now });
        }

        [TestCase(OnlineInventoryCommandKind.BindTool)]
        [TestCase(OnlineInventoryCommandKind.DropContainer)]
        [TestCase(OnlineInventoryCommandKind.PickupContainer)]
        public void ContainerDiscreteActionsUseTheOriginalSharedSixteenBurstEightPerSecondBudget(OnlineInventoryCommandKind kind)
        {
            var budget = NewOperationBudget();
            for (int i = 0; i < 16; i++) Assert.That(TakeOperation(budget, kind, 100d), Is.True);
            Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.SelectWand, 100d), Is.False,
                "绑定/丢弃/拾取共用原离散额度，不能用新kind绕限频。");
            Assert.That(TakeOperation(budget, kind, 100.125d), Is.True);
            Assert.That(TakeOperation(budget, kind, 100.125d), Is.False);
            Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.CollectMaterial, 100.125d), Is.True);
            Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.PourContainer, 100.125d), Is.True);
        }

        [TestCase(OnlineInventoryCommandKind.BindTool)]
        [TestCase(OnlineInventoryCommandKind.DropContainer)]
        [TestCase(OnlineInventoryCommandKind.PickupContainer)]
        public void ActualChannelForwardsLegitimateContainerDiscreteIntentsWithoutAnAlwaysRejectedKind(OnlineInventoryCommandKind kind)
        {
            using var fixture = new CommandChannelFixture();
            fixture.Send(kind, 1);
            Assert.That(fixture.Forwarded.Count, Is.EqualTo(1));
            Assert.That(fixture.Forwarded[0].Failure, Is.EqualTo(OnlineInventoryCommandFailure.None));
            Assert.That(fixture.Forwarded[0].Intent.Kind, Is.EqualTo(kind));
        }

        [Test] public void CollectAndPourEachSustainTenHzWithoutConsumingTheOtherOperationBudget()
        {
            var budget = NewOperationBudget();
            // 50秒长于旧16突发能掩盖8/s与10Hz差额的窗口；两操作同时合法，不能共用一个10/s桶。
            for (int sample = 0; sample < 500; sample++)
            {
                double now = 100d + sample / 10d;
                Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.CollectMaterial, now), Is.True, "F sample " + sample);
                Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.PourContainer, now), Is.True, "H sample " + sample);
            }
        }

        [Test] public void DiscreteCommandsKeepTheirSharedEightPerSecondBucketAndMaterialBucketsStayIndependent()
        {
            var budget = NewOperationBudget();
            for (int i = 0; i < 16; i++)
                Assert.That(TakeOperation(budget, (OnlineInventoryCommandKind)(1 + i % 3), 100d), Is.True);
            Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.SelectTool, 100d), Is.False);
            foreach (var kind in new[] { OnlineInventoryCommandKind.CollectMaterial, OnlineInventoryCommandKind.PourContainer })
            {
                for (int i = 0; i < 16; i++) Assert.That(TakeOperation(budget, kind, 100d), Is.True);
                Assert.That(TakeOperation(budget, kind, 100d), Is.False);
            }
            // 半秒分别补4与5个token，使用可精确表达的时间，避免浮点边界把规格测试写成偶发失败。
            for (int i = 0; i < 4; i++) Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.PlaceCard, 100.5d), Is.True);
            Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.SelectWand, 100.5d), Is.False);
            foreach (var kind in new[] { OnlineInventoryCommandKind.CollectMaterial, OnlineInventoryCommandKind.PourContainer })
            {
                for (int i = 0; i < 5; i++) Assert.That(TakeOperation(budget, kind, 100.5d), Is.True);
                Assert.That(TakeOperation(budget, kind, 100.5d), Is.False);
            }
        }

        [TestCase(OnlineInventoryCommandKind.CollectMaterial)]
        [TestCase(OnlineInventoryCommandKind.PourContainer)]
        public void MaterialFloodIsStillLimitedAndIdleTimeCannotGrowTheSixteenCommandBurst(OnlineInventoryCommandKind kind)
        {
            var budget = NewOperationBudget();
            for (int i = 0; i < 16; i++) Assert.That(TakeOperation(budget, kind, 100d), Is.True);
            for (int i = 0; i < 1000; i++) Assert.That(TakeOperation(budget, kind, 100d), Is.False);
            for (int i = 0; i < 16; i++) Assert.That(TakeOperation(budget, kind, 10000d), Is.True);
            Assert.That(TakeOperation(budget, kind, 10000d), Is.False);
        }

        [Test] public void NewEpochOnOneOperationFencesOldIdentityAcrossEveryOtherOperation()
        {
            var budget = NewOperationBudget();
            Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.SelectWand, 100d), Is.True);
            Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.CollectMaterial, 101d, 11, 3), Is.True);
            // Pour尚未使用也必须知道整个Actor已经重连，不能让独立桶分别接受两代连接。
            Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.PourContainer, 102d, 10, 2), Is.False);
            Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.SelectWand, 102d, 10, 2), Is.False);
            Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.PourContainer, 102d, 10, 3), Is.False);
            Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.PourContainer, 101d, 11, 3), Is.True);
        }

        [Test] public void InvalidOrBackwardTimeCannotAttachIdentityOrRefillAnUntouchedOperation()
        {
            var budget = NewOperationBudget();
            Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.PourContainer, double.NaN, 99, 99), Is.False);
            Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.PourContainer, double.PositiveInfinity, 99, 99), Is.False);
            Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.PourContainer, -1d), Is.False);
            Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.PourContainer, 100d, 10, 0), Is.False);
            Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.CollectMaterial, 100d), Is.True);
            Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.SelectTool, 99d), Is.False);
            Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.PourContainer, 100d), Is.True);
        }

        [Test] public void ClearReleasesAllOperationBudgetsAndTheirIdentityFence()
        {
            var budget = NewOperationBudget();
            foreach (var kind in new[] { OnlineInventoryCommandKind.SelectWand, OnlineInventoryCommandKind.CollectMaterial, OnlineInventoryCommandKind.PourContainer })
                for (int i = 0; i < 16; i++) Assert.That(TakeOperation(budget, kind, 100d), Is.True);
            var clear = budget.GetType().GetMethod("Clear"); Assert.That(clear, Is.Not.Null); clear.Invoke(budget, null);
            foreach (var kind in new[] { OnlineInventoryCommandKind.SelectTool, OnlineInventoryCommandKind.CollectMaterial, OnlineInventoryCommandKind.PourContainer })
                for (int i = 0; i < 16; i++) Assert.That(TakeOperation(budget, kind, 1d, 20, 1), Is.True);
        }

        [Test] public void ActualChannelUsesDecodedKindSoDiscreteFloodDoesNotThrottleCollectOrPour()
        {
            using var fixture = new CommandChannelFixture();
            for (ulong id = 1; id <= 16; id++) fixture.Send(OnlineInventoryCommandKind.SelectWand, id);
            fixture.Send(OnlineInventoryCommandKind.CollectMaterial, 17);
            fixture.Send(OnlineInventoryCommandKind.PourContainer, 18);
            Assert.That(fixture.Forwarded.Count, Is.EqualTo(18));
            Assert.That(fixture.Forwarded[16].Failure, Is.EqualTo(OnlineInventoryCommandFailure.None), "真实OnCommand必须按F种类取独立额度。");
            Assert.That(fixture.Forwarded[17].Failure, Is.EqualTo(OnlineInventoryCommandFailure.None), "真实OnCommand必须按H种类取独立额度。");
        }

        [Test] public void ActualChannelKeepsActorBudgetsSeparateAndForwardsSoftRejectionsAndReplaysToAuthorityLedger()
        {
            using var fixture = new CommandChannelFixture();
            for (ulong id = 1; id <= 18; id++) fixture.Send(OnlineInventoryCommandKind.CollectMaterial, id);
            fixture.Send(OnlineInventoryCommandKind.CollectMaterial, 17); // 重试原被限流ID，也不能在Channel提前吞掉。
            Assert.That(fixture.Forwarded.Count, Is.EqualTo(19));
            Assert.That(fixture.Forwarded[16].Failure, Is.EqualTo(OnlineInventoryCommandFailure.RateLimited));
            Assert.That(fixture.Forwarded[18].Intent.CommandId, Is.EqualTo(17ul));
            Assert.That(fixture.Forwarded[18].Failure, Is.EqualTo(OnlineInventoryCommandFailure.RateLimited));
            var other = fixture.Attach(2, 20, 2);
            fixture.Send(OnlineInventoryCommandKind.CollectMaterial, 1, other);
            Assert.That(fixture.Forwarded[19].Identity.ActorId, Is.EqualTo(2ul));
            Assert.That(fixture.Forwarded[19].Failure, Is.EqualTo(OnlineInventoryCommandFailure.None));
            // 此处只验证真实网络组合边界；材料不重复提交由既有Run同ID账本回归验证，不在测试里伪造材料Store。
        }

        [Test] public void ActualChannelKeepsThePredecodeSixtyFourPacketIngressBurst()
        {
            using var fixture = new CommandChannelFixture();
            for (ulong id = 1; id <= 65; id++)
                fixture.Send(id % 3 == 0 ? OnlineInventoryCommandKind.SelectTool :
                    id % 3 == 1 ? OnlineInventoryCommandKind.CollectMaterial : OnlineInventoryCommandKind.PourContainer, id);
            Assert.That(fixture.Forwarded.Count, Is.EqualTo(64), "第65包必须在解码/领域回执之前由原ingress预算截断。");
        }

        [Test] public void ActualChannelRejectsOldConnectionBeforeItCanSpendCurrentActorBudgets()
        {
            using var fixture = new CommandChannelFixture();
            var old = fixture.Connection;
            var current = fixture.Attach(1, 11, 3);
            for (ulong id = 1; id <= 64; id++) fixture.Send(OnlineInventoryCommandKind.CollectMaterial, id, old);
            fixture.Send(OnlineInventoryCommandKind.CollectMaterial, 65, current);
            Assert.That(fixture.Forwarded.Count, Is.EqualTo(1));
            Assert.That(fixture.Forwarded[0].Identity.ConnectionEpoch, Is.EqualTo(3u));
            Assert.That(fixture.Forwarded[0].Failure, Is.EqualTo(OnlineInventoryCommandFailure.None));
        }

        [TestCase("ConfigureWorld")][TestCase("DetachWorld")][TestCase("OnDisable")]
        public void ActualChannelLifecycleClearsEveryOperationBucket(string lifecycle)
        {
            using var fixture = new CommandChannelFixture();
            var rates = (Array)typeof(InventoryNetworkChannel).GetField("_rates", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fixture.Channel);
            var actualBudget = rates.GetValue(0);
            foreach (var kind in new[] { OnlineInventoryCommandKind.SelectWand, OnlineInventoryCommandKind.CollectMaterial, OnlineInventoryCommandKind.PourContainer })
                for (int i = 0; i < 16; i++) Assert.That(TakeOperation(actualBudget, kind, 100d), Is.True);
            if (lifecycle == "ConfigureWorld") fixture.Channel.ConfigureWorld(new object(), new string('a', 64), 1, null);
            else if (lifecycle == "DetachWorld") fixture.Channel.DetachWorld(fixture.Owner);
            else typeof(InventoryNetworkChannel).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(fixture.Channel, null);
            foreach (var kind in new[] { OnlineInventoryCommandKind.SelectTool, OnlineInventoryCommandKind.CollectMaterial, OnlineInventoryCommandKind.PourContainer })
                for (int i = 0; i < 16; i++) Assert.That(TakeOperation(actualBudget, kind, 1d, 20, 1), Is.True, lifecycle + " must release " + kind);
        }

        private readonly struct ForwardedCommand
        {
            public readonly JoinConnectionIdentity Identity;
            public readonly OnlineInventoryCommandIntent Intent;
            public readonly OnlineInventoryCommandFailure Failure;
            public ForwardedCommand(JoinConnectionIdentity identity, OnlineInventoryCommandIntent intent, OnlineInventoryCommandFailure failure)
            { Identity = identity; Intent = intent; Failure = failure; }
        }

        /// <summary>
        /// 仅初始化未激活GameObject上的真实Channel与Room身份槽，不启动Mirror/Transport或场景。
        /// 手动调用Channel.Awake保留生产预算的真实构造；OnCommand同样是生产方法，测试只收集它交给Authority的值。
        /// </summary>
        private sealed class CommandChannelFixture : IDisposable
        {
            private readonly GameObject _root;
            private readonly RoomNetworkManager _manager;
            private readonly ConnectionSession[] _sessions;
            private readonly InventoryCommandCodec _codec = new InventoryCommandCodec();
            private readonly byte[] _payload = new byte[InventoryCommandCodec.MaxPayloadBytes];
            private readonly MethodInfo _onCommand;
            public readonly InventoryNetworkChannel Channel;
            public readonly object Owner = new object();
            public readonly List<ForwardedCommand> Forwarded = new List<ForwardedCommand>();
            public readonly NetworkConnectionToClient Connection;
            public CommandChannelFixture()
            {
                Assert.That(NetworkServer.active || NetworkClient.active, Is.False, "测试不接管已有Mirror会话。");
                _root = new GameObject("Inventory command routing fixture"); _root.SetActive(false);
                Channel = _root.AddComponent<InventoryNetworkChannel>();
                _manager = _root.GetComponent<RoomNetworkManager>();
                _sessions = (ConnectionSession[])typeof(RoomNetworkManager).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_manager);
                for (int i = 0; i < _sessions.Length; i++) _sessions[i] = new ConnectionSession();
                typeof(InventoryNetworkChannel).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Channel, null);
                Channel.ConfigureWorld(Owner, new string('a', 64), 1, null);
                _onCommand = typeof(InventoryNetworkChannel).GetMethod("OnCommand", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(_onCommand, Is.Not.Null);
                Connection = Attach(1, 10, 2);
                Channel.CommandReceived += (identity, intent, failure) => Forwarded.Add(new ForwardedCommand(identity, intent, failure));
            }
            public NetworkConnectionToClient Attach(ulong actor, int connection, uint epoch)
            {
                var identity = new JoinConnectionIdentity("player-" + actor, "room", "run", 1, epoch, actor, "p7");
                var result = new NetworkConnectionToClient(connection) { isAuthenticated = true, authenticationData = identity };
                _sessions[(int)actor - 1].Attach(connection); return result;
            }
            public void Send(OnlineInventoryCommandKind kind, ulong command, NetworkConnectionToClient connection = null)
            {
                connection ??= Connection;
                var identity = (JoinConnectionIdentity)connection.authenticationData;
                bool material = kind == OnlineInventoryCommandKind.CollectMaterial || kind == OnlineInventoryCommandKind.PourContainer;
                var intent = new OnlineInventoryCommandIntent
                {
                    CommandId = command, ConnectionEpoch = identity.ConnectionEpoch, ExpectedInventoryVersion = 1,
                    Kind = kind, ItemId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", TargetSlot = 0,
                    WorldOwnerId = kind == OnlineInventoryCommandKind.PickupContainer ? "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" : null,
                    AutoPlace = kind == OnlineInventoryCommandKind.PickupContainer,
                    TransferBudget = material ? 1ul : 0ul, AimDirection = material ? Vector3.forward : Vector3.zero
                };
                Assert.That(_codec.TryEncodeIntent(intent, 1, new ArraySegment<byte>(_payload), out int count), Is.True);
                _onCommand.Invoke(Channel, new object[] { connection,
                    new InventoryCommandEnvelope { Payload = new ArraySegment<byte>(_payload, 0, count) } });
            }
            public void Dispose() => UnityEngine.Object.DestroyImmediate(_root);
        }

        // 工具选择只推进SelectionVersion，不改InventoryVersion/最后Wand；正式发送不能漏掉此变化。
        [TestCase(2u, 3u, 4u, 2u, 3u, 4u, false)]
        [TestCase(2u, 3u, 5u, 2u, 3u, 4u, true)]
        [TestCase(2u, 4u, 4u, 2u, 3u, 4u, true)]
        [TestCase(3u, 3u, 4u, 2u, 3u, 4u, true)]
        public void PureSelectionChangeUsesTheNormalOwnerSnapshotPumpInsteadOfReceiptForcedResend(
            uint epoch, uint inventory, uint selection, uint sentEpoch, uint sentInventory, uint sentSelection, bool expected)
        {
            var method = Require("InventoryCommandDeliveryPolicy").GetMethod("ShouldSendOwnerSnapshot");
            Assert.That(method, Is.Not.Null);
            Assert.That(method.Invoke(null, new object[] { epoch, inventory, selection, sentEpoch, sentInventory, sentSelection }), Is.EqualTo(expected));
        }

        [TestCase(0, 0, false)][TestCase(0, 4, true)][TestCase(11, 0, false)][TestCase(11, 4, false)]
        public void ReplayedReceiptCannotForceSnapshotOutsideCurrentDeliveryBudget(int budgetFailure, int receiptFailure, bool expected)
        {
            var type = Require("InventoryCommandDeliveryPolicy"); var method = type.GetMethod("ShouldResynchronize");
            var failure = typeof(GameLog).Assembly.GetType("Game.Core.OnlineInventoryCommandFailure"); Assert.That(failure, Is.Not.Null);
            Assert.That(method.Invoke(null, new[] { Enum.ToObject(failure, budgetFailure), Enum.ToObject(failure, receiptFailure) }), Is.EqualTo(expected));
        }
        [Test]
        public void SpellAttachmentSettingUsesTheExistingDiscreteBudgetAndDoesNotTakeCollectTokens()
        {
            var budget = Activator.CreateInstance(Require("InventoryCommandOperationBudget"));
            for (int i = 0; i < 16; i++) Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.ConfigureSpellAttachment, 100d), Is.True);
            Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.ConfigureSpellAttachment, 100d), Is.False);
            Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.CollectMaterial, 100d), Is.True);
            Assert.That(TakeOperation(budget, OnlineInventoryCommandKind.ConfigureSpellAttachment, 100.125d), Is.True);
        }
    }
}
