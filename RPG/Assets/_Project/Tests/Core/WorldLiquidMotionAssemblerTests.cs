using System;
using NUnit.Framework;

namespace Game.Core.Tests
{
    public sealed class WorldLiquidMotionAssemblerTests
    {
        private static uint[] Payload(out int count, int rows = 32)
        {
            var words = new uint[WorldLiquidMotionCodec.MaxWords];
            // 手工 wire 夹具使 assembler 的 RED 真正落在分页行为，而非被尚未实现的 Encode 提前挡住。
            count = 17 + rows * 10;
            words[0] = 0x4E4C4D31; ulong time = unchecked((ulong)BitConverter.DoubleToInt64Bits(1.3));
            words[1] = (uint)time; words[2] = (uint)(time >> 32); words[3] = 0x3DCCCCCD;
            words[4] = 8; words[5] = 1; words[6] = (uint)rows;
            words[7] = uint.MaxValue; words[9] = 2;
            words[10] = 4; words[12] = 6; words[14] = 9; words[16] = 1;
            for (int i = 0; i < rows; i++)
            {
                int row = 17 + i * 10; words[row + 1] = 0x01000000u | (uint)i;
                words[row + 2] = 20; words[row + 3] = 0xB4003000; words[row + 4] = 0x38003800; words[row + 5] = 0x3C00C000;
                words[row + 6] = 20; words[row + 7] = 0xB8003400; words[row + 8] = 0x3A003A00; words[row + 9] = 0x3E00C200;
            }
            return words;
        }
        private static WorldLiquidMotionPageHeader Header(uint[] words, int count, ulong sequence = 1, uint tick = 13,
            ulong world = 7, uint connection = 3) => new WorldLiquidMotionPageHeader(world, connection, sequence, tick,
                count, 1 + (count - 1) / WorldLiquidMotionAssembler.PageWords, WorldLiquidMotionCodec.ComputeChecksum(words, count));
        private static bool Page(WorldLiquidMotionAssembler assembler, WorldLiquidMotionPageHeader header,
            uint[] words, int page, double now)
        {
            int offset = page * WorldLiquidMotionAssembler.PageWords;
            int count = Math.Min(WorldLiquidMotionAssembler.PageWords, header.WordCount - offset);
            var slice = new uint[count]; Array.Copy(words, offset, slice, 0, count);
            return assembler.TryReceivePage(header, page, slice, count, now);
        }
        private static void Complete(WorldLiquidMotionAssembler assembler, WorldLiquidMotionPageHeader header,
            uint[] words, double now)
        {
            Assert.That(assembler.TryBegin(header, now), Is.True);
            for (int i = 0; i < header.PageCount; i++) Assert.That(Page(assembler, header, words, i, now + .01), Is.True);
        }

        [Test] public void OutOfOrderCompleteFrameRequiresQuantityValidationBeforePublishing()
        {
            var words = Payload(out int count); var header = Header(words, count);
            var assembler = new WorldLiquidMotionAssembler(7, 3);
            Assert.That(assembler.TryBegin(header, 0), Is.True);
            Assert.That(Page(assembler, header, words, 2, .01), Is.True);
            Assert.That(assembler.HasCompleteCandidate, Is.False);
            Assert.That(Page(assembler, header, words, 0, .02), Is.True);
            Assert.That(Page(assembler, header, words, 1, .03), Is.True);
            Assert.That(assembler.HasCompleteCandidate, Is.True); Assert.That(assembler.HasPublished, Is.False);
            Assert.That(assembler.AcceptedSequence, Is.Zero);
            Assert.That(assembler.TryGetCompletedCandidate(out var candidate), Is.True);
            Assert.That(candidate.WorldEpoch, Is.EqualTo(7)); Assert.That(candidate.Sequence, Is.EqualTo(1));
            Assert.That(assembler.CommitCandidate(), Is.True); Assert.That(assembler.HasPending, Is.False);
            var copied = new WorldLiquidMotionFrame(); Assert.That(assembler.TryCopyPublished(copied), Is.True);
            Assert.That(copied.RowCount, Is.EqualTo(32)); Assert.That(assembler.AcceptedMaterialTick, Is.EqualTo(13));
        }

        [Test] public void RejectedHighSequenceCandidateDoesNotPoisonLaterLegalSequence()
        {
            var words = Payload(out int count, 1); var assembler = new WorldLiquidMotionAssembler(7, 3);
            Complete(assembler, Header(words, count, 1), words, 0); Assert.That(assembler.CommitCandidate(), Is.True);
            Complete(assembler, Header(words, count, ulong.MaxValue, 20), words, .1);
            assembler.RejectCandidate(); Assert.That(assembler.AcceptedSequence, Is.EqualTo(1));
            Complete(assembler, Header(words, count, 2, 14), words, .2); Assert.That(assembler.CommitCandidate(), Is.True);
            Assert.That(assembler.AcceptedSequence, Is.EqualTo(2));
        }

