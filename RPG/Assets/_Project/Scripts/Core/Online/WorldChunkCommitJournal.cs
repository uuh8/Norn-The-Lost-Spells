using System;
using System.Collections.Generic;

namespace Game.Core
{
    /// <summary>
    /// 一个原生材料网格内的Chunk地址。不同Domain拥有不同CellSize，不能按World细格换算后混用。
    /// 当前每个Domain只有一个Grid，GridId固定等于Domain；同一Journal的几何配置由其WorldEpoch冻结。
    /// 如果未来同一Domain存在多个Grid，必须显式扩展Key与握手契约，不能让同坐标的两个Grid共用版本。
    /// </summary>
    public readonly struct WorldChunkKey : IEquatable<WorldChunkKey>
    {
        public readonly WorldMaterialDomain Domain;
        public readonly int X, Y, Z;
        public byte GridId => (byte)Domain;

        public WorldChunkKey(WorldMaterialDomain domain, int x, int y, int z)
        { Domain = domain; X = x; Y = y; Z = z; }

        public bool Equals(WorldChunkKey other)
            => Domain == other.Domain && X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is WorldChunkKey other && Equals(other);
        public override int GetHashCode()
        {
            // Hash允许整数自然回绕；相等判断仍比较完整坐标，不把Hash碰撞当成相同Chunk。
            unchecked { return (((int)Domain * 397 ^ X) * 397 ^ Y) * 397 ^ Z; }
        }
        public static bool operator ==(WorldChunkKey left, WorldChunkKey right) => left.Equals(right);
        public static bool operator !=(WorldChunkKey left, WorldChunkKey right) => !left.Equals(right);
    }

    /// <summary>0不表示操作，防止未填充的数组槽被当成一次合法Create。</summary>
    public enum WorldChunkChangeKind : byte { Create = 1, Update = 2, Remove = 3 }

    /// <summary>
    /// 已由材料Owner归并的生命周期意图。调用方只声明操作，不提供可伪造的Generation/Revision。
    /// 同批同Key只允许一次；材料事务成功与否由上层先裁决，本值不包含材料数量或Cell Delta。
    /// </summary>
    public readonly struct WorldChunkChange
    {
        public readonly WorldChunkKey Key;
        public readonly WorldChunkChangeKind Kind;
        public WorldChunkChange(WorldChunkKey key, WorldChunkChangeKind kind) { Key = key; Kind = kind; }
    }

    /// <summary>
    /// 某个地址最后一次提交的生命周期。IsResident=false是仍受订阅/在途引用保护的tombstone，
    /// 不是可被Capture直接复用的空槽；未显式退休时后续Create从此Generation继续。
    /// </summary>
    public readonly struct WorldChunkVersionState
    {
        public readonly WorldChunkKey Key;
        public readonly ulong Generation, Revision;
        public readonly bool IsResident;
        public WorldChunkVersionState(WorldChunkKey key, ulong generation, ulong revision, bool isResident)
        { Key = key; Generation = generation; Revision = revision; IsResident = isResident; }
    }

    /// <summary>
    /// 一个完整提交批次中的单条版本记录。Create包括重建，新代没有旧Revision，BaseRevision为0；
    /// Update/Remove的BaseRevision恰为旧状态Revision。同批记录共享CommitSequence并保持输入顺序。
    /// 这里没有材料数据，读到记录不代表安装材料成功，也不是客户端ACK或规则恢复证明。
    /// </summary>
    public readonly struct WorldChunkCommitRecord
    {
        public readonly ulong CommitSequence;
        public readonly WorldChunkKey Key;
        public readonly ulong Generation, BaseRevision, Revision;
        public readonly bool IsResident;
        public WorldChunkCommitRecord(ulong commitSequence, WorldChunkKey key, ulong generation,
            ulong baseRevision, ulong revision, bool isResident)
        {
            CommitSequence = commitSequence; Key = key; Generation = generation;
            BaseRevision = baseRevision; Revision = revision; IsResident = isResident;
        }
    }

    public enum WorldChunkJournalFailure : byte
    {
        None, InvalidInput, InvalidTransition, DuplicateKey, MetadataCapacity, RecordCapacity,
        InspectionBudget, VersionExhausted, BaselineRequired, DestinationCapacity, InvalidCursor
    }

