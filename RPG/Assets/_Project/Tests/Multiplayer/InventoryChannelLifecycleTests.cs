using System;
using System.Reflection;
using Game.Core;
using Game.Networking;
using NUnit.Framework;
using UnityEngine;

namespace Game.Multiplayer.Tests
{
    /// <summary>World结束不等于Manager/网络结束，验证定界缓冲确实随Owner解除，而非等断线清理。</summary>
    public sealed class InventoryChannelLifecycleTests
    {
        [Test]
        public void DetachReleasesOnlyItsWorldAndClearsPendingOwnerStateWithoutStoppingManager()
        {
            var configure = typeof(InventoryNetworkChannel).GetMethod("ConfigureWorld");
            var detach = typeof(InventoryNetworkChannel).GetMethod("DetachWorld");
            Assert.That(configure, Is.Not.Null); Assert.That(detach, Is.Not.Null);
            var go = new GameObject("Owner channel lifecycle fixture"); go.SetActive(false);
            try
            {
                var channel = go.AddComponent<InventoryNetworkChannel>();
                object first = new object(), second = new object();
                configure.Invoke(channel, new object[] { first, new string('a', 64), 1ul, null });
                var senders = (InventorySnapshotSender[])typeof(InventoryNetworkChannel).GetField("_snapshotSenders", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(channel);
                senders[0] = new InventorySnapshotSender();
                Assert.That(senders[0].TryQueue(2, 1, new byte[] { 1, 2 }), Is.True);
                detach.Invoke(channel, new[] { first });
                Assert.That(senders[0].BufferedCount, Is.Zero);
                Assert.That(channel.GetComponent<RoomNetworkManager>(), Is.Not.Null);
                configure.Invoke(channel, new object[] { second, new string('a', 64), 1ul, null });
                Assert.That(senders[0].TryQueue(3, 1, new byte[] { 3 }), Is.True);
                detach.Invoke(channel, new[] { first });
                Assert.That(senders[0].BufferedCount, Is.EqualTo(1), "旧World不得释放新Owner的在途状态。");
                detach.Invoke(channel, new[] { second }); Assert.That(senders[0].BufferedCount, Is.Zero);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void CongestedReliableQueueHoldsOwnerPageUntilItDrains()
        {
            var policy = typeof(InventoryNetworkChannel).GetMethod("CanQueueOwnerPage");
            Assert.That(policy, Is.Not.Null, "帧四页预算不能替代可靠在途队列回压。");
            bool Room(int queued, int reserved) => (bool)policy.Invoke(null, new object[] { queued, reserved, 16384, 1136 });
            var sender = new InventorySnapshotSender();
            Assert.That(sender.TryQueue(2, 1, new byte[20000]), Is.True);
            Assert.That(sender.TryPeek(out var before), Is.True);
            Assert.That(Room(1000, 0), Is.False);
            Assert.That(sender.TryPeek(out var held), Is.True); Assert.That(held.Index, Is.EqualTo(before.Index));
            Assert.That(Room(0, 180), Is.False); Assert.That(Room(-1, 0), Is.False);
            Assert.That(Room(0, 0), Is.True);
            Assert.That(sender.Advance(), Is.False);
            Assert.That(sender.TryPeek(out var next), Is.True); Assert.That(next.Index, Is.EqualTo(before.Index + 1));
        }
    }
}
