using System;
using System.IO;
using Game.Core;
using Game.Networking;
using Mirror;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Multiplayer
{
    /// <summary>复用真实 Manager/Transport 的进程负载探针；不创建账号、角色、UI 或第二套网络协议。</summary>
    [RequireComponent(typeof(NornNetworkManager))]
    public sealed class TransportLoadProbe : MonoBehaviour
    {
        [SerializeField] private TransportLoadProfile _profile;
        private NornNetworkManager _manager;
        private ProfilerRecorder _gc;
        private string _role;
        private bool _text, _server, _measuring, _finished, _gcFrozen;
        private double _bootAt, _connectedAt, _measureAt, _previousFrameAt;
        private long _startSent, _startReplies, _startServed, _maximumGc;
        private long _steadyTransportEvents, _steadyTransportCalls;
        private bool _transportCounterValid = true;
        private double[] _frameIntervals;
        private int _frames, _steadyFrames;
        private int _priorFrameRate;
        private bool _authEnabled, _expectedRejection;
        private string _authFault, _credentialFile;
        private LocalLabAuthenticator _authenticator;
        private LocalLabSnapshotChannel _snapshots;
        private long _startSnapshots;
        private bool _encrypted, _wrongFingerprint;
        private string _expectedFingerprint;
        private bool _loss;
        private System.Diagnostics.Process _process;
        private TimeSpan _cpuStart;
        private bool _profileAllocations;

        private void Start()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-nornTransportLoadRole" && i + 1 < args.Length) _role = args[i + 1];
                if (args[i] == "-nornTransportLoadText") _text = true;
                if (args[i] == "-nornTransportLoadAuth") _authEnabled = true;
                if (args[i] == "-nornTransportLoadEncrypted") _encrypted = true;
                if (args[i] == "-nornTransportLoadLoss") _loss = true;
                if (args[i] == "-nornProfileAllocations") _profileAllocations = true;
                if (args[i] == "-nornWrongFingerprint") _wrongFingerprint = true;
                if (args[i] == "-nornExpectedFingerprint" && i + 1 < args.Length) _expectedFingerprint = args[i + 1];
                if (args[i] == "-nornLabCredentialFile" && i + 1 < args.Length) _credentialFile = args[i + 1];
                if (args[i] == "-nornLabAuthFault" && i + 1 < args.Length) _authFault = args[i + 1];
            }
            if (_role != "server" && _role != "client-a" && _role != "client-b" && _role != "client-c" && _role != "client-d")
            { enabled = false; return; }
#if UNITY_SERVER
            _server = true;
#endif
            _manager = GetComponent<NornNetworkManager>();
            _priorFrameRate = Application.targetFrameRate;
            if (_profile == null || !_profile.IsValid || _server != (_role == "server") ||
                SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
            { Finish("InvalidConfigurationOrBuildRole"); return; }
            // 数组、连接和消息只在启动分配；普通启动没有显式参数时不会自动联网。
            _frameIntervals = new double[4096];
            _manager.ConfigureLocalLab(_profile.Port, 4, 64, 1f / _profile.RequestsPerSecond, _profile.SendRate,
                _text ? _profile.Text : string.Empty, _profile.RequestsPerSecond * 2, _profileAllocations ? 64 : 8);
            _manager.PrepareCodecMeasurement();
            if (_loss) _manager.ConfigureLocalLoss(true);
            // 同一认证场景也可做纯传输对照；无显式 auth 参数时不让未配置的认证器抢先运行。
            if (!_authEnabled) _manager.authenticator = null;
            if (_authEnabled)
            {
                _authenticator = GetComponent<LocalLabAuthenticator>();
                _snapshots = GetComponent<LocalLabSnapshotChannel>();
                if (_authenticator == null || _snapshots == null || string.IsNullOrEmpty(_credentialFile) ||
                    !File.Exists(_credentialFile) || new FileInfo(_credentialFile).Length != 32 ||
                    (!string.IsNullOrEmpty(_authFault) && _authFault != "version" && _authFault != "credential" && _authFault != "preauth"))
                { Finish("InvalidLocalAuthenticationSetup"); return; }
                _authenticator.Configure(File.ReadAllBytes(_credentialFile), _authFault);
                _manager.authenticator = _authenticator;
            }
            if (_encrypted)
            {
                if (_wrongFingerprint && !string.IsNullOrEmpty(_expectedFingerprint))
                    _expectedFingerprint = (_expectedFingerprint[0] == '0' ? "1" : "0") + _expectedFingerprint.Substring(1);
                _manager.ConfigureLocalEncryption(_expectedFingerprint, _server);
            }
            if (_profileAllocations && _server)
            {
                // Profiler 启动本身会停顿；必须在 Ready/连接前完成，不能在稳态中途开启制造积压。
                // 此诊断轮 Burst 上限 64，仍限流；正常验收始终为 8，不把诊断配置当容量结论。
                UnityEngine.Profiling.Profiler.logFile = Path.ChangeExtension(ReportPath("allocations"), ".raw");
                UnityEngine.Profiling.Profiler.enableBinaryLog = true;
                UnityEngine.Profiling.Profiler.enableAllocationCallstacks = true;
                UnityEngine.Profiling.Profiler.enabled = true;
            }
            if (_server) _manager.StartServer(); else _manager.StartClient();
            Application.targetFrameRate = _profile.FrameRate;
            Application.runInBackground = true;
            _bootAt = Time.realtimeSinceStartupAsDouble;
            if (_server)
            {
                string path = ReportPath("server-ready");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(new Ready { ReadyToConnect = true,
                    Encrypted = _encrypted, EncryptionFingerprint = _manager.EncryptionFingerprint }));
            }
        }

        private void LateUpdate()
        {
            if (_manager == null || _finished || !enabled) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (_wrongFingerprint && !_server)
            {
                if (!NetworkClient.active && !NetworkClient.isConnected && now - _bootAt > 2)
                { _expectedRejection = _manager.ReceivedReplies == 0; Finish(_expectedRejection ? null : "FingerprintValidationBypassed"); }
                else if (now - _bootAt > 20) Finish("FingerprintRejectionTimeout");
                return;
            }
            if (_authEnabled && !_server && !string.IsNullOrEmpty(_authFault))
            {
                if (_authenticator.HandshakeAttempted && !NetworkClient.active && !NetworkClient.isConnected && now - _bootAt > 2)
                { _expectedRejection = !_authenticator.ClientAuthenticated && _snapshots.ReceivedSnapshots == 0; Finish(_expectedRejection ? null : "AuthenticationBypassed"); }
                else if (now - _bootAt > 20) Finish("AuthenticationRejectionTimeout");
                return;
            }
            if (_connectedAt == 0)
            {
                bool ready = _server ? _manager.ConnectedClients == 4 : NetworkClient.isConnected && _manager.ReceivedReplies > 0;
                if (ready) _connectedAt = now;
                else if (now - _bootAt > (_authEnabled || _encrypted ? 45 : 30)) Finish("ConnectionTimeout");
                return;
            }
            if (!_server && !NetworkClient.isConnected) { Finish("UnexpectedDisconnect"); return; }
            if (!_measuring)
            {
                if (now - _connectedAt < _profile.WarmupSeconds) return;
                _measuring = true;
                _measureAt = _previousFrameAt = now;
                _startSent = _manager.SentProbes; _startReplies = _manager.ReceivedReplies; _startServed = _manager.ServedProbes;
                _startSnapshots = _snapshots != null ? _snapshots.ReceivedSnapshots : 0;
                // Process CPU 仅在测量边界取样，不在每帧调用系统属性；覆盖全部线程，不能当作主线程 Tick。
                _process = System.Diagnostics.Process.GetCurrentProcess();
                _cpuStart = _process.TotalProcessorTime;
                _manager.BeginCodecMeasurement();
                _gc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
                return;
            }
            if (_frames == _frameIntervals.Length) { Finish("FrameSampleCapacityExceeded"); return; }
            _frameIntervals[_frames++] = (now - _previousFrameAt) * 1000;
            _previousFrameAt = now;
            // 连接/断开是允许离散分配的生命周期事件；稳态全帧窗口在首次断开前停止，另报功能窗口。
            if (_server && _manager.DisconnectedClients > 0) _gcFrozen = true;
            if (!_gcFrozen && _gc.Valid && _gc.Count > 0)
            {
                _maximumGc = Math.Max(_maximumGc, _gc.LastValue); _steadyFrames++;
                long events = _manager.TransportFrameAllocationEvents;
                _steadyTransportEvents = events < 0 || _steadyTransportEvents < 0 ? -1 : _steadyTransportEvents + events;
                _steadyTransportCalls += _manager.TransportFrameMeasuredCalls;
                _transportCounterValid &= _manager.TransportAllocationCounterValid;
            }
            double duration = _server ? _profile.ServerMeasureSeconds :
                _role == "client-a" ? _profile.ShortMeasureSeconds : _profile.LongMeasureSeconds;
            if (now - _measureAt >= duration) Finish(null);
        }

        private void Finish(string error)
        {
            if (_finished) return;
            _finished = true;
            if (_profileAllocations && _server)
            {
                UnityEngine.Profiling.Profiler.enabled = false;
                UnityEngine.Profiling.Profiler.enableBinaryLog = false;
                UnityEngine.Profiling.Profiler.enableAllocationCallstacks = false;
            }
            double now = Time.realtimeSinceStartupAsDouble;
            double elapsed = _measuring ? now - _measureAt : 0;
            long sent = _manager != null ? _manager.SentProbes - _startSent : 0;
            long replies = _manager != null ? _manager.ReceivedReplies - _startReplies : 0;
            long served = _manager != null ? _manager.ServedProbes - _startServed : 0;
            double replyAge = !_server && _manager != null ? now - _manager.LastReplyAt : 0;
            bool functional = error == null && (_expectedRejection || _measuring && (_server
                ? _manager.MaximumConnectedClients == 4 && _manager.ConnectedClients == 0 && _manager.DisconnectedClients == 4 && served >= 400
                : elapsed > 0 && replies / elapsed >= _profile.RequestsPerSecond * .9 && replyAge < .5 && _manager.SkippedProbes == 0));
            if (_authEnabled && !_server && !_expectedRejection)
                functional &= _authenticator != null && _snapshots != null && _authenticator.ClientAuthenticated && _snapshots.InvalidSnapshots == 0 && elapsed > 0 &&
                    (_snapshots.ReceivedSnapshots - _startSnapshots) / elapsed >= 13.5 && now - _snapshots.LastSnapshotAt < .25;
            if (!functional && error == null) error = "FunctionalAcceptanceFailed";
            if (_frames > 0) Array.Sort(_frameIntervals, 0, _frames); // 仅终止边界排序，不在采样热路径排序/扩容。
            var report = new Report
            {
                FunctionalPassed = functional, Error = error, Role = _role, TextPayload = _text,
                DedicatedServer = _server, GraphicsDevice = SystemInfo.graphicsDeviceType.ToString(),
                UnityVersion = Application.unityVersion, RecordedAtUtc = DateTime.UtcNow.ToString("O"),
                AuthenticationEnabled = _authEnabled, ExpectedAuthRejection = _authEnabled && _expectedRejection,
                EncryptionEnabled = _encrypted, ExpectedEncryptionRejection = _wrongFingerprint && _expectedRejection,
                FingerprintValidationEnabled = _manager != null && _manager.FingerprintValidationEnabled,
                Authenticated = _authEnabled && (_server ? _manager.MaximumConnectedClients == 4 : _authenticator != null && _authenticator.ClientAuthenticated),
                ActorId = _authenticator != null ? _authenticator.ClientActorId : 0,
                RejectedVersions = _authenticator != null ? _authenticator.RejectedVersions : 0,
                RejectedCredentials = _authenticator != null ? _authenticator.RejectedCredentials : 0,
                RejectedPreAuthProbes = _manager != null ? _manager.RejectedPreAuthProbes : 0,
                ReceivedSnapshots = _snapshots != null ? _snapshots.ReceivedSnapshots : 0,
                LargestSnapshotBytes = _snapshots != null ? _snapshots.LargestSnapshotBytes : 0,
                InvalidSnapshots = _snapshots != null ? _snapshots.InvalidSnapshots : 0,
                SnapshotRate = _snapshots != null && elapsed > 0 ? (_snapshots.ReceivedSnapshots - _startSnapshots) / elapsed : 0,
                ElapsedSeconds = elapsed, Sent = sent, Replies = replies, Served = served,
                ReplyRate = elapsed > 0 ? replies / elapsed : 0, LastReplyAgeSeconds = replyAge,
                MaximumConnectedClients = _manager != null ? _manager.MaximumConnectedClients : 0,
                ConnectedClients = _manager != null ? _manager.ConnectedClients : 0,
                DisconnectedClients = _manager != null ? _manager.DisconnectedClients : 0,
                SkippedProbes = _manager != null ? _manager.SkippedProbes : 0,
                CodecOperations = _manager != null ? _manager.CodecOperations : 0,
                CodecAllocationEvents = _manager != null ? _manager.CodecAllocationEvents : -1,
                CodecAllocationCounterValid = _manager != null && _manager.CodecAllocationCounterValid,
                CodecCalibrationEvents = _manager != null ? _manager.CodecCalibrationEvents : 0,
                DotNetControlBytes = _manager != null ? _manager.DotNetControlBytes : -1,
                UsesNativeSocket = _manager != null && _manager.UsesNativeSocket,
                TransportAllocationCounterValid = _transportCounterValid,
                SteadyTransportAllocationEvents = _steadyTransportEvents, SteadyTransportCalls = _steadyTransportCalls,
                PacketLossEnabled = _loss, DroppedDatagrams = _manager != null ? _manager.DroppedDatagrams : 0,
                ProcessCpuMilliseconds = _process != null ? (_process.TotalProcessorTime - _cpuStart).TotalMilliseconds : 0,
                MaximumCodecMicroseconds = _manager != null ? _manager.MaximumCodecMicroseconds : 0,
                FrameGcRecorderValid = _gc.Valid, MaximumSteadyFrameGcBytes = _maximumGc, SteadyFrames = _steadyFrames,
                FrameSamples = _frames, FrameIntervalP95Ms = Percentile(.95), FrameIntervalP99Ms = Percentile(.99)
            };
            report.ZeroAllocationPassed = functional && report.FrameGcRecorderValid && _steadyFrames > 0 &&
                report.CodecOperations > 0 && report.CodecAllocationCounterValid && report.CodecAllocationEvents == 0 &&
                _transportCounterValid && _steadyTransportCalls > 0 && _steadyTransportEvents == 0 && _maximumGc == 0;
            string path = ReportPath(_role);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(report, true));
            _process?.Dispose(); _process = null;
            if (_gc.Valid) _gc.Dispose();
            GameLog.Info(functional ? "负载功能检查完成；分配结果请单独读取报告。" : "负载检查失败：" + error, "OnlineLab");
            Application.Quit(functional ? 0 : 1);
        }

        private double Percentile(double quantile) => _frames > 0 ? _frameIntervals[(int)Math.Ceiling(_frames * quantile) - 1] : 0;
        private string ReportPath(string role) => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ProbeReports",
            _authEnabled ? "auth-numeric" : _encrypted ? "encrypted-numeric" : _loss ? "loss-numeric" : _text ? "text" : "numeric", role + ".json"));
        private void OnDestroy()
        {
            if (_gc.Valid) _gc.Dispose();
            if (_manager != null) Application.targetFrameRate = _priorFrameRate;
        }

        [Serializable] private sealed class Ready { public bool ReadyToConnect, Encrypted; public string EncryptionFingerprint; }
        [Serializable] private sealed class Report
        {
            public bool FunctionalPassed, ZeroAllocationPassed, TextPayload, DedicatedServer, FrameGcRecorderValid, CodecAllocationCounterValid;
            public bool UsesNativeSocket, TransportAllocationCounterValid;
            public bool AuthenticationEnabled, ExpectedAuthRejection, Authenticated;
            public bool EncryptionEnabled, ExpectedEncryptionRejection, FingerprintValidationEnabled;
            public bool PacketLossEnabled;
            public long DroppedDatagrams;
            public double ProcessCpuMilliseconds;
            public ulong ActorId;
            public long ReceivedSnapshots, InvalidSnapshots;
            public long RejectedVersions, RejectedCredentials, RejectedPreAuthProbes;
            public int LargestSnapshotBytes;
            public double SnapshotRate;
            public string Role, Error, GraphicsDevice, UnityVersion, RecordedAtUtc;
            public double ElapsedSeconds, ReplyRate, LastReplyAgeSeconds, MaximumCodecMicroseconds, FrameIntervalP95Ms, FrameIntervalP99Ms;
            public long Sent, Replies, Served, SkippedProbes, CodecOperations, CodecAllocationEvents, CodecCalibrationEvents, DotNetControlBytes, MaximumSteadyFrameGcBytes;
            public long SteadyTransportAllocationEvents, SteadyTransportCalls;
            public int ConnectedClients, MaximumConnectedClients, DisconnectedClients, SteadyFrames, FrameSamples;
        }
    }
}