    /// <summary>
    /// 固定世界的有界Chunk版本账本。仅拥有版本元数据与整批日志，不读取或提交真实材料Store。
    /// 上层必须在世界所属主线程的提交边界调用：本类不加锁、不调用事件、不允许并发修改输入数组。
    ///
    /// 普通提交不会回收Metadata：即使日志早已覆盖，tombstone仍保留旧Generation。
    /// 可信装配层撤销全部订阅/在途引用后，才可单独退休非resident地址，并把Generation下界抬高，
    /// 保证未来忘记过的地址不能回到旧生命。日志仍只淘汰完整CommitSequence；落后读者必须重新Baseline。
    /// 所有数组仅在构造时分配。预检使用复用scratch，检查通过后才统一写metadata、ring和全局序号。
    /// </summary>
    public sealed class WorldChunkCommitJournal
    {
        private readonly WorldChunkVersionState[] _states;
        private readonly WorldChunkVersionState[] _preparedStates;
        private readonly WorldChunkCommitRecord[] _preparedRecords;
        private readonly int[] _preparedSlots;
        private readonly WorldChunkCommitRecord[] _records;
        private readonly int _inspectionBudget;
        private readonly Dictionary<WorldChunkKey,int> _indices;
        private readonly HashSet<WorldChunkKey> _batchKeys;
        private int _stateCount, _recordHead, _recordCount;
        // 下界记住所有已退休生命周期。新地址可以共用该值，因为完整身份还包含WorldEpoch和Key；
        // 只有同一Key的旧代不能重用。未退休地址继续原来的Generation+1，不受其他Key退休影响。
        private ulong _generationFloor = 1;
        /// <summary>退休生命周期形成的可信重建下界；检查点只读它，不把历史空地址重新放回运行时槽。</summary>
        public ulong RetiredGenerationFloor => _generationFloor;

        public ulong WorldEpoch { get; }
        public ulong CommitSequence { get; private set; }
        /// <summary>
        /// 最后一个已淘汰的完整提交序号，初始0。cursor等于本值仍可读取其后全部记录；
        /// cursor小于本值说明遗漏了至少一批，必须重新建立Baseline。
        /// </summary>
        public ulong HistoryFloorSequence { get; private set; }
        public WorldChunkJournalFailure LastFailure { get; private set; }

        public WorldChunkCommitJournal(ulong worldEpoch, int metadataCapacity, int recordCapacity, int inspectionBudget)
        {
            if (worldEpoch == 0) throw new ArgumentOutOfRangeException(nameof(worldEpoch));
            if (metadataCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(metadataCapacity));
            if (recordCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(recordCapacity));
            if (inspectionBudget <= 0) throw new ArgumentOutOfRangeException(nameof(inspectionBudget));
            WorldEpoch = worldEpoch; _inspectionBudget = inspectionBudget;
            _states = new WorldChunkVersionState[metadataCapacity];
            _indices = new Dictionary<WorldChunkKey,int>(metadataCapacity);
            _batchKeys = new HashSet<WorldChunkKey>(Math.Min(metadataCapacity,recordCapacity));
            _records = new WorldChunkCommitRecord[recordCapacity];
            _preparedStates = new WorldChunkVersionState[recordCapacity];
            _preparedRecords = new WorldChunkCommitRecord[recordCapacity];
            _preparedSlots = new int[recordCapacity];
        }

