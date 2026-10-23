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
    public sealed class PortalRoomAcceptanceProbe : MonoBehaviour
    {
        [Serializable] private sealed class Config { public string Output, Command, PortalScenePath; }
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
        }
        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnScene;
            EventBus<OnlineLobbyChangedEvent>.Unsubscribe(OnIdentity);
            EventBus<OnlineRoomChangedEvent>.Unsubscribe(OnRoom);
            EventBus<DungeonEntryChoiceRequestedEvent>.Unsubscribe(OnChoice);
            EventBus<DungeonEntryChoiceClosedEvent>.Unsubscribe(OnClosed);
            EventBus<OnlineDiagnosticSamplingEvent>.Unsubscribe(OnSampling);
        }
        private void OnSampling(OnlineDiagnosticSamplingEvent e) => _externalSampling = e.Active;
        private void OnIdentity(OnlineLobbyChangedEvent e) => _authenticated = e.Authenticated;
        private void OnRoom(OnlineRoomChangedEvent e) => _view = e;
        private void OnChoice(DungeonEntryChoiceRequestedEvent e) { _choice = true; _view.PortalId = e.PortalId; }
        private void OnClosed(DungeonEntryChoiceClosedEvent e) => _choice = false;
        private void OnScene(Scene scene, LoadSceneMode mode)
        {
            if (_config == null || !scene.path.EndsWith("SinglePlayer_Hub.unity", StringComparison.Ordinal)) return;
            foreach (var root in scene.GetRootGameObjects())
            {
                var session = root.GetComponentInChildren<SinglePlayerInventorySession>(true); if (session == null) continue;
                // sceneLoaded早于Start；已有序列化数据只读，通过公共初始化注入MemoryFiles防止修改真实档。
                if (!session.ConfigureDiagnosticMemory(_files)) GameLog.Error("原生Portal验收无法隔离单机文件，停止验收。", "OnlineAcceptance");
            }
        }
        private void Update()
        {
            if (_config == null || _measuring || _externalSampling || Time.realtimeSinceStartupAsDouble < _next) return;
            _next = Time.realtimeSinceStartupAsDouble + 1;
            if (File.Exists(_config.Command))
            {
                string command = File.ReadAllText(_config.Command).Trim(); File.Delete(_config.Command);
                if (command == "position-portal") PositionPortal(); // 唯一白名单：只定位，F与UGUI由原生输入执行。
                else if (command == "measure-idle") { StartCoroutine(MeasureIdle()); return; }
            }
            if (_view.PortalId != 0) EventBus<OnlineRoomRequestEvent>.Publish(new OnlineRoomRequestEvent { PortalId = _view.PortalId, Action = OnlineRoomAction.Observe });
            var hub = SceneManager.GetSceneByPath("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity"); bool active = false;
            if (hub.IsValid()) foreach (var root in hub.GetRootGameObjects()) active |= root.activeSelf;
            var report = new Report { Scene = SceneManager.GetActiveScene().path, Authenticated = _authenticated, ChoiceOpen = _choice,
                Phase = _view.Phase.ToString(), RoomId = _view.RoomId, Status = _view.Status, CanStart = _view.CanStart, SelfReady = _view.SelfReady, Busy = _view.Busy,
                PortalId = _view.PortalId, MemberCount = _view.Members?.Length ?? 0, ActorId = _view.ActorId, HubActive = active, RecordedAtUtc = DateTime.UtcNow.ToString("O"),
                FrameCounterCalibrated = _calibrated, IdleRecorderValid = _recorderValid, IdleFrames = _idleFrames, IdleGcPeakBytes = _idlePeak, ConnectionEpoch = _view.ConnectionEpoch, Replies = _view.Replies, NativeSocket = _view.NativeSocket };
            File.WriteAllText(_config.Output, JsonUtility.ToJson(report, true)); // 诊断IO有分配，不是零GC测量。
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
