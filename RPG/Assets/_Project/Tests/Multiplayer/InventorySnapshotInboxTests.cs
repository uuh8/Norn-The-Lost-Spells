using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using Game.Core;
using Game.Networking;
using Mirror;
using NUnit.Framework;

namespace Game.Multiplayer.Tests
{
    /// <summary>库存是 owner-only 数据；错误身份不能推进序号，旧连接也不能阻塞新连接的首包。</summary>
    public sealed class InventorySnapshotInboxTests
    {
        private const string Run = "11111111111111111111111111111111", Player = "22222222222222222222222222222222";
        private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private Type _type, _snapshot;
        private object _inbox;

        [SetUp] public void RequireOwnerInbox()
        {
            _type = typeof(OnlineWorldBootstrap).Assembly.GetType("Game.Multiplayer.InventorySnapshotInbox");
            Assert.That(_type, Is.Not.Null, "缺少独立的库存Owner/连接代次/完整快照去重入口。");
            _snapshot = typeof(OnlineOwnedAssetSnapshot).Assembly.GetType("Game.Core.OnlineInventoryPresentationSnapshot");
            Assert.That(_snapshot, Is.Not.Null);
            _inbox = Activator.CreateInstance(_type, new object[] { Identity(), Hash });
        }

        [Test] public void OtherPlayerCannotConsumeTheCurrentOwnersFirstSequence()
        {
            var invalid = Snapshot(999); Assets(invalid).PlayerId = "33333333333333333333333333333333";
            Assert.That(Queue(invalid), Is.False); Assert.That(Queue(Snapshot(1)), Is.True);
        }

        [Test] public void OldEpochWrongActorOrWrongRulesCannotReplacePendingSnapshot()
        {
            Assert.That(Queue(Snapshot(2)), Is.True);
            var old = Snapshot(100); Set(old, "ConnectionEpoch", 1u); Assert.That(Queue(old), Is.False);
            var remote = Snapshot(101); Assets(remote).ActorId = 2; Assert.That(Queue(remote), Is.False);
            var config = Snapshot(102); Assets(config).GameplayConfigHash = new string('b', 64); Assert.That(Queue(config), Is.False);
            var take = new object[] { null };
            Assert.That(_type.GetMethod("TryTake").Invoke(_inbox, take), Is.True);
            Assert.That((ulong)_snapshot.GetField("SnapshotSequence").GetValue(take[0]), Is.EqualTo(2ul));
        }

        [Test] public void DuplicateOutOfOrderAndConsumedPacketsNeverReappear()
        {
            Assert.That(Queue(Snapshot(3)), Is.True); Assert.That(Queue(Snapshot(3)), Is.False);
            Assert.That(Queue(Snapshot(2)), Is.False); Assert.That(Queue(Snapshot(4)), Is.True);
            var take = new object[] { null };
            Assert.That(_type.GetMethod("TryTake").Invoke(_inbox, take), Is.True);
            Assert.That((ulong)_snapshot.GetField("SnapshotSequence").GetValue(take[0]), Is.EqualTo(4ul));
            Assert.That(_type.GetMethod("TryTake").Invoke(_inbox, take), Is.False);
            Assert.That(take[0], Is.Null);
        }

        [Test] public void InventoryIdentityCannotBeTakenFromTheUntrustedPayload()
        {
            var wrong = Snapshot(1); Assets(wrong).InventoryId = "arbitrary-client-inventory";
            Assert.That(Queue(wrong), Is.False); Assert.That(Queue(Snapshot(1)), Is.True);
        }

        [Test] public void ActualInventoryEnvelopeDoesNotCollideWithMirrorOrExistingMessages()
        {
            var envelope = typeof(OnlineWorldBootstrap).Assembly.GetType("Game.Multiplayer.InventorySnapshotEnvelope");
            Assert.That(envelope, Is.Not.Null);
            var ids = new Dictionary<ushort, Type>();
            foreach (var type in typeof(NetworkManager).Assembly.GetTypes())
                if (type.IsValueType && !type.ContainsGenericParameters && typeof(NetworkMessage).IsAssignableFrom(type))
                    ids[type.FullName.GetStableHashCode16()] = type;
            foreach (var type in new[] { typeof(JoinTicketRequestEnvelope), typeof(JoinTicketReplyEnvelope), typeof(CombatFactEnvelope),
                typeof(CombatIntentEnvelope), typeof(MovementInputEnvelope), typeof(MovementStateEnvelope), typeof(RoomProtocolEnvelope), envelope })
            {
                ushort id = type.FullName.GetStableHashCode16();
                Assert.That(ids.TryGetValue(id, out var previous), Is.False, type.FullName + " collides with " + previous?.FullName);
                ids.Add(id, type);
            }
        }

        [Test] public void EnvelopeRejectsOversizeAndTruncatedPayloadBeforeBorrowingBytes()
        {
            var serializer = typeof(OnlineWorldBootstrap).Assembly.GetType("Game.Multiplayer.InventorySnapshotEnvelopeSerialization");
            Assert.That(serializer, Is.Not.Null);
            var read = serializer.GetMethod("ReadInventorySnapshotEnvelope");
            using var writer = NetworkWriterPool.Get(); writer.WriteUInt(65537);
            var tooLarge = Assert.Throws<TargetInvocationException>(() => read.Invoke(null, new object[] { new NetworkReader(writer.ToArraySegment()) }));
            Assert.That(tooLarge.InnerException, Is.TypeOf<InvalidDataException>());
            writer.Reset(); writer.WriteUInt(3); writer.WriteByte(1);
            var truncated = Assert.Throws<TargetInvocationException>(() => read.Invoke(null, new object[] { new NetworkReader(writer.ToArraySegment()) }));
            Assert.That(truncated.InnerException, Is.TypeOf<InvalidDataException>());
        }

        private static JoinConnectionIdentity Identity() => new JoinConnectionIdentity(Player, "room", Run, 1, 2, 1, "p7");
        private object Snapshot(ulong sequence)
        {
            var value = Activator.CreateInstance(_snapshot);
            Set(value, "SchemaVersion", 1u); Set(value, "ConnectionEpoch", 2u); Set(value, "SnapshotSequence", sequence);
            Set(value, "Assets", new OnlineOwnedAssetSnapshot { SchemaVersion = 1, ActorId = 1, PlayerId = Player,
                RunId = Run, InventoryId = Run + "/" + Player, GameplayConfigHash = Hash });
            return value;
        }
        private bool Queue(object value) => (bool)_type.GetMethod("TryQueue").Invoke(_inbox, new[] { value });
        private OnlineOwnedAssetSnapshot Assets(object value) => (OnlineOwnedAssetSnapshot)_snapshot.GetField("Assets").GetValue(value);
        private void Set(object value, string field, object data) => _snapshot.GetField(field).SetValue(value, data);
    }
}