        /// <summary>
        /// 只提交数组的有效前缀。空前缀不改变任何版本，哪怕全局序号已经耗尽也仍是合法no-op。
        /// 一个批次先检查输入、重复、生命周期、容量、版本耗尽和整批淘汰工作量，再统一提交。
        /// 批内重复与已提交地址分别按完整Key索引，批次和历史覆盖仍在同一个预算内完成。
        /// </summary>
        public bool TryCommit(WorldChunkChange[] changes, int count)
        {
            if (changes == null || count < 0 || count > changes.Length)
                return Reject(WorldChunkJournalFailure.InvalidInput);
            if (count == 0) return Accept();
            if (count > _records.Length) return Reject(WorldChunkJournalFailure.RecordCapacity);
            if (CommitSequence == ulong.MaxValue) return Reject(WorldChunkJournalFailure.VersionExhausted);

            int budget = _inspectionBudget;
            _batchKeys.Clear();
            int newStateCount = _stateCount;
            ulong nextSequence = CommitSequence + 1; // 上面的MaxValue判断保证不会回绕。
            for (int i = 0; i < count; i++)
            {
                if (!Inspect(ref budget)) return false;
                WorldChunkChange change = changes[i];
                if (!ValidKey(change.Key) || !ValidKind(change.Kind))
                    return Reject(WorldChunkJournalFailure.InvalidInput);
                if (!_batchKeys.Add(change.Key)) return Reject(WorldChunkJournalFailure.DuplicateKey);
                bool known = _indices.TryGetValue(change.Key,out int slot);

                ulong generation, baseRevision, revision;
                bool resident = change.Kind != WorldChunkChangeKind.Remove;
                if (!known)
                {
                    if (change.Kind != WorldChunkChangeKind.Create)
                        return Reject(WorldChunkJournalFailure.InvalidTransition);
                    if (newStateCount == _states.Length)
                        return Reject(WorldChunkJournalFailure.MetadataCapacity);
                    // 仅预约连续空槽，不提前修改_stateCount；不同新Key不能预约同一个槽。
                    slot = newStateCount++;
                    generation = _generationFloor; revision = 1; baseRevision = 0;
                }
                else
                {
                    WorldChunkVersionState previous = _states[slot];
                    if (change.Kind == WorldChunkChangeKind.Create)
                    {
                        if (previous.IsResident) return Reject(WorldChunkJournalFailure.InvalidTransition);
                        if (previous.Generation == ulong.MaxValue)
                            return Reject(WorldChunkJournalFailure.VersionExhausted);
                        generation = previous.Generation + 1; baseRevision = 0; revision = 1;
                    }
                    else
                    {
                        if (!previous.IsResident) return Reject(WorldChunkJournalFailure.InvalidTransition);
                        if (previous.Revision == ulong.MaxValue)
                            return Reject(WorldChunkJournalFailure.VersionExhausted);
                        generation = previous.Generation; baseRevision = previous.Revision;
                        revision = previous.Revision + 1;
                    }
                }
                _preparedSlots[i] = slot;
                _preparedStates[i] = new WorldChunkVersionState(change.Key, generation, revision, resident);
                _preparedRecords[i] = new WorldChunkCommitRecord(nextSequence, change.Key,
                    generation, baseRevision, revision, resident);
            }

            // 先在局部变量上规划覆盖。Ring中同批记录连续，因此沿头部同序号整组移除，
            // 不需要额外分配batch对象。预检预算失败时，旧日志和HistoryFloor完全不动。
            int nextHead = _recordHead, nextCount = _recordCount;
            ulong nextFloor = HistoryFloorSequence;
            while (_records.Length - nextCount < count)
            {
                if (!Inspect(ref budget)) return false;
                ulong evictedSequence = _records[nextHead].CommitSequence;
                do
                {
                    nextHead = NextIndex(nextHead); nextCount--;
                    if (nextCount == 0) break;
                    if (!Inspect(ref budget)) return false;
                }
                while (_records[nextHead].CommitSequence == evictedSequence);
                nextFloor = evictedSequence;
            }

            // 已检查的计划只做固定数组赋值；这里不再执行可拒绝的领域操作或调用外部代码。
            // CommitSequence最后发布。所有读取也必须在同一主线程边界进行，不能并发观察中间赋值。
            for (int i = 0; i < count; i++)
            {
                _states[_preparedSlots[i]] = _preparedStates[i];
                if (_preparedSlots[i] >= _stateCount) _indices.Add(_preparedStates[i].Key,_preparedSlots[i]);
                _records[RingIndex(nextHead, nextCount)] = _preparedRecords[i]; nextCount++;
            }
            _stateCount = newStateCount;
            _recordHead = nextHead; _recordCount = nextCount;
            HistoryFloorSequence = nextFloor; CommitSequence = nextSequence;
            return Accept();
        }

        /// <summary>
        /// 可信协调器已关闭该地址所有Peer订阅、排入可靠Leave并撤销在途引用后，显式退休tombstone。
        /// Journal只验证本层的非resident/版本/预算条件，不拥有网络ACK，调用本API本身不授予退休资格。
        /// 退休是独立的已提交metadata操作：不夹进TryCommit预检、不改CommitSequence或历史批次。
        /// 先预检再压缩末槽，并把新地址Generation下界抬到该旧代之后；失败不改地址、下界或历史。
        /// </summary>
        public bool TryRetireInactiveChunk(WorldChunkKey key)
        {
            if (!ValidKey(key)) return Reject(WorldChunkJournalFailure.InvalidInput);
            if (!_indices.TryGetValue(key,out int slot)) return Reject(WorldChunkJournalFailure.InvalidInput);
            WorldChunkVersionState previous = _states[slot];
            if (previous.IsResident) return Reject(WorldChunkJournalFailure.InvalidTransition);
            if (previous.Generation == ulong.MaxValue) return Reject(WorldChunkJournalFailure.VersionExhausted);
            ulong nextFloor = Math.Max(_generationFloor, previous.Generation + 1);

            // 以下仅固定数组和值赋值，不调用外部逻辑。历史中的旧Key/Generation仍是已发生的事实，
            // 保持整批可读；上层payload Owner另行阻止把退休/旧代记录当作当前Delta复制。
            int last = _stateCount - 1;
            _indices.Remove(key);
            if (slot != last) { _states[slot] = _states[last]; _indices[_states[slot].Key]=slot; }
            _states[last] = default;
            _stateCount = last;
            _generationFloor = nextFloor;
            return Accept();
        }

