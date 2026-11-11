using System;
using System.Collections;
using Game.Core;
using Game.NetworkProtocol;
using UnityEngine;

namespace Game.Multiplayer
{
    public sealed partial class OnlineRoomClient
    {
        private bool _singlePlayerEntry, _singleEntryPosted;
        private string _singleEntryPlayerId, _singleEntryBody;
        private SinglePlayerEntryRequest _singleEntryCommand;

        private void OnSinglePlayer(DungeonSinglePlayerRequestedEvent request)
            => BeginPortalRoom(request.PortalId, request.DestinationKey, true);

        private void BeginPortalRoom(int portal, string destination, bool singlePlayer)
        {
            if (_routine != null) { Publish(); return; }
            if (_exitRequested && !_exitHubReady) { Status("上次离局结果未确认，不能开始另一局。"); return; }
            _portal = portal;
            if (_account == null || !_account.Authenticated || _profile == null || !_profile.TryDungeon(destination, out var dungeon))
            { ReleaseChoice(false, "副本配置或登录无效，请重新登录后重试。"); return; }
            bool resume = _singlePlayerEntry && _singleEntryPosted && _singleEntryBody != null && !_exitHubReady;
            if (resume && (!singlePlayer || !SinglePlayerContextMatches() || _singleEntryCommand.DungeonId != dungeon))
            { ReleaseChoice(false, "原单人入场结果未确认，请用原账号与原入口恢复。"); return; }
            if (!resume && !_account.CanBeginServerRun(out var error)) { ReleaseChoice(false, error); return; }
            ResetMemberExit();
            if (!resume)
            {
                ClearSinglePlayerCandidate(); _singlePlayerEntry = singlePlayer;
                if (singlePlayer)
                {
                    _singleEntryPlayerId = _account.PlayerId;
                    _singleEntryCommand = new SinglePlayerEntryRequest { CommandId = Guid.NewGuid().ToString("N"), DungeonId = dungeon };
                }
            }
            _cancelRequested = false; _command = OnlineRoomAction.Observe;
            _originalBackground = Application.runInBackground; Application.runInBackground = true;
            _routine = StartCoroutine(Lobby(dungeon));
        }

        private bool SinglePlayerContextMatches() => _account != null && _account.Authenticated
            && !string.IsNullOrEmpty(_singleEntryPlayerId) && _account.PlayerId == _singleEntryPlayerId;

        private IEnumerator SendPortalEntry(string dungeon)
        {
            if (!_singlePlayerEntry)
            { yield return Send("POST", "/api/online/rooms/match", JsonUtility.ToJson(new CreateRoomRequest { DungeonId = dungeon })); yield break; }
            if (!SinglePlayerContextMatches() || _singleEntryCommand == null || _singleEntryCommand.DungeonId != dungeon)
            { RejectSinglePlayerContext(); yield break; }
            if (_singleEntryBody == null)
            {
                // 本次真实Profile读取固定ExpectedVersion；HTTP未知后不再刷新或重新序列化候选。
                yield return Send("GET", "/api/online/profile", null);
                if (!_success) yield break;
                OnlineProfileReply profile;
                try { profile = JsonUtility.FromJson<OnlineProfileReply>(_body); }
                catch (ArgumentException) { RejectSinglePlayerContext(); yield break; }
                if (!SinglePlayerContextMatches() || profile == null || profile.PlayerId != _singleEntryPlayerId
                    || profile.Version == 0 || profile.AssetsLocked || !string.IsNullOrEmpty(profile.ActiveRunId))
                { RejectSinglePlayerContext(); yield break; }
                _singleEntryCommand.ExpectedInventoryVersion = profile.Version;
                _singleEntryBody = JsonUtility.ToJson(_singleEntryCommand);
            }
            if (!SinglePlayerContextMatches()) { RejectSinglePlayerContext(); yield break; }
            _singleEntryPosted = true;
            yield return Send("POST", "/api/online/rooms/single-entry", _singleEntryBody);
        }

        private void RejectSinglePlayerContext()
        { _success = false; _httpStatus = 409; _code = "SinglePlayerEntryContextUnavailable"; _body = null; }
        private void ClearSinglePlayerCandidate()
        { _singlePlayerEntry = _singleEntryPosted = false; _singleEntryPlayerId = _singleEntryBody = null; _singleEntryCommand = null; }
    }
}
