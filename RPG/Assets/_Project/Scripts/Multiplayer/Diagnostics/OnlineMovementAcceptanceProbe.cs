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
            if (world != null && world.Registry != null)
            {
                report.Ready = world.MovementReady; report.Frozen = world.Frozen; report.Suppressed = world.LocalInputSuppressed;
                report.ServerTick = world.ServerTick; report.InputSequence = world.LastInputSequence; report.ReplayCount = world.PredictionReplayCount;
                report.CorrectionDistance = world.LastCorrectionDistance;
                var driver = world.GetComponent<AuthorityTickDriver>();
                if (driver != null) { report.TickCounterValid = driver.TickCounterValid; report.MeasuredTicks = driver.MeasuredTicks; report.TickAllocationEvents = driver.TickAllocationEvents; }
                if (world.LocalPlayer != null)
                {
                    var pose = world.LocalPlayer.CaptureMovementSnapshot(); report.Position = pose.Position; report.Grounded = pose.IsGrounded;
                    // 只读当前真实输入/瞄准结果，用于区分自动化松键与实际瞄偏；绝不替玩家设置镜头或目标。
                    report.AttackHeld = world.LocalPlayer.IsOnlineAttackHeld;
                    if (world.LocalPlayer.CameraRoot != null) report.ViewDirection = world.LocalPlayer.CameraRoot.forward;
                    if (world.LocalPlayer is WizardController wizard) report.AimPoint = wizard.CaptureOnlineCastAim();
                }
                for (int i = 0; i < 4; i++) if ((ulong)(i + 1) != actor && world.Registry[i].Connected)
                { report.RemoteCount++; report.RemotePosition = world.Registry[i].Player.transform.position; }
                var enemy = world.Registry[4].Enemy;
                if (enemy != null)
                {
                    report.EnemyPosition = enemy.transform.position; report.EnemyTarget = enemy.Perception != null ? enemy.Perception.TargetActorId : 0;
                    var collider = enemy.GetComponent<CharacterController>();
                    report.EnemyAimCenter = collider != null ? enemy.transform.TransformPoint(collider.center) : enemy.transform.position + Vector3.up;
                }
                var boss = world.Registry[6].Boss;
                if (boss != null)
                {
                    report.BossPosition = boss.transform.position;
                    var collider = boss.GetComponent<CharacterController>();
                    report.BossAimCenter = collider != null ? boss.transform.TransformPoint(collider.center) : boss.transform.position + Vector3.up;
                }
                // 仅诊断写报告时分配数组；Measure 窗口完全停写，不把该 IO 成本混进稳态性能结论。
                report.ActorHp = new float[8]; report.ActorMana = new float[8]; report.ActorCharges = new int[8];
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
                    _frames++;
                }
            }
            _valid = recorder.Valid; recorder.Dispose(); _measuring = false;
            EventBus<OnlineDiagnosticSamplingEvent>.Publish(new OnlineDiagnosticSamplingEvent { Active = false });
        }
    }
}
