using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game.Core;
using Game.Networking;
using Mirror;
using NUnit.Framework;

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

        [TestCase(0, 0, false)][TestCase(0, 4, true)][TestCase(11, 0, false)][TestCase(11, 4, false)]
        public void ReplayedReceiptCannotForceSnapshotOutsideCurrentDeliveryBudget(int budgetFailure, int receiptFailure, bool expected)
        {
            var type = Require("InventoryCommandDeliveryPolicy"); var method = type.GetMethod("ShouldResynchronize");
            var failure = typeof(GameLog).Assembly.GetType("Game.Core.OnlineInventoryCommandFailure"); Assert.That(failure, Is.Not.Null);
            Assert.That(method.Invoke(null, new[] { Enum.ToObject(failure, budgetFailure), Enum.ToObject(failure, receiptFailure) }), Is.EqualTo(expected));
        }
    }
}
