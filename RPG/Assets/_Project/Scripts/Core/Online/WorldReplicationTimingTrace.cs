using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Game.Core
{
    /// <summary>
    /// 同一材料版本经过的可观测边界。这里只描述发生了什么，不驱动发送、安装或渲染。
    /// 更细的分支放在事件Reason，避免为了诊断改变正式协议或分裂Gameplay流程。
    /// </summary>
    public enum WorldReplicationTimingStage : byte
    {
        Committed, PagePrepared, PageSent, PageReceived, PageRejected, ClientReceive,
        Installed, ReplySent, ReplyAccepted, ResyncRequested, RendererRead,
        RendererRejected, SeedChosen, SeedUploaded, DrawSubmitted
    }

    /// <summary>
    /// 一条纯值诊断事件。Header保存完整版本身份；连接/订阅与材料Generation各自独立。
    /// Frame、GameplayTick和Header.ServerTick不是统一时钟，不能相减冒充网络延迟。
    /// Timestamp/Sequence由记录器写入；其他字段原样保留，未使用的观测字段可以为0。
    /// </summary>
    public struct WorldReplicationTimingEvent
    {
        public WorldReplicationTimingStage Stage;
        public WorldChunkPayloadHeader Header;
        public long Timestamp, Sequence;
        public int ConnectionId;
        // Actor来自真实认证身份；Client本地connectionId为0时也不能冒充DS连接标识。
        public ulong ActorId;
        public uint ConnectionEpoch;
        public ulong SubscriptionEpoch;
        public int PageIndex, PageCount, Frame, Reason, Count0, Count1;
        public uint GameplayTick, PresentationVersion;
        public int CellX, CellY, CellZ;
        public uint Quantity;
        public float UnscaledDeltaTime;
        // 客户端已安装版本不能塞进int计数槽；完整ulong保留跨Generation及大Revision身份。
        public ulong InstalledGeneration, InstalledRevision;
        public bool InstalledPresent;
        public double Tokens;
    }

    /// <summary>
    /// 单Unity主线程使用的显式诊断Owner：一次预分配，填满前缀后拒绝写入，不覆盖最早事件。
    /// Core不读取ENV、不做IO、不创建MonoBehaviour；启动/导出和生命周期由外层显式装配。
    /// 默认Active为空，调用方必须先取得Active再Record，正常游戏不会自动创建诊断缓冲。
    /// </summary>
    public sealed class WorldReplicationTimingTrace
    {
        private readonly WorldReplicationTimingEvent[] _events;
        public static WorldReplicationTimingTrace Active { get; private set; }
        public int Count { get; private set; }
        public int Capacity => _events.Length;
        public long DroppedCount { get; private set; }
        public WorldChunkKey TargetKey { get; }
        /// <summary>0只表示诊断不限定World；不会授予任何Gameplay或协议身份。</summary>
        public ulong WorldEpochFilter { get; }

        public WorldReplicationTimingTrace(int capacity, WorldChunkKey targetKey, ulong worldEpoch = 0)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _events = new WorldReplicationTimingEvent[capacity];
            TargetKey = targetKey; WorldEpochFilter = worldEpoch;
        }

        /// <summary>
        /// 只允许空闲入口挂载一个Owner；不隐式抢占已有诊断。
        /// 同实例重复挂载也返回false，成功只表示本次确实取得原本空闲的入口。
        /// </summary>
        public static bool TryAttach(WorldReplicationTimingTrace trace)
        {
            if (trace == null || Active != null) return false;
            Active = trace; return true;
        }

        /// <summary>旧场景/旧Owner退出时只能解除自己，不能清除随后安装的新Owner。</summary>
        public static bool Detach(WorldReplicationTimingTrace trace)
        {
            if (trace == null || !ReferenceEquals(Active, trace)) return false;
            Active = null; return true;
        }

        public bool Record(in WorldReplicationTimingEvent value)
        {
            // 先过滤，再计算容量丢失：未观察的Key/World不是本trace丢掉的目标事件。
            if (value.Header.Key != TargetKey ||
                (WorldEpochFilter != 0 && value.Header.WorldEpoch != WorldEpochFilter)) return false;
            if (Count == _events.Length)
            {
                // 诊断计数不应回绕成负数；饱和仅影响极端长运行的计量，不动已记录前缀。
                if (DroppedCount < long.MaxValue) DroppedCount++;
                return false;
            }
            var captured = value;
            captured.Timestamp = Stopwatch.GetTimestamp();
            captured.Sequence = (long)Count + 1;
            _events[Count] = captured;
            Count++;
            return true;
        }

        public bool TryRead(int index, out WorldReplicationTimingEvent value)
        {
            if ((uint)index >= (uint)Count) { value = default; return false; }
            value = _events[index]; return true;
        }
    }

    /// <summary>
    /// 进程启动时的时钟关联值，不是“Stopwatch等于QPC”的假设。
    /// 只有WindowsQpcCalibrated=true时，才可用QPC频率与offset区间换算；失败时SW本地时长仍可用。
    /// 换算关系：QPC约等于SW * QpcFrequency / StopwatchFrequency + offset；offset单位为QPC tick。
    /// 校准只夹住本次取样，不证明以后无漂移；跨进程比较必须保留该区间及采样的不确定度。
    /// </summary>
    public readonly struct WorldReplicationTimingCalibration
    {
        public readonly bool WindowsQpcCalibrated, IsHighResolution;
        public readonly long StopwatchFrequency, StopwatchTimestamp;
        public readonly long QpcFrequency, QpcBefore, QpcAfter;
        public readonly long QpcOffsetLowerTicks, QpcOffsetUpperTicks;

        internal WorldReplicationTimingCalibration(bool calibrated, bool highResolution,
            long stopwatchFrequency, long stopwatchTimestamp, long qpcFrequency = 0,
            long qpcBefore = 0, long qpcAfter = 0, long lower = 0, long upper = 0)
        {
            WindowsQpcCalibrated = calibrated; IsHighResolution = highResolution;
            StopwatchFrequency = stopwatchFrequency; StopwatchTimestamp = stopwatchTimestamp;
            QpcFrequency = qpcFrequency; QpcBefore = qpcBefore; QpcAfter = qpcAfter;
            QpcOffsetLowerTicks = lower; QpcOffsetUpperTicks = upper;
        }
    }

    /// <summary>只在显式诊断启动期调用，Record热路径不P/Invoke、不计算频率换算。</summary>
    public static class WorldReplicationTimingClock
    {
        public static WorldReplicationTimingCalibration Calibrate()
        {
            long frequency = Stopwatch.Frequency, timestamp = Stopwatch.GetTimestamp();
            bool highResolution = Stopwatch.IsHighResolution;
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                return LocalOnly(frequency, timestamp, highResolution);
            try
            {
                if (!QueryPerformanceFrequency(out long qpcFrequency) || qpcFrequency <= 0 || frequency <= 0 ||
                    !QueryPerformanceCounter(out long before)) return LocalOnly(frequency, timestamp, highResolution);
                timestamp = Stopwatch.GetTimestamp();
                if (!QueryPerformanceCounter(out long after) || after < before)
                    return LocalOnly(frequency, timestamp, highResolution);

                // Mono的SW原点可能与QPC完全不同。decimal在一次启动校准中保留大整数低位，
                // 不做long乘法或double大数相消；下界floor/上界ceil保证取整不会缩窄实际夹持区间。
                decimal scaled = (decimal)timestamp * qpcFrequency / frequency;
                long lower = checked((long)decimal.Floor(before - scaled));
                long upper = checked((long)decimal.Ceiling(after - scaled));
                return new WorldReplicationTimingCalibration(true, highResolution, frequency, timestamp,
                    qpcFrequency, before, after, lower, upper);
            }
            // 诊断兼容性失败不能阻止游戏启动，也不能把无效跨进程换算伪装成有效0偏移。
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (BadImageFormatException) { }
            catch (OverflowException) { }
            catch (NotSupportedException) { }
            return LocalOnly(frequency, timestamp, highResolution);
        }

        private static WorldReplicationTimingCalibration LocalOnly(long frequency, long timestamp, bool highResolution)
            => new WorldReplicationTimingCalibration(false, highResolution, frequency, timestamp);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryPerformanceCounter(out long value);
        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryPerformanceFrequency(out long value);
    }
}
