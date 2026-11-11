using System;
using System.Reflection;
using NUnit.Framework;

namespace Game.Networking.Tests
{
    /// <summary>
    /// 仅测试Owner字节分页/有界队列/候选重组，不创建Mirror连接或把checksum当作身份认证。
    /// 通过Reflection观察缺入口的行为RED；新助手未实现时仍可编译并给出明确断言。
    /// </summary>
    public sealed class InventorySnapshotTransferTests
    {
        private Type _pageType, _pagingType, _senderType, _assemblerType;
        private const int PageBytes = 16 * 1024, MaximumBytes = 32 * 1024 * 1024;

        [SetUp] public void RequireTransferHelpers()
        {
            var assembly = typeof(MovementCodec).Assembly;
            _pageType = assembly.GetType("Game.Networking.InventorySnapshotPage");
            _pagingType = assembly.GetType("Game.Networking.InventorySnapshotPaging");
            _senderType = assembly.GetType("Game.Networking.InventorySnapshotSender");
            _assemblerType = assembly.GetType("Game.Networking.InventorySnapshotAssembler");
            Assert.That(_pageType, Is.Not.Null, "Owner完整包需要独立的有界页合同，不能整包直接发送到Mirror。");
            Assert.That(_pagingType, Is.Not.Null); Assert.That(_senderType, Is.Not.Null); Assert.That(_assemblerType, Is.Not.Null);
        }

        [Test] public void ChecksumUsesByteFnv1AWithoutBorrowingSegmentPrefixOrSuffix()
        {
            // hello的FNV1a32是公开可手算的固定向量；这里不是CRC，也不验证发送方权限。
            var bytes = new byte[] { 99, 104, 101, 108, 108, 111, 88 };
            Assert.That(Checksum(new ArraySegment<byte>(bytes, 1, 5)), Is.EqualTo(0x4f9f2cabu));
            Assert.That(Checksum(default), Is.EqualTo(2166136261u));
        }

        [TestCase(1, 1)] [TestCase(16384, 1)] [TestCase(16385, 2)]
        [TestCase(65537, 5)] [TestCase(20 * 1024 * 1024, 1280)] [TestCase(MaximumBytes, 2048)]
        public void CreatePageKeepsExactLengthIndexAndBorrowedOffset(int length, int expectedCount)
        {
            var bytes = Data(length); uint checksum = Checksum(new ArraySegment<byte>(bytes));
            for (int i = 0; i < expectedCount; i++)
            {
                var page = Page(bytes, 3, 7, checksum, i); var payload = Field<ArraySegment<byte>>(page, "Payload");
                Assert.That(Valid(page), Is.True); Assert.That(Field<int>(page, "Count"), Is.EqualTo(expectedCount));
                Assert.That(Field<int>(page, "TotalBytes"), Is.EqualTo(length)); Assert.That(Field<int>(page, "Index"), Is.EqualTo(i));
                Assert.That(payload.Array, Is.SameAs(bytes)); Assert.That(payload.Offset, Is.EqualTo(i * PageBytes));
                Assert.That(payload.Count, Is.EqualTo(Math.Min(PageBytes, length - i * PageBytes)));
            }
            Assert.That(Create(bytes, 3, 7, checksum, expectedCount, out _), Is.False);
        }

        [Test] public void InvalidPageMetadataAndTruncatedSlicesNeverBecomeValidPages()
        {
            Assert.That(Create(null, 3, 7, 0, 0, out _), Is.False);
            Assert.That(Create(Array.Empty<byte>(), 3, 7, 0, 0, out _), Is.False);
            Assert.That(Create(new byte[MaximumBytes + 1], 3, 7, 0, 0, out _), Is.False);
            Assert.That(Create(new byte[1], 0, 7, 0, 0, out _), Is.False);
            Assert.That(Create(new byte[1], 3, 0, 0, 0, out _), Is.False);
            Assert.That(Create(new byte[1], 3, 7, 0, -1, out _), Is.False);
            Assert.That(Valid(Manual(16385, 0, 1, 0, new byte[PageBytes])), Is.False);
            Assert.That(Valid(Manual(16385, 0, 2, 0, new byte[PageBytes - 1])), Is.False);
            Assert.That(Valid(Manual(16385, 1, 2, 0, new byte[2])), Is.False);
            Assert.That(Valid(Manual(16385, 2, 2, 0, new byte[1])), Is.False);
            Assert.That(Valid(Manual(0, 0, 0, 0, Array.Empty<byte>())), Is.False);
        }

