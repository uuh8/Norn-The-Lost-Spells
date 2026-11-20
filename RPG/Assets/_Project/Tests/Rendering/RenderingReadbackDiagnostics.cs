using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// 仅测试证据：Editor test-step、PlayerLoop和CPU构建工作量是三个不同的时钟。
    /// 把它们分开记录，避免把Editor idle等待误称GPU耗时，也不把真实deadline失败吞掉。
    /// 每次等待独立写Assets外的新目录，失败finally同样保留证据，不覆盖历史验收。
    /// </summary>
    internal static class RenderingReadbackDiagnostics
    {
        internal sealed class Probe
        {
            private readonly Record _record;
            private readonly double _started;
            internal Probe(string kind)
            {
                _started=EditorApplication.timeSinceStartup;
                _record=new Record{Utc=DateTime.UtcNow.ToString("O"),Kind=kind,
                    Test=TestContext.CurrentContext.Test.FullName,StartFrame=Time.frameCount,
                    StartPaused=EditorApplication.isPaused,StartPlaying=Application.isPlaying,
                    Device=SystemInfo.graphicsDeviceType+" / "+SystemInfo.graphicsDeviceName};
            }
            internal void Poll()=>_record.Polls++;
            internal string State=>"polls="+_record.Polls+",frames="+(Time.frameCount-_record.StartFrame)
                +",paused="+EditorApplication.isPaused+",playing="+Application.isPlaying;
            internal void Save(string requestState)
            {
                _record.ElapsedSeconds=EditorApplication.timeSinceStartup-_started;
                _record.EndFrame=Time.frameCount;_record.EndPaused=EditorApplication.isPaused;
                _record.EndPlaying=Application.isPlaying;_record.RequestState=requestState;
                string directory=Path.Combine("TestResults","ElementPerformance","ReadbackDiagnostics",
                    DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ")+"-"+Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory,_record.Kind+".json"),JsonUtility.ToJson(_record,true));
            }
        }
        [Serializable]
        private sealed class Record
        {
            public string Utc,Kind,Test,Device,RequestState;
            public int Polls,StartFrame,EndFrame;
            public bool StartPaused,StartPlaying,EndPaused,EndPlaying;
            public double ElapsedSeconds;
        }
    }
}
