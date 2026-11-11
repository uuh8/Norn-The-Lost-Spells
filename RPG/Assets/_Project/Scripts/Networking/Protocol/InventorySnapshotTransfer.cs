using System;

namespace Game.Networking
{
    /// <summary>
    /// Owner完整快照的一页中立字节合同。Epoch属于当前已认证连接，Sequence属于完整快照；
    /// 单页不是可安装的库存，也不能凭checksum取得身份或Gameplay写权限。
    /// Payload只是发送时借用的只读视图；调用方不得在Sender接管所有权后修改原数组。
    /// </summary>
    public readonly struct InventorySnapshotPage
    {
        public readonly uint Epoch;
        public readonly ulong Sequence;
        public readonly int TotalBytes, Index, Count;
        public readonly uint Checksum;
        public readonly ArraySegment<byte> Payload;

        public InventorySnapshotPage(uint epoch, ulong sequence, int totalBytes, int index, int count,
            uint checksum, ArraySegment<byte> payload)
        {
            Epoch = epoch; Sequence = sequence; TotalBytes = totalBytes; Index = index; Count = count;
            Checksum = checksum; Payload = payload;
        }
    }

    /// <summary>
    /// 固定16KiB可靠页的纯字节规则。完整32MiB预算最多2048页（含有界公开地面投影），不能把总预算误用为Mirror单包上限。
    /// 校验使用与World字节合同相同的FNV1a32，只检测完整字节一致性，不是CRC或认证/加密证明。
    /// </summary>
    public static class InventorySnapshotPaging
    {
        public const int MaxPageBytes = 16 * 1024;
        public const int MaxSnapshotBytes = InventorySnapshotCodec.MaxPayloadBytes;
        public const int MaxPages = 2048;

        public static bool Valid(InventorySnapshotPage page)
        {
            if (page.Epoch == 0 || page.Sequence == 0 || page.TotalBytes <= 0 || page.TotalBytes > MaxSnapshotBytes
                || page.Count <= 0 || page.Count > MaxPages || page.Index < 0 || page.Index >= page.Count
                || page.Payload.Array == null || page.Count != PageCount(page.TotalBytes)) return false;
            int offset = page.Index * MaxPageBytes;
            return page.Payload.Count == Math.Min(MaxPageBytes, page.TotalBytes - offset);
        }

        public static uint Checksum(ArraySegment<byte> bytes)
        {
            uint hash = 2166136261u;
            // 只读取segment自身，保留offset/count；default空segment使用FNV空串初值。
            // unchecked明确32位回绕，不让调用方checked上下文改变协议字节规则。
            for (int i = 0; i < bytes.Count; i++) hash = unchecked((hash ^ bytes.Array[bytes.Offset + i]) * 16777619u);
            return hash;
        }

        public static bool CreatePage(byte[] bytes, uint epoch, ulong sequence, uint checksum, int index,
            out InventorySnapshotPage page)
        {
            page = default;
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxSnapshotBytes || epoch == 0 || sequence == 0) return false;
            int count = PageCount(bytes.Length);
            if (index < 0 || index >= count) return false;
            int offset = index * MaxPageBytes;
            page = new InventorySnapshotPage(epoch, sequence, bytes.Length, index, count, checksum,
                new ArraySegment<byte>(bytes, offset, Math.Min(MaxPageBytes, bytes.Length - offset)));
            // checksum由接管完整数组时计算一次，不在每页创建时重新扫描整份20MiB快照。
            return true;
        }

