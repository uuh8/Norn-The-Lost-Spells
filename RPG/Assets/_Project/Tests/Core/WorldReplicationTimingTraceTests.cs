using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace Game.Core.Tests
{
    /// <summary>
    /// 验证真正的有界诊断记录器，而不是另写一份模拟实现。
    /// 新类型通过反射访问：生产类型尚未实现时，工程仍可编译，并由明确断言形成 RED。
    /// 反射、装箱及测试辅助数组只存在于测试，不参与正式 Record 的热路径分配结论。
    /// </summary>
    public sealed class WorldReplicationTimingTraceTests
    {
        [Test]
        public void WarmEnabledRecordHasZeroPositivelyCalibratedAllocationEvents()
        {
            // 性能证据不通过反射调用：反射本身会装箱/建参数数组，不能混进Record方法范围。
            var key = new WorldChunkKey(WorldMaterialDomain.LegacyCell, -26, 0, 28);
            var trace = new WorldReplicationTimingTrace(1024, key, 7);
            var header = new WorldChunkPayloadHeader(7, 1, key, 1, 0, 1, 42, 18, 123, true);
            var item = new WorldReplicationTimingEvent { Stage = WorldReplicationTimingStage.Committed, Header = header };
            for (int i = 0; i < 8; i++) Assert.That(trace.Record(item), Is.True);
            using var recorder = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Internal, "GC.Alloc", 128,
                Unity.Profiling.ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
            Assert.That(recorder.Valid, Is.True);
            recorder.Stop(); recorder.Reset(); recorder.Start();
            var positive = new byte[1024]; System.GC.KeepAlive(positive); recorder.Stop();
            int calibration = recorder.Count;
            Assert.That(calibration, Is.GreaterThan(0)); Assert.That(calibration, Is.LessThan(recorder.Capacity));
            bool allRecorded = true; long before = System.Diagnostics.Stopwatch.GetTimestamp();
            recorder.Reset(); recorder.Start();
            for (int i = 0; i < 256; i++) allRecorded &= trace.Record(item);
            recorder.Stop(); long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - before;
            Assert.That(allRecorded, Is.True); Assert.That(trace.Count, Is.EqualTo(264));
            Assert.That(recorder.Count, Is.Zero, "仅预热Record方法范围，不代表整个Native帧或渲染零GC");
            TestContext.WriteLine("Timing Record calibration events=" + calibration + ", measured events=" + recorder.Count +
                ", total Stopwatch ticks/256=" + elapsed + ", frequency=" + System.Diagnostics.Stopwatch.Frequency);
        }
        private Type _traceType, _eventType, _stageType;
        private object _firstOwnedTrace, _secondOwnedTrace;
        private static readonly WorldChunkKey Target = new WorldChunkKey(WorldMaterialDomain.LegacyCell, -26, 0, 28);

        [SetUp]
        public void SetUp()
        {
            _traceType = RequireType("WorldReplicationTimingTrace");
            _eventType = RequireType("WorldReplicationTimingEvent");
            _stageType = RequireType("WorldReplicationTimingStage");
            Assert.That(ReadStatic("Active"), Is.Null, "测试开始前不应有显式诊断Owner；不能覆盖其他记录器。");
        }

        [TearDown]
        public void TearDown()
        {
            // 只解除本用例创建的Owner；不能提供清空一切的生产测试后门。
            if (_traceType == null) return;
            var detach = _traceType.GetMethod("Detach", BindingFlags.Public | BindingFlags.Static);
            if (detach == null) return;
            if (_firstOwnedTrace != null) detach.Invoke(null, new[] { _firstOwnedTrace });
            if (_secondOwnedTrace != null) detach.Invoke(null, new[] { _secondOwnedTrace });
            _firstOwnedTrace = _secondOwnedTrace = null;
        }

        [Test]
        public void ConstructorLeavesDiagnosticsInactiveAndBufferEmpty()
        {
            var trace = Create(3, 17);
            Assert.That(ReadStatic("Active"), Is.Null, "构造缓冲不等于获准挂载，正常进程默认不记录。");
            Assert.That(Read<int>(trace, "Count"), Is.Zero);
            Assert.That(Read<int>(trace, "Capacity"), Is.EqualTo(3));
            Assert.That(Read<long>(trace, "DroppedCount"), Is.Zero);
            Assert.That(Read<WorldChunkKey>(trace, "TargetKey"), Is.EqualTo(Target));
            Assert.That(Read<ulong>(trace, "WorldEpochFilter"), Is.EqualTo(17ul));
        }

        [Test]
        public void FullBufferRejectsFurtherEventsWithoutOverwritingAcceptedPrefix()
        {
            var trace = Create(2, 17);
            Assert.That(Record(trace, Event(Header(17, Target, 31), quantity: 7)), Is.True);
            Assert.That(Record(trace, Event(Header(17, Target, 32), quantity: 9)), Is.True);
            Assert.That(Record(trace, Event(Header(17, Target, 33), quantity: 100)), Is.False);
            Assert.That(Record(trace, Event(Header(17, Target, 34), quantity: 101)), Is.False);
            Assert.That(Read<int>(trace, "Count"), Is.EqualTo(2));
            Assert.That(Read<int>(trace, "Capacity"), Is.EqualTo(2));
            Assert.That(Read<long>(trace, "DroppedCount"), Is.EqualTo(2));
            Assert.That(Read<uint>(At(trace, 0), "Quantity"), Is.EqualTo(7u));
            Assert.That(Read<uint>(At(trace, 1), "Quantity"), Is.EqualTo(9u));
            Assert.That(Read<WorldChunkPayloadHeader>(At(trace, 0), "Header").SnapshotId, Is.EqualTo(31ul));
            Assert.That(Read<WorldChunkPayloadHeader>(At(trace, 1), "Header").SnapshotId, Is.EqualTo(32ul));
        }

        [Test]
        public void KeyFilterIncludesDomainAndEverySignedCoordinate()
        {
            var trace = Create(1);
            var foreign = new[]
            {
                new WorldChunkKey(WorldMaterialDomain.Liquid, -26, 0, 28),
                new WorldChunkKey(WorldMaterialDomain.LegacyCell, 26, 0, 28),
                new WorldChunkKey(WorldMaterialDomain.LegacyCell, -26, -1, 28),
                new WorldChunkKey(WorldMaterialDomain.LegacyCell, -26, 0, 29)
            };
            for (int i = 0; i < foreign.Length; i++)
                Assert.That(Record(trace, Event(Header(17, foreign[i]))), Is.False);
            Assert.That(Record(trace, Event(Header(17, Target))), Is.True);
            // 满后继续遇到别的Key，仍然是过滤而非丢失目标事件，不能抬高溢出计数。
            Assert.That(Record(trace, Event(Header(17, foreign[0]))), Is.False);
            Assert.That(Read<int>(trace, "Count"), Is.EqualTo(1));
            Assert.That(Read<long>(trace, "DroppedCount"), Is.Zero);
            Assert.That(Read<WorldChunkPayloadHeader>(At(trace, 0), "Header").Key, Is.EqualTo(Target));
        }

        [Test]
        public void ZeroEpochFilterAllowsTheTargetKeyInDifferentWorlds()
        {
            var trace = Create(2);
            Assert.That(Record(trace, Event(Header(17, Target))), Is.True);
            Assert.That(Record(trace, Event(Header(18, Target))), Is.True);
            Assert.That(Read<WorldChunkPayloadHeader>(At(trace, 0), "Header").WorldEpoch, Is.EqualTo(17ul));
            Assert.That(Read<WorldChunkPayloadHeader>(At(trace, 1), "Header").WorldEpoch, Is.EqualTo(18ul));
            Assert.That(Read<long>(trace, "DroppedCount"), Is.Zero);
        }

        [Test]
        public void BoundEpochFilterDoesNotConsumeCapacityOrCountFilteredEventsAsDrops()
        {
            var trace = Create(1, 17);
            Assert.That(Record(trace, Event(Header(18, Target))), Is.False);
            Assert.That(Record(trace, Event(Header(17, Target))), Is.True);
            Assert.That(Record(trace, Event(Header(18, Target))), Is.False);
            Assert.That(Read<int>(trace, "Count"), Is.EqualTo(1));
            Assert.That(Read<long>(trace, "DroppedCount"), Is.Zero);
            Assert.That(Read<WorldChunkPayloadHeader>(At(trace, 0), "Header").WorldEpoch, Is.EqualTo(17ul));
        }

        [Test]
        public void HeaderAndObservationFieldsAreCapturedByValueWithoutVersionTruncation()
        {
            var trace = Create(1, 17);
            var header = new WorldChunkPayloadHeader(17, ulong.MaxValue - 3, Target, ulong.MaxValue - 9,
                ulong.MaxValue - 8, ulong.MaxValue - 7, 902, 39, 0xFEDCBA98u, true);
            object value = Event(header, "ResyncRequested", 101);
            Set(value, "ConnectionId", -37);
            Set(value, "ActorId", 0xFEDCBA9876543210ul);
            Set(value, "ConnectionEpoch", uint.MaxValue - 2);
            Set(value, "SubscriptionEpoch", ulong.MaxValue - 4);
            Set(value, "PageIndex", 2); Set(value, "PageCount", 5); Set(value, "Frame", 120);
            Set(value, "Reason", 6); Set(value, "Count0", -3); Set(value, "Count1", 19);
            Set(value, "GameplayTick", uint.MaxValue - 5); Set(value, "PresentationVersion", 53u);
            Set(value, "CellX", -204); Set(value, "CellY", -1); Set(value, "CellZ", 228);
            Set(value, "UnscaledDeltaTime", .125f);
            Set(value, "InstalledGeneration", ulong.MaxValue - 21);
            Set(value, "InstalledRevision", ulong.MaxValue - 20);
            Set(value, "InstalledPresent", true);
            Set(value, "Tokens", 1234.875d);
            Assert.That(Record(trace, value), Is.True);

            // 调用者复用自己的值载体后，已接受事件不能借用它或跟随变化。
            Set(value, "Header", Header(18, Target)); Set(value, "Quantity", 1u);
            Set(value, "InstalledGeneration", 1ul); Set(value, "InstalledPresent", false);
            Set(value, "Tokens", -1d);
            Set(value, "ActorId", 1ul);
            object captured = At(trace, 0);
            Assert.That(Read<WorldChunkPayloadHeader>(captured, "Header"), Is.EqualTo(header));
            Assert.That(Read<object>(captured, "Stage"), Is.EqualTo(Stage("ResyncRequested")));
            Assert.That(Read<int>(captured, "ConnectionId"), Is.EqualTo(-37));
            Assert.That(Read<ulong>(captured, "ActorId"), Is.EqualTo(0xFEDCBA9876543210ul));
            Assert.That(Read<uint>(captured, "ConnectionEpoch"), Is.EqualTo(uint.MaxValue - 2));
            Assert.That(Read<ulong>(captured, "SubscriptionEpoch"), Is.EqualTo(ulong.MaxValue - 4));
            Assert.That(Read<int>(captured, "PageIndex"), Is.EqualTo(2));
            Assert.That(Read<int>(captured, "PageCount"), Is.EqualTo(5));
            Assert.That(Read<int>(captured, "Frame"), Is.EqualTo(120));
            Assert.That(Read<int>(captured, "Reason"), Is.EqualTo(6));
            Assert.That(Read<int>(captured, "Count0"), Is.EqualTo(-3));
            Assert.That(Read<int>(captured, "Count1"), Is.EqualTo(19));
            Assert.That(Read<uint>(captured, "GameplayTick"), Is.EqualTo(uint.MaxValue - 5));
            Assert.That(Read<uint>(captured, "PresentationVersion"), Is.EqualTo(53u));
            Assert.That(Read<int>(captured, "CellX"), Is.EqualTo(-204));
            Assert.That(Read<int>(captured, "CellY"), Is.EqualTo(-1));
            Assert.That(Read<int>(captured, "CellZ"), Is.EqualTo(228));
            Assert.That(Read<uint>(captured, "Quantity"), Is.EqualTo(101u));
            Assert.That(Read<float>(captured, "UnscaledDeltaTime"), Is.EqualTo(.125f));
            Assert.That(Read<ulong>(captured, "InstalledGeneration"), Is.EqualTo(ulong.MaxValue - 21));
            Assert.That(Read<ulong>(captured, "InstalledRevision"), Is.EqualTo(ulong.MaxValue - 20));
            Assert.That(Read<bool>(captured, "InstalledPresent"), Is.True);
            Assert.That(Read<double>(captured, "Tokens"), Is.EqualTo(1234.875d));
        }

        [Test]
        public void RecordUsesItsOwnRealStopwatchTimestampAndAcceptedEventSequence()
        {
            var trace = Create(2);
            object value = Event(Header(17, Target));
            Set(value, "Timestamp", long.MinValue); Set(value, "Sequence", 999L);
            long before = Stopwatch.GetTimestamp();
            Assert.That(Record(trace, value), Is.True);
            long between = Stopwatch.GetTimestamp();
            Assert.That(Record(trace, value), Is.True);
            long after = Stopwatch.GetTimestamp();
            object first = At(trace, 0), second = At(trace, 1);
            Assert.That(Read<long>(first, "Timestamp"), Is.InRange(before, between));
            Assert.That(Read<long>(second, "Timestamp"), Is.InRange(between, after));
            Assert.That(Read<long>(first, "Sequence"), Is.EqualTo(1));
            Assert.That(Read<long>(second, "Sequence"), Is.EqualTo(2));
            Assert.That(Read<long>(value, "Timestamp"), Is.EqualTo(long.MinValue), "in参数不能被改成输出缓冲。");
        }

        [TestCase(-1)]
        [TestCase(1)]
        public void InvalidReadReturnsFalseAndClearsTheOutValue(int index)
        {
            var trace = Create(1);
            Assert.That(Record(trace, Event(Header(17, Target), quantity: 101)), Is.True);
            object[] args = { index, Event(Header(99, Target), quantity: 999) };
            Assert.That((bool)Method("TryRead").Invoke(trace, args), Is.False);
            Assert.That(Read<long>(args[1], "Timestamp"), Is.Zero);
            Assert.That(Read<long>(args[1], "Sequence"), Is.Zero);
            Assert.That(Read<uint>(args[1], "Quantity"), Is.Zero);
            Assert.That(Read<WorldChunkPayloadHeader>(args[1], "Header"), Is.EqualTo(default(WorldChunkPayloadHeader)));
            Assert.That(Read<int>(trace, "Count"), Is.EqualTo(1));
            Assert.That(Read<long>(trace, "DroppedCount"), Is.Zero);
        }

        [Test]
        public void AttachmentRejectsAnotherOwnerAndOldOwnerCannotDetachTheNewTrace()
        {
            object first = Create(1), second = Create(1);
            Assert.That(Attach(first), Is.True);
            Assert.That(Attach(second), Is.False);
            Assert.That(ReadStatic("Active"), Is.SameAs(first));
            Assert.That(Detach(second), Is.False);
            Assert.That(ReadStatic("Active"), Is.SameAs(first));
            Assert.That(Detach(first), Is.True);
            Assert.That(ReadStatic("Active"), Is.Null);
            Assert.That(Attach(second), Is.True);
            Assert.That(Detach(first), Is.False, "旧Scene/Owner退出不能清除后挂载的诊断实例。");
            Assert.That(ReadStatic("Active"), Is.SameAs(second));
            Assert.That(Detach(second), Is.True);
            Assert.That(ReadStatic("Active"), Is.Null);
        }

        [Test]
        public void WindowsCalibrationMapsARealRecordedStopwatchValueIntoAnIndependentQpcInterval()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                Assert.Ignore("此用例核对Windows原生QPC；其他平台不冒称已校准。");
            Assert.That(QueryPerformanceFrequency(out long frequency), Is.True);
            Assert.That(frequency, Is.GreaterThan(0));
            Assert.That(QueryPerformanceCounter(out long outerBefore), Is.True);
            long swBefore = Stopwatch.GetTimestamp();
            var clock = RequireType("WorldReplicationTimingClock");
            var calibrate = clock.GetMethod("Calibrate", BindingFlags.Public | BindingFlags.Static);
            Assert.That(calibrate, Is.Not.Null, "缺少真实时钟校准API。");
            object calibration = calibrate.Invoke(null, null);
            long swAfter = Stopwatch.GetTimestamp();
            Assert.That(QueryPerformanceCounter(out long outerAfter), Is.True);
            Assert.That(Read<bool>(calibration, "WindowsQpcCalibrated"), Is.True);
            Assert.That(Read<long>(calibration, "StopwatchFrequency"), Is.EqualTo(Stopwatch.Frequency));
            Assert.That(Read<bool>(calibration, "IsHighResolution"), Is.EqualTo(Stopwatch.IsHighResolution));
            Assert.That(Read<long>(calibration, "StopwatchTimestamp"), Is.InRange(swBefore, swAfter));
            Assert.That(Read<long>(calibration, "QpcFrequency"), Is.EqualTo(frequency));
            long qpcBefore = Read<long>(calibration, "QpcBefore"), qpcAfter = Read<long>(calibration, "QpcAfter");
            Assert.That(qpcBefore, Is.InRange(outerBefore, outerAfter));
            Assert.That(qpcAfter, Is.InRange(qpcBefore, outerAfter));

            // 原生QPC是独立取样的边界；不能把SW原始tick假定为同一启动原点。
            // decimal只用于一次校准/测试计算，避免大Mono时间戳乘频率时long溢出或double消去低位。
            decimal scaled = (decimal)Read<long>(calibration, "StopwatchTimestamp") * frequency / Stopwatch.Frequency;
            long lower = Read<long>(calibration, "QpcOffsetLowerTicks"), upper = Read<long>(calibration, "QpcOffsetUpperTicks");
            Assert.That(lower, Is.LessThanOrEqualTo(upper));
            Assert.That((decimal)lower, Is.EqualTo(decimal.Floor(qpcBefore - scaled)));
            Assert.That((decimal)upper, Is.EqualTo(decimal.Ceiling(qpcAfter - scaled)));

            var trace = Create(1);
            Assert.That(QueryPerformanceCounter(out long recordBefore), Is.True);
            Assert.That(Record(trace, Event(Header(17, Target))), Is.True);
            Assert.That(QueryPerformanceCounter(out long recordAfter), Is.True);
            decimal actual = (decimal)Read<long>(At(trace, 0), "Timestamp") * frequency / Stopwatch.Frequency;
            Assert.That(actual + lower, Is.LessThanOrEqualTo((decimal)recordAfter));
            Assert.That(actual + upper, Is.GreaterThanOrEqualTo((decimal)recordBefore));
        }

        private static Type RequireType(string name)
        {
            var type = typeof(GameLog).Assembly.GetType("Game.Core." + name);
            if (type == null) Assert.Fail("缺少真实生产类型 Game.Core." + name + "；此为可编译的缺类型RED，不创建空生产实现。");
            return type;
        }

        private object Create(int capacity, ulong worldEpoch = 0)
        {
            object value = Activator.CreateInstance(_traceType, capacity, Target, worldEpoch);
            if (_firstOwnedTrace == null) _firstOwnedTrace = value;
            else if (_secondOwnedTrace == null) _secondOwnedTrace = value;
            else Assert.Fail("测试仅拥有两个诊断Owner，不应泄露未登记实例。");
            return value;
        }

        private static WorldChunkPayloadHeader Header(ulong world, WorldChunkKey key, ulong snapshot = 31)
            => new WorldChunkPayloadHeader(world, snapshot, key, 2, 3, 4, 902, 39, 0x12345678u, true);

        private object Stage(string name) => Enum.Parse(_stageType, name);
        private object Event(WorldChunkPayloadHeader header, string stage = "PageReceived", uint quantity = 7)
        {
            object value = Activator.CreateInstance(_eventType);
            Set(value, "Stage", Stage(stage)); Set(value, "Header", header); Set(value, "Quantity", quantity);
            return value;
        }

        private MethodInfo Method(string name)
        {
            var method = _traceType.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "缺少真实记录器API：" + name);
            return method;
        }
        private bool Record(object trace, object value) => (bool)Method("Record").Invoke(trace, new[] { value });
        private object At(object trace, int index)
        {
            object[] args = { index, Activator.CreateInstance(_eventType) };
            Assert.That((bool)Method("TryRead").Invoke(trace, args), Is.True, "读取已接受的前缀事件应成功。");
            return args[1];
        }
        private bool Attach(object trace) => (bool)_traceType.GetMethod("TryAttach", BindingFlags.Public | BindingFlags.Static).Invoke(null, new[] { trace });
        private bool Detach(object trace) => (bool)_traceType.GetMethod("Detach", BindingFlags.Public | BindingFlags.Static).Invoke(null, new[] { trace });
        private object ReadStatic(string name)
        {
            var property = _traceType.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            Assert.That(property, Is.Not.Null, "缺少只读静态诊断入口：" + name);
            return property.GetValue(null);
        }
        private static T Read<T>(object value, string name)
        {
            var type = value.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field != null) return (T)field.GetValue(value);
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.That(property, Is.Not.Null, "缺少诊断值成员：" + type.Name + "." + name);
            return (T)property.GetValue(value);
        }
        private static void Set(object value, string name, object fieldValue)
        {
            var field = value.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "Event合同要求公开值字段：" + name);
            field.SetValue(value, fieldValue);
        }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryPerformanceCounter(out long value);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryPerformanceFrequency(out long value);
    }
}
