using System;
using NUnit.Framework;
using UnityEngine;

namespace Game.Core.Tests
{
    public sealed class WorldLiquidMotionCodecTests
    {
        internal static WorldLiquidMotionFrame SampleFrame(int rows = 1)
        {
            var frame = new WorldLiquidMotionFrame
            { WorldEpoch = 7, ConnectionEpoch = 3, Sequence = 2, MaterialTick = 13,
                SampleTime = 1.3, Interval = .1f, ChunkSize = 8, AnchorCount = 1, RowCount = rows };
            frame.Anchors[0] = new WorldLiquidMotionAnchor(new WorldChunkKey(WorldMaterialDomain.Liquid, -1, 0, 2), 4, 6, 9, true);
            for (int i = 0; i < rows; i++)
                frame.Rows[i] = new WorldLiquidMotionSample(new Vector3Int(-8 + i % 8, i / 8 % 8, 16 + i / 64), 1,
                    20, 20, new Vector3(.125f, -.25f, .5f), new Vector3(.5f, -2, 1),
                    new Vector3(.25f, -.5f, .75f), new Vector3(.75f, -3, 1.5f), 0);
            return frame;
        }

        [Test] public void MotionBodyRoundTripsNegativeNativeCoordinatesWithoutChangingEnvelopeIdentity()
        {
            var source = SampleFrame(); var words = new uint[WorldLiquidMotionCodec.MaxWords];
            Assert.That(WorldLiquidMotionCodec.TryEncode(source, words, out int count), Is.True);
            var target = new WorldLiquidMotionFrame { WorldEpoch = 81, ConnectionEpoch = 82, Sequence = 83, MaterialTick = 84 };
            Assert.That(WorldLiquidMotionCodec.TryDecode(words, count, target), Is.True);
            Assert.That(target.WorldEpoch, Is.EqualTo(81)); Assert.That(target.ConnectionEpoch, Is.EqualTo(82));
            Assert.That(target.Sequence, Is.EqualTo(83)); Assert.That(target.MaterialTick, Is.EqualTo(84));
            Assert.That(target.SampleTime, Is.EqualTo(source.SampleTime)); Assert.That(target.Interval, Is.EqualTo(.1f));
            Assert.That(target.Rows[0].Cell, Is.EqualTo(source.Rows[0].Cell));
            Assert.That(target.Rows[0].CurrentOffset, Is.EqualTo(source.Rows[0].CurrentOffset));
            Assert.That(target.Rows[0].PreviousVelocity, Is.EqualTo(source.Rows[0].PreviousVelocity));
            Assert.That(target.Anchors[0].Generation, Is.EqualTo(4)); Assert.That(target.Anchors[0].SubscriptionEpoch, Is.EqualTo(9));
        }

        [Test] public void FullCapacityFrameRemainsWithinFixedWordBudgetAndCopiesWithoutAliasing()
        {
            var frame = SampleFrame(128); frame.AnchorCount = 32;
            for (int i = 1; i < frame.AnchorCount; i++)
                frame.Anchors[i] = new WorldLiquidMotionAnchor(new WorldChunkKey(WorldMaterialDomain.Liquid, i, 0, 0), 1, 1, (ulong)i + 10, true);
            // 每份被声明的 Anchor 必须真正被行引用，避免空依赖扩大发布障碍。
            for (int i = 0; i < 32; i++) frame.Rows[i] = new WorldLiquidMotionSample(
                new Vector3Int(frame.Anchors[i].Key.X * 8, 0, frame.Anchors[i].Key.Z * 8), 2, 1, 1,
                Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero, i);
            var words = new uint[WorldLiquidMotionCodec.MaxWords];
            Assert.That(WorldLiquidMotionCodec.TryEncode(frame, words, out int count), Is.True);
            Assert.That(count, Is.EqualTo(WorldLiquidMotionCodec.MaxWords));
            var copied = new WorldLiquidMotionFrame(); copied.CopyFrom(frame);
            frame.Rows[0] = default;
            Assert.That(copied.Rows[0].CurrentQuantity, Is.EqualTo(1));
            Assert.That(WorldLiquidMotionCodec.TryDecode(words, count, copied), Is.True);
            Assert.That(copied.RowCount, Is.EqualTo(128)); Assert.That(copied.AnchorCount, Is.EqualTo(32));
        }

        [Test] public void EmptyFrameCanClearMotionAndTombstoneCanCarryOnlyPreviousQuantity()
        {
            var frame = SampleFrame(); var words = new uint[WorldLiquidMotionCodec.MaxWords];
            frame.AnchorCount = frame.RowCount = 0;
            Assert.That(WorldLiquidMotionCodec.TryEncode(frame, words, out int count), Is.True);
            Assert.That(count, Is.EqualTo(WorldLiquidMotionCodec.PrefixWords));
            var target = SampleFrame(); Assert.That(WorldLiquidMotionCodec.TryDecode(words, count, target), Is.True);
            Assert.That(target.RowCount, Is.Zero);
            frame = SampleFrame(); frame.Anchors[0] = new WorldLiquidMotionAnchor(frame.Anchors[0].Key, 4, 7, 9, false);
            frame.Rows[0] = new WorldLiquidMotionSample(frame.Rows[0].Cell, 1, 20, 0,
                Vector3.zero, Vector3.down, Vector3.zero, Vector3.zero, 0);
            Assert.That(WorldLiquidMotionCodec.TryEncode(frame, words, out count), Is.True);
            Assert.That(WorldLiquidMotionCodec.TryDecode(words, count, target), Is.True);
            Assert.That(target.Anchors[0].IsResident, Is.False); Assert.That(target.Rows[0].PreviousQuantity, Is.EqualTo(20));
        }

