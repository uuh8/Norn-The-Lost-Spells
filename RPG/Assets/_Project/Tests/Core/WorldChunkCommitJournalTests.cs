using System;
using System.Reflection;
using NUnit.Framework;

namespace Game.Core.Tests
{
    /// <summary>
    /// N5 的 Journal 只记录已经提交的 Chunk 生命周期与版本，不伪造材料 Delta。
    /// Reflection 使产品类型缺失时表现为可定位的 RED；只有 ulong 耗尽用例才改内部计数，
    /// 普通生命周期、容量与日志覆盖全部通过生产 API 建立，避免测试自己实现另一份 Journal。
    /// </summary>
    public sealed class WorldChunkCommitJournalTests
    {
        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private Type _journalType, _keyType, _changeType, _kindType, _stateType, _recordType;
        private object _journal;

        [SetUp]
        public void SetUp()
        {
            _journalType = Contract("WorldChunkCommitJournal");
            _keyType = Contract("WorldChunkKey");
            _changeType = Contract("WorldChunkChange");
            _kindType = Contract("WorldChunkChangeKind");
            _stateType = Contract("WorldChunkVersionState");
            _recordType = Contract("WorldChunkCommitRecord");
            _journal = Journal();
        }

        private static Type Contract(string name)
        {
            Type type = typeof(GameLog).Assembly.GetType("Game.Core." + name);
            Assert.That(type, Is.Not.Null, "N5-1C 缺少生产契约 Game.Core." + name);
            return type;
        }

        private object Journal(int metadata = 8, int records = 8, int budget = 4096, ulong epoch = 7)
            => Activator.CreateInstance(_journalType, epoch, metadata, records, budget);

        private object Key(int x = -3, int y = 2, int z = -1, WorldMaterialDomain domain = WorldMaterialDomain.Liquid)
            => Activator.CreateInstance(_keyType, domain, x, y, z);

        private object Change(object key, string kind)
            => Activator.CreateInstance(_changeType, key, Enum.Parse(_kindType, kind));

        private Array Changes(params object[] values)
        {
            Array result = Array.CreateInstance(_changeType, values.Length);
            for (int i = 0; i < values.Length; i++) result.SetValue(values[i], i);
            return result;
        }

        private bool Commit(params object[] changes) => CommitArray(Changes(changes), changes.Length);
        private bool CommitArray(Array changes, int count)
            => (bool)_journalType.GetMethod("TryCommit").Invoke(_journal, new object[] { changes, count });
        private ulong Sequence => (ulong)Read(_journal, "CommitSequence");
        private ulong Floor => (ulong)Read(_journal, "HistoryFloorSequence");
        private string Failure => Read(_journal, "LastFailure").ToString();

        private static object Read(object value, string member)
        {
            Type type = value.GetType();
            FieldInfo field = type.GetField(member, InstanceMembers);
            if (field != null) return field.GetValue(value);
            PropertyInfo property = type.GetProperty(member, InstanceMembers);
            Assert.That(property, Is.Not.Null, type.Name + " 缺成员 " + member);
            return property.GetValue(value);
        }

        private bool Find(object key, out object state)
        {
            object[] arguments = { key, null };
            bool found = (bool)_journalType.GetMethod("TryGetState").Invoke(_journal, arguments);
            state = arguments[1]; return found;
        }

        private void State(object key, ulong generation, ulong revision, bool resident)
        {
            Assert.That(Find(key, out object state), Is.True);
            Assert.That(Read(state, "Key"), Is.EqualTo(key));
            Assert.That(Read(state, "Generation"), Is.EqualTo(generation));
            Assert.That(Read(state, "Revision"), Is.EqualTo(revision));
            Assert.That(Read(state, "IsResident"), Is.EqualTo(resident));
        }

        private bool Copy(ulong after, Array destination, out int count)
        {
            object[] arguments = { after, destination, 0 };
            bool copied = (bool)_journalType.GetMethod("TryCopyAfter").Invoke(_journal, arguments);
            count = (int)arguments[2]; return copied;
        }

        private Array Records(ulong after = 0)
        {
            Array buffer = Array.CreateInstance(_recordType, 64);
            Assert.That(Copy(after, buffer, out int count), Is.True);
            Array result = Array.CreateInstance(_recordType, count);
            Array.Copy(buffer, result, count); return result;
        }

