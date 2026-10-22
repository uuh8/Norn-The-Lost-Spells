using System;
using System.Globalization;
using System.IO;
using Game.Networking;
using Mirror;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Multiplayer
{
    /// <summary>仅显式传入实验参数才启用的进程验收器；普通启动仍由玩家点击界面连接。</summary>
    [RequireComponent(typeof(NornNetworkManager))]
    public sealed class OnlineLabProbe : MonoBehaviour
    {
        private NornNetworkManager _manager;
        private string _name;
        private float _duration;
        private double _startedAt;
        private bool _started;

        private void Awake()
        {
            _manager = GetComponent<NornNetworkManager>();
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "-nornLabProbeName") _name = args[i + 1];
                if (args[i] == "-nornLabProbeSeconds") float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out _duration);
            }
            // 固定名称也限定报告路径，不能把任意命令行文本当成磁盘路径写入。
            if ((_name != "server-ready" && _name != "client-a" && _name != "client-b") || _duration < 3 || _duration > 60)
                enabled = false;
        }

        private void Update()
        {
            if (!_manager.IsConfigured) return;
#if UNITY_SERVER
            if (NetworkServer.active && _manager.transport.ServerActive())
            {
                WriteReport(true, true, 0);
                enabled = false; // Ready 只写一次；服务进程不会因报告完成而退出。
            }
#else
            if (!_started)
            {
                _started = true;
                _startedAt = Time.realtimeSinceStartupAsDouble;
                _manager.StartClient();
            }
            if (Time.realtimeSinceStartupAsDouble - _startedAt < _duration) return;
            // 后退出的客户端必须在结束前仍收到新回复，不能只凭开头几次成功判断服务器存活。
            bool passed = NetworkClient.isConnected && _manager.ReceivedReplies >= 3 &&
                Time.realtimeSinceStartupAsDouble - _manager.LastReplyAt < 2.5d;
            WriteReport(passed, false, Time.realtimeSinceStartupAsDouble - _startedAt);
            enabled = false;
            Application.Quit(passed ? 0 : 1);
#endif
        }

        private void WriteReport(bool passed, bool server, double elapsed)
        {
            // 这是结束/就绪时的一次离散报告，不在稳态 Update 中持续拼 JSON 或写磁盘。
            string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ProbeReports"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, _name + ".json"), JsonUtility.ToJson(new Report
            {
                Passed = passed, DedicatedServer = server, UnityVersion = Application.unityVersion,
                GraphicsDevice = SystemInfo.graphicsDeviceType.ToString(), NullGraphicsDevice = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null,
                Sent = _manager.SentProbes, Replies = _manager.ReceivedReplies, ElapsedSeconds = elapsed,
                LastReplyAgeSeconds = server ? 0 : Time.realtimeSinceStartupAsDouble - _manager.LastReplyAt,
                RecordedAtUtc = DateTime.UtcNow.ToString("O")
            }, true));
        }

        [Serializable]
        private sealed class Report
        {
            public bool Passed, DedicatedServer, NullGraphicsDevice;
            public string UnityVersion, GraphicsDevice, RecordedAtUtc;
            public long Sent, Replies;
            public double ElapsedSeconds, LastReplyAgeSeconds;
        }
    }
}