        [Test] public void InvalidChecksumAndInvalidBodyNeverReplacePublishedMotion()
        {
            var words = Payload(out int count, 1); var assembler = new WorldLiquidMotionAssembler(7, 3);
            Complete(assembler, Header(words, count), words, 0); Assert.That(assembler.CommitCandidate(), Is.True);
            var next = Header(words, count, ulong.MaxValue, 14);
            Assert.That(assembler.TryBegin(next, .1), Is.True); words[0]++;
            Assert.That(Page(assembler, next, words, 0, .11), Is.False);
            Assert.That(assembler.LastFailure, Is.EqualTo(WorldLiquidMotionAssemblyFailure.Checksum));
            next = Header(words, count, ulong.MaxValue, 14);
            Assert.That(assembler.TryBegin(next, .2), Is.True); Assert.That(Page(assembler, next, words, 0, .21), Is.False);
            Assert.That(assembler.LastFailure, Is.EqualTo(WorldLiquidMotionAssemblyFailure.Payload));
            Assert.That(assembler.AcceptedSequence, Is.EqualTo(1)); Assert.That(assembler.HasPublished, Is.True);
        }

        [Test] public void DuplicatePageIsIdempotentButConflictCancelsOnlyCandidate()
        {
            var words = Payload(out int count); var header = Header(words, count); var assembler = new WorldLiquidMotionAssembler(7, 3);
            Assert.That(assembler.TryBegin(header, 0), Is.True);
            Assert.That(Page(assembler, header, words, 0, .01), Is.True); Assert.That(Page(assembler, header, words, 0, .02), Is.True);
            words[0]++;
            Assert.That(Page(assembler, header, words, 0, .03), Is.False);
            Assert.That(assembler.LastFailure, Is.EqualTo(WorldLiquidMotionAssemblyFailure.ConflictingPage));
            Assert.That(assembler.HasPending, Is.False); Assert.That(assembler.HasPublished, Is.False);
        }

        [Test] public void InvalidHighHeaderAndForeignPageCannotPreemptValidCandidate()
        {
            var words = Payload(out int count); var header = Header(words, count); var assembler = new WorldLiquidMotionAssembler(7, 3);
            Assert.That(assembler.TryBegin(header, 0), Is.True);
            var invalid = new WorldLiquidMotionPageHeader(7, 3, ulong.MaxValue, 14, int.MaxValue, 1, 0);
            Assert.That(assembler.TryBegin(invalid, .01), Is.False);
            Assert.That(Page(assembler, Header(words, count, ulong.MaxValue), words, 0, .02), Is.False);
            Assert.That(Page(assembler, header, words, 0, .03), Is.True);
            Assert.That(Page(assembler, header, words, 1, .04), Is.True);
            Assert.That(Page(assembler, header, words, 2, .05), Is.True);
            Assert.That(assembler.CommitCandidate(), Is.True); Assert.That(assembler.AcceptedSequence, Is.EqualTo(1));
        }

        [Test] public void TimeoutIsMeasuredFromFirstPageAndDuplicateBeginDoesNotExtendIt()
        {
            var words = Payload(out int count); var header = Header(words, count); var assembler = new WorldLiquidMotionAssembler(7, 3, .2);
            Assert.That(assembler.TryBegin(header, 1), Is.True); Assert.That(Page(assembler, header, words, 0, 1.05), Is.True);
            Assert.That(assembler.TryBegin(header, 1.15), Is.True);
            Assert.That(assembler.ExpirePending(1.21), Is.True); Assert.That(assembler.HasPending, Is.False);
            Assert.That(assembler.CommitCandidate(), Is.False); Assert.That(assembler.AcceptedSequence, Is.Zero);
        }

        [Test] public void ReconnectClearsBothMotionBuffersAndOldEpochCannotReestablishThem()
        {
            var words = Payload(out int count, 1); var assembler = new WorldLiquidMotionAssembler(7, 3);
            Complete(assembler, Header(words, count), words, 0); Assert.That(assembler.CommitCandidate(), Is.True);
            Assert.That(assembler.ResetConnection(2), Is.False); Assert.That(assembler.HasPublished, Is.True);
            Assert.That(assembler.ResetConnection(3), Is.True); Assert.That(assembler.HasPublished, Is.True);
            Assert.That(assembler.ResetConnection(4), Is.True); Assert.That(assembler.HasPublished, Is.False);
            Assert.That(assembler.TryBegin(Header(words, count), .2), Is.False);
            Complete(assembler, Header(words, count, 1, 14, connection: 4), words, .3);
            Assert.That(assembler.CommitCandidate(), Is.True);
        }

        [Test] public void AlreadyAcceptedSequenceAndBackwardMaterialTickCannotRollPlaybackBack()
        {
            var words = Payload(out int count, 1); var assembler = new WorldLiquidMotionAssembler(7, 3);
            Complete(assembler, Header(words, count, 2, 15), words, 0); Assert.That(assembler.CommitCandidate(), Is.True);
            Assert.That(assembler.TryBegin(Header(words, count, 2, 15), .1), Is.False);
            Assert.That(assembler.TryBegin(Header(words, count, 3, 14), .1), Is.False);
            Assert.That(assembler.TryBegin(Header(words, count, 3, 16, world: 8), .1), Is.False);
            Assert.That(assembler.AcceptedSequence, Is.EqualTo(2));
        }
    }
}
