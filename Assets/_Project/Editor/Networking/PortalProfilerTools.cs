using System;
using System.Collections.Generic;
using System.IO;
using Game.Core;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Game.Project.Editor
{
    /// <summary>显式读取/采集原生Player的Profiler，保留当前Editor数据与连接；不自动分析Editor帧冒充Client。</summary>
    public static class PortalProfilerTools
    {
        private const string Root = "Tools/Networking/.artifacts/n2-profiler";
        [Serializable] private sealed class Target { public int Id; public string Name; }
        [Serializable] private sealed class Targets { public Target[] Items; }
        [MenuItem("Tools/Norn/Online/List Native Player Profilers")]
        public static void List()
        {
            OnlineSceneSetup.RequireCleanEditor(); Directory.CreateDirectory(Root);
            int[] ids = ProfilerDriver.GetAvailableProfilers(); var list = new Target[ids.Length];
            for (int i = 0; i < ids.Length; i++) list[i] = new Target { Id = ids[i], Name = ProfilerDriver.GetConnectionIdentifier(ids[i]) };
            File.WriteAllText(Root + "/targets.json", JsonUtility.ToJson(new Targets { Items = list }, true));
            GameLog.Info("已列出Profiler目标，尚未切换连接或录制。", "OnlineProfiler");
        }
        [Serializable] private sealed class Request { public int TargetId, ProcessId; public long StartTicks; public uint PlayerGuid; }
        [MenuItem("Tools/Norn/Online/Capture Selected Native Allocations")]
        public static void Capture()
        {
            OnlineSceneSetup.RequireCleanEditor(); Directory.CreateDirectory(Root);
            var request = JsonUtility.FromJson<Request>(File.ReadAllText(Root + "/request.json"));
            string name = ProfilerDriver.GetConnectionIdentifier(request.TargetId);
            using (var process = System.Diagnostics.Process.GetProcessById(request.ProcessId))
            {
                string expected = Path.GetFullPath("Builds/OnlineAccounts/Menu/NornMenuClient.exe");
                if (process.StartTime.ToUniversalTime().Ticks != request.StartTicks || !string.Equals(Path.GetFullPath(process.MainModule.FileName), expected, StringComparison.OrdinalIgnoreCase)
                    || name.IndexOf("WindowsPlayer", StringComparison.Ordinal) < 0 || unchecked((uint)request.TargetId) != request.PlayerGuid)
                    throw new InvalidOperationException("Profiler Guid与明确登记的原生Client进程身份不匹配。");
            }
            int previousTarget = ProfilerDriver.connectedProfiler;
            bool previousEnabled = ProfilerDriver.enabled, previousEditor = ProfilerDriver.profileEditor;
            var previousMemory = ProfilerDriver.memoryRecordMode;
            bool previousFrames = ProfilerDriver.lastFrameIndex >= ProfilerDriver.firstFrameIndex && ProfilerDriver.lastFrameIndex >= 0;
            ProfilerDriver.enabled = false;
            if (previousFrames) ProfilerDriver.SaveProfile(Root + "/before.data");
            ProfilerDriver.ClearAllFrames(); ProfilerDriver.connectedProfiler = request.TargetId; ProfilerDriver.profileEditor = false;
            ProfilerDriver.memoryRecordMode = ProfilerMemoryRecordMode.GCAlloc; ProfilerDriver.enabled = true;
            double end = EditorApplication.timeSinceStartup + 8;
            EditorApplication.CallbackFunction poll = null;
            poll = () =>
            {
                if (EditorApplication.timeSinceStartup < end) return;
                EditorApplication.update -= poll; ProfilerDriver.enabled = false;
                try
                {
                    ProfilerDriver.SaveProfile(Root + "/native-capture.data");
                    using var output = new StreamWriter(Root + "/allocations.tsv"); output.WriteLine("frame\tthread\tbytes\tstack");
                    var addresses = new List<ulong>(128); int count = 0;
                    for (int frame = ProfilerDriver.firstFrameIndex; frame <= ProfilerDriver.lastFrameIndex; frame++)
                        for (int thread = 0; thread < 32; thread++)
                        {
                            using var view = ProfilerDriver.GetRawFrameDataView(frame, thread); if (!view.valid) break;
                            for (int sample = 0; sample < view.sampleCount; sample++)
                            {
                                if (view.GetSampleName(sample) != "GC.Alloc") continue;
                                addresses.Clear(); view.GetSampleCallstack(sample, addresses);
                                output.Write(frame + "\t" + view.threadName + "\t" + (view.GetSampleMetadataCount(sample) > 0 ? view.GetSampleMetadataAsLong(sample, 0) : 0) + "\t");
                                foreach (ulong address in addresses) output.Write(view.ResolveMethodInfo(address).methodName + " > ");
                                output.WriteLine(); count++;
                            }
                        }
                    File.WriteAllText(Root + "/capture-complete.json", "{\"Events\":" + count + "}");
                    GameLog.Info("原生GC调用栈采集结束，事件数：" + count, "OnlineProfiler");
                }
                finally
                {
                    ProfilerDriver.ClearAllFrames(); ProfilerDriver.connectedProfiler = previousTarget; ProfilerDriver.profileEditor = previousEditor;
                    ProfilerDriver.memoryRecordMode = previousMemory;
                    if (previousFrames) ProfilerDriver.LoadProfile(Root + "/before.data", false);
                    ProfilerDriver.enabled = previousEnabled;
                }
            };
            EditorApplication.update += poll;
        }
    }
}