        internal static int PageCount(int bytes) => (bytes - 1) / MaxPageBytes + 1;
    }

    /// <summary>
    /// Unity主线程上的离散Owner发送队列：固定“当前+最新待传”两个数组所有权槽。
    /// 同Epoch的新快照不打断当前页序列，只替换尚未开传的pending，避免高频变化造成无界积压。
    /// TryPeek成功不代表底层已发送；只有真实Send成功后调用Advance。预算/轮转由上层Channel负责。
    /// </summary>
    public sealed class InventorySnapshotSender
    {
        private byte[] _current, _pending;
        private uint _epoch, _currentChecksum, _pendingChecksum;
        private ulong _latestSequence, _currentSequence, _pendingSequence;
        private int _index;

        public int BufferedCount => (_current == null ? 0 : 1) + (_pending == null ? 0 : 1);
        public int BufferedBytes => (_current == null ? 0 : _current.Length) + (_pending == null ? 0 : _pending.Length);

        /// <summary>
        /// 成功后接管payload独占所有权；调用者不能再写入或复用这个数组，失败则所有权仍归调用者。
        /// 绑定的Epoch只由Clear解除，重连必须先清理旧发送身份，不能把旧连接半包续到新连接。
        /// </summary>
        public bool TryQueue(uint epoch, ulong sequence, byte[] payload)
        {
            if (epoch == 0 || sequence == 0 || payload == null || payload.Length == 0
                || payload.Length > InventorySnapshotPaging.MaxSnapshotBytes
                || _epoch != 0 && epoch != _epoch || sequence <= _latestSequence) return false;
            uint checksum = InventorySnapshotPaging.Checksum(new ArraySegment<byte>(payload));
            _epoch = epoch; _latestSequence = sequence;
            if (_current == null)
            {
                _current = payload; _currentSequence = sequence; _currentChecksum = checksum; _index = 0;
            }
            else
            {
                _pending = payload; _pendingSequence = sequence; _pendingChecksum = checksum;
            }
            return true;
        }

        public bool TryPeek(out InventorySnapshotPage page)
            => InventorySnapshotPaging.CreatePage(_current, _epoch, _currentSequence, _currentChecksum, _index, out page);

        /// <summary>返回本次是否刚发完一份完整快照；若有pending，立即成为下一份current但不额外发送。</summary>
        public bool Advance()
        {
            if (_current == null) return false;
            if (++_index < InventorySnapshotPaging.PageCount(_current.Length)) return false;
            _current = _pending; _currentSequence = _pendingSequence; _currentChecksum = _pendingChecksum; _index = 0;
            _pending = null; _pendingSequence = 0; _pendingChecksum = 0;
            return true;
        }

        public void Clear()
        {
            _current = null; _pending = null; _epoch = 0; _latestSequence = 0;
            _currentSequence = 0; _pendingSequence = 0; _currentChecksum = 0; _pendingChecksum = 0; _index = 0;
        }
    }

    /// <summary>
    /// 固定已认证Epoch的可靠有序流重组器，只拥有一个有界staging。
    /// 首页必须为0，后续按顺序接收；已收页的重复须逐字节相同，不缓存任意乱序页或自行重试。
    /// 完成时把新数组所有权交给调用方并立即释放staging，允许同一帧连续完成多个小快照。
    /// 字节完成与Run安装是两个Gate：只有上层Codec/身份/Source全通过后才Confirm安装水位。
    /// </summary>
    public sealed class InventorySnapshotAssembler
    {
        private readonly uint _expectedEpoch;
        private byte[] _staging;
        private ulong _sequence, _highestCompletedSequence;
        private int _totalBytes, _pageCount, _nextIndex;
        private uint _checksum;

        public ulong InstalledSequence { get; private set; }
        public int BufferedBytes => _staging == null ? 0 : _staging.Length;

        public InventorySnapshotAssembler(uint expectedEpoch)
        {
            if (expectedEpoch == 0) throw new ArgumentOutOfRangeException(nameof(expectedEpoch));
            _expectedEpoch = expectedEpoch;
        }

        public bool TryAccept(InventorySnapshotPage page, out ArraySegment<byte> completed)
        {
            completed = default;
            if (!InventorySnapshotPaging.Valid(page) || page.Epoch != _expectedEpoch || page.Sequence <= InstalledSequence) return false;
            if (_staging == null)
            {
                if (page.Index != 0) return false;
                Start(page);
            }
            else if (page.Sequence != _sequence)
            {
                // active只是未验证候选，序号再高也不能成为拒旧水位，否则未完成的高seq会永久堵住恢复。
                // 任一高于Installed的不同序号首0页都可整体替换；非0页不能开启或切换候选。
                if (page.Index != 0) return false;
                Start(page);
            }
            else if (page.TotalBytes != _totalBytes || page.Count != _pageCount || page.Checksum != _checksum) return false;

            if (page.Index > _nextIndex) return false;
            int offset = page.Index * InventorySnapshotPaging.MaxPageBytes;
            if (page.Index < _nextIndex)
            {
                for (int i = 0; i < page.Payload.Count; i++)
                    if (_staging[offset + i] != page.Payload.Array[page.Payload.Offset + i]) return false;
                return true;
            }

            Buffer.BlockCopy(page.Payload.Array, page.Payload.Offset, _staging, offset, page.Payload.Count);
            if (++_nextIndex < _pageCount) return true;
            if (InventorySnapshotPaging.Checksum(new ArraySegment<byte>(_staging)) != _checksum)
            {
                ResetStaging(); return false;
            }
            // out参数转交数组所有权，后续Start只创建新数组，绝不覆盖此前交给Codec/Inbox的完整字节。
            completed = new ArraySegment<byte>(_staging);
            if (_sequence > _highestCompletedSequence) _highestCompletedSequence = _sequence;
            ResetStaging(); return true;
        }

        /// <summary>
        /// 可信组合层只确认自己确实安装的序号。highestCompleted仅限制伪造确认，不是接收高水位；
        /// Codec/身份/Run拒绝的高序号即使字节正确，也不能阻挡较低但仍高于Installed的新合法快照。
        /// </summary>
        public void Confirm(ulong sequence, bool installed)
        {
            if (!installed || sequence <= InstalledSequence || sequence > _highestCompletedSequence) return;
            InstalledSequence = sequence;
            if (_staging != null && _sequence <= InstalledSequence) ResetStaging();
        }

        /// <summary>释放当前候选和未确认完成资格；同Epoch的已安装水位保留。新连接须创建新Epoch实例。</summary>
        public void Clear()
        {
            ResetStaging(); _highestCompletedSequence = InstalledSequence;
        }

        private void Start(InventorySnapshotPage page)
        {
            // 只为一份实际TotalBytes分配；没有Max20MiB常驻数组，也不按页数创建对象/缓存字典。
            _staging = new byte[page.TotalBytes]; _sequence = page.Sequence; _totalBytes = page.TotalBytes;
            _pageCount = page.Count; _checksum = page.Checksum; _nextIndex = 0;
        }

        private void ResetStaging()
        {
            _staging = null; _sequence = 0; _totalBytes = 0; _pageCount = 0; _nextIndex = 0; _checksum = 0;
        }
    }
}
