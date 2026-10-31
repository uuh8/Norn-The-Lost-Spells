using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using Mirror;
using UnityEngine;
using UnityEngine.Networking;

namespace Game.Multiplayer
{
    /// <summary>组合层领取可信启动描述；只有Dedicated构建自动监听，普通Client不会因batchmode变成副本。</summary>
    [RequireComponent(typeof(RoomNetworkManager), typeof(JoinTicketAuthenticator))]
    public sealed class RoomInstanceBootstrap : MonoBehaviour
    {
        private RoomNetworkManager _manager;
        private InstanceLaunchDescriptor _launch;
        private UnityWebRequest _request;
        private string _secret, _statePath;
        private double _lastRenew, _lastConnection;
        private int _lastCount;
        public bool Ready { get; private set; }
        public int Heartbeats { get; private set; }
        public OnlineInventorySession Inventory { get; private set; }
        // 来自业务启动描述的授权键；它不是 Scene 路径。后续内容装配必须用此键查受控资源表。
        public string DungeonId => _launch != null ? _launch.DungeonId : null;
        public string RunId => _launch != null ? _launch.RunId : null;
        public bool HasValidatedLaunch { get; private set; }
#if UNITY_SERVER && !UNITY_EDITOR
        private IEnumerator Start()
        {
            _manager = GetComponent<RoomNetworkManager>();
            string path = Argument("--norn-instance");
            if (path == null || !File.Exists(path)) { Fail("MissingLaunchDescriptor"); yield break; }
            _launch = JsonUtility.FromJson<InstanceLaunchDescriptor>(File.ReadAllText(path)); _statePath = Path.Combine(Path.GetDirectoryName(path), "state.json");
            _secret = Environment.GetEnvironmentVariable("NORN_INSTANCE_SECRET"); Environment.SetEnvironmentVariable("NORN_INSTANCE_SECRET", null);
            var asset = Resources.Load<TextAsset>("Online/ContentManifest");
            var combat = Resources.Load<Game.Character.OnlineCombatDefinition>("Online/CombatDefinition");
            var manifest = asset == null ? null : JsonUtility.FromJson<ContentManifest>(asset.text);
            if (_launch == null || !DungeonIdentityRules.IsValidId(_launch.DungeonId) || combat == null || combat.WireHash == 0 || _launch.GameplayConfigHash != combat.GameplayConfigHash || manifest == null || manifest.Hash != ContentManifestRules.CalculateHash(manifest) || _launch.ManifestHash != manifest.Hash || _launch.BuildVersion != manifest.BuildVersion || _launch.ProtocolVersion != manifest.ProtocolVersion || _secret == null || _secret.Length != 64)
            { Fail("LaunchCompatibilityMismatch"); yield break; }
            HasValidatedLaunch = true;
            // 地图、原碰撞/NavMesh和NPC配置必须在监听/业务Ready之前完成；启动了进程不代表副本已可模拟。
            var world = OnlineWorldBootstrap.Active;
            if (world == null || !world.TryInitializeDungeon(_launch.DungeonId) || !IsMovementWorldReady(world, _launch.DungeonId))
            { Fail("DungeonWorldNotReady"); yield break; }
            GetComponent<JoinTicketAuthenticator>().ConfigureServer(_launch, _secret); _manager.Configure(_launch.Port); _manager.StartServer();
            yield return null;
            // N3的碰撞/NavMesh资源必须实际装配完成，不能仅监听成功就向业务服宣告可模拟。
            if (!IsMovementWorldReady(world, _launch.DungeonId)) { Fail("MovementWorldNotReady"); yield break; }
            if (!NetworkServer.active || !_manager.transport.ServerActive()) { Fail("TransportNotListening"); yield break; }
            using var process = Process.GetCurrentProcess();
            var request = new InstanceReadyRequest { RoomEpoch = _launch.RoomEpoch, ProtocolVersion = manifest.ProtocolVersion, BuildVersion = manifest.BuildVersion, ManifestHash = manifest.Hash,
                GameplayConfigHash = combat.GameplayConfigHash, DungeonId = _launch.DungeonId,
                ProcessId = process.Id, ProcessStartTicks = process.StartTime.ToUniversalTime().Ticks };
            // 正常父进程先登记PID；小概率竞争时重试Ready，而不是跳过PID校验。
            double deadline = Time.realtimeSinceStartupAsDouble + 35; var retry = new WaitForSecondsRealtime(.5f);
            while (!Ready && Time.realtimeSinceStartupAsDouble < deadline)
            {
                _request = InstanceControlHttp.Create(_launch, _secret, "ready", JsonUtility.ToJson(request)); yield return _request.SendWebRequest();
                if (_request.result == UnityWebRequest.Result.Success)
                {
                    var reply = JsonUtility.FromJson<InstanceReadyReply>(_request.downloadHandler.text);
                    // HTTP 成功本身不代表身份正确。先核对同一授权地图，再安装冻结资产并宣告 Ready，
                    // 拒绝旧业务产物缺失字段或跨实例回复，不能从返回值悄悄切换本进程的期望地图。
                    if (reply == null || !DungeonIdentityRules.Matches(_launch.DungeonId, reply.DungeonId))
                    { Fail("ReadyDungeonMismatch"); Release(); yield break; }
                    Inventory = new OnlineInventorySession(reply, manifest.Hash); Ready = true;
                }
                Release(); if (!Ready) yield return retry;
            }
            if (!Ready) { Fail("ReadyRejected"); yield break; }
            _manager.SetAuthorityReady(true); _lastRenew = _lastConnection = Time.realtimeSinceStartupAsDouble; WriteState();
            var interval = new WaitForSecondsRealtime(_launch.HeartbeatSeconds);
            while (NetworkServer.active)
            {
                yield return interval;
                _request = InstanceControlHttp.Create(_launch, _secret, "heartbeat", JsonUtility.ToJson(new InstanceHeartbeatRequest { RoomEpoch = _launch.RoomEpoch }));
                yield return _request.SendWebRequest(); bool success = _request.result == UnityWebRequest.Result.Success;
                long status = _request.responseCode; Release();
                if (success) { _lastRenew = Time.realtimeSinceStartupAsDouble; Heartbeats++; _manager.SetFrozen(false); }
                else if (status == 401 || status == 409) { Fail("InstanceEpochExpired"); yield break; }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                // 本机显式采样仅在稀疏Heartbeat检查，不在Update轮询文件，也不新增远端控制API。
                string diagnostic = Path.Combine(Path.GetDirectoryName(_statePath), "movement-diagnostics.request");
                if (File.Exists(diagnostic))
                {
                    string command = File.ReadAllText(diagnostic).Trim(); File.Delete(diagnostic);
                    if (command == "measure" && OnlineWorldBootstrap.Active != null)
                        OnlineWorldBootstrap.Active.GetComponent<AuthorityTickDriver>().StartDiagnosticMeasurement();
                }
#endif
                WriteState();
            }
        }
        private void Update()
        {
            if (!Ready) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (!_manager.Frozen && now - _lastRenew >= _launch.FreezeAfterSeconds) { _manager.SetFrozen(true); WriteState(); }
            if (_manager.AuthenticatedConnections > 0) _lastConnection = now;
            if (_lastCount != _manager.AuthenticatedConnections) { _lastCount = _manager.AuthenticatedConnections; WriteState(); }
            if (now - _lastConnection > _launch.IdleShutdownSeconds) Application.Quit();
        }
#endif
        // Ready是“可执行权威模拟”的承诺，独立方法也让缺装配的失败边界能够回归。
        public static bool IsMovementWorldReady(OnlineWorldBootstrap world, string expectedDungeonId = null) =>
            world != null && world.isActiveAndEnabled && world.WorldReady && world.IsAuthority && !string.IsNullOrEmpty(world.DungeonId) &&
            (expectedDungeonId == null || string.Equals(expectedDungeonId, world.DungeonId, StringComparison.Ordinal));
        public static string Argument(string name)
        { string[] args = Environment.GetCommandLineArgs(); for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1]; return null; }
        private void Release() { var request = _request; _request = null; request?.Dispose(); }
        private void OnDisable() { _request?.Abort(); Release(); StopAllCoroutines(); _secret = null; }
        private void Fail(string code) { if (_manager != null) _manager.SetAuthorityReady(false); GameLog.Error(code, "OnlineRooms"); WriteState(code); Application.Quit(1); }
        private void WriteState(string error = null)
        {
            if (_statePath == null || _manager == null) return;
            // 稀疏控制/连接事件的诊断报告，不在每Tick写文件；不包含Secret/Ticket/密码。
            using var process = Process.GetCurrentProcess();
            var world = OnlineWorldBootstrap.Active;
            var actors = new Vector3[4];
            var pendingInputs = new int[4];
            var supersededInputs = new long[4];
            if (world != null && world.Registry != null)
                for (int i = 0; i < 4; i++)
                {
                    if (world.Registry[i].Player != null) actors[i] = world.Registry[i].Player.transform.position;
                    pendingInputs[i] = world.Registry[i].Input.PendingCount;
                    supersededInputs[i] = world.Registry[i].Input.SupersededContinuousFrames;
                }
            var enemy = world != null && world.Registry != null ? world.Registry[4].Enemy : null;
            var driver = world != null ? world.GetComponent<AuthorityTickDriver>() : null;
            var movementChannel = _manager.GetComponent<MovementNetworkChannel>();
            File.WriteAllText(_statePath, JsonUtility.ToJson(new State { Ready = Ready, Frozen = _manager.Frozen, CanSimulate = _manager.CanSimulate, Heartbeats = Heartbeats, Members = Inventory == null ? 0 : Inventory.MemberCount,
                Clients = _manager.AuthenticatedConnections, Accepted = GetComponent<JoinTicketAuthenticator>().Accepted, Served = _manager.Served,
                MovementTick = world != null ? world.ServerTick : 0, ActorPositions = actors, EnemyPosition = enemy != null ? enemy.transform.position : Vector3.zero,
                EnemyTarget = enemy != null && enemy.Perception != null ? enemy.Perception.TargetActorId : 0,
                MeasuredTicks = driver != null ? driver.MeasuredTicks : 0, TickCounterValid = driver != null && driver.TickCounterValid,
                TickAllocationEvents = driver != null ? driver.TickAllocationEvents : 0,
                // 稀疏诊断记录真实DS负载和输入积压；不能拿Client的注入RTT代替这些观测。
                InputPendingCounts = pendingInputs, DroppedSteps = driver != null ? driver.DroppedSteps : 0,
                SupersededContinuousInputs = supersededInputs,
                SnapshotBatchesSent = world != null ? world.SnapshotBatchesSent : 0,
                MovementPayloadBytesSent = movementChannel != null ? movementChannel.SentStatePayloadBytes : 0,
                ProcessId = process.Id, ProcessStartTicks = process.StartTime.ToUniversalTime().Ticks, InstanceId = _launch.InstanceId, Error = error, RecordedAtUtc = DateTime.UtcNow.ToString("O") }, true));
        }
        [Serializable] private sealed class State { public bool Ready, Frozen, CanSimulate; public int Heartbeats, Members, Clients, ProcessId; public long ProcessStartTicks, Accepted, Served; public string InstanceId, Error, RecordedAtUtc;
            public uint MovementTick; public Vector3[] ActorPositions; public Vector3 EnemyPosition; public ulong EnemyTarget;
            public int MeasuredTicks; public bool TickCounterValid; public long TickAllocationEvents;
            public int[] InputPendingCounts; public long[] SupersededContinuousInputs;
            public long DroppedSteps, SnapshotBatchesSent, MovementPayloadBytesSent; }
    }
}