        [Test] public void SenderKeepsCurrentAndOnlyLatestPendingWithoutAdvancingOnPeek()
        {
            var sender = Sender(); var first = Data(65537, 1); var middle = Data(20, 2); var latest = Data(30, 3);
            Assert.That(Queue(sender, 3, 7, first), Is.True); Assert.That(Queue(sender, 3, 8, middle), Is.True);
            Assert.That(Queue(sender, 3, 9, latest), Is.True);
            Assert.That(Property<int>(sender, "BufferedCount"), Is.EqualTo(2));
            Assert.That(Property<int>(sender, "BufferedBytes"), Is.EqualTo(first.Length + latest.Length));
            Assert.That(Peek(sender, out var page), Is.True); Assert.That(Peek(sender, out var same), Is.True);
            Assert.That(Field<int>(page, "Index"), Is.Zero); Assert.That(Field<int>(same, "Index"), Is.Zero);
            Assert.That(Field<ArraySegment<byte>>(page, "Payload").Array, Is.SameAs(first), "Queue接管数组所有权，不另做20MiB副本。");
            for (int i = 0; i < 5; i++)
            {
                Assert.That(Peek(sender, out page), Is.True); Assert.That(Field<ulong>(page, "Sequence"), Is.EqualTo(7));
                Assert.That(Field<int>(page, "Index"), Is.EqualTo(i)); Assert.That(Advance(sender), Is.EqualTo(i == 4));
            }
            Assert.That(Peek(sender, out page), Is.True); Assert.That(Field<ulong>(page, "Sequence"), Is.EqualTo(9));
            Assert.That(Field<ArraySegment<byte>>(page, "Payload").Array, Is.SameAs(latest));
            Assert.That(Advance(sender), Is.True); Assert.That(Peek(sender, out _), Is.False);
            Assert.That(Advance(sender), Is.False); Assert.That(Property<int>(sender, "BufferedCount"), Is.Zero);
            Assert.That(Property<int>(sender, "BufferedBytes"), Is.Zero);
        }

        [Test] public void SenderRejectsOldIdentityAndInvalidPayloadWithoutReplacingValidQueue()
        {
            var sender = Sender(); Assert.That(Queue(sender, 3, 10, new byte[2]), Is.True);
            Assert.That(Queue(sender, 2, 11, new byte[2]), Is.False); Assert.That(Queue(sender, 3, 10, new byte[2]), Is.False);
            Assert.That(Queue(sender, 3, 9, new byte[2]), Is.False); Assert.That(Queue(sender, 3, 11, null), Is.False);
            Assert.That(Queue(sender, 3, 11, Array.Empty<byte>()), Is.False);
            Assert.That(Queue(sender, 3, 11, new byte[MaximumBytes + 1]), Is.False);
            Assert.That(Property<int>(sender, "BufferedCount"), Is.EqualTo(1)); Assert.That(Peek(sender, out var page), Is.True);
            Assert.That(Field<ulong>(page, "Sequence"), Is.EqualTo(10));
            Call(sender, "Clear"); Assert.That(Property<int>(sender, "BufferedBytes"), Is.Zero);
            Assert.That(Queue(sender, 4, 1, new byte[2]), Is.True); Assert.That(Peek(sender, out page), Is.True);
            Assert.That(Field<uint>(page, "Epoch"), Is.EqualTo(4));
        }

        [Test] public void AssemblerPublishesOnlyCompleteChecksumAndWaitsForInstallationConfirmation()
        {
            var assembler = Assembler(3); var bytes = Data(65537); uint checksum = Checksum(new ArraySegment<byte>(bytes));
            for (int i = 0; i < 5; i++)
            {
                Assert.That(Accept(assembler, Page(bytes, 3, 7, checksum, i), out var completed), Is.True);
                if (i < 4) Assert.That(completed.Count, Is.Zero);
                else CollectionAssert.AreEqual(bytes, completed);
            }
            Assert.That(Property<int>(assembler, "BufferedBytes"), Is.Zero, "完整数组所有权已转交调用方，Assembler不继续持有。");
            Assert.That(Property<ulong>(assembler, "InstalledSequence"), Is.Zero);
            Confirm(assembler, 8, true); Assert.That(Property<ulong>(assembler, "InstalledSequence"), Is.Zero);
            Confirm(assembler, 7, true); Assert.That(Property<ulong>(assembler, "InstalledSequence"), Is.EqualTo(7));
            Assert.That(Property<int>(assembler, "BufferedBytes"), Is.Zero);
            Assert.That(Accept(assembler, Page(bytes, 3, 7, checksum, 0), out _), Is.False);
        }

