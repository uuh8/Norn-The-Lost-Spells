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
            var world = OnlineWorldBootstrap.Active;
            var report = new Report { Done = done, Passed = _config.ExpectRejection ? _auth.HandshakeAttempted && !_auth.ClientAuthenticated && !NetworkClient.active : _auth.ClientAuthenticated && _manager.Replies >= 3,
                HandshakeAttempted = _auth.HandshakeAttempted, Authenticated = _auth.ClientAuthenticated, Connected = NetworkClient.isConnected, Replies = _manager.Replies,
                ActorId = identity == null ? 0 : identity.ActorId, ConnectionEpoch = identity == null ? 0 : identity.ConnectionEpoch,
                // 只读记录真实认证握手与场景安装的交界，不能从 Ticket 或测试配置伪造 WorldEpoch。
                AuthenticatedWorldEpoch = _auth.ClientWorldEpoch, WorldEpoch = world != null ? world.WorldEpoch : 0,
                WorldReady = world != null && world.WorldReady, Authority = world != null && world.IsAuthority,
                // 稀疏诊断的值副本：逐Chunk核对实际安装版本/数量payload校验，不把消息已收到当作成功。
                MaterialChunks = world != null ? world.MaterialReplication?.DescribeChunks() : null,
                MaterialInstalled = world != null && world.MaterialReplication != null ? world.MaterialReplication.Installed : 0,
                MaterialFaulted = world != null && world.MaterialReplication != null && world.MaterialReplication.Faulted,
                MaterialRejected=world!=null && world.MaterialReplication!=null?world.MaterialReplication.Rejected:0,
                MaterialResyncRequested=world!=null && world.MaterialReplication!=null?world.MaterialReplication.ResyncRequested:0,
                MaterialBaselines=world!=null && world.MaterialReplication!=null?world.MaterialReplication.BaselineInstalled:0,
                MaterialDeltas=world!=null && world.MaterialReplication!=null?world.MaterialReplication.DeltaInstalled:0,
                MaterialLastFailure=world!=null && world.MaterialReplication!=null?world.MaterialReplication.LastAssemblyFailure.ToString():null,
                MaterialRejectedCandidate=world!=null && world.MaterialReplication!=null?world.MaterialReplication.LastRejectedCandidate:default,
                MaterialRejectedInstalled=world!=null && world.MaterialReplication!=null?world.MaterialReplication.LastRejectedInstalled:default,
                MaterialRejectedPayloadAvailable=world!=null && world.MaterialReplication!=null && world.MaterialReplication.LastRejectedPayloadAvailable,
                RecordedAtUtc = DateTime.UtcNow.ToString("O") };
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
        [Serializable] private sealed class Report { public bool Done, Passed, HandshakeAttempted, Authenticated, Connected; public long Replies; public ulong ActorId; public uint ConnectionEpoch; public string RecordedAtUtc;
            public ulong AuthenticatedWorldEpoch, WorldEpoch; public bool WorldReady, Authority;
            public WorldReplicationChunkStamp[] MaterialChunks; public long MaterialInstalled,MaterialRejected,MaterialResyncRequested,MaterialBaselines,MaterialDeltas;
            public bool MaterialFaulted,MaterialRejectedPayloadAvailable;public string MaterialLastFailure;
            public WorldReplicationChunkStamp MaterialRejectedCandidate,MaterialRejectedInstalled; }
    }
}