        private void Record(object record, object key, ulong sequence, ulong generation,
            ulong before, ulong revision, bool resident)
        {
            Assert.That(Read(record, "Key"), Is.EqualTo(key));
            Assert.That(Read(record, "CommitSequence"), Is.EqualTo(sequence));
            Assert.That(Read(record, "Generation"), Is.EqualTo(generation));
            Assert.That(Read(record, "BaseRevision"), Is.EqualTo(before));
            Assert.That(Read(record, "Revision"), Is.EqualTo(revision));
            Assert.That(Read(record, "IsResident"), Is.EqualTo(resident));
        }

        private void Unchanged(ulong sequence, ulong floor, Array history)
        {
            Assert.That(Sequence, Is.EqualTo(sequence)); Assert.That(Floor, Is.EqualTo(floor));
            CollectionAssert.AreEqual(history, Records(floor));
        }

        [Test]
        public void ConstructorRejectsZeroEpochAndNonPositiveCapacitiesOrBudget()
        {
            object[][] invalid = {
                new object[] { 0UL, 8, 8, 100 }, new object[] { 7UL, 0, 8, 100 },
                new object[] { 7UL, -1, 8, 100 }, new object[] { 7UL, 8, 0, 100 },
                new object[] { 7UL, 8, -1, 100 }, new object[] { 7UL, 8, 8, 0 },
                new object[] { 7UL, 8, 8, -1 }
            };
            foreach (object[] arguments in invalid)
            {
                var exception = Assert.Throws<TargetInvocationException>(() => Activator.CreateInstance(_journalType, arguments));
                Assert.That(exception.InnerException, Is.InstanceOf<ArgumentOutOfRangeException>());
            }
            Assert.That(Read(_journal, "WorldEpoch"), Is.EqualTo(7UL));
            Assert.That(Sequence, Is.Zero); Assert.That(Floor, Is.Zero);
        }

        [Test]
        public void NativeDomainAndNegativeCoordinatesIdentifyIndependentChunks()
        {
            object liquid = Key(), same = Key(), gas = Key(domain: WorldMaterialDomain.Gas), neighbor = Key(x: -2);
            Assert.That(liquid, Is.EqualTo(same)); Assert.That(liquid, Is.Not.EqualTo(gas));
            Assert.That(Commit(Change(liquid, "Create"), Change(gas, "Create"), Change(neighbor, "Create")), Is.True);
            Assert.That(Commit(Change(same, "Update")), Is.True);
            State(liquid, 1, 2, true); State(gas, 1, 1, true); State(neighbor, 1, 1, true);
        }

        [Test]
        public void FirstResidentStartsAtGenerationOneRevisionOne()
        {
            object key = Key(); Assert.That(Commit(Change(key, "Create")), Is.True);
            Assert.That(Failure, Is.EqualTo("None")); Assert.That(Sequence, Is.EqualTo(1UL));
            State(key, 1, 1, true); Array records = Records(); Assert.That(records.Length, Is.EqualTo(1));
            Record(records.GetValue(0), key, 1, 1, 0, 1, true);
        }

        [Test]
        public void OneBatchUsesOneCommitSequenceAndPreservesInputOrder()
        {
            object first = Key(), second = Key(x: 8), third = Key(y: -9);
            Assert.That(Commit(Change(first, "Create"), Change(second, "Create"), Change(third, "Create")), Is.True);
            Assert.That(Sequence, Is.EqualTo(1UL)); Array records = Records(); Assert.That(records.Length, Is.EqualTo(3));
            Record(records.GetValue(0), first, 1, 1, 0, 1, true);
            Record(records.GetValue(1), second, 1, 1, 0, 1, true);
            Record(records.GetValue(2), third, 1, 1, 0, 1, true);
        }

        [Test]
        public void UpdateKeepsGenerationAndRecordsExactPreviousRevision()
        {
            object key = Key(); Assert.True(Commit(Change(key, "Create"))); Assert.True(Commit(Change(key, "Update")));
            State(key, 1, 2, true); Array records = Records(1);
            Assert.That(records.Length, Is.EqualTo(1)); Record(records.GetValue(0), key, 2, 1, 1, 2, true);
        }

        [Test]
        public void RemoveRetainsAddressAndIncrementsRevisionAsTombstone()
        {
            object key = Key(); Assert.True(Commit(Change(key, "Create"))); Assert.True(Commit(Change(key, "Remove")));
            State(key, 1, 2, false); Record(Records(1).GetValue(0), key, 2, 1, 1, 2, false);
        }

        [Test]
        public void RecreateIncrementsGenerationAndStartsIndependentRevision()
        {
            object key = Key(); Assert.True(Commit(Change(key, "Create"))); Assert.True(Commit(Change(key, "Remove")));
            Assert.True(Commit(Change(key, "Create"))); State(key, 2, 1, true);
            Record(Records(2).GetValue(0), key, 3, 2, 0, 1, true);
        }

