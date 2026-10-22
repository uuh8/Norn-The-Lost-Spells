using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Game.Core;
using Game.Multiplayer;
using Game.Networking;
using kcp2k;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Project.Editor
{
    /// <summary>显式场景/构建/五进程实验入口；不接管单机场景，不按进程名终止用户程序。</summary>
    [InitializeOnLoad]
    public static class TransportLoadTools
    {
        private const string ScenePath = "Assets/_Project/Scenes/Tests/TransportLoadLab.unity";
        private const string OptimizedScenePath = "Assets/_Project/Scenes/Tests/TransportAllocationLab.unity";
        private const string AuthScenePath = "Assets/_Project/Scenes/Tests/AuthenticatedTransportLab.unity";
        private const string FixturePath = "Assets/_Project/Tests/Fixtures/Networking/TransportLoad";
        private const string OutputRoot = "Builds/OnlineLab/TransportLoad";
        public const string ReportRoot = "Tools/Networking/.artifacts/transport-load";
        private static readonly string[] Roles = { "server", "client-a", "client-b", "client-c", "client-d" };
        private static readonly Process[] Processes = new Process[5];
        private static readonly DateTime[] StartedUtc = new DateTime[5];
        private static readonly ProbeResult[] Results = new ProbeResult[5];
        private static bool _running, _text, _clientsStarted;
        private static double _nextPoll;
        private static DateTime _deadline;
        private static Summary _summary;
        private static bool _authMode, _encryptedMode, _lossMode, _faultsStarted, _rejectionsComplete;
        private static string _credentialPath, _expectedFingerprint;
        private static bool _profileAllocations;
        private static readonly bool[] FaultDone = new bool[3];
        private static readonly string[] Faults = { "version", "credential", "preauth" };

        static TransportLoadTools()
        {
            EditorApplication.update += Poll;
            AssemblyReloadEvents.beforeAssemblyReload += Interrupt;
            EditorApplication.quitting += Interrupt;
        }

        [MenuItem("Tools/Norn/Online/Create Transport Load Lab")]
        public static void CreateLab() => CreateLabAt(ScenePath, false);

        [MenuItem("Tools/Norn/Online/Create Optimized Transport Lab")]
        public static void CreateOptimizedLab() => CreateLabAt(OptimizedScenePath, true);

        [MenuItem("Tools/Norn/Online/Create Authenticated Transport Lab")]
        public static void CreateAuthLab() => CreateLabAt(AuthScenePath, true, true);

        private static void CreateLabAt(string scenePath, bool optimized, bool authenticate = false)
        {
            OnlineSceneSetup.RequireCleanEditor();
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) != null)
            { GameLog.Info("负载实验场景已存在，没有覆盖。", "OnlineLab"); return; }
            EnsureFolder(FixturePath);
            var profile = AssetDatabase.LoadAssetAtPath<TransportLoadProfile>(FixturePath + "/Profile.asset");
            if (profile == null)
            {
                if (AssetDatabase.LoadMainAssetAtPath(FixturePath + "/Profile.asset") != null)
                    throw new InvalidOperationException("负载 Profile 路径存在其他资产，拒绝覆盖。");
                profile = ScriptableObject.CreateInstance<TransportLoadProfile>();
                AssetDatabase.CreateAsset(profile, FixturePath + "/Profile.asset");
            }
            Scene original = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(original.path)) throw new InvalidOperationException("先处理当前未命名场景。");
            Scene lab = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(lab);
                var root = new GameObject("Transport Load Root");
                KcpTransport kcp = optimized ? root.AddComponent<NornKcpTransport>() : root.AddComponent<KcpTransport>();
                kcp.DualMode = false;
                var manager = root.AddComponent<NornNetworkManager>();
                manager.transport = kcp;
                manager.autoCreatePlayer = false;
                manager.dontDestroyOnLoad = false;
                manager.editorAutoStart = false;
                manager.headlessStartMode = Mirror.HeadlessStartOptions.DoNothing;
                var probe = root.AddComponent<TransportLoadProbe>();
                if (authenticate)
                {
                    root.AddComponent<LocalLabSnapshotChannel>();
                    manager.authenticator = root.AddComponent<LocalLabAuthenticator>();
                }
                var serialized = new SerializedObject(probe);
                serialized.FindProperty("_profile").objectReferenceValue = profile;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                if (!EditorSceneManager.SaveScene(lab, scenePath)) throw new InvalidOperationException("负载场景保存失败。");
                AssetDatabase.SaveAssets();
                GameLog.Info("负载实验场景已建立；不含 Camera 或 IMGUI。", "OnlineLab");
            }
            finally
            {
                if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
                EditorSceneManager.CloseScene(lab, true);
            }
        }

        [MenuItem("Tools/Norn/Online/Build Transport Load Windows Server")]
        public static void BuildServer() => OnlineBuildTools.Build(true, ScenePath, OutputRoot + "/Server/NornLoadServer.exe", "load-server");

        [MenuItem("Tools/Norn/Online/Build Transport Load Windows Client")]
        public static void BuildClient() => OnlineBuildTools.Build(false, ScenePath, OutputRoot + "/Client/NornLoadClient.exe", "load-client");

        [MenuItem("Tools/Norn/Online/Build Optimized Transport Windows Server")]
        public static void BuildOptimizedServer() => OnlineBuildTools.Build(true, OptimizedScenePath, OutputRoot + "/Server/NornLoadServer.exe", "load-server");

        [MenuItem("Tools/Norn/Online/Build Optimized Transport Windows Client")]
        public static void BuildOptimizedClient() => OnlineBuildTools.Build(false, OptimizedScenePath, OutputRoot + "/Client/NornLoadClient.exe", "load-client");

        [MenuItem("Tools/Norn/Online/Build Authenticated Transport Windows Server")]
        public static void BuildAuthServer() => OnlineBuildTools.Build(true, AuthScenePath, OutputRoot + "/Server/NornLoadServer.exe", "load-server");
        [MenuItem("Tools/Norn/Online/Build Authenticated Transport Windows Client")]
        public static void BuildAuthClient() => OnlineBuildTools.Build(false, AuthScenePath, OutputRoot + "/Client/NornLoadClient.exe", "load-client");

        [MenuItem("Tools/Norn/Online/Run Transport Load Verification")]
        public static void Run() => RunInternal(false, false, false);
        [MenuItem("Tools/Norn/Online/Run Authenticated Snapshot Verification")]
        public static void RunAuth() => RunInternal(true, false, false);
        [MenuItem("Tools/Norn/Online/Run Encrypted Transport Verification")]
        public static void RunEncrypted() => RunInternal(false, true, false);
        [MenuItem("Tools/Norn/Online/Run Packet Loss Verification")]
        public static void RunLoss() => RunInternal(false, false, true);
        [MenuItem("Tools/Norn/Online/Capture Packet Loss Allocations")]
        public static void CaptureLoss()
        { _profileAllocations = true; RunInternal(false, false, true); }
        [MenuItem("Tools/Norn/Online/Capture Numeric Transport Allocations")]
        public static void CaptureNumeric()
        { _profileAllocations = true; RunInternal(false, false, false); }
        private static void RunInternal(bool authenticate, bool encrypted, bool loss)
        {
            OnlineSceneSetup.RequireCleanEditor();
            if (_running) throw new InvalidOperationException("负载实验正在运行。");
            if (!File.Exists(ExePath(0)) || !File.Exists(ExePath(1))) throw new InvalidOperationException("先构建负载 Server 和 Client。");
            _summary = new Summary { State = "Running" };
            _authMode = authenticate;
            _encryptedMode = encrypted;
            _lossMode = loss;
            _expectedFingerprint = null;
            _text = false;
            _running = true;
            WriteSummary();
            if (_authMode)
            {
                _credentialPath = Path.GetFullPath(Path.Combine(ReportRoot, ".session-credential.bin"));
                byte[] credential = new byte[32];
                using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(credential);
                File.WriteAllBytes(_credentialPath, credential);
            }
            try { StartRound(); }
            catch (Exception exception) { Fail(exception); }
        }

        private static void StartRound()
        {
            _clientsStarted = false;
            _faultsStarted = _rejectionsComplete = false;
            Array.Clear(FaultDone, 0, FaultDone.Length);
            _deadline = DateTime.UtcNow.AddSeconds(75);
            Array.Clear(Results, 0, Results.Length);
            Directory.CreateDirectory(Path.Combine(ReportRoot, CaseName));
            StartProcess(0);
        }

        private static void StartProcess(int index, string fault = null)
        {
            string log = Path.GetFullPath(Path.Combine(ReportRoot, CaseName, Roles[index] + (fault != null ? "-" + fault : "") + ".log"));
            StartedUtc[index] = DateTime.UtcNow;
            Processes[index] = Process.Start(new ProcessStartInfo
            {
                FileName = ExePath(index), WorkingDirectory = Path.GetDirectoryName(ExePath(index)),
                UseShellExecute = false, CreateNoWindow = true,
                Arguments = "-batchmode -nographics -nornTransportLoadRole " + Roles[index] +
                    (_text ? " -nornTransportLoadText" : "") +
                    (_authMode ? " -nornTransportLoadAuth -nornLabCredentialFile \"" + _credentialPath + "\"" : "") +
                    (_encryptedMode ? " -nornTransportLoadEncrypted" : "") +
                    (_lossMode ? " -nornTransportLoadLoss" : "") +
                    (_profileAllocations && index == 0 ? " -nornProfileAllocations" : "") +
                    (_encryptedMode && index > 0 ? " -nornExpectedFingerprint \"" + _expectedFingerprint + "\"" : "") +
                    (fault == "fingerprint" ? " -nornWrongFingerprint" : fault != null ? " -nornLabAuthFault " + fault : "") + " -logFile \"" + log + "\""
            });
            if (Processes[index] == null) throw new InvalidOperationException("无法启动 " + Roles[index]);
        }

        private static void Poll()
        {
            if (!_running || EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + .25d;
            try
            {
                if (DateTime.UtcNow > _deadline) throw new TimeoutException("负载轮次超时，清理本工具子进程。");
                if (!_clientsStarted)
                {
                    if (Processes[0].HasExited) throw new InvalidOperationException("Server 在 Ready 前退出。");
                    string ready = Path.Combine(Path.GetDirectoryName(ExePath(0)), "ProbeReports", CaseName, "server-ready.json");
                    if (!IsFresh(ready, 0)) return;
                    if (_encryptedMode && _expectedFingerprint == null)
                    {
                        // 仅信任本工具刚启动的本地 Server 报告；指纹是公钥摘要，不是密钥。
                        var metadata = JsonUtility.FromJson<ReadyMetadata>(File.ReadAllText(ready));
                        if (!metadata.Encrypted || string.IsNullOrEmpty(metadata.EncryptionFingerprint))
                            throw new InvalidOperationException("加密 Server 缺少公钥指纹。");
                        _expectedFingerprint = metadata.EncryptionFingerprint;
                    }
                    if ((_authMode || _encryptedMode) && !_rejectionsComplete)
                    {
                        int faultCount = _authMode ? 3 : 1;
                        if (!_faultsStarted)
                        {
                            for (int i = 1; i <= faultCount; i++) StartProcess(i, FaultAt(i));
                            _faultsStarted = true; return;
                        }
                        bool rejected = true;
                        for (int i = 1; i <= faultCount; i++)
                        {
                            if (FaultDone[i - 1]) continue;
                            if (!Processes[i].HasExited) { rejected = false; continue; }
                            int code = Processes[i].ExitCode; Processes[i].Dispose(); Processes[i] = null;
                            string source = Path.Combine(Path.GetDirectoryName(ExePath(i)), "ProbeReports", CaseName, Roles[i] + ".json");
                            if (!IsFresh(source, i)) throw new InvalidOperationException("负向认证缺少新报告。");
                            string json = File.ReadAllText(source);
                            File.WriteAllText(Path.Combine(ReportRoot, CaseName, "rejected-" + FaultAt(i) + ".json"), json);
                            var result = JsonUtility.FromJson<ProbeResult>(json);
                            if (code != 0 || result == null || !result.FunctionalPassed ||
                                (_authMode ? !result.ExpectedAuthRejection : !result.ExpectedEncryptionRejection) ||
                                result.Authenticated || result.ReceivedSnapshots != 0) throw new InvalidOperationException("负向连接未按预期拒绝：" + FaultAt(i));
                            FaultDone[i - 1] = true;
                        }
                        if (!rejected) return;
                        _rejectionsComplete = _summary.RejectionsPassed = true;
                    }
                    for (int i = 1; i < Processes.Length; i++) StartProcess(i);
                    _clientsStarted = true;
                }
                bool allFinished = true;
                for (int i = 0; i < Processes.Length; i++)
                {
                    if (Results[i] != null) continue;
                    if (!Processes[i].HasExited) { allFinished = false; continue; }
                    int code = Processes[i].ExitCode;
                    Processes[i].Dispose(); Processes[i] = null;
                    string source = Path.Combine(Path.GetDirectoryName(ExePath(i)), "ProbeReports", CaseName, Roles[i] + ".json");
                    if (!IsFresh(source, i)) throw new InvalidOperationException(Roles[i] + " 没有本轮报告，退出码 " + code);
                    string json = File.ReadAllText(source);
                    File.WriteAllText(Path.Combine(ReportRoot, CaseName, Roles[i] + ".json"), json);
                    Results[i] = JsonUtility.FromJson<ProbeResult>(json);
                    if (code != 0 || Results[i] == null || !Results[i].FunctionalPassed)
                        throw new InvalidOperationException(Roles[i] + " 功能检查失败，退出码 " + code);
                    if (!Results[i].CodecAllocationCounterValid || (_text && Results[i].CodecAllocationEvents <= 0))
                        throw new InvalidOperationException(Roles[i] + " 分配记录器未通过校准或文本正向检查，不能采纳零值。");
                    if (_authMode && i > 0 && (!Results[i].Authenticated || Results[i].ReceivedSnapshots < 80 ||
                        Results[i].InvalidSnapshots != 0 || Results[i].LargestSnapshotBytes < 1 || Results[i].LargestSnapshotBytes > 64))
                        throw new InvalidOperationException(Roles[i] + " 认证后 Snapshot 检查未通过。");
                    if (_encryptedMode && (!Results[i].EncryptionEnabled || (i > 0 && !Results[i].FingerprintValidationEnabled)))
                        throw new InvalidOperationException("加密或指纹校验没有实际启用。");
                    if (_lossMode && (!Results[i].PacketLossEnabled || Results[i].DroppedDatagrams < 1))
                        throw new InvalidOperationException("丢包没有实际注入。");
                }
                if (!allFinished) return;
                // 各进程使用 UTC 报告时间，只比较退出先后，不用不共享起点的 realtime 互相相减。
                DateTime aFinished = ParseUtc(Results[1].RecordedAtUtc);
                for (int i = 2; i < Results.Length; i++)
                {
                    DateTime otherFinished = ParseUtc(Results[i].RecordedAtUtc);
                    if ((otherFinished - aFinished).TotalSeconds < 2 ||
                        (ParseUtc(Results[0].RecordedAtUtc) - otherFinished).TotalSeconds < 1)
                        throw new InvalidOperationException("未证明 A 退出后其他 Client 与 Server 持续运行。");
                }
                bool zero = true;
                for (int i = 0; i < Results.Length; i++) zero &= Results[i].ZeroAllocationPassed;
                if (_authMode)
                {
                    if (Results[0].RejectedVersions < 1 || Results[0].RejectedCredentials < 1 || Results[0].RejectedPreAuthProbes < 1)
                        throw new InvalidOperationException("Server 未记录三种预期拒绝原因。");
                    for (int i = 1; i < Results.Length; i++)
                        for (int j = i + 1; j < Results.Length; j++)
                            if (Results[i].ActorId == 0 || Results[i].ActorId == Results[j].ActorId)
                                throw new InvalidOperationException("认证连接没有独立的服务器 ActorId。");
                    _summary.AuthFunctionalPassed = true; _summary.AuthZeroAllocationPassed = zero;
                    _summary.State = "Finished"; _running = false; DeleteCredential(); WriteSummary();
                    GameLog.Info("认证负向和四 Client 有界 Snapshot 检查完成。", "OnlineLab");
                }
                else if (_encryptedMode)
                {
                    _summary.EncryptedFunctionalPassed = true; _summary.EncryptedZeroAllocationPassed = zero;
                    _summary.State = "Finished"; _running = false; WriteSummary();
                    GameLog.Info("加密指纹负向与四 Client 基线完成；分配结果独立登记。", "OnlineLab");
                }
                else if (_lossMode)
                {
                    _summary.LossFunctionalPassed = true; _summary.LossZeroAllocationPassed = zero;
                    _summary.State = "Finished"; _running = false; WriteSummary();
                    GameLog.Info("双向 5% Datagram 丢弃的四 Client 检查完成。", "OnlineLab");
                }
                else if (_profileAllocations)
                {
                    // 诊断只采数值轮；非空 string 的已知分配会放大调用栈开销，不能拿它污染定位。
                    _summary.NumericFunctionalPassed = true; _summary.NumericZeroAllocationPassed = zero;
                    _summary.State = "Finished"; _running = false; WriteSummary();
                }
                else if (!_text)
                {
                    _summary.NumericFunctionalPassed = true;
                    _summary.NumericZeroAllocationPassed = zero;
                    _text = true;
                    WriteSummary(); StartRound();
                }
                else
                {
                    _summary.TextFunctionalPassed = true;
                    _summary.TextZeroAllocationPassed = zero;
                    _summary.State = "Finished";
                    _running = false;
                    WriteSummary();
                    GameLog.Info("四客户端数值/文本负载功能检查完成；ZeroAllocationPassed 需单独查看。", "OnlineLab");
                }
            }
            catch (Exception exception) { Fail(exception); }
        }

        private static bool IsFresh(string path, int index) => File.Exists(path) && File.GetLastWriteTimeUtc(path) >= StartedUtc[index].AddSeconds(-1);
        private static string FaultAt(int index) => _authMode ? Faults[index - 1] : "fingerprint";
        private static string CaseName => _authMode ? "auth-numeric" : _encryptedMode ? "encrypted-numeric" : _lossMode ? "loss-numeric" : _text ? "text" : "numeric";
        private static string ExePath(int index) => Path.GetFullPath(Path.Combine(OutputRoot,
            index == 0 ? "Server/NornLoadServer.exe" : "Client/NornLoadClient.exe"));
        private static DateTime ParseUtc(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        private static void Fail(Exception exception)
        {
            Cleanup(); _running = false;
            _summary.State = "Failed"; _summary.Error = exception.ToString();
            WriteSummary(); GameLog.Error(exception.Message, "OnlineLab");
        }
        private static void Interrupt()
        {
            if (!_running) return;
            Cleanup(); _running = false;
            _summary.State = "Interrupted"; _summary.Error = "Editor 重载或退出，本轮不能通过。";
            WriteSummary();
        }
        private static void Cleanup()
        {
            for (int i = 0; i < Processes.Length; i++)
            {
                Process process = Processes[i];
                if (process == null) continue;
                try { if (!process.HasExited) process.Kill(); }
                finally { process.Dispose(); Processes[i] = null; }
            }
            DeleteCredential();
        }
        private static void DeleteCredential()
        {
            if (_credentialPath != null && File.Exists(_credentialPath)) File.Delete(_credentialPath);
            _credentialPath = null;
        }
        private static void WriteSummary()
        {
            Directory.CreateDirectory(ReportRoot);
            _summary.RecordedAtUtc = DateTime.UtcNow.ToString("O");
            File.WriteAllText(Path.Combine(ReportRoot, "verification.json"), JsonUtility.ToJson(_summary, true));
            if (_summary.State != "Running") _profileAllocations = false;
        }
        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int split = path.LastIndexOf('/'); EnsureFolder(path.Substring(0, split));
            AssetDatabase.CreateFolder(path.Substring(0, split), path.Substring(split + 1));
        }
        [Serializable] private sealed class ProbeResult
        {
            public bool FunctionalPassed, ZeroAllocationPassed, CodecAllocationCounterValid;
            public string RecordedAtUtc;
            public long CodecAllocationEvents;
            public bool ExpectedAuthRejection, ExpectedEncryptionRejection, Authenticated, EncryptionEnabled, FingerprintValidationEnabled;
            public long ReceivedSnapshots, InvalidSnapshots, RejectedVersions, RejectedCredentials, RejectedPreAuthProbes;
            public int LargestSnapshotBytes;
            public ulong ActorId;
            public bool PacketLossEnabled;
            public long DroppedDatagrams;
        }
        [Serializable] private sealed class Summary
        {
            public string State, Error, RecordedAtUtc;
            public bool NumericFunctionalPassed, TextFunctionalPassed, NumericZeroAllocationPassed, TextZeroAllocationPassed;
            public bool AuthFunctionalPassed, AuthZeroAllocationPassed, EncryptedFunctionalPassed, EncryptedZeroAllocationPassed, RejectionsPassed;
            public bool LossFunctionalPassed, LossZeroAllocationPassed;
        }
        [Serializable] private sealed class ReadyMetadata { public bool Encrypted; public string EncryptionFingerprint; }
    }
}
