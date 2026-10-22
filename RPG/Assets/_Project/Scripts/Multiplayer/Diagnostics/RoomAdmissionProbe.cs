using System;
using System.IO;
using Game.Networking;
using Game.NetworkProtocol;
using Mirror;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>仅隔离Scene/显式私有配置启用的原生探针；不保存线上密码/Token，不作为正式房间UI。</summary>
    public sealed class RoomAdmissionProbe : MonoBehaviour
    {
        [Serializable] private sealed class Config { public JoinTicketReply Ticket; public string Output; public int DurationSeconds = 90; public bool ExpectRejection; }
        private Config _config;
        private RoomNetworkManager _manager;
        private JoinTicketAuthenticator _auth;
        private double _started, _nextReport;
        private string _label = "Waiting for explicit diagnostic configuration";
        private long _lastReplies = -1;
        private void Start()
        {
#if !UNITY_SERVER
            string path = RoomInstanceBootstrap.Argument("--norn-room-client"); if (path == null || !File.Exists(path)) return;
            _config = JsonUtility.FromJson<Config>(File.ReadAllText(path)); _manager = GetComponent<RoomNetworkManager>(); _auth = GetComponent<JoinTicketAuthenticator>();
            Application.runInBackground = true; Application.targetFrameRate = 60;
            Screen.SetResolution(800, 450, FullScreenMode.Windowed); // 诊断进程只改自身窗口，不保存用户Settings或PlayerPrefs。
            _manager.Configure(_config.Ticket.Port); _auth.ConfigureClient(_config.Ticket); _started = Time.realtimeSinceStartupAsDouble; _manager.StartClient();
#endif
        }
        private void Update()
        {
            if (_config == null || Time.realtimeSinceStartupAsDouble < _nextReport) return;
            _nextReport = Time.realtimeSinceStartupAsDouble + 1;
            bool done = Time.realtimeSinceStartupAsDouble - _started >= Mathf.Clamp(_config.DurationSeconds, 10, 300);
            var identity = _auth.ClientIdentity;
            var report = new Report { Done = done, Passed = _config.ExpectRejection ? _auth.HandshakeAttempted && !_auth.ClientAuthenticated && !NetworkClient.active : _auth.ClientAuthenticated && _manager.Replies >= 3,
                HandshakeAttempted = _auth.HandshakeAttempted, Authenticated = _auth.ClientAuthenticated, Connected = NetworkClient.isConnected, Replies = _manager.Replies,
                ActorId = identity == null ? 0 : identity.ActorId, ConnectionEpoch = identity == null ? 0 : identity.ConnectionEpoch, RecordedAtUtc = DateTime.UtcNow.ToString("O") };
            // 诊断路径的JSON/IO每秒分配，不装入正式场景，不将此探针用于Zero-GC验收。
            Directory.CreateDirectory(Path.GetDirectoryName(_config.Output)); File.WriteAllText(_config.Output, JsonUtility.ToJson(report, true));
            if (done) Application.Quit(report.Passed ? 0 : 1);
        }
#if !UNITY_SERVER
        private void OnGUI()
        {
            if (_config == null) { GUI.Label(new Rect(20, 20, 650, 50), _label); return; }
            if (_lastReplies != _manager.Replies) { _lastReplies = _manager.Replies; _label = "Authenticated: " + _auth.ClientAuthenticated + " | Replies: " + _lastReplies; }
            GUI.Box(new Rect(20, 20, 650, 160), "Norn - Real Account / JoinTicket Admission Lab");
            GUI.Label(new Rect(40, 65, 600, 30), _label);
            GUI.Label(new Rect(40, 100, 600, 30), "Loopback diagnostic only | gameplay actors are N3");
            if (GUI.Button(new Rect(40, 135, 150, 30), "Quit Client")) Application.Quit();
        }
#endif
        [Serializable] private sealed class Report { public bool Done, Passed, HandshakeAttempted, Authenticated, Connected; public long Replies; public ulong ActorId; public uint ConnectionEpoch; public string RecordedAtUtc; }
    }
}
