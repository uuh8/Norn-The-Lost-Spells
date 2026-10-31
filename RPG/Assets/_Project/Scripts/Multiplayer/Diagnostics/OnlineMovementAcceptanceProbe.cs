using System;
using System.Collections;
using System.IO;
using Game.Character;
using Game.Core;
using Game.Networking;
using Mirror;
using Unity.Profiling;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>显式Development验收探针。只读运行状态/执行网络故障与Profiler，不替代玩家移动或业务认证。</summary>
    public sealed class OnlineMovementAcceptanceProbe : MonoBehaviour
    {
        [Serializable] private sealed class Config { public string MovementOutput, MovementCommand; }
        [Serializable] private sealed class Report
        {
            public string RecordedAtUtc, PhysicsMode;
            public bool Ready, Frozen, Suppressed, Grounded, Calibrated, RecorderValid, TickCounterValid, AttackHeld;
            public uint ServerTick, InputSequence, Epoch;
            public ulong ActorId, EnemyTarget;
            public Vector3 Position, RemotePosition, EnemyPosition;
            public Vector3 ViewDirection, AimPoint, EnemyAimCenter, BossPosition, BossAimCenter;
            public int RemoteCount, ReplayCount, Frames, Rtt, Jitter, Loss, MeasuredTicks;
            public long PeakFrameBytes, DroppedMessages, TickAllocationEvents;
            public float TimeScale, CorrectionDistance;
            public float[] ActorHp, ActorMana;
            public int[] ActorCharges;
            public int ActiveOutputs;
            public long SpawnedOutputs, DamageFacts;
            public long[] FrameGcBytes, FrameSpawnTotals, FrameDamageTotals;
            public int[] FrameActiveOutputs;
            public bool SharedCameraBound;
            public bool ClientInventoryReady;
            public int ClientInventoryCount;
            public uint ClientInventoryVersion;
            public ulong ClientInventorySequence;
            public float ClientInventoryMana;
            public string MainCameraName;
            public float MainCameraFov;
            public Vector3 MainCameraPosition, CameraTargetPosition;
            public string DungeonId;
            public int ConfiguredNpcCount, ActiveNpcCount, DungeonPhase;
            public uint DungeonRevision, DungeonEncounter, DungeonRemaining;
            // 这些是本机实际接收/播放指标。上面的Rtt/Jitter/Loss只是故障注入设置，绝不等同真实网络测量。
            public long RemoteReceivedSnapshots, RemoteUnderflows, RemoteResyncs, DriverDroppedSteps;
            public int RemoteBufferedSnapshots, RemoteAnimatorState;
            public double RemoteBufferedSeconds, RemotePlaybackTime, RemoteEffectiveDelay, RemoteAnimatorTime;
            public float RemotePlanarSpeed, RemoteAnimatorMove, ObservedRemoteSnapshotHz, ObservedRenderHz;
            public float ObservedMovementPayloadBytesPerSecond;
            public double[] FrameTimes, FrameRemoteHeadroom;
            public Vector3[] FrameRemotePositions;
            public float[] FrameRemoteMove;
            public long[] FrameRemoteReceived;
            public long[] FrameMovementPayloadBytes;
        }
        private Config _config;
        private double _next;
        private bool _measuring, _calibrated, _valid;
        private int _frames, _rtt, _jitter, _loss;
        private long _peak;
        private MovementNetworkChannel _configured;
        // 仅显式QA进程创建；保留原始逐帧数据，便于分开核对持续热路径与出生/命中等离散生命周期分配。
        private readonly long[] _frameBytes = new long[240], _frameSpawns = new long[240], _frameDamages = new long[240];
        private readonly int[] _frameActive = new int[240];
        // 窗口开始前预分配；采样时仅写值，不做JSON/文件IO、不创建集合或每帧委托。
        private readonly double[] _frameTimes = new double[240], _frameHeadroom = new double[240];
        private readonly Vector3[] _frameRemotePositions = new Vector3[240];
        private readonly float[] _frameRemoteMove = new float[240];
        private readonly long[] _frameReceived = new long[240];
        private readonly long[] _framePayloadBytes = new long[240];
        private static readonly int MoveSpeedHash = Animator.StringToHash("speed");
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
#if (DEVELOPMENT_BUILD || UNITY_EDITOR) && !UNITY_SERVER
            string path = RoomInstanceBootstrap.Argument("--norn-portal-acceptance");
            if (path == null || !File.Exists(path)) return;
            var config = JsonUtility.FromJson<Config>(File.ReadAllText(path));
            if (string.IsNullOrEmpty(config.MovementOutput) || string.IsNullOrEmpty(config.MovementCommand)) return;
            var root = new GameObject("ExplicitMovementAcceptance"); DontDestroyOnLoad(root);
            root.AddComponent<OnlineMovementAcceptanceProbe>()._config = config;
#endif
        }
        private void Update()
        {
            if (_config == null || _measuring || Time.realtimeSinceStartupAsDouble < _next) return;
            _next = Time.realtimeSinceStartupAsDouble + 1;
            var world = OnlineWorldBootstrap.Active;
            var manager = NetworkManager.singleton as RoomNetworkManager;
            var channel = manager != null ? manager.GetComponent<MovementNetworkChannel>() : null;
            if (channel != null && channel != _configured) { _configured = channel; channel.ConfigureDiagnosticFaults(_rtt, _jitter, _loss); }
            if (File.Exists(_config.MovementCommand))
            {
                string command = File.ReadAllText(_config.MovementCommand).Trim(); File.Delete(_config.MovementCommand);
                if (command == "measure") { StartCoroutine(Measure()); return; }
                if (command == "disconnect") NetworkClient.Disconnect();
                // 三档注入只影响运动应用消息，账户/Ready/JoinTicket始终走原产品路径。
                if (command == "fault-0") SetFaults(channel, 0, 0, 0);
                if (command == "fault-100") SetFaults(channel, 100, 20, 1);
                if (command == "fault-200") SetFaults(channel, 200, 40, 3);
            }
            var auth = manager != null ? manager.GetComponent<JoinTicketAuthenticator>() : null;
            ulong actor = auth != null && auth.ClientIdentity != null ? auth.ClientIdentity.ActorId : 0;
            var report = new Report { RecordedAtUtc = DateTime.UtcNow.ToString("O"), TimeScale = Time.timeScale, PhysicsMode = Physics.simulationMode.ToString(),
                ActorId = actor, Epoch = auth != null && auth.ClientIdentity != null ? auth.ClientIdentity.ConnectionEpoch : 0,
                Calibrated = _calibrated, RecorderValid = _valid, Frames = _frames, PeakFrameBytes = _peak, Rtt = _rtt, Jitter = _jitter, Loss = _loss,
                DroppedMessages = channel != null ? channel.DiagnosticDropped : 0 };
            report.FrameGcBytes = _frameBytes; report.FrameSpawnTotals = _frameSpawns;
            report.FrameDamageTotals = _frameDamages; report.FrameActiveOutputs = _frameActive;
            report.FrameTimes = _frameTimes; report.FrameRemoteHeadroom = _frameHeadroom;
            report.FrameRemotePositions = _frameRemotePositions; report.FrameRemoteMove = _frameRemoteMove;
            report.FrameRemoteReceived = _frameReceived;
            report.FrameMovementPayloadBytes = _framePayloadBytes;
            if (_frames > 1 && _frameTimes[_frames - 1] > _frameTimes[0])
            {
                double elapsed = _frameTimes[_frames - 1] - _frameTimes[0];
                // 这是接受到的单个Actor快照Hz，不能叫UDP包Hz（Mirror可能把多条消息合批）。
                report.ObservedRemoteSnapshotHz = (float)((_frameReceived[_frames - 1] - _frameReceived[0]) / elapsed);
                report.ObservedRenderHz = (float)((_frames - 1) / elapsed);
                report.ObservedMovementPayloadBytesPerSecond = (float)((_framePayloadBytes[_frames - 1] - _framePayloadBytes[0]) / elapsed);
            }
            if (world != null && world.Registry != null)
            {
                report.Ready = world.MovementReady; report.Frozen = world.Frozen; report.Suppressed = world.LocalInputSuppressed;
                report.ServerTick = world.ServerTick; report.InputSequence = world.LastInputSequence; report.ReplayCount = world.PredictionReplayCount;
                report.CorrectionDistance = world.LastCorrectionDistance;
                var driver = world.GetComponent<AuthorityTickDriver>();
                if (driver != null) { report.TickCounterValid = driver.TickCounterValid; report.MeasuredTicks = driver.MeasuredTicks; report.TickAllocationEvents = driver.TickAllocationEvents; report.DriverDroppedSteps = driver.DroppedSteps; }
                if (world.LocalPlayer != null)
                {
                    var pose = world.LocalPlayer.CaptureMovementSnapshot(); report.Position = pose.Position; report.Grounded = pose.IsGrounded;
                    // 只读当前真实输入/瞄准结果，用于区分自动化松键与实际瞄偏；绝不替玩家设置镜头或目标。
                    report.AttackHeld = world.LocalPlayer.IsOnlineAttackHeld;
                    // 显式验收时读取真正由网络快照安装的本机Source，不能用业务Entry或技术HUD的Mana冒充库存已连通。
                    var inventoryDriver = world.LocalPlayer.GetComponent<OnlineCastDriver>();
                    if (inventoryDriver != null)
                    {
                        report.ClientInventoryReady = inventoryDriver.ClientInventoryReady;
                        report.ClientInventoryCount = inventoryDriver.ClientInventoryCount;
                        report.ClientInventoryVersion = inventoryDriver.ClientInventoryVersion;
                        report.ClientInventorySequence = inventoryDriver.ClientInventorySequence;
                        report.ClientInventoryMana = inventoryDriver.ClientInventoryMana;
                    }
                    if (world.LocalPlayer.CameraRoot != null) report.ViewDirection = world.LocalPlayer.CameraRoot.forward;
                    if (world.LocalPlayer is WizardController wizard) report.AimPoint = wizard.CaptureOnlineCastAim();
                    var camera = world.LocalPlayer.MainCamera;
                    if (camera != null)
                    {
                        // 验收原相机的实际输出，而非仅检查场景里存在一个同名组件；采样窗口仍跳过此诊断 IO。
                        report.MainCameraName = camera.name; report.MainCameraFov = camera.fieldOfView;
                        report.MainCameraPosition = camera.transform.position;
                        report.SharedCameraBound = LocalActorCameraPresentation.TryGet(world.LocalPlayer.gameObject, out var shared) && shared == camera;
                    }
                    if (LocalActorPresentation.CameraTarget != null) report.CameraTargetPosition = LocalActorPresentation.CameraTarget.position;
                }
                // 只读记录实际地图与已安装进度，避免用Scene同名或预期配置冒充“正确副本已经连通”。
                report.DungeonId = world.DungeonId;
                for (int i = OnlineActorLimits.PlayerCount; i < world.Registry.Capacity; i++)
                {
                    if (world.Registry[i].Root != null) report.ConfiguredNpcCount++;
                    if (world.Registry[i].Connected) report.ActiveNpcCount++;
                }
                if (DungeonPresentation.TryGet(world.gameObject.scene.handle, out var dungeon) && dungeon.TryCaptureProgress(out var progress))
                {
                    report.DungeonRevision = progress.Revision; report.DungeonEncounter = progress.EncounterIndexPlusOne;
                    report.DungeonRemaining = progress.RemainingEnemies; report.DungeonPhase = (int)progress.Phase;
                }
                for (int i = 0; i < 4; i++) if ((ulong)(i + 1) != actor && world.Registry[i].Connected)
                {
                    var remote = world.Registry[i];
                    if (remote.Player == null) continue;
                    report.RemoteCount++;
                    // 总人数仍统计全部远端；详细窗口只固定首个Actor，避免四人时把多条流相加算Hz。
                    if (report.RemoteCount > 1) continue;
                    report.RemotePosition = remote.Player.transform.position;
                    var interpolation = remote.Interpolation;
                    report.RemoteReceivedSnapshots = interpolation.ReceivedCount;
                    report.RemoteUnderflows = interpolation.UnderflowCount; report.RemoteResyncs = interpolation.ResyncCount;
                    report.RemoteBufferedSnapshots = interpolation.BufferedCount; report.RemoteBufferedSeconds = interpolation.BufferedSeconds;
                    report.RemotePlaybackTime = interpolation.PlaybackTime; report.RemoteEffectiveDelay = interpolation.EffectiveDelay;
                    report.RemotePlanarSpeed = interpolation.PlanarSpeed;
                    var animator = remote.Player.Animator;
                    if (animator != null)
                    {
                        report.RemoteAnimatorMove = animator.GetFloat(MoveSpeedHash);
                        var animation = animator.GetCurrentAnimatorStateInfo(0);
                        report.RemoteAnimatorState = animation.shortNameHash; report.RemoteAnimatorTime = animation.normalizedTime;
                    }
                }
                var enemy = world.Registry[4].Enemy;
                if (enemy != null)
                {
                    report.EnemyPosition = enemy.transform.position; report.EnemyTarget = enemy.Perception != null ? enemy.Perception.TargetActorId : 0;
                    var collider = enemy.GetComponent<CharacterController>();
                    report.EnemyAimCenter = collider != null ? enemy.transform.TransformPoint(collider.center) : enemy.transform.position + Vector3.up;
                }
                // 正式副本由Definition分配NPC ActorId，Boss不再固定为技术场景的Actor7。
                // 诊断也必须读取实际注册表，否则P8中的Actor5 Boss会被报告为“不存在”。
                WizardBossController boss = null;
                for (int i = OnlineActorLimits.PlayerCount; i < world.Registry.Capacity; i++)
                    if (world.Registry[i].Boss != null) { boss = world.Registry[i].Boss; break; }
                if (boss != null)
                {
                    report.BossPosition = boss.transform.position;
                    var collider = boss.GetComponent<CharacterController>();
                    report.BossAimCenter = collider != null ? boss.transform.TransformPoint(collider.center) : boss.transform.position + Vector3.up;
                }
                // 仅诊断写报告时分配数组；Measure 窗口完全停写，不把该 IO 成本混进稳态性能结论。
                report.ActorHp = new float[world.Registry.Capacity]; report.ActorMana = new float[world.Registry.Capacity];
                report.ActorCharges = new int[world.Registry.Capacity];
                for (int i = 0; i < world.Registry.Capacity; i++)
                {
                    if (world.Registry[i].Health != null) report.ActorHp[i] = world.Registry[i].Health.CurrentHp;
                    if (world.Combat != null && world.Combat.TryGetResources((ulong)i + 1, out var resources))
                    { report.ActorMana[i] = resources.CurrentMana; report.ActorCharges[i] = resources.RemainingCharges; }
                }
                if (world.Combat != null) { report.ActiveOutputs = world.Combat.ActiveOutputs; report.SpawnedOutputs = world.Combat.SpawnedOutputs; report.DamageFacts = world.Combat.DamageFacts; }
            }
            // JSON/磁盘IO是每秒一次的诊断分配；正式Profiler窗口期间完全停写。
            File.WriteAllText(_config.MovementOutput, JsonUtility.ToJson(report, true));
        }
        private void SetFaults(MovementNetworkChannel channel, int rtt, int jitter, int loss)
        { _rtt = rtt; _jitter = jitter; _loss = loss; if (channel != null) channel.ConfigureDiagnosticFaults(rtt, jitter, loss); }
        private IEnumerator Measure()
        {
            _measuring = true; _frames = 0; _peak = 0; _calibrated = _valid = false;
            EventBus<OnlineDiagnosticSamplingEvent>.Publish(new OnlineDiagnosticSamplingEvent { Active = true });
            var control = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            yield return null; var bytes = new byte[1024]; GC.KeepAlive(bytes); yield return null;
            _calibrated = control.Valid && control.Count > 0 && control.LastValue >= 1024; control.Dispose();
            yield return new WaitForSecondsRealtime(2);
            var world = OnlineWorldBootstrap.Active;
            if (world != null) world.GetComponent<AuthorityTickDriver>().StartDiagnosticMeasurement();
            var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            for (int i = 0; i < 240; i++)
            {
                yield return null;
                if (recorder.Valid && recorder.Count > 0)
                {
                    _frameBytes[_frames] = recorder.LastValue; _peak = Math.Max(_peak, recorder.LastValue);
                    var combat = world != null ? world.Combat : null;
                    _frameSpawns[_frames] = combat != null ? combat.SpawnedOutputs : 0;
                    _frameDamages[_frames] = combat != null ? combat.DamageFacts : 0;
                    _frameActive[_frames] = combat != null ? combat.ActiveOutputs : 0;
                    _frameTimes[_frames] = Time.realtimeSinceStartupAsDouble;
                    if (world != null && world.Registry != null)
                    {
                        var manager = NetworkManager.singleton as RoomNetworkManager;
                        var channel = manager != null ? manager.GetComponent<MovementNetworkChannel>() : null;
                        _framePayloadBytes[_frames] = channel != null ? channel.ReceivedStatePayloadBytes : 0;
                        var auth = manager != null ? manager.GetComponent<JoinTicketAuthenticator>() : null;
                        ulong local = auth != null && auth.ClientIdentity != null ? auth.ClientIdentity.ActorId : 0;
                        for (int actor = 0; actor < OnlineActorLimits.PlayerCount; actor++)
                        {
                            var remote = world.Registry[actor];
                            if ((ulong)actor + 1 == local || !remote.Connected || remote.Player == null) continue;
                            _frameRemotePositions[_frames] = remote.Player.transform.position;
                            _frameHeadroom[_frames] = remote.Interpolation.BufferedSeconds;
                            _frameReceived[_frames] = remote.Interpolation.ReceivedCount;
                            var animator = remote.Player.Animator;
                            _frameRemoteMove[_frames] = animator != null ? animator.GetFloat(MoveSpeedHash) : 0;
                            break;
                        }
                    }
                    _frames++;
                }
            }
            _valid = recorder.Valid; recorder.Dispose(); _measuring = false;
            EventBus<OnlineDiagnosticSamplingEvent>.Publish(new OnlineDiagnosticSamplingEvent { Active = false });
        }
    }
}
