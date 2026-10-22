using System;
using System.Collections.Generic;
using System.IO;
using Game.Core;
using UnityEditor;
using UnityEditorInternal;

namespace Game.Project.Editor
{
    /// <summary>离线读取 Server GC.Alloc 调用栈；只在显式菜单执行，保留并恢复已有 Profiler 缓存。</summary>
    public static class TransportAllocationTraceTools
    {
        [MenuItem("Tools/Norn/Online/Read Packet Loss Allocation Trace")]
        public static void Read() => ReadTrace(false);
        [MenuItem("Tools/Norn/Online/Read Numeric Allocation Trace")]
        public static void ReadNumeric() => ReadTrace(true);
        private static void ReadTrace(bool numeric)
        {
            OnlineSceneSetup.RequireCleanEditor();
            const string root = "Tools/Networking/.artifacts/transport-load";
            string trace = "Builds/OnlineLab/TransportLoad/Server/ProbeReports/" + (numeric ? "numeric" : "loss-numeric") + "/allocations.raw";
            if (!File.Exists(trace)) throw new FileNotFoundException("请先 Capture Packet Loss Allocations 并等候进程退出。", trace);
            bool previous = ProfilerDriver.lastFrameIndex >= ProfilerDriver.firstFrameIndex && ProfilerDriver.lastFrameIndex >= 0;
            bool wasRecording = ProfilerDriver.enabled;
            string backup = root + "/profiler-before.data";
            ProfilerDriver.enabled = false;
            if (previous) ProfilerDriver.SaveProfile(backup);
            try
            {
                if (!ProfilerDriver.LoadProfile(trace, false)) throw new InvalidOperationException("Profiler 未能加载本轮二进制记录。");
                using var output = new StreamWriter(root + (numeric ? "/numeric-allocation-stacks.tsv" : "/loss-allocation-stacks.tsv"));
                output.WriteLine("frame\tthread\tbytes\tstack");
                var addresses = new List<ulong>(128);
                int count = 0;
                for (int frame = ProfilerDriver.firstFrameIndex; frame <= ProfilerDriver.lastFrameIndex; frame++)
                    for (int thread = 0; thread < 64; thread++)
                    {
                        using var view = ProfilerDriver.GetRawFrameDataView(frame, thread);
                        if (!view.valid) break;
                        for (int sample = 0; sample < view.sampleCount; sample++)
                        {
                            if (view.GetSampleName(sample) != "GC.Alloc") continue;
                            addresses.Clear(); view.GetSampleCallstack(sample, addresses);
                            long bytes = view.GetSampleMetadataCount(sample) > 0 ? view.GetSampleMetadataAsLong(sample, 0) : 0;
                            output.Write(frame + "\t" + view.threadName + "\t" + bytes + "\t");
                            for (int a = 0; a < addresses.Count; a++)
                            {
                                var method = view.ResolveMethodInfo(addresses[a]);
                                output.Write(method.methodName + " > ");
                            }
                            output.WriteLine(); count++;
                        }
                    }
                if (count == 0) throw new InvalidOperationException("这次 trace 未捕获 GC.Alloc，不能据此推断分配来源。");
                GameLog.Info("Server GC.Alloc 离线调用栈已导出，事件数 " + count, "OnlineLab");
            }
            finally
            {
                ProfilerDriver.ClearAllFrames();
                if (previous) ProfilerDriver.LoadProfile(backup, false);
                ProfilerDriver.enabled = wasRecording;
            }
        }
    }
}
