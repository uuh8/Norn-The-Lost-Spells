using System;
using System.Collections;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using Mirror;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif

namespace Game.Multiplayer
{
    /// <summary>Portal房间组合：身份来自Application Session；HTTP控制与KCP准入有各自生命周期。</summary>
    public sealed partial class OnlineRoomClient : MonoBehaviour
    {
        private OnlineRoomProfile _profile;
        private OnlineAccountClient _account;
        private RoomReply _room;
        private RoomNetworkManager _manager;
        private JoinTicketAuthenticator _auth;
        private UnityWebRequest _http;
        private Coroutine _routine;
        private readonly WaitForSecondsRealtime _poll = new WaitForSecondsRealtime(2);
        private readonly WaitForSecondsRealtime _connectionPoll = new WaitForSecondsRealtime(.25f);
        private OnlineRoomAction _command = OnlineRoomAction.Observe;
        private OnlineRoomPhase _phase;
        private int _portal;
        private bool _success, _busy, _cancelRequested, _inConnectionScene;
        private bool _originalBackground;
        private string _body, _code, _status;
        private long _httpStatus;
        private Scene _suspendedHub, _connectionScene;
        private GameObject[] _hubRoots;
        private bool[] _hubRootActive;
        public OnlineRoomPhase Phase => _phase;
        public string RoomId => _room?.RoomId;
        // Unity对象销毁后托管引用可能仍在；显式Unity null判断避免把旧身份展示为当前连接。
        public ulong ActorId => _auth != null && _auth.ClientIdentity != null ? _auth.ClientIdentity.ActorId : 0;
        public uint ConnectionEpoch => _auth != null && _auth.ClientIdentity != null ? _auth.ClientIdentity.ConnectionEpoch : 0;
        public long Replies => _manager != null ? _manager.Replies : 0;
        public bool Busy => _busy;