        [Test] public void AssemblerCopiesCorrectSegmentOffsetsAndNeverAliasesReceivedPageArray()
        {
            var assembler = Assembler(3); var source = Data(20); var framed = new byte[30]; Array.Copy(source, 0, framed, 5, source.Length);
            var page = Manual(20, 0, 1, Checksum(new ArraySegment<byte>(source)), framed, offset: 5, length: 20);
            Assert.That(Accept(assembler, page, out var completed), Is.True);
            CollectionAssert.AreEqual(source, completed); framed[5] = 255;
            Assert.That(completed.Array[completed.Offset], Is.EqualTo(source[0]));
        }

        [Test] public void ReliableOrderedAssemblyRejectsFirstNonZeroAndGapsWithoutUnboundedCache()
        {
            var assembler = Assembler(3); var bytes = Data(PageBytes * 2 + 1); uint checksum = Checksum(new ArraySegment<byte>(bytes));
            Assert.That(Accept(assembler, Page(bytes, 3, 7, checksum, 1), out _), Is.False);
            Assert.That(Property<int>(assembler, "BufferedBytes"), Is.Zero);
            Assert.That(Accept(assembler, Page(bytes, 3, 7, checksum, 0), out _), Is.True);
            Assert.That(Accept(assembler, Page(bytes, 3, 7, checksum, 2), out _), Is.False);
            Assert.That(Accept(assembler, Page(bytes, 3, 7, checksum, 1), out _), Is.True);
            Assert.That(Accept(assembler, Page(bytes, 3, 7, checksum, 2), out var completed), Is.True);
            CollectionAssert.AreEqual(bytes, completed);
        }

        [Test] public void DuplicatePagesMustMatchBytesAndMetadataWithoutPublishingPartialData()
        {
            var assembler = Assembler(3); var bytes = Data(PageBytes + 1); uint checksum = Checksum(new ArraySegment<byte>(bytes));
            var first = Page(bytes, 3, 7, checksum, 0); Assert.That(Accept(assembler, first, out _), Is.True);
            Assert.That(Accept(assembler, first, out var completed), Is.True); Assert.That(completed.Count, Is.Zero);
            var corrupt = (byte[])bytes.Clone(); corrupt[17] ^= 1;
            Assert.That(Accept(assembler, Page(corrupt, 3, 7, checksum, 0), out _), Is.False);
            Assert.That(Accept(assembler, Page(bytes, 3, 7, checksum + 1, 0), out _), Is.False);
            Assert.That(Accept(assembler, Page(bytes, 3, 7, checksum, 1), out completed), Is.True);
            CollectionAssert.AreEqual(bytes, completed);
            Assert.That(Accept(assembler, Page(bytes, 3, 7, checksum, 1), out var duplicate), Is.False);
            Assert.That(duplicate.Count, Is.Zero, "没有active候选时非首0页不能重建上一份完整包。");
        }

        [Test] public void WrongEpochAndMetadataCannotChangeCurrentCandidate()
        {
            var assembler = Assembler(3); var bytes = Data(PageBytes + 1); uint checksum = Checksum(new ArraySegment<byte>(bytes));
            Assert.That(Accept(assembler, Page(bytes, 2, 7, checksum, 0), out _), Is.False);
            Assert.That(Accept(assembler, Page(bytes, 3, 7, checksum, 0), out _), Is.True);
            Assert.That(Accept(assembler, Page(bytes, 4, 7, checksum, 1), out _), Is.False);
            Assert.That(Accept(assembler, Page(bytes, 3, 7, checksum + 1, 1), out _), Is.False);
            Assert.That(Accept(assembler, Manual(bytes.Length + 1, 1, 2, checksum, new byte[2]), out _), Is.False);
            Assert.That(Accept(assembler, Page(bytes, 3, 7, checksum, 1), out var completed), Is.True);
            CollectionAssert.AreEqual(bytes, completed);
        }

        [Test] public void BadHighChecksumAndCodecRejectionDoNotPoisonInstalledSequence()
        {
            var assembler = Assembler(3); var bytes = Data(20); uint checksum = Checksum(new ArraySegment<byte>(bytes));
            Assert.That(Accept(assembler, Page(bytes, 3, 100, checksum + 1, 0), out var completed), Is.False); Assert.That(completed.Count, Is.Zero);
            Assert.That(Property<ulong>(assembler, "InstalledSequence"), Is.Zero);
            Assert.That(Accept(assembler, Page(bytes, 3, 50, checksum, 0), out completed), Is.True);
            Confirm(assembler, 50, false); Assert.That(Property<ulong>(assembler, "InstalledSequence"), Is.Zero);
            Assert.That(Accept(assembler, Page(bytes, 3, 7, checksum, 0), out completed), Is.True);
            Confirm(assembler, 7, true); Assert.That(Property<ulong>(assembler, "InstalledSequence"), Is.EqualTo(7));
        }

