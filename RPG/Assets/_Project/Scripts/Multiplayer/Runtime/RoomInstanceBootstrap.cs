using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using Game.Character;
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
    public sealed partial class RoomInstanceBootstrap : MonoBehaviour
    {
        private RoomNetworkManager _manager;
        private InstanceLaunchDescriptor _launch;
        private UnityWebRequest _request;
        private string _secret, _statePath;
        private double _lastRenew, _lastConnection;
        private int _lastCount;
        // 显式Development资源验收才复制当前工作区；普通DS的既有state报告不增加库存枚举。
        private bool _playerResourceProbe;
#if DEVELOPMENT_BUILD && UNITY_SERVER && !UNITY_EDITOR
        private string _interestProbe;
        private double _nextInterestProbe;
        private bool _hasInterestHome;
        private PlayerMovementSnapshot _interestHome;
        private string _interestPose="Home",_interestProbeError;
#endif
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
#if DEVELOPMENT_BUILD
            _interestProbe=Environment.GetEnvironmentVariable("NORN_N5_INTEREST_PROBE");
            _playerResourceProbe=Environment.GetEnvironmentVariable("NORN_N5_PLAYER_RESOURCE_PROBE") == "1";
#endif
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
                    _checkpointProviderSupported = InstanceControlHttp.SupportsCompleteCheckpoints(reply);
                    Inventory = new OnlineInventorySession(reply, manifest.Hash); Ready = true;
                }
                Release(); if (!Ready) yield return retry;
            }
            if (!Ready) { Fail("ReadyRejected"); yield break; }
            _manager.SetAuthorityReady(true); _lastRenew = _lastConnection = Time.realtimeSinceStartupAsDouble; WriteState();
            StartCheckpointPublisher();
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
            PumpCheckpointPublisher();
            PumpMemberExits();
            PumpInstanceCompletion();
            double now = Time.realtimeSinceStartupAsDouble;
#if DEVELOPMENT_BUILD
            PollExplicitInterestProbe(now);
#endif
            if (!_manager.Frozen && now - _lastRenew >= _launch.FreezeAfterSeconds) { _manager.SetFrozen(true); WriteState(); }
            if (_manager.AuthenticatedConnections > 0) _lastConnection = now;
            if (_lastCount != _manager.AuthenticatedConnections) { _lastCount = _manager.AuthenticatedConnections; WriteState(); }
            if (now - _lastConnection > _launch.IdleShutdownSeconds) Application.Quit();
        }
#if DEVELOPMENT_BUILD
        private void PollExplicitInterestProbe(double now)
        {
            if(string.IsNullOrEmpty(_interestProbe) || now<_nextInterestProbe)return;
            _nextInterestProbe=now+1;
            string request=_interestProbe+".request";
            if(File.Exists(request))
            {
                string command=File.ReadAllText(request).Trim();File.Delete(request);
                var world=OnlineWorldBootstrap.Active;
                var connection=_manager.ActorConnection(0);
                var identity=connection!=null?connection.authenticationData as JoinConnectionIdentity:null;
                bool authenticated=world!=null && world.WorldReady && world.IsAuthority && world.Registry!=null &&
                    _manager.IsCurrentActorConnection(connection,identity) && world.Registry[0].Player!=null;
                if(TryResolveExplicitInterestProbeCommand(command,authenticated,out var offset,out _interestProbeError))
                {
                    var player=world.Registry[0].Player;
                    if(!_hasInterestHome){_interestHome=player.CaptureMovementSnapshot();_hasInterestHome=true;}
                    var pose=_interestHome;pose.Position+=offset;
                    // 仅隔离Development DS，复用既有快照位置入口移动真实认证Actor，随后正式Pump自行判交。
                    // 不改材料/订阅状态，不发伪造Leave；该证据是诊断位移，不能称键盘或完整玩家路径验收。
                    player.RestoreMovementSnapshot(pose);_interestPose=command;
                }
            }
            WriteState(); // 仅显式夹具1Hz读数；正常启动无文件轮询/分配，也不用于性能验收。
        }
#endif
#endif
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        /// <summary>
        /// 显式诊断每条请求独立计算结果。旧错误只用于报告，不能成为下一条合法命令的权限门。
        /// 纯判断可在Editor开发检查，文件轮询/Actor恢复仍只编译进原Development DS入口；
        /// 不扩大诊断命令、认证范围、轮询频率或正式玩家移动能力。
        /// </summary>
        private static bool TryResolveExplicitInterestProbeCommand(string command, bool authenticated,
            out Vector3 offset, out string error)
        {
            offset=Vector3.zero;
            error=!authenticated ? "No authenticated DS Actor for explicit position diagnostic" :
                command!="home" && command!="away" ? "Unknown explicit interest diagnostic command" : null;
            if(error!=null)return false;
            if(command=="away")offset=Vector3.right*60;
            return true;
        }
