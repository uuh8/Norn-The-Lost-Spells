using System;
using System.Collections;
using Game.Character;
using Game.Core;
using Game.NetworkProtocol;
using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Multiplayer
{
    /// <summary>
    /// 新进程只有账户Profile，没有原Hub/开局请求。此入口仅恢复业务服已有的Run，
    /// 复用Ticket、Connected与个人退出链；菜单是独立navigation origin，不冒充可写Hub。
    /// </summary>
    public sealed partial class OnlineRoomClient
    {
        private string _recoveryPlayerId, _recoveryRunId;
        private uint _recoveryVersion, _recoveryAttempt, _recoverySessionGeneration;
        private int _recoveryMenuOwner;
        private Scene _recoveryMenu;
        private GameObject[] _recoveryMenuRoots;
        private bool[] _recoveryMenuRootActive;
        private AsyncOperation _recoverySceneLoad;
        private bool _exitMenuReady, _recoveryMenuSuspended;
        internal bool HasAccountRecovery => !string.IsNullOrEmpty(_recoveryPlayerId);
        internal bool IsRecoveryPlayer(string player) => !HasAccountRecovery || player == _recoveryPlayerId;
        // 从已有导航事实推导，Observe即可重放；菜单根停用本身不代表异步Scene已取得输入权。
        internal bool RecoveryMenuVisible => HasAccountRecovery && (!_recoveryMenuSuspended || !_inConnectionScene
            || !_connectionScene.IsValid() || !_connectionScene.isLoaded || SceneManager.GetActiveScene() != _connectionScene
            || !RecoveryContextMatches());
        private bool ExitNavigationReady => _exitHubReady || _exitMenuReady;
        private bool HasRecoveryGameplayScene => _inConnectionScene || _recoverySceneLoad != null
            || _connectionScene.IsValid() && _connectionScene.isLoaded;

        internal bool TryBeginAccountRecovery(OnlineProfileReply profile, int menuOwner, out string error)
        {
            error = null;
            if (_routine != null) { error = "当前恢复仍在进行，请等待本次结果。"; return false; }
            if (!TryCaptureAccountRecovery(profile, menuOwner, SceneManager.GetActiveScene(), out error)) return false;
            _originalBackground = Application.runInBackground; Application.runInBackground = true;
            BeginRecoveryAttempt(); _routine = StartCoroutine(RecoverAccountRoom()); return true;
        }

        private bool TryCaptureAccountRecovery(OnlineProfileReply profile, int menuOwner, Scene menu, out string error)
        {
            error = null;
            if (_account == null || !_account.Authenticated || profile == null || profile.PlayerId != _account.PlayerId
                || string.IsNullOrEmpty(profile.PlayerId) || profile.Version == 0 || menuOwner == 0
                || !menu.IsValid() || !menu.isLoaded || _profile == null)
            { error = "恢复需要本次登录的完整Profile与原主菜单。"; return false; }
            if (HasAccountRecovery)
            {
                // 已解锁Profile可能是退出/启动失败的新结果；仍由原恢复链确认，不能抢先进入Hub。
                if (profile.PlayerId != _recoveryPlayerId || menuOwner != _recoveryMenuOwner || menu != _recoveryMenu
                    || !string.IsNullOrEmpty(profile.ActiveRunId) && profile.ActiveRunId != _recoveryRunId)
                { error = "仍有原账号的副本恢复待确认，请使用原账号和原主菜单重试。"; return false; }
                return true;
            }
            if (!profile.AssetsLocked || string.IsNullOrEmpty(profile.ActiveRunId) || _suspendedHub.IsValid()
                || _account.HasPendingAccountCommand)
            { error = "当前Profile没有可恢复的独立副本，或原资产操作仍待确认。"; return false; }
            _recoveryPlayerId = profile.PlayerId; _recoveryRunId = profile.ActiveRunId; _recoveryVersion = profile.Version;
            _recoveryMenu = menu; _recoveryMenuOwner = menuOwner;
            // 0是常驻DungeonEntryDialog的默认PortalId。独立owner使入场前的Error/Starting只由菜单显示。
            _portal = GetInstanceID(); ClearSinglePlayerCandidate(); ResetMemberExit();
            OnlineAccountInventoryHubScope.TryMarkAssetsLocked(_account.GetInstanceID(), _recoveryPlayerId, _recoveryRunId);
            _account.SetRoomRecoveryState(true, false, "发现未结束的副本，可恢复当前副本。"); return true;
        }

        private void BeginRecoveryAttempt()
        {
            _recoveryAttempt++; _recoverySessionGeneration = _account.SessionGeneration;
            _cancelRequested = false; _command = OnlineRoomAction.Observe;
            // 重试只重开查询预算，保留原退出Command/receipt，不把上次三次失败永久带入新尝试。
            _exitQueries = _exitProfileAttempts = 0; _exitNextQuery = _exitNextProfile = 0;
            if (_exitRequested) _exitResolveRequested = true;
            _account.SetRoomRecoveryState(true, true, "正在确认原副本…");
        }
        private bool MatchesRecoveryAttempt(uint attempt, uint session)
            => HasAccountRecovery && attempt == _recoveryAttempt && _account != null && _account.Authenticated
                && session == _account.SessionGeneration && _account.PlayerId == _recoveryPlayerId;
        private bool RecoveryContextMatches()
            => !HasAccountRecovery || MatchesRecoveryAttempt(_recoveryAttempt, _recoverySessionGeneration);
        private bool RecoveryAdmissionMatches()
        {
            var identity = _auth != null ? _auth.ClientIdentity : null;
            if (identity == null || _room == null || identity.PlayerId != _recoveryPlayerId
                || identity.RoomId != _room.RoomId || identity.RunId != _recoveryRunId || identity.RoomEpoch != _room.RoomEpoch
                || identity.ConnectionEpoch == 0) return false;
            foreach (var member in _room.Members)
                if (member.PlayerId == _recoveryPlayerId) return identity.ActorId == (ulong)(member.Seat + 1);
            return false;
        }

        private bool TryAcceptRecoveryRoom(RoomReply view, out bool released, out string error)
        {
            released = false; error = "当前房间与原账号副本不一致，保留恢复状态。";
            if (!HasAccountRecovery || _account == null || !_account.Authenticated || _account.PlayerId != _recoveryPlayerId
                || view == null || string.IsNullOrEmpty(view.RoomId) || view.Members == null
                || view.Members.Length < 1 || view.Members.Length > 4) return false;
            int selfCount = 0; bool leader = false;
            for (int i = 0; i < view.Members.Length; i++)
            {
                var member = view.Members[i];
                if (member == null || string.IsNullOrEmpty(member.PlayerId) || member.Seat < 0 || member.Seat > 3) return false;
                for (int j = 0; j < i; j++)
                    if (view.Members[j].PlayerId == member.PlayerId || view.Members[j].Seat == member.Seat) return false;
                if (member.PlayerId == _recoveryPlayerId) selfCount++;
                leader |= member.PlayerId == view.LeaderPlayerId;
            }
            if (selfCount != 1 || !leader || view.SinglePlayer && (view.Members.Length != 1 || view.LeaderPlayerId != _recoveryPlayerId)) return false;
            if (_room != null && view.RoomId != _room.RoomId) return false;
            // FailStart会将同一Party还原Open并提升Epoch，没有member-exit receipt。
            // 这里只声明需要读回Profile，不根据Open/404自行撤销资产锁。
            if (view.State == "Open" && string.IsNullOrEmpty(view.RunId))
            {
                if (_room != null && view.RoomEpoch <= _room.RoomEpoch) return false;
                released = true; error = null; return true;
            }
            var catalog = Resources.Load<OnlineDungeonCatalog>(OnlineDungeonCatalog.ResourcePath);
            if ((view.State != "Starting" && view.State != "Running") || view.RunId != _recoveryRunId || view.RoomEpoch == 0
                || catalog == null || !catalog.TryGet(view.DungeonId, out _)
                || _room != null && (view.DungeonId != _room.DungeonId || view.RoomEpoch < _room.RoomEpoch
                    || view.SinglePlayer != _room.SinglePlayer)) return false;
            if (_room != null)
                foreach (var prior in _room.Members)
                    if (prior.PlayerId == _recoveryPlayerId)
                        foreach (var current in view.Members)
                            if (current.PlayerId == _recoveryPlayerId && current.Seat != prior.Seat) return false;
            _room = view; _singlePlayerEntry = view.SinglePlayer;
            OnlineAccountInventoryHubScope.TryMarkAssetsLocked(_account.GetInstanceID(), _recoveryPlayerId, _recoveryRunId);
            error = null; return true;
        }

        private IEnumerator RecoverAccountRoom()
        {
            uint attempt = _recoveryAttempt, session = _recoverySessionGeneration;
            try
            {
                // Disable不能取消Unity原生Scene加载。Enable等待原操作，而不是再加载一份副本。
                if (_recoverySceneLoad != null && !_recoverySceneLoad.isDone) yield return _recoverySceneLoad;
                if (!MatchesRecoveryAttempt(attempt, session)) yield break;
                if (_recoverySceneLoad != null)
                {
                    _connectionScene = SceneManager.GetSceneByPath(_profile.ConnectionScene); _recoverySceneLoad = null;
                    _inConnectionScene |= _connectionScene.IsValid() && _connectionScene.isLoaded;
                }
                _phase = _inConnectionScene ? OnlineRoomPhase.Disconnected : OnlineRoomPhase.Matching;
                Status("正在恢复账号所属的原副本…");
                while (MatchesRecoveryAttempt(attempt, session))
                {
                    yield return Send("GET", "/api/online/rooms/current", null);
                    if (!MatchesRecoveryAttempt(attempt, session)) yield break;
                    bool released = false; string error = null;
                    if (_success)
                    {
                        RoomReply current = null;
                        try { current = JsonUtility.FromJson<RoomReply>(_body); } catch (ArgumentException) { }
                        if (!TryAcceptRecoveryRoom(current, out released, out error))
                        { PauseAccountRecovery(error); yield break; }
                    }
                    else if (_httpStatus != 404) { PauseAccountRecovery(Failure()); yield break; }
                    if (released || _httpStatus == 404)
                    {
                        if (HasRecoveryGameplayScene || _exitRequested)
                        {
                            // 已接入Gameplay的原现场仍要求个人收据；不能拿房间消失冒充保存成功。
                            yield return ProbeTerminalMemberExit();
                            if (!MatchesRecoveryAttempt(attempt, session)) yield break;
                            if (_exitCommitted) { yield return Connected(); yield break; }
                            PauseAccountRecovery("原副本已不可见，但个人退出尚未确认；保留现场，请重试恢复。"); yield break;
                        }
                        yield return Send("GET", "/api/online/profile", null);
                        if (!MatchesRecoveryAttempt(attempt, session)) yield break;
                        OnlineProfileReply profile = null;
                        if (_success) try { profile = JsonUtility.FromJson<OnlineProfileReply>(_body); } catch (ArgumentException) { }
                        if (!TryConfirmRecoveryReleased(profile, out error)) PauseAccountRecovery(error);
                        yield break;
                    }
                    if (_room.State == "Starting")
                    {
                        _phase = OnlineRoomPhase.Starting; Status("原副本正在启动，等待同一Run就绪…");
                        yield return _poll;
                        if (!MatchesRecoveryAttempt(attempt, session)) yield break;
                        continue;
                    }
                    // 状态包可能在旧进程退出前丢失；先查个人终态，404才允许申请新票。
                    yield return ProbeTerminalMemberExit();
                    if (!MatchesRecoveryAttempt(attempt, session)) yield break;
                    if (_exitCommitted) { yield return Connected(); yield break; }
                    if (!_terminalExitProbeAllowsReconnect)
                    { PauseAccountRecovery("原副本的离局结果尚未确认，请重试恢复。"); yield break; }
                    yield return Connect();
                    if (!MatchesRecoveryAttempt(attempt, session)) { StopConnection(); yield break; }
                    if (_auth == null || !_auth.ClientAuthenticated || !NetworkClient.isConnected)
                    {
                        // 保留Replica不代表副本Dialog仍持有导航。Disable恢复主菜单后连接失败，
                        // 必须结束本次Busy，让原Enter可重试；否则会等待被菜单屏蔽的Reconnect命令。
                        if (!_inConnectionScene || !_recoveryMenuSuspended)
                        { PauseAccountRecovery(Failure("原副本暂未连接，请重试恢复。")); yield break; }
                        _phase = OnlineRoomPhase.Disconnected; Status(Failure()); yield return Connected(); yield break;
                    }
                    yield return EnterConnectionScene();
                    if (!MatchesRecoveryAttempt(attempt, session)) { StopConnection(); yield break; }
                    _inConnectionScene = true; _phase = OnlineRoomPhase.Connected;
                    Status(_singlePlayerEntry ? "已恢复原单人副本。" : "已恢复原合作副本。");
                    yield return Connected(); yield break;
                }
            }
            finally
            {
                _busy = false; _routine = null; Application.runInBackground = _originalBackground;
                if (HasAccountRecovery)
                {
                    if (_exitMenuReady) FinishAccountRecovery("已保存，可进入据点。");
                    else
                    {
                        if (!RecoveryContextMatches()) { StopConnection(); RestoreRecoveryMenu(); }
                        _account.SetRoomRecoveryState(true, false,
                            RecoveryContextMatches() ? _status : "原副本恢复已暂停，请登录原账号后重试。");
                    }
                }
            }
        }

        private void PauseAccountRecovery(string message)
        {
            // 主菜单尚未挂起时只让它接收错误和重试；不弹常驻DungeonEntryDialog抢同一按键。
            _phase = _inConnectionScene ? OnlineRoomPhase.Disconnected : OnlineRoomPhase.Error;
            _busy = false; Status(message ?? "原副本结果尚未确认，请重试恢复。");
            _account.SetRoomRecoveryState(true, false, _status);
        }
        private bool TryConfirmRecoveryReleased(OnlineProfileReply profile, out string error)
        {
            error = "原副本资产仍被占用或结果未知，请重试恢复。";
            if (!HasAccountRecovery || HasRecoveryGameplayScene || _exitRequested || _account == null
                || profile == null || profile.PlayerId != _recoveryPlayerId || profile.AssetsLocked
                || !string.IsNullOrEmpty(profile.ActiveRunId) || !_account.TryAdoptVerifiedHubProfile(profile, _recoveryVersion, out error)) return false;
            RestoreRecoveryMenu(); _room = null; ReleaseChoice(true, "原副本已结束，账号资产已确认，可进入据点。");
            FinishAccountRecovery("原副本已结束，账号资产已确认，可进入据点。"); return true;
        }
        private bool TryConfirmRecoveryExitProfile(OnlineProfileReply profile, out string error)
        {
            error = "退出或账号资产尚未确认，暂不恢复主菜单。";
            return HasAccountRecovery && _exitRequested && _exitCommitted && MatchesMemberExitPlayer()
                && _recoveryMenu.IsValid() && _recoveryMenu.isLoaded && profile != null && profile.PlayerId == _recoveryPlayerId
                && _account.TryAdoptVerifiedHubProfile(profile, _exitCommittedVersion, out error);
        }
        private IEnumerator PrepareMemberExitMenu(OnlineProfileReply profile)
        {
            if (!TryConfirmRecoveryExitProfile(profile, out var error)) { Status(error); yield break; }
            uint attempt = _recoveryAttempt, session = _recoverySessionGeneration;
            StopConnection(); OnlineWorldBootstrap.ShutdownActive();
            if (_connectionScene.IsValid() && _connectionScene.isLoaded) yield return SceneManager.UnloadSceneAsync(_connectionScene);
            if (!MatchesRecoveryAttempt(attempt, session) || !MatchesMemberExitPlayer()) { ShowMemberExitAccountMismatch(); yield break; }
            RestoreRecoveryMenu(); _inConnectionScene = false;
            _exitMenuReady = _recoveryMenu.IsValid() && _recoveryMenu.isLoaded && SceneManager.GetActiveScene() == _recoveryMenu
                && !_account.HasActiveRun && _account.InventoryVersion >= _exitCommittedVersion;
            if (_exitMenuReady) _account.SetRoomRecoveryState(true, false, "已保存，可进入据点。");
        }
        private void SuspendRecoveryMenu()
        {
            _recoveryMenuSuspended = true;
            if (_recoveryMenuRoots == null)
            {
                _recoveryMenuRoots = _recoveryMenu.GetRootGameObjects(); _recoveryMenuRootActive = new bool[_recoveryMenuRoots.Length];
                // 先冻结全部激活位再执行OnDisable，避免并列根回调改写原状态。
                for (int i = 0; i < _recoveryMenuRoots.Length; i++) _recoveryMenuRootActive[i] = _recoveryMenuRoots[i].activeSelf;
            }
            for (int i = 0; i < _recoveryMenuRoots.Length; i++) if (_recoveryMenuRoots[i] != null) _recoveryMenuRoots[i].SetActive(false);
        }
        private void RestoreRecoveryMenu()
        {
            if (!_recoveryMenu.IsValid() || !_recoveryMenu.isLoaded) return;
            _recoveryMenuSuspended = false;
            // 先通过可重放账户视图交还导航权，再激活菜单；保留的Replica可能仍持续发布Gameplay事件。
            _account?.SetRoomRecoveryState(true, _account.Busy, _status);
            SceneManager.SetActiveScene(_recoveryMenu);
            if (_recoveryMenuRoots != null)
                for (int i = 0; i < _recoveryMenuRoots.Length; i++) if (_recoveryMenuRoots[i] != null) _recoveryMenuRoots[i].SetActive(_recoveryMenuRootActive[i]);
            Time.timeScale = 1; Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }
        private void FinishAccountRecovery(string status)
        {
            _recoveryPlayerId = _recoveryRunId = null; _recoveryMenuOwner = 0; _recoveryVersion = 0;
            _recoveryMenu = default; _recoveryMenuRoots = null; _recoveryMenuRootActive = null;
            _recoveryMenuSuspended = false;
            _recoverySceneLoad = null; ClearSinglePlayerCandidate();
            _account.SetRoomRecoveryState(false, false, status);
        }
    }
}