        [Test] public void UnfinishedHighSequenceCannotBlockLowerUninstalledSnapshotStartingAtZero()
        {
            var assembler = Assembler(3); var unfinished = Data(PageBytes * 2, 1); var valid = Data(PageBytes * 2, 2);
            uint highChecksum = Checksum(new ArraySegment<byte>(unfinished)); uint validChecksum = Checksum(new ArraySegment<byte>(valid));
            Assert.That(Accept(assembler, Page(unfinished, 3, 100, highChecksum, 0), out var completed), Is.True);
            Assert.That(completed.Count, Is.Zero); Assert.That(Property<ulong>(assembler, "InstalledSequence"), Is.Zero);
            Assert.That(Property<int>(assembler, "BufferedBytes"), Is.EqualTo(32768));
            // seq100只有首0页，既没有完整字节校验，也没有Run安装；它不能成为拒绝seq7的水位。
            // 非0页依旧不能启新候选，只有合法首0页能整体替换当前唯一staging。
            Assert.That(Accept(assembler, Page(valid, 3, 7, validChecksum, 1), out _), Is.False);
            Assert.That(Accept(assembler, Page(valid, 3, 7, validChecksum, 0), out completed), Is.True);
            Assert.That(completed.Count, Is.Zero); Assert.That(Property<int>(assembler, "BufferedBytes"), Is.EqualTo(32768));
            Assert.That(Accept(assembler, Page(unfinished, 3, 100, highChecksum, 1), out _), Is.False);
            Assert.That(Accept(assembler, Page(valid, 3, 7, validChecksum, 1), out completed), Is.True);
            AssertBytes(valid, completed); Assert.That(Property<int>(assembler, "BufferedBytes"), Is.Zero);
            Assert.That(Property<ulong>(assembler, "InstalledSequence"), Is.Zero);
            Confirm(assembler, 7, true); Assert.That(Property<ulong>(assembler, "InstalledSequence"), Is.EqualTo(7));
            Assert.That(Accept(assembler, Page(valid, 3, 6, validChecksum, 0), out _), Is.False);
            Assert.That(Accept(assembler, Page(valid, 3, 7, validChecksum, 0), out _), Is.False);
        }

        [Test] public void CompletedArraysTransferOwnershipAndAllowNextSnapshotBeforeLateInstallation()
        {
            var assembler = Assembler(3); var first = Data(20, 1); var next = Data(20, 2);
            Assert.That(Accept(assembler, Page(first, 3, 7, Checksum(new ArraySegment<byte>(first)), 0), out var borrowed), Is.True);
            Assert.That(Accept(assembler, Page(next, 3, 8, Checksum(new ArraySegment<byte>(next)), 0), out var completed), Is.True);
            CollectionAssert.AreEqual(first, borrowed); CollectionAssert.AreEqual(next, completed);
            Assert.That(completed.Array, Is.Not.SameAs(borrowed.Array));
            Assert.That(Property<int>(assembler, "BufferedBytes"), Is.Zero);
            Assert.That(Property<ulong>(assembler, "InstalledSequence"), Is.Zero);
            // 同一NetworkUpdate可以完成两包，LateUpdate只安装最新那份；旧数组仍由原调用方持有。
            Confirm(assembler, 8, true); Assert.That(Property<ulong>(assembler, "InstalledSequence"), Is.EqualTo(8));
            Confirm(assembler, 7, true); Assert.That(Property<ulong>(assembler, "InstalledSequence"), Is.EqualTo(8));
            CollectionAssert.AreEqual(first, borrowed);
        }

        [Test] public void ClearReleasesStagingAndKeepsSameEpochInstalledFence()
        {
            var assembler = Assembler(3); var small = Data(20); uint checksum = Checksum(new ArraySegment<byte>(small));
            Assert.That(Accept(assembler, Page(small, 3, 7, checksum, 0), out _), Is.True); Confirm(assembler, 7, true);
            var large = Data(PageBytes + 1); Assert.That(Accept(assembler, Page(large, 3, 8, Checksum(new ArraySegment<byte>(large)), 0), out _), Is.True);
            Call(assembler, "Clear"); Assert.That(Property<int>(assembler, "BufferedBytes"), Is.Zero);
            Assert.That(Property<ulong>(assembler, "InstalledSequence"), Is.EqualTo(7));
            Assert.That(Accept(assembler, Page(small, 3, 7, checksum, 0), out _), Is.False);
            Assert.That(Accept(assembler, Page(small, 4, 8, checksum, 0), out _), Is.False);
            Assert.That(Accept(assembler, Page(small, 3, 8, checksum, 0), out _), Is.True);
        }