        [Test]
        public void InvalidLifecycleOperationsDoNotInventOrAdvanceState()
        {
            object key = Key(); Assert.False(Commit(Change(key, "Update"))); Assert.That(Failure, Is.EqualTo("InvalidTransition"));
            Assert.False(Commit(Change(key, "Remove"))); Assert.False(Find(key, out _)); Assert.That(Sequence, Is.Zero);
            Assert.True(Commit(Change(key, "Create"))); Assert.False(Commit(Change(key, "Create"))); State(key, 1, 1, true);
            Assert.True(Commit(Change(key, "Remove"))); Array old = Records();
            Assert.False(Commit(Change(key, "Remove"))); Assert.That(Failure, Is.EqualTo("InvalidTransition"));
            Assert.False(Commit(Change(key, "Update"))); State(key, 1, 2, false); Unchanged(2, 0, old);
        }

        [Test]
        public void DuplicateKeysAreRejectedBeforeAnyPartOfBatchCommits()
        {
            object key = Key(); Assert.False(Commit(Change(key, "Create"), Change(Key(), "Create")));
            Assert.That(Failure, Is.EqualTo("DuplicateKey")); Assert.That(Sequence, Is.Zero); Assert.False(Find(key, out _));
            Assert.True(Commit(Change(key, "Create"))); Array old = Records();
            Assert.False(Commit(Change(key, "Update"), Change(Key(), "Remove")));
            Assert.That(Failure, Is.EqualTo("DuplicateKey")); State(key, 1, 1, true); Unchanged(1, 0, old);
        }

        [Test]
        public void InvalidArrayCountKindAndDomainPreserveExistingCommit()
        {
            object key = Key(); Assert.True(Commit(Change(key, "Create"))); Array old = Records();
            Array valid = Changes(Change(key, "Update"));
            Assert.False(CommitArray(null, 1)); Assert.That(Failure, Is.EqualTo("InvalidInput"));
            Assert.False(CommitArray(valid, -1)); Assert.That(Failure, Is.EqualTo("InvalidInput"));
            Assert.False(CommitArray(valid, 2)); Assert.That(Failure, Is.EqualTo("InvalidInput"));
            object unknownKind = Activator.CreateInstance(_changeType, key, Enum.ToObject(_kindType, 255));
            Assert.False(Commit(unknownKind)); Assert.That(Failure, Is.EqualTo("InvalidInput"));
            Assert.False(Commit(Change(Key(domain: (WorldMaterialDomain)255), "Create")));
            Assert.That(Failure, Is.EqualTo("InvalidInput")); State(key, 1, 1, true); Unchanged(1, 0, old);
        }

        [Test]
        public void EmptyPrefixIsNoOpAndDoesNotReadUnsubmittedArrayTail()
        {
            object key = Key(); Assert.True(Commit(Change(key, "Create"))); Array old = Records();
            Assert.True(CommitArray(Changes(Change(key, "Create")), 0));
            Assert.That(Failure, Is.EqualTo("None")); State(key, 1, 1, true); Unchanged(1, 0, old);
        }

        [Test]
        public void MetadataCapacityKeepsTombstoneAndRejectsEntireMixedBatch()
        {
            _journal = Journal(metadata: 1); object key = Key(), other = Key(x: 10);
            Assert.True(Commit(Change(key, "Create"))); Assert.True(Commit(Change(key, "Remove"))); Array old = Records();
            Assert.False(Commit(Change(key, "Create"), Change(other, "Create")));
            Assert.That(Failure, Is.EqualTo("MetadataCapacity")); State(key, 1, 2, false);
            Assert.False(Find(other, out _)); Unchanged(2, 0, old);
            Assert.True(Commit(Change(key, "Create"))); State(key, 2, 1, true);
        }

        [Test]
        public void BatchLargerThanRecordCapacityCannotEvictHistoryOrCommitPrefix()
        {
            _journal = Journal(records: 2); object key = Key(); Assert.True(Commit(Change(key, "Create"))); Array old = Records();
            Assert.False(Commit(Change(key, "Update"), Change(Key(x: 4), "Create"), Change(Key(x: 5), "Create")));
            Assert.That(Failure, Is.EqualTo("RecordCapacity")); State(key, 1, 1, true); Unchanged(1, 0, old);
            Assert.False(Find(Key(x: 4), out _)); Assert.False(Find(Key(x: 5), out _));
        }

