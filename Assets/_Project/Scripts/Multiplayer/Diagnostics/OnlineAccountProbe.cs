using System;
using System.Collections;
using System.IO;
using Game.Core;
using Unity.Profiling;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>仅隔离场景挂载、显式参数启用的真实 Client 验收；正常玩家入口不自动注册或读取凭据文件。</summary>
    public sealed class OnlineAccountProbe : MonoBehaviour
    {
        [Serializable] private sealed class Credentials { public string Username, Password, PlayerId, InstanceId; public uint Version; }
        [Serializable] private sealed class Report
        {
            public bool Passed, Resume, WrongPasswordRejected, StaleVersionRejected, PersistentProfileMatched, LoggedOut;
            public bool FrameCounterCalibrated, FrameRecorderValid;
            public int IdleFrames; public long IdleGcPeakBytes;
            public string PlayerId, InstanceId, Error, RecordedAtUtc, ClientStatus, InitializationError, GraphicsDevice;
            public long LastHttpStatus;
            public uint ProfileVersion;
        }
        private OnlineLobbyChangedEvent _view;
        private int _changes;
        private bool _enabled, _finished;
        private OnlineAccountClient _client;
        private Report _report;
        private string _credentialsPath;
        private void OnEnable() => EventBus<OnlineLobbyChangedEvent>.Subscribe(OnChanged);
        private void OnDisable() => EventBus<OnlineLobbyChangedEvent>.Unsubscribe(OnChanged);
        private void OnChanged(OnlineLobbyChangedEvent view) { _view = view; _changes++; }
        private IEnumerator Start()
        {
            string[] args = Environment.GetCommandLineArgs(); bool resume = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-nornAccountProbe") _enabled = true;
                if (args[i] == "-nornAccountResume") resume = true;
                if (args[i] == "-nornAccountCredentials" && i + 1 < args.Length) _credentialsPath = args[i + 1];
            }
            if (!_enabled) yield break;
            Application.runInBackground = true; Application.targetFrameRate = 60;
            _report = new Report { Resume = resume };
            _client = FindFirstObjectByType<OnlineAccountClient>();
            if (!Check(_client != null && !string.IsNullOrEmpty(_credentialsPath) && File.Exists(_credentialsPath), "InvalidProbeConfiguration")) yield break;
            var credentials = JsonUtility.FromJson<Credentials>(File.ReadAllText(_credentialsPath));
            yield return new WaitForSecondsRealtime(1);
            if (!resume)
            {
                yield return Request(OnlineLobbyAction.Register, credentials);
                if (!Check(!_finished && _view.Status.Contains("注册成功"), "RegisterFailed")) yield break;
                string saved = credentials.Password; credentials.Password += "wrong";
                yield return Request(OnlineLobbyAction.Login, credentials); credentials.Password = saved;
                _report.WrongPasswordRejected = !_view.Authenticated && _view.Status.Contains("错误");
                if (!Check(_report.WrongPasswordRejected, "WrongPasswordAccepted")) yield break;
            }
            yield return Request(OnlineLobbyAction.Login, credentials);
            if (!Check(!_finished && _view.Authenticated && _view.Gold == 0 && _view.Items.Length == 2, "ProfileReadFailed")) yield break;
            if (resume)
            {
                _report.PersistentProfileMatched = _view.PlayerId == credentials.PlayerId && _view.Version == credentials.Version && Contains(credentials.InstanceId, "Warehouse");
                if (!Check(_report.PersistentProfileMatched, "PersistentProfileMismatch")) yield break;
            }
            else
            {
                if (!Check(_view.Version == 1, "InitialVersionMismatch")) yield break;
                credentials.PlayerId = _view.PlayerId; credentials.InstanceId = _view.Items[0].InstanceId;
                int before = _changes;
                EventBus<OnlineLobbyRequestEvent>.Publish(new OnlineLobbyRequestEvent { Action = OnlineLobbyAction.Move, ExpectedVersion = 1,
                    InstanceId = credentials.InstanceId, Destination = "Warehouse", Slot = 0 });
                yield return WaitForIdle(before);
                if (!Check(!_finished && _view.Version == 2 && Contains(credentials.InstanceId, "Warehouse"), "MoveNotCommitted")) yield break;
                before = _changes;
                EventBus<OnlineLobbyRequestEvent>.Publish(new OnlineLobbyRequestEvent { Action = OnlineLobbyAction.Move, ExpectedVersion = 1,
                    InstanceId = credentials.InstanceId, Destination = "Backpack", Slot = 3 });
                yield return WaitForIdle(before);
                _report.StaleVersionRejected = _view.Version == 2 && _view.Status.Contains("刷新");
                if (!Check(_report.StaleVersionRejected, "StaleVersionAccepted")) yield break;
                credentials.Version = _view.Version;
                File.WriteAllText(_credentialsPath, JsonUtility.ToJson(credentials)); // 只写调用者指定的忽略目录，不写证据中的密码。
            }
            _report.PlayerId = _view.PlayerId; _report.InstanceId = credentials.InstanceId; _report.ProfileVersion = _view.Version;
            // 已知数组正向校准全帧计数，之后充分预热。这里不使用已被 N0 否定的 .NET 字节计数。
            var control = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            yield return null;
            byte[] bytes = new byte[1024]; GC.KeepAlive(bytes);
            yield return null;
            _report.FrameCounterCalibrated = control.Valid && control.Count > 0 && control.LastValue >= 1024;
            control.Dispose();
            yield return new WaitForSecondsRealtime(2);
            var gc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            for (int i = 0; i < 120; i++)
            {
                yield return null;
                if (gc.Valid && gc.Count > 0) { _report.IdleGcPeakBytes = Math.Max(_report.IdleGcPeakBytes, gc.LastValue); _report.IdleFrames++; }
            }
            _report.FrameRecorderValid = gc.Valid; gc.Dispose();
            if (resume)
            {
                yield return Request(OnlineLobbyAction.Logout, credentials);
                _report.LoggedOut = !_view.Authenticated && !_client.Authenticated;
                if (!Check(_report.LoggedOut, "LogoutFailed")) yield break;
            }
            if (!Check(_report.FrameCounterCalibrated && _report.FrameRecorderValid && _report.IdleFrames == 120 && _report.IdleGcPeakBytes == 0, "IdleGcBudgetFailed")) yield break;
            Finish(null);
        }
        private IEnumerator Request(OnlineLobbyAction action, Credentials credentials)
        {
            int before = _changes;
            EventBus<OnlineLobbyRequestEvent>.Publish(new OnlineLobbyRequestEvent { Action = action, Username = credentials.Username, Password = credentials.Password });
            yield return WaitForIdle(before);
        }
        private IEnumerator WaitForIdle(int before)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 15;
            do
            {
                yield return null;
                if (Time.realtimeSinceStartupAsDouble > deadline) { Finish("RequestTimeout"); yield break; }
            } while (_changes <= before || _client.Busy);
        }
        private bool Contains(string instance, string area)
        {
            for (int i = 0; i < _view.Items.Length; i++) if (_view.Items[i].InstanceId == instance && _view.Items[i].Area == area) return true;
            return false;
        }
        private bool Check(bool passed, string error) { if (_finished) return false; if (!passed) Finish(error); return passed; }
        private void Finish(string error)
        {
            if (_finished) return; _finished = true;
            _report.Passed = error == null; _report.Error = error ?? ""; _report.RecordedAtUtc = DateTime.UtcNow.ToString("O");
            _report.ClientStatus = _client != null ? _client.LastStatus : "MissingClient";
            _report.InitializationError = _client != null ? _client.InitializationError : "MissingClient";
            _report.LastHttpStatus = _client != null ? _client.LastHttpStatus : 0;
            _report.GraphicsDevice = SystemInfo.graphicsDeviceType.ToString();
            string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ProbeReports")); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, _report.Resume ? "resume.json" : "first.json"), JsonUtility.ToJson(_report, true));
            Application.Quit(_report.Passed ? 0 : 1);
        }
    }
}
