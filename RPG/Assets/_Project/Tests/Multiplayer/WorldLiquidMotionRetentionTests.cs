using System;
using Game.Core;
using Game.NetworkProtocol;
using NUnit.Framework;
using UnityEngine;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 一份10 GMU从A搬至B，走真实Coordinator/ReplicaStore/ACK链。
    /// hold只延后空源地址的订阅退休；测试直接检查源数量已经删除，不用“没发tombstone”伪装保留成功。
    /// </summary>
    public sealed class WorldLiquidMotionRetentionTests
    {
        // 原点Chunk固定承载该Domain的Geometry，即使无数量也保持resident；
        // 真正的tombstone夹具必须选非原点A/B，否则只会测试普通空after-image Delta。
        private static readonly Vector3Int SourceCell = new Vector3Int(8, 0, 0);
        private static readonly Vector3Int TargetCell = new Vector3Int(16, 0, 0);
        private static readonly WorldChunkKey SourceKey = new WorldChunkKey(WorldMaterialDomain.Liquid, 1, 0, 0);
        private static readonly WorldChunkKey TargetKey = new WorldChunkKey(WorldMaterialDomain.Liquid, 2, 0, 0);
        private static readonly Bounds Inside = new Bounds(Vector3.one * 4, Vector3.one * 64);

        [Test]
        public void TombstoneIsInstalledAndAcknowledgedWhileMotionWindowKeepsItsSubscription()
        {
            var f = new Fixture();
            WorldLiquidMotionFrame frame = f.MoveAndRetain(1);
            f.PumpUntilSourceDeletionAck(1.01);
            Assert.That(f.SourceTombstoneInstalled, Is.True);
            Assert.That(f.SourceTombstoneAcknowledged, Is.True);
            Assert.That(f.Quantity(SourceCell), Is.Zero, "hold不能让已收走的旧量继续留在Replica Store。");
            Assert.That(f.Quantity(TargetCell), Is.EqualTo(10u)); Assert.That(f.TotalQuantity, Is.EqualTo(10UL));

            f.Pump(1.10, Inside);
            Assert.That(f.SourceLeaves, Is.Zero);
            Assert.That(f.Server.IsCurrentPeerSubscription(0, f.Connection, f.Epoch, SourceKey, frame.Anchors[0].SubscriptionEpoch), Is.True);
            Assert.That(f.Client.AcceptsLiquidMotionFrame(frame), Is.True, "已经安装tombstone的Previous端点仍有短暂合法订阅。");
            Assert.That(f.HasAuthorityKey(SourceKey), Is.True);
        }

        [Test]
        public void ExpiredMotionWindowSendsLeaveAndRetiresEmptyMetadataWithoutRemovingTargetQuantity()
        {
            var f = new Fixture(); WorldLiquidMotionFrame frame = f.MoveAndRetain(1);
            f.PumpUntilSourceDeletionAck(1.01);
            ulong retiredBefore = f.Server.RetiredKeys;
            f.Pump(1.25, Inside);
            Assert.That(f.SourceLeaves, Is.EqualTo(1));
            Assert.That(f.Client.AcceptsLiquidMotionFrame(frame), Is.False);
            Assert.That(f.HasAuthorityKey(SourceKey), Is.False);
            Assert.That(f.Server.RetiredKeys, Is.GreaterThan(retiredBefore));
            Assert.That(f.TotalQuantity, Is.EqualTo(10UL)); Assert.That(f.Quantity(TargetCell), Is.EqualTo(10u));
            Assert.That(f.Server.Faulted || f.Client.Faulted, Is.False);
        }

        [Test]
        public void RealSpatialInterestExitSendsLeaveBeforeMotionHoldExpires()
        {
            var f = new Fixture(); WorldLiquidMotionFrame frame = f.MoveAndRetain(1);
            f.PumpUntilSourceDeletionAck(1.01);
            var outside = new Bounds(new Vector3(1000, 1000, 1000), Vector3.one * 4);
            f.Pump(1.06, outside);
            // 原点Geometry、A、B共三份订阅，正式限额每轮两页；下一轮完成B的Leave，
            // 两轮都远早于hold的1.15秒截止，不能通过取消页预算伪造“立即退出”。
            f.Pump(1.065, outside);
            Assert.That(f.SourceLeaves, Is.EqualTo(1), "真实空间Interest离开不能等待运动窗口。");
            Assert.That(f.Client.AcceptsLiquidMotionFrame(frame), Is.False);
            Assert.That(f.HasAuthorityKey(SourceKey), Is.False);
            Assert.That(f.TotalQuantity, Is.Zero, "客户端释放离开Interest的缓存，不是删除Authority中的10 GMU。");
            Assert.That(f.HasAuthorityKey(TargetKey), Is.True);
        }

        [Test]
        public void ReconnectCannotReuseThePreviousConnectionsHoldForANewTombstone()
        {
            var f = new Fixture();
            // 先给仍resident的A建立真实已编码运动窗口。若BindPeer未清hold，
            // 新连接稍后对同一A的删除会错误继承旧连接deadline。
            var oldFrame = f.ResidentFrame(1);
            f.Server.BeginRound(1); f.EncodeAndRetain(oldFrame, 1);
            f.Reconnect(11, 4);
            f.Pump(1.01, Inside); f.Pump(1.02, Inside);
            Assert.That(f.Quantity(SourceCell), Is.EqualTo(10u));
            Assert.That(f.Client.AcceptsLiquidMotionFrame(oldFrame), Is.False);

            f.MoveWithoutRetention();
            f.PumpUntilSourceDeletionAck(1.03);
            f.Pump(1.08, Inside); // 仍早于旧连接1.15秒的deadline。
            Assert.That(f.SourceLeaves, Is.EqualTo(1));
            Assert.That(f.HasAuthorityKey(SourceKey), Is.False);
            Assert.That(f.Quantity(SourceCell), Is.Zero); Assert.That(f.TotalQuantity, Is.EqualTo(10UL));
            Assert.That(f.Server.Faulted || f.Client.Faulted, Is.False);
        }

        private sealed class MovingSource : IWorldReplicationSource
        {
            internal Vector3Int Cell = SourceCell;
            internal uint Tick;
            public bool TryCapture(WorldReplicaBuffer buffer, out WorldCaptureFailure failure)
            {
                buffer.Geometries[0] = new WorldGridGeometry(WorldMaterialDomain.Liquid, Vector3.zero, 1, 8);
                buffer.Cells[0] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid, WorldMaterialStorage.Dynamic, Cell, 2, 10);
                buffer.Header = new WorldReplicaHeader(++Tick, 1, 0, 1); failure = default; return true;
            }
        }

        private sealed class Fixture
        {
            internal readonly WorldReplicationCoordinator Server = new WorldReplicationCoordinator(7, 99, 8, 128, 8, 0, 1000000);
            internal readonly WorldReplicationCoordinator Client;
            private readonly object _store;
            private readonly MovingSource _source = new MovingSource();
            private readonly WorldReplicaBuffer _buffer = new WorldReplicaBuffer(8, 0, 1000000);
            internal int Connection = 10;
            internal uint Epoch = 3;
            internal int SourceLeaves;
            internal bool SourceTombstoneInstalled, SourceTombstoneAcknowledged;

            internal Fixture()
            {
                // 与现有Retirement fixture一致，Reflection仅跨测试asmdef调用真正的ElementField Store。
                var type = Type.GetType("Game.ElementField.WorldReplicaStore, Game.ElementField", true);
                _store = Activator.CreateInstance(type, new object[] { 7UL, 8, 8, 0, 128 });
                Assert.That(type.GetMethod("ConfigureGeometry").Invoke(_store,
                    new object[] { new WorldGridGeometry(WorldMaterialDomain.Liquid, Vector3.zero, 1, 8) }), Is.True);
                Assert.That(type.GetMethod("ConfigureMaterials").Invoke(_store,
                    new object[] { WorldMaterialDomain.Liquid, 65535u, new byte[] { 2 }, false, 8 }), Is.True);
                Client = new WorldReplicationCoordinator(7, 99, 8, 128, 8, 0, 1000000, (IWorldChunkPayloadInstaller)_store);
                Assert.That(Client.SetClientConnectionEpoch(Epoch), Is.True);
                Assert.That(Server.BindPeer(0, Connection, Epoch), Is.True);
                Assert.That(Server.Capture(_source, _buffer), Is.True);
                for (int i = 0; i < 4; i++) Pump(i * .02, Inside);
                Assert.That(Quantity(SourceCell), Is.EqualTo(10u)); Assert.That(TotalQuantity, Is.EqualTo(10UL));
            }

            internal ulong TotalQuantity => (ulong)_store.GetType().GetMethod("TotalQuantity").Invoke(_store, new object[] { WorldMaterialDomain.Liquid });
            internal uint Quantity(Vector3Int cell)
            {
                object[] args = { WorldMaterialDomain.Liquid, cell, (byte)2, 0u };
                _store.GetType().GetMethod("TryGetQuantity").Invoke(_store, args); return (uint)args[3];
            }
            internal bool HasAuthorityKey(WorldChunkKey key)
            {
                foreach (var stamp in Server.DescribeChunks())
                    if (stamp.Domain == (int)key.Domain && stamp.X == key.X && stamp.Y == key.Y && stamp.Z == key.Z) return true;
                return false;
            }

            internal void Reconnect(int connection, uint epoch)
            {
                Connection = connection; Epoch = epoch;
                Assert.That(Client.SetClientConnectionEpoch(epoch), Is.True);
                Assert.That(Server.BindPeer(0, connection, epoch), Is.True);
                SourceLeaves = 0; SourceTombstoneInstalled = SourceTombstoneAcknowledged = false;
            }
            internal void MoveWithoutRetention()
            {
                _source.Cell = TargetCell; Assert.That(Server.Capture(_source, _buffer), Is.True);
            }
            internal WorldLiquidMotionFrame ResidentFrame(double now)
            {
                Assert.That(Server.TryGetLiquidMotionAnchor(0, SourceKey, out var anchor), Is.True);
                var frame = Frame(now); frame.AnchorCount = frame.RowCount = 1; frame.Anchors[0] = anchor;
                frame.Rows[0] = new WorldLiquidMotionSample(SourceCell, 2, 10, 10, Vector3.zero, Vector3.down, Vector3.zero, Vector3.down, 0);
                return frame;
            }
            internal WorldLiquidMotionFrame MoveAndRetain(double now)
            {
                MoveWithoutRetention(); Server.BeginRound(now);
                Assert.That(Server.UpdatePeerInterest(0, Connection, Epoch, Inside), Is.True);
                // 正式发送先宣布B。两个anchor均沿已宣布订阅取得，不伪造订阅代次。
                WorldPageMessage entered = SendOne(now);
                Assert.That(entered, Is.Not.Null); Assert.That(entered.Kind, Is.EqualTo(6u)); Assert.That(entered.ChunkX, Is.EqualTo(TargetKey.X));
                Assert.That(Server.TryGetLiquidMotionAnchor(0, SourceKey, out var from), Is.True);
                Assert.That(Server.TryGetLiquidMotionAnchor(0, TargetKey, out var to), Is.True);
                Assert.That(from.IsResident, Is.False, "夹具必须真实删除非原点Chunk，不能把resident空Delta当成tombstone。");
                Assert.That(to.IsResident, Is.True);
                var frame = Frame(now); frame.AnchorCount = frame.RowCount = 2; frame.Anchors[0] = from; frame.Anchors[1] = to;
                frame.Rows[0] = new WorldLiquidMotionSample(SourceCell, 2, 10, 0, Vector3.zero, Vector3.down, Vector3.zero, Vector3.zero, 0);
                frame.Rows[1] = new WorldLiquidMotionSample(TargetCell, 2, 0, 10, Vector3.zero, Vector3.zero, Vector3.zero, Vector3.down, 1);
                EncodeAndRetain(frame, now);
                SendOne(now); Assert.That(Server.CompleteSendRound(), Is.True); return frame;
            }
            private WorldLiquidMotionFrame Frame(double now) => new WorldLiquidMotionFrame
            { WorldEpoch = 7, ConnectionEpoch = Epoch, Sequence = 1, MaterialTick = _source.Tick, SampleTime = now, Interval = .1f, ChunkSize = 8 };

            internal void EncodeAndRetain(WorldLiquidMotionFrame frame, double now)
            {
                Assert.That(WorldLiquidMotionCodec.TryEncode(frame, new uint[WorldLiquidMotionCodec.MaxWords], out _), Is.True);
                Server.RetainLiquidMotionAnchors(0, frame, now);
            }
            internal void PumpUntilSourceDeletionAck(double start)
            {
                for (int i = 0; i < 8 && !SourceTombstoneAcknowledged; i++) Pump(start + i * .005, Inside);
                Assert.That(SourceTombstoneInstalled, Is.True, "必须收到并安装真实Kind5，不能仅凭源格数量已清零判断删除路径通过。");
                Assert.That(SourceTombstoneAcknowledged, Is.True, "真实Kind5的ACK必须已由Server接受。");
            }
            internal void Pump(double now, Bounds interest)
            {
                Server.BeginRound(now);
                Assert.That(Server.UpdatePeerInterest(0, Connection, Epoch, interest), Is.True);
                for (int i = 0; i < 2; i++) if (SendOne(now) == null) break;
                Assert.That(Server.CompleteSendRound(), Is.True);
            }
            private WorldPageMessage SendOne(double now)
            {
                if (!Server.TryPreparePage(0, now, out var borrowed)) return null;
                var message = borrowed.Clone(); Assert.That(Server.MarkPageSent(0, now, 200), Is.True);
                bool source = message.Domain == (uint)SourceKey.Domain && message.ChunkX == SourceKey.X
                    && message.ChunkY == SourceKey.Y && message.ChunkZ == SourceKey.Z;
                if (message.Kind == 6 || message.Kind == 7)
                {
                    Assert.That(Client.ReceiveSubscription(message), Is.True);
                    if (source && message.Kind == 7) SourceLeaves++;
                }
                else
                {
                    Assert.That(Client.ReceivePage(message, (long)(now * 1000), out var reply), Is.True);
                    if (source && message.Kind == 5)
                    { SourceTombstoneInstalled = true; Assert.That(Quantity(SourceCell), Is.Zero); }
                    if (reply != null)
                    {
                        Assert.That(Server.ReceiveControl(0, Connection, Epoch, reply, now), Is.True);
                        if (source && message.Kind == 5 && reply.Kind == 3) SourceTombstoneAcknowledged = true;
                    }
                }
                return message;
            }
        }
    }
}