        [Test]
        public void DuplicateAndLookupInspectionWorkCannotBypassTinyBudget()
        {
            _journal = Journal(metadata: 8, records: 8, budget: 1);
            Assert.False(Commit(Change(Key(x: 1), "Create"), Change(Key(x: 2), "Create"), Change(Key(x: 3), "Create")));
            Assert.That(Failure, Is.EqualTo("InspectionBudget")); Assert.That(Sequence, Is.Zero); Assert.That(Floor, Is.Zero);
            Assert.False(Find(Key(x: 1), out _)); Assert.False(Find(Key(x: 2), out _)); Assert.False(Find(Key(x: 3), out _));
            Assert.That(Records().Length, Is.Zero);
        }

        [Test]
        public void InvalidLastChangeCannotPublishEarlierValidUpdateOrCreate()
        {
            object first = Key(), added = Key(x: 9), absent = Key(x: 10);
            Assert.True(Commit(Change(first, "Create"))); Array old = Records();
            Assert.False(Commit(Change(first, "Update"), Change(added, "Create"), Change(absent, "Remove")));
            Assert.That(Failure, Is.EqualTo("InvalidTransition")); State(first, 1, 1, true);
            Assert.False(Find(added, out _)); Assert.False(Find(absent, out _)); Unchanged(1, 0, old);
            Assert.True(Commit(Change(first, "Update"))); State(first, 1, 2, true);
        }

        private void FillAndEvictFirstTwoRecordBatch()
        {
            _journal = Journal(records: 4);
            Assert.True(Commit(Change(Key(x: 1), "Create"), Change(Key(x: 2), "Create")));
            Assert.True(Commit(Change(Key(x: 3), "Create"), Change(Key(x: 4), "Create")));
            Assert.True(Commit(Change(Key(x: 1), "Update")));
        }

        [Test]
        public void RingEvictsWholeCommitBatchAndRetainsChronologicalCompleteSuccessors()
        {
            FillAndEvictFirstTwoRecordBatch(); Assert.That(Floor, Is.EqualTo(1UL)); Assert.That(Sequence, Is.EqualTo(3UL));
            Array records = Records(1); Assert.That(records.Length, Is.EqualTo(3));
            Record(records.GetValue(0), Key(x: 3), 2, 1, 0, 1, true);
            Record(records.GetValue(1), Key(x: 4), 2, 1, 0, 1, true);
            Record(records.GetValue(2), Key(x: 1), 3, 1, 1, 2, true);
            // 日志淘汰不等于删除地址元数据；稍后重建仍必须继承原Generation。
            State(Key(x: 2), 1, 1, true);
        }

        [Test]
        public void CursorBeforeHistoryFloorRequiresBaselineAndDoesNotOverwriteDestination()
        {
            FillAndEvictFirstTwoRecordBatch(); Array destination = Records(1), old = (Array)destination.Clone();
            Assert.False(Copy(0, destination, out int count)); Assert.That(Failure, Is.EqualTo("BaselineRequired"));
            Assert.That(count, Is.Zero); CollectionAssert.AreEqual(old, destination);
            Assert.True(Copy(Floor, destination, out count)); Assert.That(count, Is.EqualTo(3));
        }

        [Test]
        public void CopyCapacityAndNullFailuresPreserveDestinationAndAllowLaterFullRetry()
        {
            object key = Key(); Assert.True(Commit(Change(key, "Create")));
            Array destination = Records(), old = (Array)destination.Clone(); Assert.True(Commit(Change(key, "Update")));
            Assert.False(Copy(0, destination, out int count)); Assert.That(Failure, Is.EqualTo("DestinationCapacity"));
            Assert.That(count, Is.Zero); CollectionAssert.AreEqual(old, destination);
            Assert.False(Copy(0, null, out count)); Assert.That(Failure, Is.EqualTo("InvalidInput")); Assert.That(count, Is.Zero);
            Array full = Records(); Assert.That(full.Length, Is.EqualTo(2));
            Assert.That(Sequence, Is.EqualTo(2UL)); Assert.That(Floor, Is.Zero);
        }

        [Test]
        public void FutureCursorIsRejectedAndCurrentCursorReturnsNoRecordsWithoutClearingTail()
        {
            Assert.True(Commit(Change(Key(), "Create"))); Array destination = Records(), old = (Array)destination.Clone();
            Assert.False(Copy(2, destination, out int count)); Assert.That(Failure, Is.EqualTo("InvalidCursor"));
            Assert.That(count, Is.Zero); CollectionAssert.AreEqual(old, destination);
            Assert.True(Copy(1, destination, out count)); Assert.That(count, Is.Zero); Assert.That(Failure, Is.EqualTo("None"));
            CollectionAssert.AreEqual(old, destination);
        }

