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
    public sealed class OnlineRoomClient : MonoBehaviour
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
            EventBus<OnlineRoomRequestEvent>.Subscribe(OnCommand);
        }
        private void OnDisable()
        {
            EventBus<DungeonMatchRequestedEvent>.Unsubscribe(OnMatch);
            EventBus<OnlineRoomRequestEvent>.Unsubscribe(OnCommand);
            _http?.Abort(); _http?.Dispose(); _http = null; StopAllCoroutines(); StopConnection();
            // 应用退出不跨网络等待DELETE；Open大厅由90秒Presence过期回收，运行中由DS Lease裁决。
            RestoreHub(); if (_routine != null) Application.runInBackground = _originalBackground; _routine = null; _room = null;
        }
        private void OnMatch(DungeonMatchRequestedEvent request)
        {
            if (_routine != null) { Publish(); return; }
            _portal = request.PortalId;
            if (_account == null || !_account.Authenticated || _profile == null || !_profile.TryDungeon(request.DestinationKey, out string dungeon))
            { ReleaseChoice(false, "联机配置或登录无效，请重新登录后重试。"); return; }
            _cancelRequested = false; _command = OnlineRoomAction.Observe;
            _originalBackground = Application.runInBackground; Application.runInBackground = true;
            _routine = StartCoroutine(Lobby(dungeon));
        }
        private void OnCommand(OnlineRoomRequestEvent request)
        {
            if (request.PortalId != _portal || _routine == null) return;
            if (request.Action == OnlineRoomAction.Observe) { Publish(); return; }
            if (request.Action == OnlineRoomAction.Cancel || request.Action == OnlineRoomAction.Disconnect)
            {
                _cancelRequested = true; Publish(); return; // 在途POST不能直接Abort并假定没有创建房间。
            }
            if (!_busy) _command = request.Action;
        }
        private IEnumerator Lobby(string dungeon)
        {
            _phase = OnlineRoomPhase.Matching; _room = null; Status("正在查找同一副本的房间…");
            yield return Send("POST", "/api/online/rooms/match", JsonUtility.ToJson(new CreateRoomRequest { DungeonId = dungeon }));
            while (!_success || !ReadRoom())
            {
                if (!_account.Authenticated)
                {
                    _account.InvalidateSession(); ReleaseChoice(true, "登录已失效，请重新登录；未确认的大厅席位由服务器超时回收。");
                    Application.runInBackground = _originalBackground; _routine = null; yield break;
                }
                if (_httpStatus == 404 || _httpStatus == 405 || _httpStatus == 400 || _httpStatus == 401 || _httpStatus == 409)
                {
                    // 明确拒绝可回选择页；超时/500却可能已提交，必须先恢复当前房间，不能直接放行单机。
                    string message = _httpStatus == 404 || _httpStatus == 405 ? "匹配暂不可用，请选择单机或稍后再试。" : Failure();
                    ReleaseChoice(false, message); Application.runInBackground = _originalBackground; _routine = null; yield break;
                }
                _phase = OnlineRoomPhase.Error; _command = OnlineRoomAction.Observe;
                Status("匹配结果尚未确认。请重试恢复房间，或取消并确认已退出；不会直接进入单机。");
                while (!_cancelRequested && _command != OnlineRoomAction.Reconnect) yield return _connectionPoll;
                if (_cancelRequested)
                {
                    _cancelRequested = false; // 先消费本次意图；请求在途收到的新取消不能在失败后被清掉。
                    yield return Send("GET", "/api/online/rooms/current", null);
                    if (_httpStatus == 404) { ReleaseChoice(true, "已确认没有房间。"); Application.runInBackground = _originalBackground; _routine = null; yield break; }
                    if (!_success) continue;
                    _cancelRequested = true; // 找到房间后进入下方正常DELETE流程，不能仅关闭本地框。
                }
                else yield return Send("POST", "/api/online/rooms/match", JsonUtility.ToJson(new CreateRoomRequest { DungeonId = dungeon }));
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
                    if (_success || _httpStatus == 404) { _room = null; ReleaseChoice(true, "已退出房间。"); break; }
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
                        _phase = OnlineRoomPhase.Connected; Status("已进入联机连接测试区，可验证重连与返回；副本战斗尚未开放。");
                        yield return Connected();
                        break;
                    }
                    _phase = OnlineRoomPhase.Disconnected; Status(Failure("入场失败，可重连或断开返回据点；不会切入单机副本。"));
                    while (!_cancelRequested && _command != OnlineRoomAction.Reconnect) yield return _connectionPoll;
                    if (_cancelRequested) { ReleaseChoice(true, "已断开；原副本席位暂时保留，可从同一门重连。"); break; }
                    _command = OnlineRoomAction.Observe; continue;
                }
                _phase = _room.State == "Starting" ? OnlineRoomPhase.Starting : OnlineRoomPhase.Lobby;
                if (_cancelRequested && _room.State != "Open")
                { _cancelRequested = false; Status("副本正在启动，请等待入场结果；不会因一个成员取消而结束其他人的副本。"); }
                if (_command == OnlineRoomAction.Ready && _room.State == "Open")
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
                else if (_command == OnlineRoomAction.Start && _room.State == "Open")
                {
                    _command = OnlineRoomAction.Observe;
                    yield return Send("POST", RoomPath("/start"), JsonUtility.ToJson(new StartRoomRequest { ExpectedRevision = _room.ReadyRevision }));
                    if (!_success) Status(Failure());
                }
                else _command = OnlineRoomAction.Observe;
                Publish(); yield return _poll;
                yield return Send("POST", RoomPath("/presence"), "{}");
                if (_success) { ReadRoom(); if (_room.State == "Open") Status("房间已建立；所有成员准备后，由队长开始。"); }
                else if (_httpStatus == 404) { _room = null; ReleaseChoice(false, "房间已结束或大厅席位已过期，请重新匹配。"); break; }
                else Status(Failure()); // 网络失联保留当前RoomId，可取消/恢复；不伪造本地Ready。
            }
            StopConnection(); _phase = OnlineRoomPhase.None; _busy = false; Application.runInBackground = _originalBackground; _routine = null;
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
            if (ticket == null || ticket.RoomId != _room.RoomId || ticket.PlayerId != _account.PlayerId || ticket.RunId != _room.RunId || ticket.RoomEpoch != _room.RoomEpoch || _profile.ConnectionPrefab == null)
            { _code = "InvalidTicketReply"; yield break; }
            var root = Instantiate(_profile.ConnectionPrefab); _manager = root.GetComponent<RoomNetworkManager>(); _auth = root.GetComponent<JoinTicketAuthenticator>();
            _manager.Configure(ticket.Port); _auth.ConfigureClient(ticket); _manager.StartClient();
            double deadline = Time.realtimeSinceStartupAsDouble + 12;
            while (Time.realtimeSinceStartupAsDouble < deadline && NetworkClient.active && !_auth.ClientAuthenticated) yield return _connectionPoll;
            if (!_auth.ClientAuthenticated || !NetworkClient.isConnected) { _code = "AdmissionFailed"; StopConnection(); }
        }
        private IEnumerator Connected()
        {
            // 认证后不再持续HTTP轮询大厅；KCP/副本Heartbeat负责连接，UI离散命令才创建业务请求。
            while (!_cancelRequested)
            {
                if (_phase == OnlineRoomPhase.Connected && !NetworkClient.isConnected)
                { _phase = OnlineRoomPhase.Disconnected; Status("连接已断开。重连会领取新票，旧连接不能继续控制此角色。"); }
                if (_command == OnlineRoomAction.Reconnect && _phase == OnlineRoomPhase.Disconnected)
                {
                    _command = OnlineRoomAction.Observe; yield return Send("POST", RoomPath("/presence"), "{}");
                    if (_success && ReadRoom() && _room.State == "Running")
                    {
                        yield return Connect();
                        if (_auth != null && _auth.ClientAuthenticated && NetworkClient.isConnected) { _phase = OnlineRoomPhase.Connected; Status("已重新连接同一副本。"); }
                        else { _phase = OnlineRoomPhase.Disconnected; Status(Failure()); }
                    }
                    else { _phase = OnlineRoomPhase.Disconnected; Status("原副本已结束或业务服不可达，请返回据点。"); }
                }
                yield return _connectionPoll;
            }
            StopConnection();
            if (_inConnectionScene) { yield return ReturnToHub(); _inConnectionScene = false; }
            ReleaseChoice(true, "已返回据点，原副本席位暂时保留；同一门可重新连接。");
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
            bool self = false; for (int i = 0; i < view.Members.Length; i++) if (view.Members[i].PlayerId == _account.PlayerId) self = true;
            if (!self) return false; _room = view; return true;
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
                Members = members, SelfReady = SelfReady(), CanStart = _room != null && _room.State == "Open" && _room.LeaderPlayerId == _account.PlayerId && allReady,
                ActorId = ActorId, ConnectionEpoch = ConnectionEpoch, Replies = Replies,
                NativeSocket = _manager != null && _manager.GetComponent<NornKcpTransport>().UsesNativeSocket });
        }
        private IEnumerator EnterConnectionScene()
        {
            // 暂存据点原Scene与激活位，防止在线往返丢失Scene Preview/尚未保存的单机整备。
            // 内存代价是N2连接期间保留Hub；不读取/上传这些单机对象，不让它们继续模拟。
            _suspendedHub = SceneManager.GetActiveScene(); _hubRoots = _suspendedHub.GetRootGameObjects(); _hubRootActive = new bool[_hubRoots.Length];
            for (int i = 0; i < _hubRoots.Length; i++) { _hubRootActive[i] = _hubRoots[i].activeSelf; _hubRoots[i].SetActive(false); }
            Time.timeScale = 1; Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
#if UNITY_EDITOR
            var operation = EditorSceneManager.LoadSceneAsyncInPlayMode(_profile.ConnectionScene, new LoadSceneParameters(LoadSceneMode.Additive));
#else
            var operation = SceneManager.LoadSceneAsync(_profile.ConnectionScene, LoadSceneMode.Additive);
#endif
            if (operation != null) yield return operation;
            _connectionScene = SceneManager.GetSceneByPath(_profile.ConnectionScene); if (_connectionScene.IsValid()) SceneManager.SetActiveScene(_connectionScene);
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
            RestoreHub();
            if (_connectionScene.IsValid() && _connectionScene.isLoaded) yield return SceneManager.UnloadSceneAsync(_connectionScene);
            Time.timeScale = 1; Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        }
    }
}
