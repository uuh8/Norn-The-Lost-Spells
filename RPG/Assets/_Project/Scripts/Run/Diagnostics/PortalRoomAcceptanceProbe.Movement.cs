using System;
using System.IO;
using Game.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Run
{
    /// <summary>
    /// 原Portal探针的只读模式。只在显式配置后、Hub已加载时运行一个短窗口。
    /// 不依赖Character/InputSystem/UI程序集，不消费输入，不改变存档、位置、暂停或焦点。
    /// 常规游戏没有此探针组件；只读模式关闭时不会创建缓存或扫描Scene。
    /// </summary>
    public sealed partial class PortalRoomAcceptanceProbe
    {
        private const string HubScenePath = "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private const int MaxMovementSamples = 240, MaxVisibilityOwners = 16;
        private const double MaxMovementSeconds = 5;

        [Serializable] private struct ActorObservation
        {
            public bool Available, Active;
            public int InstanceId, SceneHandle;
            public string ScenePath;
            public Vector3 Position;
        }

        [Serializable] private struct MovementSample
        {
            public int Frame;
            public double Realtime;
            public bool Focused;
            public float TimeScale;
            public int CursorLock;
            public bool CursorVisible;
            public ActorObservation CachedActor, CurrentActor, PresentationActor, CameraActor;
            public bool CachedEqualsCurrent, PresentationEqualsCurrent, CameraEqualsCurrent;
            public bool CurrentReadAvailable, CachedReadAvailable;
            public PlayerMovementReadOnlyObservation Current, Cached;
            public bool ObservedDeltaAvailable;
            public Vector3 ObservedDelta;
            public bool PauseReadAvailable, Paused;
            public int PauseReasons;
            public bool OnlineViewObserved, OnlineViewAppliesToCurrent;
            public int OnlineSceneHandle;
            public bool OnlineActive, OnlineMenuOpen, OnlineFrozen, OnlineConnected, OnlineInputCaptured;
            // 晚订阅无法知道先前已打开的UI，不能把“未收到事件”解释成关闭。
            public bool VisibilityEventObserved, VisibilityOverflow;
            public int ObservedOpenUiOwners, ContactCount;
        }

        [Serializable] private sealed class MovementReport
        {
            public string Kind = "HubMovementReadOnly", StopReason, RecordedAtUtc;
            public int SampleCount;
            public double StartedAt, Deadline;
            public bool LastMoveFrameKnown, RequestedDisplacementAvailable;
            public string CollisionMeaning = "CC flags/velocity are from last Move; contact Frame is callback frame; observed delta is between samples.";
            public MovementSample[] Samples;
            public HubMovementContactWindow.Contact[] Contacts;
            public int ContactCount, DroppedContacts;
        }

        private MovementSample[] _movementSamples;
        private int _movementCount, _movementLastFrame = -1;
        private double _movementStarted, _movementDeadline;
        private bool _movementStopped, _movementContextResolved;
        private Scene _movementHub;
        private Transform _movementCachedActor;
        private IPlayerMovementReadOnlyObservation _movementSource;
        private RunPauseCoordinator _movementPause;
        private HubMovementContactWindow _movementContacts;
        private HubMovementContactProbe _movementContactProbe;
        private bool _movementOnlineObserved, _movementVisibilityObserved, _movementVisibilityOverflow;
        private OnlineMovementPresentationEvent _movementOnline;
        private readonly int[] _movementVisibilityOwners = new int[MaxVisibilityOwners];
        private int _movementVisibilityCount;

        private void SubscribeMovementObservation()
        {
            EventBus<OnlineMovementPresentationEvent>.Subscribe(OnMovementView);
            EventBus<InventoryPresentationVisibilityEvent>.Subscribe(OnMovementVisibility);
        }

        private void UnsubscribeMovementObservation()
        {
            EventBus<OnlineMovementPresentationEvent>.Unsubscribe(OnMovementView);
            EventBus<InventoryPresentationVisibilityEvent>.Unsubscribe(OnMovementVisibility);
            FinishMovementObservation("disabled");
        }

        private void OnMovementView(OnlineMovementPresentationEvent view)
        {
            if (_config == null || !_config.ObserveMovementOnly || _movementStopped) return;
            _movementOnlineObserved = true; _movementOnline = view;
        }

        private void OnMovementVisibility(InventoryPresentationVisibilityEvent view)
        {
            if (_config == null || !_config.ObserveMovementOnly || _movementStopped || view.OwnerId == 0) return;
            _movementVisibilityObserved = true;
            int index = -1;
            for (int i = 0; i < _movementVisibilityCount; i++)
                if (_movementVisibilityOwners[i] == view.OwnerId) { index = i; break; }
            if (view.Open)
            {
                if (index >= 0) return;
                if (_movementVisibilityCount == MaxVisibilityOwners) { _movementVisibilityOverflow = true; return; }
                _movementVisibilityOwners[_movementVisibilityCount++] = view.OwnerId;
            }
            else if (index >= 0)
                _movementVisibilityOwners[index] = _movementVisibilityOwners[--_movementVisibilityCount];
        }

        private void LateUpdate()
        {
            if (_config == null || !_config.ObserveMovementOnly || _movementStopped || _config.MovementSamples <= 0) return;
            // 安装发生在主菜单之前。等待Hub加载，避免在登录阶段就花完短窗口；
            // 首次解析后不逐帧扫描Scene/root；切图则保留局部证据并收尾。
            if (!_movementContextResolved)
            {
                var hub = SceneManager.GetSceneByPath(HubScenePath);
                if (!hub.IsValid() || !hub.isLoaded) return;
            }
            else if (!_movementHub.IsValid() || !_movementHub.isLoaded)
            { FinishMovementObservation("hub-unloaded"); return; }
            if (_externalSampling) { FinishMovementObservation("profiler-window"); return; }
            CaptureMovementObservation(Time.frameCount, Time.realtimeSinceStartupAsDouble);
        }

        /// <summary>
        /// 生产LateUpdate传同帧时钟；参数化边界允许EditMode用逻辑时钟验证期限，无需推进游戏。
        /// 输入只读口和绑定在同一主线程顺序复制，不使用陈旧磁盘报告拼接同帧结论。
        /// </summary>
        private void CaptureMovementObservation(int frame, double now)
        {
            if (_config == null || !_config.ObserveMovementOnly || _movementStopped || _config.MovementSamples <= 0 ||
                double.IsNaN(now) || double.IsInfinity(now)) return;
            if (_movementSamples == null)
            {
                if (string.IsNullOrWhiteSpace(_config.Output) || float.IsNaN(_config.MovementSeconds) || _config.MovementSeconds <= 0) return;
                _movementSamples = new MovementSample[Math.Min(_config.MovementSamples, MaxMovementSamples)];
                _movementStarted = now;
                _movementDeadline = now + Math.Min(_config.MovementSeconds, MaxMovementSeconds);
                _movementContacts = new HubMovementContactWindow(_movementDeadline);
                ResolveMovementContext();
            }
            if (now >= _movementDeadline) { FinishMovementObservation("deadline"); return; }
            if (frame == _movementLastFrame) return;
            _movementLastFrame = frame;
            if (!SourceAlive(_movementSource))
                _movementSource = ObservationSource(LocalActorPresentation.Actor != null
                    ? LocalActorPresentation.Actor.transform : LocalActorCameraPresentation.Actor != null
                        ? LocalActorCameraPresentation.Actor.transform : null);
            var current = SourceAlive(_movementSource) ? _movementSource.CurrentInputActor : null;
            var currentSource = ObservationSource(current);
            var cachedSource = ObservationSource(_movementCachedActor);
            var presentation = LocalActorPresentation.Actor;
            var cameraActor = LocalActorCameraPresentation.Actor;
            var sample = new MovementSample
            {
                Frame = frame, Realtime = now, Focused = Application.isFocused, TimeScale = Time.timeScale,
                CursorLock = (int)Cursor.lockState, CursorVisible = Cursor.visible,
                CachedActor = Describe(_movementCachedActor), CurrentActor = Describe(current),
                PresentationActor = Describe(presentation != null ? presentation.transform : null),
                CameraActor = Describe(cameraActor != null ? cameraActor.transform : null),
                CachedEqualsCurrent = current != null && _movementCachedActor == current,
                PresentationEqualsCurrent = current != null && presentation != null && presentation.transform == current,
                CameraEqualsCurrent = current != null && cameraActor != null && cameraActor.transform == current,
                CurrentReadAvailable = SourceAlive(currentSource), CachedReadAvailable = SourceAlive(cachedSource),
                PauseReadAvailable = _movementPause != null,
                Paused = _movementPause != null && _movementPause.IsPaused,
                PauseReasons = _movementPause != null ? (int)_movementPause.ActiveReasons : 0,
                OnlineViewObserved = _movementOnlineObserved,
                OnlineViewAppliesToCurrent = current != null && _movementOnlineObserved && _movementOnline.WorldSceneHandle == current.gameObject.scene.handle,
                // 原事件不是Serializable，不直接塞进JsonUtility；明确复制原始值，保留Scene适用性。
                OnlineSceneHandle = _movementOnline.WorldSceneHandle, OnlineActive = _movementOnline.Active,
                OnlineMenuOpen = _movementOnline.MenuOpen, OnlineFrozen = _movementOnline.Frozen,
                OnlineConnected = _movementOnline.Connected, OnlineInputCaptured = _movementOnline.InputCaptured,
                VisibilityEventObserved = _movementVisibilityObserved,
                VisibilityOverflow = _movementVisibilityOverflow, ObservedOpenUiOwners = _movementVisibilityCount,
                ContactCount = _movementContacts.Count
            };
            if (sample.CurrentReadAvailable) sample.Current = currentSource.CaptureReadOnlyObservation();
            if (sample.CachedReadAvailable) sample.Cached = cachedSource.CaptureReadOnlyObservation();
            if (_movementCount > 0)
            {
                var previous = _movementSamples[_movementCount - 1];
                sample.ObservedDeltaAvailable = sample.CurrentActor.Available && previous.CurrentActor.Available &&
                    sample.CurrentActor.InstanceId == previous.CurrentActor.InstanceId;
                if (sample.ObservedDeltaAvailable) sample.ObservedDelta = sample.CurrentActor.Position - previous.CurrentActor.Position;
            }
            _movementSamples[_movementCount++] = sample;
            BindMovementContactObservation(current);
            if (_movementCount == _movementSamples.Length) FinishMovementObservation("capacity");
        }

        private void ResolveMovementContext()
        {
            _movementContextResolved = true;
            _movementHub = SceneManager.GetSceneByPath(HubScenePath);
            if (!_movementHub.IsValid() || !_movementHub.isLoaded) return;
            // 只解析一次，并刻意保留与旧Probe相同的首个Session缓存选择，供Actual Current交叉核实。
            // inactive缓存不被改为active；不因诊断去初始化/绑定它。
            var roots = _movementHub.GetRootGameObjects();
            foreach (var root in roots)
            {
                if (_movementCachedActor == null)
                {
                    var session = root.GetComponentInChildren<SinglePlayerInventorySession>(true);
                    if (session != null) _movementCachedActor = session.ActorTransform;
                }
                if (_movementPause == null) _movementPause = root.GetComponentInChildren<RunPauseCoordinator>(false);
            }
            _movementSource = ObservationSource(_movementCachedActor);
            if (SourceAlive(_movementSource)) return;
            // 仅初次找可选读口，不按名字选玩家，不把找到的Controller当Current；真正Current仍由其getter给出。
            foreach (var root in roots)
                foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
                    if (component is IPlayerMovementReadOnlyObservation source) { _movementSource = source; return; }
        }

        private static bool SourceAlive(IPlayerMovementReadOnlyObservation source) =>
            source != null && (!(source is UnityEngine.Object obj) || obj != null);

        private static IPlayerMovementReadOnlyObservation ObservationSource(Transform actor) =>
            actor != null ? actor.GetComponent<IPlayerMovementReadOnlyObservation>() : null;

        private static ActorObservation Describe(Transform actor)
        {
            if (actor == null) return default;
            return new ActorObservation
            {
                Available = true, Active = actor.gameObject.activeInHierarchy,
                InstanceId = actor.gameObject.GetInstanceID(), SceneHandle = actor.gameObject.scene.handle,
                ScenePath = actor.gameObject.scene.path, Position = actor.position
            };
        }

        private void BindMovementContactObservation(Transform actor)
        {
            if (_movementContactProbe != null && actor != null && _movementContactProbe.transform == actor)
            {
                // Actor两次采样间可能禁用又启用；观察器OnDisable会断开引用。
                // 重新接同一窗口，不重置deadline/预算、不改玩家enabled或Current。
                _movementContactProbe.Begin(_movementContacts); return;
            }
            RemoveMovementContactObservation();
            if (actor == null || actor.GetComponent<CharacterController>() == null || !_movementContacts.Accepting) return;
            _movementContactProbe = actor.gameObject.AddComponent<HubMovementContactProbe>();
            _movementContactProbe.Begin(_movementContacts);
        }

        private void RemoveMovementContactObservation()
        {
            if (_movementContactProbe == null) return;
            _movementContactProbe.End();
            // 仅销毁本Probe自己附加的观察器；不按类型批量删除，也不启停CharacterController。
            if (Application.isPlaying) Destroy(_movementContactProbe); else DestroyImmediate(_movementContactProbe);
            _movementContactProbe = null;
        }

        private void FinishMovementObservation(string reason)
        {
            if (_movementStopped || _movementSamples == null) return;
            _movementStopped = true;
            _movementContacts.Stop(); RemoveMovementContactObservation();
            var report = new MovementReport
            {
                StopReason = reason, RecordedAtUtc = DateTime.UtcNow.ToString("O"), SampleCount = _movementCount,
                StartedAt = _movementStarted, Deadline = _movementDeadline,
                Samples = new MovementSample[_movementCount], Contacts = new HubMovementContactWindow.Contact[_movementContacts.Count],
                ContactCount = _movementContacts.Count, DroppedContacts = _movementContacts.Dropped
            };
            Array.Copy(_movementSamples, report.Samples, _movementCount);
            _movementContacts.CopyTo(report.Contacts);
            try { File.WriteAllText(_config.Output, JsonUtility.ToJson(report, true)); }
            catch (IOException) { GameLog.Error("Hub只读观测输出失败（IO），采样已停止，不自动重试。", "OnlineAcceptance"); }
            catch (UnauthorizedAccessException) { GameLog.Error("Hub只读观测输出无写权限，采样已停止，不自动重试。", "OnlineAcceptance"); }
            // 释放本窗口固定缓冲与Scene引用；stopped锁存避免每帧重新创建/重复导出。
            _movementSamples = null; _movementContacts = null; _movementSource = null; _movementCachedActor = null; _movementPause = null;
            // 只读模式收尾后停止Unity回调；OnDisable重复进入由stopped锁存保护。
            enabled = false;
        }
    }
}