        /// <summary>
        /// 返回已提交状态的值副本，包括tombstone。未见地址返回false且Failure=None；
        /// 预算不足也返回false，但Failure=InspectionBudget，调用方不能把它误当作“可以首次Create”。
        /// Journal不公开live数组，外部不能修改版本或把一个已删除地址当作从未出现。
        /// </summary>
        public bool TryGetState(WorldChunkKey key, out WorldChunkVersionState state)
        {
            state = default;
            if (!ValidKey(key)) return Reject(WorldChunkJournalFailure.InvalidInput);
            if (_indices.TryGetValue(key,out int i))
            {
                state = _states[i]; return Accept();
            }
            LastFailure = WorldChunkJournalFailure.None;
            return false;
        }

        /// <summary>
        /// 复制cursor之后的全部完整批次，保持提交顺序。不保存或自动推进读者cursor：
        /// 上层成功处理相应数据后才能推进；读到纯元数据不授予材料写权或发送ACK的资格。
        /// 先确认历史、容量及读取预算，之后才写caller数组。失败count=0且destination完全不变；
        /// 成功只写有效前缀，保留尾部旧值。Wire分页和Baseline冻结是后续协调器的责任。
        /// </summary>
        public bool TryCopyAfter(ulong lastAppliedCommitSequence, WorldChunkCommitRecord[] destination, out int count)
        {
            count = 0;
            if (destination == null) return Reject(WorldChunkJournalFailure.InvalidInput);
            if (lastAppliedCommitSequence > CommitSequence) return Reject(WorldChunkJournalFailure.InvalidCursor);
            if (lastAppliedCommitSequence < HistoryFloorSequence) return Reject(WorldChunkJournalFailure.BaselineRequired);
            if (lastAppliedCommitSequence == CommitSequence) return Accept();

            int budget = _inspectionBudget, skipped = 0;
            while (skipped < _recordCount)
            {
                if (!Inspect(ref budget)) return false;
                if (_records[RingIndex(_recordHead, skipped)].CommitSequence > lastAppliedCommitSequence) break;
                skipped++;
            }
            int required = _recordCount - skipped;
            if (required > destination.Length) return Reject(WorldChunkJournalFailure.DestinationCapacity);
            // 预先预约接下来每条实际复制的检查量，避免写到一半才发现预算不足。
            if (required > budget) return Reject(WorldChunkJournalFailure.InspectionBudget);
            for (int i = 0; i < required; i++)
                destination[i] = _records[RingIndex(_recordHead, skipped + i)];
            count = required; return Accept();
        }

        private bool Inspect(ref int budget)
        {
            if (budget <= 0) return Reject(WorldChunkJournalFailure.InspectionBudget);
            budget--; return true;
        }
        private bool Accept() { LastFailure = WorldChunkJournalFailure.None; return true; }
        private bool Reject(WorldChunkJournalFailure failure) { LastFailure = failure; return false; }
        private static bool ValidKey(WorldChunkKey key) => (byte)key.Domain <= (byte)WorldMaterialDomain.Cellular;
        private static bool ValidKind(WorldChunkChangeKind kind) => kind == WorldChunkChangeKind.Create
            || kind == WorldChunkChangeKind.Update || kind == WorldChunkChangeKind.Remove;
        private int NextIndex(int index) => index == _records.Length - 1 ? 0 : index + 1;
        private int RingIndex(int head, int offset)
        {
            // offset始终小于capacity；采用减法拆分避免head+offset在极大配置下发生int溢出。
            int untilEnd = _records.Length - head;
            return offset >= untilEnd ? offset - untilEnd : head + offset;
        }
    }
}