        [Test] public void Maximum2048PagesAssembleWithinOneBoundedCandidate()
        {
            var assembler = Assembler(3); var bytes = Data(MaximumBytes); uint checksum = Checksum(new ArraySegment<byte>(bytes));
            ArraySegment<byte> completed = default;
            for (int i = 0; i < 2048; i++)
            {
                Assert.That(Accept(assembler, Page(bytes, 3, 7, checksum, i), out completed), Is.True);
                if (i < 2047) Assert.That(Property<int>(assembler, "BufferedBytes"), Is.EqualTo(MaximumBytes));
            }
            Assert.That(completed.Count, Is.EqualTo(MaximumBytes)); Assert.That(Property<int>(assembler, "BufferedBytes"), Is.Zero);
            AssertBytes(bytes, completed); Confirm(assembler, 7, true);
            Assert.That(Property<int>(assembler, "BufferedBytes"), Is.Zero);
        }

        private object Sender() => Activator.CreateInstance(_senderType);
        private object Assembler(uint epoch) => Activator.CreateInstance(_assemblerType, epoch);
        private bool Queue(object sender, uint epoch, ulong sequence, byte[] bytes) => (bool)Call(sender, "TryQueue", epoch, sequence, bytes);
        private bool Advance(object sender) => (bool)Call(sender, "Advance");
        private bool Peek(object sender, out object page)
        { var args = new object[] { null }; bool ok = (bool)_senderType.GetMethod("TryPeek").Invoke(sender, args); page = args[0]; return ok; }
        private void Confirm(object assembler, ulong sequence, bool installed) => Call(assembler, "Confirm", sequence, installed);
        private bool Accept(object assembler, object page, out ArraySegment<byte> completed)
        { var args = new object[] { page, null }; bool ok = (bool)_assemblerType.GetMethod("TryAccept").Invoke(assembler, args); completed = (ArraySegment<byte>)args[1]; return ok; }
        private bool Valid(object page) => (bool)_pagingType.GetMethod("Valid").Invoke(null, new[] { page });
        private uint Checksum(ArraySegment<byte> bytes) => (uint)_pagingType.GetMethod("Checksum").Invoke(null, new object[] { bytes });
        private bool Create(byte[] bytes, uint epoch, ulong sequence, uint checksum, int index, out object page)
        { var args = new object[] { bytes, epoch, sequence, checksum, index, null }; bool ok = (bool)_pagingType.GetMethod("CreatePage").Invoke(null, args); page = args[5]; return ok; }
        private object Page(byte[] bytes, uint epoch, ulong sequence, uint checksum, int index)
        { Assert.That(Create(bytes, epoch, sequence, checksum, index, out var page), Is.True); return page; }
        private object Manual(int total, int index, int count, uint checksum, byte[] bytes, int offset = 0, int length = -1)
            => Activator.CreateInstance(_pageType, 3u, 7UL, total, index, count, checksum, new ArraySegment<byte>(bytes, offset, length < 0 ? bytes.Length - offset : length));
        private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name).GetValue(instance);
        private static T Property<T>(object instance, string name) => (T)instance.GetType().GetProperty(name).GetValue(instance);
        private static object Call(object instance, string method, params object[] args) => instance.GetType().GetMethod(method).Invoke(instance, args);
        private static byte[] Data(int count, int salt = 1)
        { var bytes = new byte[count]; for (int i = 0; i < count; i++) bytes[i] = (byte)(i * 31 + salt); return bytes; }
        private static void AssertBytes(byte[] expected, ArraySegment<byte> actual)
        {
            Assert.That(actual.Count, Is.EqualTo(expected.Length)); int mismatch = -1;
            // 最大20MiB样本不交给非泛型IEnumerable逐byte boxing，测试自身避免数千万临时对象。
            for (int i = 0; i < expected.Length; i++)
                if (expected[i] != actual.Array[actual.Offset + i]) { mismatch = i; break; }
            Assert.That(mismatch, Is.EqualTo(-1), "重组后的字节必须与原完整快照逐字节一致。");
        }
    }
}
