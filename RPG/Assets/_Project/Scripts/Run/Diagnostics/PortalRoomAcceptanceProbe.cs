using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Game.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Profiling;

namespace Game.Run
{
    /// <summary>仅显式Development参数启用：隔离单机文件、定位F门并记录Core事件。绝不代替认证或房间按钮。</summary>
    public sealed partial class PortalRoomAcceptanceProbe : MonoBehaviour
    {
        [Serializable] private sealed class Config
        {
            public string Output, Command, PortalScenePath;
            // 独立只读模式：零样本是关闭；不得复用下面会传送/替换档/发Observe的旧验收流程。
            public bool ObserveMovementOnly;
            public int MovementSamples;
            public float MovementSeconds = 5;
        }
        [Serializable] private sealed class Report
        {
            public string Scene, Phase, RoomId, Status, RecordedAtUtc;
            public bool Authenticated, ChoiceOpen, CanStart, SelfReady, Busy, HubActive;
            public int PortalId, MemberCount;
            public ulong ActorId;
            public uint ConnectionEpoch;
            public long Replies;
            public bool NativeSocket;
            public bool FrameCounterCalibrated, IdleRecorderValid;
            public int IdleFrames;
            public long IdleGcPeakBytes;
            public string CursorLock;
            public bool CursorVisible, Focused;
            public float TimeScale;
            public bool HubLoaded, HubActorAvailable, CameraAvailable;
            public Vector3 HubActorPosition, HubActorForward, CameraPosition, CameraForward;
            public string CameraScene;
            public PortalReport[] HubPortals;
        }
        [Serializable] private sealed class PortalReport
        {
            public string ScenePath;
            public Vector3 Position;
            public float DistanceToActor;
            public bool ActiveInHierarchy;
        }
        private Config _config;
        private bool _authenticated, _choice;
        private OnlineRoomChangedEvent _view;
        private double _next;
        private bool _measuring, _calibrated, _recorderValid;
        private bool _externalSampling;
        private int _idleFrames;
        private long _idlePeak;
        private readonly MemoryFiles _files = new MemoryFiles();
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
#if (DEVELOPMENT_BUILD || UNITY_EDITOR) && !UNITY_SERVER
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--norn-portal-acceptance" && File.Exists(args[i + 1]))
            {
                var root = new GameObject("ExplicitPortalAcceptance"); DontDestroyOnLoad(root);
                var probe = root.AddComponent<PortalRoomAcceptanceProbe>(); probe._config = JsonUtility.FromJson<Config>(File.ReadAllText(args[i + 1])); break;
            }
#endif
        }
        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnScene;
            EventBus<OnlineLobbyChangedEvent>.Subscribe(OnIdentity);
            EventBus<OnlineRoomChangedEvent>.Subscribe(OnRoom);
            EventBus<DungeonEntryChoiceRequestedEvent>.Subscribe(OnChoice);
            EventBus<DungeonEntryChoiceClosedEvent>.Subscribe(OnClosed);
            EventBus<OnlineDiagnosticSamplingEvent>.Subscribe(OnSampling);
            SubscribeMovementObservation();
        }
        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnScene;
            EventBus<OnlineLobbyChangedEvent>.Unsubscribe(OnIdentity);
            EventBus<OnlineRoomChangedEvent>.Unsubscribe(OnRoom);
            EventBus<DungeonEntryChoiceRequestedEvent>.Unsubscribe(OnChoice);
            EventBus<DungeonEntryChoiceClosedEvent>.Unsubscribe(OnClosed);
            EventBus<OnlineDiagnosticSamplingEvent>.Unsubscribe(OnSampling);
            UnsubscribeMovementObservation();
        }
        private void OnSampling(OnlineDiagnosticSamplingEvent e) => _externalSampling = e.Active;
        private void OnIdentity(OnlineLobbyChangedEvent e) => _authenticated = e.Authenticated;
        private void OnRoom(OnlineRoomChangedEvent e) => _view = e;
        private void OnChoice(DungeonEntryChoiceRequestedEvent e) { _choice = true; _view.PortalId = e.PortalId; }
        private void OnClosed(DungeonEntryChoiceClosedEvent e) => _choice = false;
        private void OnScene(Scene scene, LoadSceneMode mode)
        {
            if (_config == null || _config.ObserveMovementOnly || !scene.path.EndsWith("SinglePlayer_Hub.unity", StringComparison.Ordinal)) return;
            foreach (var root in scene.GetRootGameObjects())
            {
                var session = root.GetComponentInChildren<SinglePlayerInventorySession>(true); if (session == null) continue;
                // sceneLoaded早于Start；已有序列化数据只读，通过公共初始化注入MemoryFiles防止修改真实档。
                if (!session.ConfigureDiagnosticMemory(_files)) GameLog.Error("原生Portal验收无法隔离单机文件，停止验收。", "OnlineAcceptance");
            }
        }
        private void Update()
        {
            if (_config == null || _config.ObserveMovementOnly || _measuring || _externalSampling || Time.realtimeSinceStartupAsDouble < _next) return;
            _next = Time.realtimeSinceStartupAsDouble + 1;
            if (File.Exists(_config.Command))
            {
                string command = File.ReadAllText(_config.Command).Trim(); File.Delete(_config.Command);
                if (command == "position-portal") PositionPortal(); // 唯一白名单：只定位，F与UGUI由原生输入执行。
                else if (command == "measure-idle") { StartCoroutine(MeasureIdle()); return; }
            }
            if (_view.PortalId != 0) EventBus<OnlineRoomRequestEvent>.Publish(new OnlineRoomRequestEvent { PortalId = _view.PortalId, Action = OnlineRoomAction.Observe });
            var report = new Report { Scene = SceneManager.GetActiveScene().path, Authenticated = _authenticated, ChoiceOpen = _choice,
                Phase = _view.Phase.ToString(), RoomId = _view.RoomId, Status = _view.Status, CanStart = _view.CanStart, SelfReady = _view.SelfReady, Busy = _view.Busy,
                PortalId = _view.PortalId, MemberCount = _view.Members?.Length ?? 0, ActorId = _view.ActorId, RecordedAtUtc = DateTime.UtcNow.ToString("O"),
                FrameCounterCalibrated = _calibrated, IdleRecorderValid = _recorderValid, IdleFrames = _idleFrames, IdleGcPeakBytes = _idlePeak, ConnectionEpoch = _view.ConnectionEpoch, Replies = _view.Replies, NativeSocket = _view.NativeSocket,
                // 仅显式验收的只读状态，区分门状态已打开与鼠标仍被其他生命周期锁住；不代替真实按钮。
                CursorLock = Cursor.lockState.ToString(), CursorVisible = Cursor.visible, Focused = Application.isFocused, TimeScale = Time.timeScale };
            CaptureNavigation(report);
            File.WriteAllText(_config.Output, JsonUtility.ToJson(report, true)); // 诊断IO有分配，不是零GC测量。
        }
        /// <summary>
        /// 仅在已有1Hz显式诊断采样中读取位姿，不移动角色、不触发Portal，也不创建Camera。
        /// Hub可因换图保留为inactive，故读取其原Session/门；Camera.main则代表当前实际启用的主镜头。
        /// 查找/数组分配仍受Update的测量禁区保护，不能将这些诊断分配混入Idle或外部Profiler采样。
        /// </summary>
        private static void CaptureNavigation(Report report)
        {
            var camera = Camera.main;
            report.CameraAvailable = camera != null;
            if (camera != null)
            {
                report.CameraPosition = camera.transform.position; report.CameraForward = camera.transform.forward;
                report.CameraScene = camera.gameObject.scene.path;
            }
            report.HubPortals = Array.Empty<PortalReport>();
            var hub = SceneManager.GetSceneByPath("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity");
            report.HubLoaded = hub.IsValid() && hub.isLoaded;
            if (!report.HubLoaded) return;
            var roots = hub.GetRootGameObjects(); SinglePlayerInventorySession session = null;
            foreach (var root in roots)
            {
                report.HubActive |= root.activeSelf;
                if (session == null) session = root.GetComponentInChildren<SinglePlayerInventorySession>(true);
            }
            var actor = session != null ? session.ActorTransform : null;
            report.HubActorAvailable = actor != null;
            if (actor != null) { report.HubActorPosition = actor.position; report.HubActorForward = actor.forward; }
            var portals = new List<PortalReport>();
            foreach (var root in roots)
                foreach (var portal in root.GetComponentsInChildren<HubPracticePortal>(true))
                {
                    if (portal == null) continue;
                    var position = portal.transform.position;
                    // -1明确表示没有Hub角色可测距，避免默认0被误读成已经走到门前。
                    portals.Add(new PortalReport { ScenePath = portal.ScenePath, Position = position,
                        DistanceToActor = actor != null ? Vector3.Distance(report.HubActorPosition, position) : -1,
                        ActiveInHierarchy = portal.gameObject.activeInHierarchy });
                }
            report.HubPortals = portals.ToArray();
        }
        private IEnumerator MeasureIdle()
        {
            _measuring = true; _idleFrames = 0; _idlePeak = 0; _calibrated = _recorderValid = false;
            var control = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            yield return null; byte[] bytes = new byte[1024]; GC.KeepAlive(bytes); yield return null;
            _calibrated = control.Valid && control.Count > 0 && control.LastValue >= 1024; control.Dispose();
            yield return new WaitForSecondsRealtime(2); // 预热与校准不算正式120帧。
            var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            for (int i = 0; i < 120; i++) { yield return null; if (recorder.Valid && recorder.Count > 0) { _idleFrames++; _idlePeak = Math.Max(_idlePeak, recorder.LastValue); } }
            _recorderValid = recorder.Valid; recorder.Dispose(); _measuring = false;
        }
        private void PositionPortal()
        {
            var scene = SceneManager.GetActiveScene(); SinglePlayerInventorySession session = null; HubPracticePortal portal = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (session == null) session = root.GetComponentInChildren<SinglePlayerInventorySession>(true);
                foreach (var candidate in root.GetComponentsInChildren<HubPracticePortal>(true)) if (candidate.ScenePath == _config.PortalScenePath) portal = candidate;
            }
            if (session == null || session.ActorTransform == null || portal == null) return;
            var actor = session.ActorTransform; var controller = actor.GetComponent<CharacterController>(); bool enabled = controller != null && controller.enabled;
            if (controller != null) controller.enabled = false; actor.position = portal.transform.position; if (controller != null) controller.enabled = enabled; Physics.SyncTransforms();
        }
        private sealed class MemoryFiles : IProfileFileSystem
        {
            private readonly Dictionary<string, string> _data = new Dictionary<string, string>();
            public bool Exists(string p) => _data.ContainsKey(p); public string Read(string p) => _data[p]; public void WriteFlushed(string p, string c) => _data[p] = c;
            public void Move(string a, string b) { _data[b] = _data[a]; _data.Remove(a); }
            public void Replace(string a, string b, string backup) { _data[backup] = _data[b]; Move(a, b); }
        }
    }
}