        [TestCase("time")][TestCase("interval")][TestCase("chunk")][TestCase("material")]
        [TestCase("quantity")][TestCase("offset")][TestCase("velocity")][TestCase("emptyPose")]
        [TestCase("anchor")][TestCase("foreignCell")][TestCase("duplicateRow")][TestCase("duplicateAnchor")]
        [TestCase("coordinateOverflow")][TestCase("tombstoneCurrent")]
        public void InvalidSourceDoesNotWriteEvenTheOutputPrefix(string invalid)
        {
            var frame = SampleFrame(); var row = frame.Rows[0];
            switch (invalid)
            {
                case "time": frame.SampleTime = double.NaN; break;
                case "interval": frame.Interval = 0; break;
                case "chunk": frame.ChunkSize = 257; break;
                case "material": frame.Rows[0] = Change(row, material: 0); break;
                case "quantity": frame.Rows[0] = Change(row, quantity: 65536); break;
                case "offset": frame.Rows[0] = Change(row, offset: new Vector3(4.1f, 0, 0)); break;
                case "velocity": frame.Rows[0] = Change(row, velocity: new Vector3(float.PositiveInfinity, 0, 0)); break;
                case "emptyPose": frame.Rows[0] = Change(row, quantity: 0); break;
                case "anchor": frame.Rows[0] = Change(row, anchor: 32); break;
                case "foreignCell": frame.Rows[0] = Change(row, cell: Vector3Int.zero); break;
                case "duplicateRow": frame.RowCount = 2; frame.Rows[1] = row; break;
                case "duplicateAnchor": frame.AnchorCount = 2; frame.Anchors[1] = frame.Anchors[0]; break;
                case "coordinateOverflow": frame.Anchors[0] = new WorldLiquidMotionAnchor(new WorldChunkKey(WorldMaterialDomain.Liquid, int.MaxValue, 0, 0), 1, 1, 1, true); break;
                case "tombstoneCurrent": frame.Anchors[0] = new WorldLiquidMotionAnchor(frame.Anchors[0].Key, 4, 7, 9, false); break;
            }
            var words = new uint[WorldLiquidMotionCodec.MaxWords]; words[0] = 123;
            Assert.That(WorldLiquidMotionCodec.TryEncode(frame, words, out int count), Is.False);
            Assert.That(count, Is.Zero); Assert.That(words[0], Is.EqualTo(123));
        }

        [TestCase(0, 0u)][TestCase(3, 0x7F800000u)][TestCase(5, 33u)][TestCase(6, 129u)]
        [TestCase(17, 32u)][TestCase(18, 0u)][TestCase(19, 65536u)][TestCase(20, 0x7C007C00u)]
        public void MalformedBodyPreservesAllDestinationFields(int word, uint value)
        {
            var words = new uint[WorldLiquidMotionCodec.MaxWords];
            Assert.That(WorldLiquidMotionCodec.TryEncode(SampleFrame(), words, out int count), Is.True);
            words[word] = value;
            var destination = SampleFrame(); destination.Sequence = 99; destination.SampleTime = 77;
            Assert.That(WorldLiquidMotionCodec.TryDecode(words, count, destination), Is.False);
            Assert.That(destination.Sequence, Is.EqualTo(99)); Assert.That(destination.SampleTime, Is.EqualTo(77));
            Assert.That(destination.Rows[0].CurrentQuantity, Is.EqualTo(20));
        }

        [Test] public void TrailingWordsAndShortOutputCapacityAreRejectedWithoutMutation()
        {
            var words = new uint[WorldLiquidMotionCodec.MaxWords];
            Assert.That(WorldLiquidMotionCodec.TryEncode(SampleFrame(), words, out int count), Is.True);
            var destination = SampleFrame(); destination.SampleTime = 88;
            Assert.That(WorldLiquidMotionCodec.TryDecode(words, count + 1, destination), Is.False);
            Assert.That(destination.SampleTime, Is.EqualTo(88));
            var shortWords = new uint[count - 1]; shortWords[0] = 55;
            Assert.That(WorldLiquidMotionCodec.TryEncode(SampleFrame(), shortWords, out _), Is.False);
            Assert.That(shortWords[0], Is.EqualTo(55));
        }

        private static WorldLiquidMotionSample Change(WorldLiquidMotionSample row, byte? material = null,
            uint? quantity = null, Vector3? offset = null, Vector3? velocity = null, int? anchor = null, Vector3Int? cell = null)
            => new WorldLiquidMotionSample(cell ?? row.Cell, material ?? row.MaterialId, row.PreviousQuantity,
                quantity ?? row.CurrentQuantity, row.PreviousOffset, row.PreviousVelocity,
                offset ?? row.CurrentOffset, velocity ?? row.CurrentVelocity, anchor ?? row.AnchorIndex);
    }
}