        [Test]
        public void LastRevisionIncrementIsValidButExhaustedChunkRejectsEntireBatch()
        {
            object exhausted = Key(), other = Key(x: 8); Assert.True(Commit(Change(exhausted, "Create"), Change(other, "Create")));
            SeedStateCounters(exhausted, 1, ulong.MaxValue - 1);
            Assert.True(Commit(Change(exhausted, "Update"))); State(exhausted, 1, ulong.MaxValue, true); Array old = Records();
            Assert.False(Commit(Change(other, "Update"), Change(exhausted, "Remove")));
            Assert.That(Failure, Is.EqualTo("VersionExhausted")); State(other, 1, 1, true);
            State(exhausted, 1, ulong.MaxValue, true); Unchanged(2, 0, old);
        }

        [Test]
        public void MaximumGenerationAllowsCurrentLifeUpdatesButForbidsRecreateWrap()
        {
            object key = Key(); Assert.True(Commit(Change(key, "Create"))); SeedStateCounters(key, ulong.MaxValue, 1);
            Assert.True(Commit(Change(key, "Update"))); State(key, ulong.MaxValue, 2, true);
            Assert.True(Commit(Change(key, "Remove"))); State(key, ulong.MaxValue, 3, false); Array old = Records();
            Assert.False(Commit(Change(key, "Create"))); Assert.That(Failure, Is.EqualTo("VersionExhausted"));
            State(key, ulong.MaxValue, 3, false); Unchanged(3, 0, old);
        }

        [Test]
        public void LastCommitSequenceIsValidThenNonEmptyBatchesFailWhileEmptyBatchRemainsNoOp()
        {
            object key = Key(); Assert.True(Commit(Change(key, "Create")));
            SetBoundaryMember(_journal, "CommitSequence", ulong.MaxValue - 1);
            Assert.True(Commit(Change(key, "Update"))); Assert.That(Sequence, Is.EqualTo(ulong.MaxValue)); State(key, 1, 2, true);
            Assert.False(Commit(Change(key, "Remove"))); Assert.That(Failure, Is.EqualTo("VersionExhausted"));
            State(key, 1, 2, true); Assert.That(Sequence, Is.EqualTo(ulong.MaxValue));
            Assert.True(CommitArray(Changes(), 0)); Assert.That(Sequence, Is.EqualTo(ulong.MaxValue));
            Assert.True(Copy(ulong.MaxValue, Array.CreateInstance(_recordType, 0), out int count)); Assert.That(count, Is.Zero);
        }

        /// <summary>
        /// 仅用于达到实际运行无法循环到达的 ulong 边界。先用API创建真实metadata，再改对应槽的计数；
        /// 不调用测试自制的版本运算或提交实现。支持字段与auto-property，避免绑定某个私有数组名称。
        /// </summary>
        private void SeedStateCounters(object key, ulong generation, ulong revision)
        {
            foreach (FieldInfo field in _journalType.GetFields(InstanceMembers))
            {
                if (!field.FieldType.IsArray || field.FieldType.GetElementType() != _stateType) continue;
                var states = (Array)field.GetValue(_journal);
                for (int i = 0; i < states.Length; i++)
                {
                    object original = states.GetValue(i);
                    object state = states.GetValue(i);
                    if (!Equals(Read(state, "Key"), key)) continue;
                    SetBoundaryMember(state, "Generation", generation); SetBoundaryMember(state, "Revision", revision);
                    states.SetValue(state, i);
                    // 同类型数组也可能是预检scratch；用公开查询确认修改的是实际metadata，
                    // 不把碰巧同Key的临时计划误当成已经提交的状态。
                    if (Find(key, out object observed)
                        && (ulong)Read(observed, "Generation") == generation
                        && (ulong)Read(observed, "Revision") == revision) return;
                    states.SetValue(original, i);
                }
            }
            Assert.Fail("ulong边界Fixture需要找到已由生产API创建的WorldChunkVersionState槽。");
        }

        private static void SetBoundaryMember(object value, string member, object replacement)
        {
            Type type = value.GetType();
            string camel = char.ToLowerInvariant(member[0]) + member.Substring(1);
            FieldInfo field = type.GetField(member, InstanceMembers)
                ?? type.GetField("<" + member + ">k__BackingField", InstanceMembers)
                ?? type.GetField("_" + camel, InstanceMembers);
            Assert.That(field, Is.Not.Null, "ulong边界Fixture缺少 " + type.Name + "." + member + " 存储。");
            field.SetValue(value, replacement);
        }
    }
}