        private void Awake() { _account = GetComponent<OnlineAccountClient>(); _profile = Resources.Load<OnlineRoomProfile>("Online/RoomClientProfile"); }
        private void OnEnable()
        {
            EventBus<DungeonMatchRequestedEvent>.Subscribe(OnMatch);
            EventBus<DungeonSinglePlayerRequestedEvent>.Subscribe(OnSinglePlayer);
            EventBus<OnlineRoomRequestEvent>.Subscribe(OnCommand);
            EventBus<OnlineRunExitStatusChangedEvent>.Subscribe(OnMemberExitStatus);
            EventBus<OnlineReturnPortalRequestedEvent>.Subscribe(OnReturnPortal);
            if (_routine == null && _exitRequested && !_exitHubReady && _room != null)
            {
                _originalBackground = Application.runInBackground; Application.runInBackground = true;
                _routine = StartCoroutine(ResumePendingMemberExit());
            }
        }
        private void OnDisable()
        {
            EventBus<DungeonMatchRequestedEvent>.Unsubscribe(OnMatch);
            EventBus<DungeonSinglePlayerRequestedEvent>.Unsubscribe(OnSinglePlayer);
            EventBus<OnlineRoomRequestEvent>.Unsubscribe(OnCommand);
            EventBus<OnlineRunExitStatusChangedEvent>.Unsubscribe(OnMemberExitStatus);
            EventBus<OnlineReturnPortalRequestedEvent>.Unsubscribe(OnReturnPortal);
            _http?.Abort(); _http?.Dispose(); _http = null; StopAllCoroutines(); StopConnection();
            // 应用退出不跨网络等待DELETE；Open大厅由90秒Presence过期回收，运行中由DS Lease裁决。
            // 临时停用时保留尚未确认退出的Replica World；连接已停止，CanAdvance=false。
            // 这样Enable后可查询/原重连，不会留下_inConnectionScene=true却World永久stopped的死路。
            if (!_exitRequested || _exitHubReady) { OnlineWorldBootstrap.ShutdownActive(); RestoreHub(); }
            if (_routine != null) Application.runInBackground = _originalBackground; _routine = null;
            if (!_exitRequested || _exitHubReady) _room = null;
        }
        private void OnMatch(DungeonMatchRequestedEvent request)
            => BeginPortalRoom(request.PortalId, request.DestinationKey, false);
        private void OnCommand(OnlineRoomRequestEvent request)
        {
            if (request.PortalId != _portal || _routine == null) return;
            if (request.Action == OnlineRoomAction.Observe) { Publish(); return; }
            if (request.Action == OnlineRoomAction.ExitRun || (request.Action == OnlineRoomAction.Cancel || request.Action == OnlineRoomAction.Disconnect)
                && (_inConnectionScene || _phase == OnlineRoomPhase.Connected || _phase == OnlineRoomPhase.Disconnected))
            { BeginMemberExit(); return; }
            if (request.Action == OnlineRoomAction.Cancel || request.Action == OnlineRoomAction.Disconnect)
            {
                _cancelRequested = true; Publish(); return; // 在途POST不能直接Abort并假定没有创建房间。
            }
            if (!_busy) _command = request.Action;
        }
        private IEnumerator Lobby(string dungeon)
        {
            _phase = OnlineRoomPhase.Matching; _room = null; Status(_singlePlayerEntry ? "正在预留本人资产并启动单人服务器…" : "正在查找同一副本的房间…");
            yield return SendPortalEntry(dungeon);
            while (!_success || !ReadRoom())
            {
                if (_singlePlayerEntry && _singleEntryPosted && !SinglePlayerContextMatches())
                {
                    _phase = OnlineRoomPhase.Error; _command = OnlineRoomAction.Observe;
                    Status("原单人入场结果未确认，请登录原账号后重试；不会用另一账号重放。");
                    while (!SinglePlayerContextMatches() || _command != OnlineRoomAction.Reconnect) yield return _connectionPoll;
                    _command = OnlineRoomAction.Observe; yield return SendPortalEntry(dungeon); continue;
                }
                if (!_account.Authenticated)
                {
                    _account.InvalidateSession(); ReleaseChoice(true, "登录已失效，请重新登录；未确认的大厅席位由服务器超时回收。");
                    Application.runInBackground = _originalBackground; _routine = null; yield break;
                }
                if (_httpStatus == 404 || _httpStatus == 405 || _httpStatus == 400 || _httpStatus == 401 || _httpStatus == 409)
                {
                    // 明确拒绝可回选择页；超时/500却可能已提交，必须先恢复当前房间，不能直接放行单机。
                    string message = _httpStatus == 404 || _httpStatus == 405 ? "服务器入场暂不可用，请稍后重试；不会回落本地资产。" : Failure();
                    if (_singlePlayerEntry) ClearSinglePlayerCandidate();
                    ReleaseChoice(false, message); Application.runInBackground = _originalBackground; _routine = null; yield break;
                }
                _phase = OnlineRoomPhase.Error; _command = OnlineRoomAction.Observe;
                Status("匹配结果尚未确认。请重试恢复房间，或取消并确认已退出；不会直接进入单机。");
                while (!_cancelRequested && _command != OnlineRoomAction.Reconnect) yield return _connectionPoll;
                if (_cancelRequested)
                {
                    if (_singlePlayerEntry)
                    {
                        if (!_singleEntryPosted)
                        { ClearSinglePlayerCandidate(); ReleaseChoice(true, "单人入场尚未发出，已取消。"); Application.runInBackground = _originalBackground; _routine = null; yield break; }
                        // 404/current不能证明在途预留未提交；以相同命令正文收敛，再走本人保存退出。
                        yield return SendPortalEntry(dungeon); continue;
                    }
                    _cancelRequested = false; // 先消费本次意图；请求在途收到的新取消不能在失败后被清掉。
                    yield return Send("GET", "/api/online/rooms/current", null);
                    if (_httpStatus == 404) { ReleaseChoice(true, "已确认没有房间。"); Application.runInBackground = _originalBackground; _routine = null; yield break; }
                    if (!_success) continue;
                    _cancelRequested = true; // 找到房间后进入下方正常DELETE流程，不能仅关闭本地框。
                }
                else yield return SendPortalEntry(dungeon);
            }
            while (_room != null && isActiveAndEnabled)
            {
                if (!_account.Authenticated && _room.State == "Open")
                {
                    // 失效身份不能发DELETE，也不能把大厅永久锁在暂停；席位交给Presence超时回收，绝不解锁Run资产。
                    _account.InvalidateSession(); _room = null; ReleaseChoice(true, "登录已失效，请返回主菜单重新登录。大厅席位将自动回收。"); break;
                }
                if (_cancelRequested && _room.State == "Open")
                {
                    _cancelRequested = false;
                    yield return Send("DELETE", RoomPath("/members/me"), null);
                    if (_success || _httpStatus == 404)
                    {
                        // 失败开局仍可能已可靠授予包/瓶；只为原账号读取完整Profile，不能靠取消本地解锁旧快照。
                        if (_singlePlayerEntry && SinglePlayerContextMatches())
                            EventBus<OnlineLobbyRequestEvent>.Publish(new OnlineLobbyRequestEvent { Action = OnlineLobbyAction.Refresh });
                        _room = null; ReleaseChoice(true, "已退出房间。"); break;
                    }
                    Status(Failure());
                }
                if (_room.State == "Running")
                {
                    yield return Connect();
                    if (_auth != null && _auth.ClientAuthenticated && NetworkClient.isConnected)
                    {
                        // 只有真实认证成功才释放门暂停；UI没有LoadScene权，也不把本地资产带进去。
                        EventBus<DungeonOnlineEntryConfirmedEvent>.Publish(new DungeonOnlineEntryConfirmedEvent { PortalId = _portal });
                        yield return EnterConnectionScene(); _inConnectionScene = true;
            _phase = OnlineRoomPhase.Connected; Status(_singlePlayerEntry ? "已进入单人服务器副本。" : "已进入合作副本。");
                        yield return Connected();
                        break;
                    }
                    _phase = OnlineRoomPhase.Disconnected; Status(Failure("入场失败，可重连或断开返回据点；不会切入单机副本。"));
                    // 即使尚未加载副本，也交给同一恢复循环泵退出查询，不能停在只识别旧Cancel的死循环。
                    yield return Connected(); break;
                }
                _phase = _room.State == "Starting" ? OnlineRoomPhase.Starting : OnlineRoomPhase.Lobby;
                if (_cancelRequested && _room.State != "Open")
                { if (!_singlePlayerEntry) _cancelRequested = false; Status(_singlePlayerEntry ? "正在确认单人入场，确认后保存退出。" : "副本正在启动，请等待入场结果；不会因一个成员取消而结束其他人的副本。"); }
                if (_command == OnlineRoomAction.Ready && _room.State == "Open" && !_singlePlayerEntry)
                {
                    _command = OnlineRoomAction.Observe;
                    // 准备前现读线上Version；不能拿登录时的缓存或单机Inventory Version提交Ready。
                    yield return Send("GET", "/api/online/profile", null);
                    var profile = _success ? JsonUtility.FromJson<OnlineProfileReply>(_body) : null;
                    if (profile != null)
                    {
                        yield return Send("POST", RoomPath("/ready"), JsonUtility.ToJson(new RoomReadyRequest
                        { ExpectedRevision = _room.ReadyRevision, ExpectedInventoryVersion = profile.Version, Ready = !SelfReady() }));
                        if (_success) ReadRoom(); else Status(Failure());
                    }
                }
                else if (_command == OnlineRoomAction.Start && _room.State == "Open" && !_singlePlayerEntry)
                {
                    _command = OnlineRoomAction.Observe;
                    yield return Send("POST", RoomPath("/start"), JsonUtility.ToJson(new StartRoomRequest { ExpectedRevision = _room.ReadyRevision }));
                    if (!_success) Status(Failure());
                }
                else _command = OnlineRoomAction.Observe;
                Publish(); yield return _poll;
                yield return Send("POST", RoomPath("/presence"), "{}");
                if (_success) { ReadRoom(); if (_room.State == "Open") Status(_singlePlayerEntry ? "单人服务器未启动，请取消本次后重试；资产未重置。" : "房间已建立；所有成员准备后，由队长开始。"); }
                else if (_httpStatus == 404) { _room = null; ReleaseChoice(false, "房间已结束或大厅席位已过期，请重新匹配。"); break; }
                else Status(Failure()); // 网络失联保留当前RoomId，可取消/恢复；不伪造本地Ready。
            }
            StopConnection(); _phase = OnlineRoomPhase.None; _busy = false; Application.runInBackground = _originalBackground; _routine = null;
            if (_room == null && !_exitRequested) ClearSinglePlayerCandidate();
        }
        private IEnumerator Connect()
        {
            _phase = OnlineRoomPhase.Connecting; Status("副本已Ready，正在领取一次性入场票…");
            StopConnection(); yield return null; // Destroy延迟到帧末，等旧NetworkManager释放singleton再建新的。
            yield return Send("POST", RoomPath("/ticket"), "{}");
            if (!_success) yield break;
            JoinTicketReply ticket;
            try { ticket = JsonUtility.FromJson<JoinTicketReply>(_body); } catch (ArgumentException) { _code = "InvalidTicketReply"; yield break; }
            _body = null; // 票只留在握手对象中，不写Client存档或UI视图。
            // Room 的地图是业务授权的入口；Ticket 即使其他身份相同，也不能把当前玩家送往另一张地图。
            // 先比对再创建连接对象，避免在 UI 看起来入场后才发现“门和实际副本不一致”。
            if (ticket == null || ticket.RoomId != _room.RoomId || ticket.PlayerId != _account.PlayerId || ticket.RunId != _room.RunId || ticket.RoomEpoch != _room.RoomEpoch ||
                !DungeonIdentityRules.Matches(_room.DungeonId, ticket.DungeonId) || _profile.ConnectionPrefab == null)
            { _code = "InvalidTicketReply"; yield break; }
            var root = Instantiate(_profile.ConnectionPrefab); _manager = root.GetComponent<RoomNetworkManager>(); _auth = root.GetComponent<JoinTicketAuthenticator>();
            if (root.GetComponent<MovementNetworkChannel>() == null) root.AddComponent<MovementNetworkChannel>();
            _manager.Configure(ticket.Port); _auth.ConfigureClient(ticket); _manager.StartClient();
            double deadline = Time.realtimeSinceStartupAsDouble + 12;
            while (Time.realtimeSinceStartupAsDouble < deadline && NetworkClient.active && !_auth.ClientAuthenticated) yield return _connectionPoll;
            if (!_auth.ClientAuthenticated || !NetworkClient.isConnected) { _code = "AdmissionFailed"; StopConnection(); }
            else if (_auth.ClientIdentity == null || !DungeonIdentityRules.Matches(_room.DungeonId, _auth.ClientIdentity.DungeonId))
            { _code = "AdmissionDungeonMismatch"; StopConnection(); }
        }
        private IEnumerator Connected()
        {
            // 认证后不再持续HTTP轮询大厅；KCP/副本Heartbeat负责连接，UI离散命令才创建业务请求。
            if (_exitRequested) { _exitServerObserved = false; _exitIntentAttempts = 0; _exitNextIntent = 0; }
            while (!_exitHubReady)
            {
                // Enable恢复或HTTP等待中换号都只保留原退出现场，不用B的Session查询/重连A的副本。
                if (_exitRequested && !MatchesMemberExitPlayer())
                { _command = OnlineRoomAction.Observe; ShowMemberExitAccountMismatch(); yield return _connectionPoll; continue; }
                if (_cancelRequested) BeginMemberExit();
                if (_phase == OnlineRoomPhase.Connected && !NetworkClient.isConnected)
                { _phase = OnlineRoomPhase.Disconnected; Status("连接已断开。重连会领取新票，旧连接不能继续控制此角色。"); }
                if (_exitRequested)
                {
                    double now = Time.realtimeSinceStartupAsDouble;
                    if (!_exitCommitted && !_exitServerObserved && _exitIntentAttempts < 3 && now >= _exitNextIntent)
                        SendStableMemberExitIntent();
                    if (!_exitCommitted && (_exitResolveRequested || _exitQueries < 3 && now >= _exitNextQuery))
                        yield return ResolveMemberExitReceipt();
                    if (_exitCommitted && _exitProfileAttempts < 3 && now >= _exitNextProfile)
                        yield return PrepareMemberExitHub();
                    if (_exitHubReady) break;
                }
                if (_command == OnlineRoomAction.Reconnect && _phase == OnlineRoomPhase.Disconnected)
                {
                    // 先查已提交结果，避免业务已结束本人资格后反复申请必然失败的新票。
                    yield return ProbeTerminalMemberExit();
                    if (_exitCommitted) { _command = OnlineRoomAction.Observe; continue; }
                    if (!_terminalExitProbeAllowsReconnect)
                    { _command = OnlineRoomAction.Observe; Status("离局结果尚未查清，暂不领取新票；请重试确认。"); continue; }
                    _command = OnlineRoomAction.Observe; yield return Send("POST", RoomPath("/presence"), "{}");
                    if (_success && ReadRoom() && _room.State == "Running")
                    {
                        yield return Connect();
                        if (_auth != null && _auth.ClientAuthenticated && NetworkClient.isConnected)
                        {
                            if (!_inConnectionScene)
                            {
                                EventBus<DungeonOnlineEntryConfirmedEvent>.Publish(new DungeonOnlineEntryConfirmedEvent { PortalId = _portal });
                                yield return EnterConnectionScene(); _inConnectionScene = true;
                            }
                            _phase = OnlineRoomPhase.Connected;
                            if (_exitRequested) { _exitServerObserved = false; _exitIntentAttempts = 0; _exitNextIntent = 0; SendStableMemberExitIntent(); }
                            Status(_exitRequested ? "已重连，继续确认原退出请求。" : "已重新连接同一副本。");
                        }
                        else { _phase = OnlineRoomPhase.Disconnected; Status(Failure()); }
                    }
                    else { _phase = OnlineRoomPhase.Disconnected; Status("原副本已结束或业务服不可达，请返回据点。"); }
                }
                yield return _connectionPoll;
            }
            _room = null;
            ReleaseChoice(true, "已保存物资并退出本局；队友继续。之后可重新匹配。");
        }
        private void StopConnection()
        {
            if (_manager != null) { if (NetworkClient.active) _manager.StopClient(); Destroy(_manager.gameObject); }
            _manager = null; _auth = null;
        }
        private IEnumerator Send(string method, string path, string json)
        {
            _success = false; _body = _code = null; _httpStatus = 0;
            if (_account == null || !_account.Authenticated) { _code = "SessionUnavailable"; yield break; }
            _busy = true; Publish();
            _http = _account.CreateSessionRequest(method, path, json);
            GameLog.Info("房间HTTP：" + method + " " + path, "OnlineRooms"); // 不打印Header/请求体/票。
            yield return _http.SendWebRequest(); _httpStatus = _http.responseCode; _success = _http.result == UnityWebRequest.Result.Success;
            GameLog.Info("房间HTTP完成：" + _httpStatus, "OnlineRooms");
            _body = _http.downloadHandler.text;
            if (!_success && !string.IsNullOrEmpty(_body))
                try { _code = JsonUtility.FromJson<OnlineErrorReply>(_body)?.Code; } catch (ArgumentException) { _code = "InvalidReply"; }
            _http.Dispose(); _http = null; _busy = false;
            if (_httpStatus == 401) _account.InvalidateSession();
        }
        private bool ReadRoom()
        {
            RoomReply view;
            try { view = JsonUtility.FromJson<RoomReply>(_body); } catch (ArgumentException) { return false; }
            if (view == null || view.Members == null || view.Members.Length < 1 || view.Members.Length > 4 || string.IsNullOrEmpty(view.RoomId)) return false;
            if (!_singlePlayerEntry && view.SinglePlayer) return false;
            if (_singlePlayerEntry && (!view.SinglePlayer || view.Members.Length != 1 || _singleEntryCommand == null
                || view.DungeonId != _singleEntryCommand.DungeonId || view.LeaderPlayerId != _singleEntryPlayerId)) return false;
            bool self = false; for (int i = 0; i < view.Members.Length; i++) if (view.Members[i].PlayerId == _account.PlayerId) self = true;
            if (!self) return false;
            if (view.State == "Starting" || view.State == "Running")
            {
                if (string.IsNullOrEmpty(view.RunId) || view.RoomEpoch == 0) return false;
                // 可信房间已经接管资产时立即撤Hub能力；解除只能由完整、同账号的Profile读回确认。
                OnlineAccountInventoryHubScope.TryMarkAssetsLocked(_account.GetInstanceID(), _account.PlayerId, view.RunId);
            }
            _room = view; return true;
        }
        private string RoomPath(string suffix) => "/api/online/rooms/" + _room.RoomId + suffix;
        private bool SelfReady()
        { if (_room != null) for (int i = 0; i < _room.Members.Length; i++) if (_room.Members[i].PlayerId == _account.PlayerId) return _room.Members[i].Ready; return false; }
        private string Failure(string fallback = "业务请求未完成，请稍后刷新或重试。")
        {
            if (_httpStatus == 429) return "请求过于频繁，请至少等待5秒再重连。";
            if (_httpStatus == 401 || _code == "SessionUnavailable") return "登录已失效；请返回主菜单重新登录。";
            if (_code == "DifferentDungeonRoom") return "你仍在另一个副本房间，请先返回对应入口处理。";
            if (_code == "RoomRevisionConflict" || _code == "ProfileVersionConflict") return "成员或线上资产已变化，请刷新后重新准备。";
            if (_code == "BuildNotFound" || _code == "LaunchFailed") return "副本启动失败，资产已恢复，请稍后重试。";
            return _httpStatus == 0 && _code != "AdmissionFailed" ? "业务服不可达，请检查本机服务后重试。" : fallback;
        }
        private void Status(string text) { _status = text; Publish(); }
        private void ReleaseChoice(bool close, string text)
        {
            _phase = OnlineRoomPhase.None; _busy = false; _status = text; Publish();
            EventBus<DungeonMatchReleasedEvent>.Publish(new DungeonMatchReleasedEvent { PortalId = _portal, CloseChoice = close });
            EventBus<DungeonMatchStatusEvent>.Publish(new DungeonMatchStatusEvent { PortalId = _portal, Message = text });
        }
        private void Publish()
        {
            var members = _room == null ? Array.Empty<OnlineRoomMemberView>() : new OnlineRoomMemberView[_room.Members.Length];
            bool allReady = members.Length >= 2;
            for (int i = 0; i < members.Length; i++)
            {
                var m = _room.Members[i]; members[i] = new OnlineRoomMemberView(m.Username, m.PlayerId == _account.PlayerId, m.PlayerId == _room.LeaderPlayerId, m.Ready); allReady &= m.Ready;
            }
            EventBus<OnlineRoomChangedEvent>.Publish(new OnlineRoomChangedEvent { PortalId = _portal, Phase = _phase, Busy = _busy, Status = _status, RoomId = _room?.RoomId,
                SinglePlayer = _singlePlayerEntry,
                Members = members, SelfReady = SelfReady(), CanStart = _room != null && _room.State == "Open" && _room.LeaderPlayerId == _account.PlayerId && allReady,
                ActorId = ActorId, ConnectionEpoch = ConnectionEpoch, Replies = Replies,
                NativeSocket = _manager != null && _manager.GetComponent<NornKcpTransport>().UsesNativeSocket });
        }
        private IEnumerator EnterConnectionScene()
        {
            // 暂存据点原Scene与激活位，防止在线往返丢失Scene Preview/尚未保存的单机整备。
            // 内存代价是N2连接期间保留Hub；不读取/上传这些单机对象，不让它们继续模拟。
            SuspendHub(SceneManager.GetActiveScene());
            Time.timeScale = 1; Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
#if UNITY_EDITOR
            var operation = EditorSceneManager.LoadSceneAsyncInPlayMode(_profile.ConnectionScene, new LoadSceneParameters(LoadSceneMode.Additive));
#else
            var operation = SceneManager.LoadSceneAsync(_profile.ConnectionScene, LoadSceneMode.Additive);
#endif
            if (operation != null) yield return operation;
            _connectionScene = SceneManager.GetSceneByPath(_profile.ConnectionScene); if (_connectionScene.IsValid()) SceneManager.SetActiveScene(_connectionScene);
        }
        // Scene挂起只管理进程内对象的激活位；从异步场景加载中拆出，避免验证生命周期时连接网络或读取资产。
        private void SuspendHub(Scene hub)
        {
            _suspendedHub = hub; _hubRoots = _suspendedHub.GetRootGameObjects(); _hubRootActive = new bool[_hubRoots.Length];
            // SetActive(false)会同步派发OnDisable；库存Session会在其中关闭并列WorldItems_Runtime根。
            // 必须先完整冻结所有原激活位，再统一停用，否则后续根会把回调修改后的false误记成原状态，
            // 返回Hub便会再次关闭拾取父根，使已提交地面资产和后续Drop代理无法显示。
            for (int i = 0; i < _hubRoots.Length; i++) _hubRootActive[i] = _hubRoots[i].activeSelf;
            for (int i = 0; i < _hubRoots.Length; i++) _hubRoots[i].SetActive(false);
        }
        private void RestoreHub()
        {
            if (_suspendedHub.IsValid() && _suspendedHub.isLoaded)
            {
                SceneManager.SetActiveScene(_suspendedHub);
                if (_hubRoots != null) for (int i = 0; i < _hubRoots.Length; i++) if (_hubRoots[i] != null) _hubRoots[i].SetActive(_hubRootActive[i]);
            }
            _hubRoots = null; _hubRootActive = null;
        }
        private IEnumerator ReturnToHub()
        {
            // 在线Tick/Physics先结束，再卸载世界并恢复Hub，避免全局物理模式污染单机一帧。
            OnlineWorldBootstrap.ShutdownActive();
            if (_connectionScene.IsValid() && _connectionScene.isLoaded) yield return SceneManager.UnloadSceneAsync(_connectionScene);
            // Unload会跨帧；确认期间换号也不能在回调尾部重新激活原账号据点。
            if (_exitRequested && !MatchesMemberExitPlayer()) { ShowMemberExitAccountMismatch(); yield break; }
            RestoreHub();
            Time.timeScale = 1; Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        }
    }
}