#endif
        // Ready是“可执行权威模拟”的承诺，独立方法也让缺装配的失败边界能够回归。
        public static bool IsMovementWorldReady(OnlineWorldBootstrap world, string expectedDungeonId = null) =>
            world != null && world.isActiveAndEnabled && world.WorldReady && world.IsAuthority && !string.IsNullOrEmpty(world.DungeonId) &&
            (expectedDungeonId == null || string.Equals(expectedDungeonId, world.DungeonId, StringComparison.Ordinal));
        public static string Argument(string name)
        { string[] args = Environment.GetCommandLineArgs(); for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1]; return null; }
        private void Release() { var request = _request; _request = null; request?.Dispose(); }
        private void OnDisable() { StopInstanceCompletion(); StopMemberExitPublisher(); StopCheckpointPublisher(); _request?.Abort(); Release(); StopAllCoroutines(); _secret = null; }
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
            OnlineCastInventoryDiagnosticSnapshot[] inventories = null;
#if DEVELOPMENT_BUILD && UNITY_SERVER && !UNITY_EDITOR
            if (_playerResourceProbe && world != null && world.IsAuthority)
                inventories = new OnlineCastInventoryDiagnosticSnapshot[4];
#endif
            if (world != null && world.Registry != null)
                for (int i = 0; i < 4; i++)
                {
                    if (world.Registry[i].Player != null) actors[i] = world.Registry[i].Player.transform.position;
                    pendingInputs[i] = world.Registry[i].Input.PendingCount;
                    supersededInputs[i] = world.Registry[i].Input.SupersededContinuousFrames;
                    var slot = world.Registry[i];
                    // 复用现有当前值导出：sequence=1只生成诊断副本，绝不改正式库存通道发送序列。
                    // 断线席位仍可读取其存活工作区；Epoch和采样Tick明确随值保留，不能重新发放Entry资产。
                    if (inventories != null && slot.Cast != null && slot.Epoch != 0
                        && slot.Cast.TryCaptureInventory(slot.Epoch, 1ul, world.ServerTick, out var currentInventory))
                        OnlineCastInventoryDiagnosticSnapshot.TryFromAuthoritySnapshot(currentInventory, world.ServerTick, out inventories[i]);
                }
            var enemy = world != null && world.Registry != null ? world.Registry[4].Enemy : null;
            var driver = world != null ? world.GetComponent<AuthorityTickDriver>() : null;
            var movementChannel = _manager.GetComponent<MovementNetworkChannel>();
            var inventoryChannel = _manager.GetComponent<InventoryNetworkChannel>();
            File.WriteAllText(_statePath, JsonUtility.ToJson(new State { Ready = Ready, Frozen = _manager.Frozen, CanSimulate = _manager.CanSimulate, Heartbeats = Heartbeats, Members = Inventory == null ? 0 : Inventory.MemberCount,
                Clients = _manager.AuthenticatedConnections, Accepted = GetComponent<JoinTicketAuthenticator>().Accepted, Served = _manager.Served,
                // 与 Client 的认证后握手报告对照；不记录实例 Secret/Ticket，也不改变 Ready 或租约裁决。
                WorldEpoch = world != null ? world.WorldEpoch : 0, WorldReady = world != null && world.WorldReady,
                MaterialChunks = world != null ? world.MaterialReplication?.DescribeChunks() : null,
                MaterialAcks = world != null && world.MaterialReplication != null ? world.MaterialReplication.Acknowledged : 0,
                MaterialPages = world != null && world.MaterialReplication != null ? world.MaterialReplication.PagesSent : 0,
                MaterialFaulted = world != null && world.MaterialReplication != null && world.MaterialReplication.Faulted,
                MaterialFailure = world != null ? world.MaterialReplication?.Failure : null,
                MaterialPeerAcks = world != null && world.MaterialReplication!=null ? new[]{world.MaterialReplication.PeerAcknowledged(0),world.MaterialReplication.PeerAcknowledged(1),world.MaterialReplication.PeerAcknowledged(2),world.MaterialReplication.PeerAcknowledged(3)} : null,
// 每连接每Key保留当前ACK Header，恢复证据不能用聚合数或另一个Key的ACK代替。
                MaterialPeerAck0=world!=null?world.MaterialReplication?.DescribePeerAcknowledgements(0):null,
                MaterialPeerAck1=world!=null?world.MaterialReplication?.DescribePeerAcknowledgements(1):null,
                MaterialPeerAck2=world!=null?world.MaterialReplication?.DescribePeerAcknowledgements(2):null,
                MaterialPeerAck3=world!=null?world.MaterialReplication?.DescribePeerAcknowledgements(3):null,
                MaterialHistoryRecords=world!=null && world.MaterialReplication!=null?world.MaterialReplication.HistoryRecordsWritten:0,
                MaterialHistoryCapacity=world!=null && world.MaterialReplication!=null?world.MaterialReplication.HistoryCapacity:0,
                MaterialResyncAccepted=world!=null && world.MaterialReplication!=null?world.MaterialReplication.ResyncAccepted:0,
                MaterialResyncActorSlot=world!=null && world.MaterialReplication!=null?world.MaterialReplication.LastResyncActorSlot:-1,
                MaterialLastAcceptedResync=world!=null && world.MaterialReplication!=null?world.MaterialReplication.LastAcceptedResync:default,
#if DEVELOPMENT_BUILD && UNITY_SERVER && !UNITY_EDITOR
                InterestProbePose=_interestPose,InterestProbeError=_interestProbeError,
#endif
                MovementTick = world != null ? world.ServerTick : 0, ActorPositions = actors, EnemyPosition = enemy != null ? enemy.transform.position : Vector3.zero,
                ActorInventories = inventories,
                InventorySentSnapshots = inventoryChannel != null ? inventoryChannel.SentSnapshots : 0,
                InventorySentPages = inventoryChannel != null ? inventoryChannel.SentSnapshotPages : 0,
                InventoryRejectedSnapshots = inventoryChannel != null ? inventoryChannel.RejectedSnapshots : 0,
                InventoryAcceptedCommands = inventoryChannel != null ? inventoryChannel.AcceptedCommands : 0,
                InventoryRejectedCommands = inventoryChannel != null ? inventoryChannel.RejectedCommands : 0,
                InventoryDelivery = inventories != null && inventoryChannel != null ? inventoryChannel.CaptureOwnerDeliveryDiagnostics() : null,
                EnemyTarget = enemy != null && enemy.Perception != null ? enemy.Perception.TargetActorId : 0,
                MeasuredTicks = driver != null ? driver.MeasuredTicks : 0, TickCounterValid = driver != null && driver.TickCounterValid,
                TickAllocationEvents = driver != null ? driver.TickAllocationEvents : 0,
                // 稀疏诊断记录真实DS负载和输入积压；不能拿Client的注入RTT代替这些观测。
                InputPendingCounts = pendingInputs, DroppedSteps = driver != null ? driver.DroppedSteps : 0,
                SupersededContinuousInputs = supersededInputs,
                SnapshotBatchesSent = world != null ? world.SnapshotBatchesSent : 0,
                MovementPayloadBytesSent = movementChannel != null ? movementChannel.SentStatePayloadBytes : 0,
                CheckpointSequence = LastConfirmedCheckpointSequence, PendingCheckpoint = PendingCheckpointId, CheckpointError = CheckpointFailure,
                CompletionStarted = _completionStarted, CompletionConfirmed = _completionCompleted, CompletionError = _completionError,
                ProcessId = process.Id, ProcessStartTicks = process.StartTime.ToUniversalTime().Ticks, InstanceId = _launch.InstanceId, Error = error, RecordedAtUtc = DateTime.UtcNow.ToString("O") }, true));
        }
        [Serializable] private sealed class State { public bool Ready, Frozen, CanSimulate; public int Heartbeats, Members, Clients, ProcessId; public long ProcessStartTicks, Accepted, Served; public string InstanceId, Error, RecordedAtUtc;
            public bool CompletionStarted, CompletionConfirmed; public string CompletionError;
            public ulong WorldEpoch; public bool WorldReady;
            public ulong CheckpointSequence; public string PendingCheckpoint, CheckpointError;
            public WorldReplicationChunkStamp[] MaterialChunks; public long MaterialAcks, MaterialPages; public bool MaterialFaulted; public string MaterialFailure;
            public ulong[] MaterialPeerAcks;public string InterestProbePose,InterestProbeError;
            public WorldReplicationChunkStamp[] MaterialPeerAck0,MaterialPeerAck1,MaterialPeerAck2,MaterialPeerAck3;
            public ulong MaterialHistoryRecords;public int MaterialHistoryCapacity;
            public long MaterialResyncAccepted;public int MaterialResyncActorSlot;public WorldReplicationChunkStamp MaterialLastAcceptedResync;
            public uint MovementTick; public Vector3[] ActorPositions; public Vector3 EnemyPosition; public ulong EnemyTarget;
            public OnlineCastInventoryDiagnosticSnapshot[] ActorInventories;
            public long InventorySentSnapshots, InventorySentPages, InventoryRejectedSnapshots, InventoryAcceptedCommands, InventoryRejectedCommands;
            public InventoryNetworkChannel.OwnerDeliveryDiagnostic[] InventoryDelivery;
            public int MeasuredTicks; public bool TickCounterValid; public long TickAllocationEvents;
            public int[] InputPendingCounts; public long[] SupersededContinuousInputs;
            public long DroppedSteps, SnapshotBatchesSent, MovementPayloadBytesSent; }
    }
}
