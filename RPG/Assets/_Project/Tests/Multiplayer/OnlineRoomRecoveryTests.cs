using System;
using System.Reflection;
using Game.Core;
using Game.NetworkProtocol;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 冷启动恢复沿真实账户/房间边界验证：固定旧Run，重新取得Session票，不建立第二个入场命令。
    /// 全部使用未激活Client和内存Scene；不启动HTTP/KCP，不读取或改写本地存档。
    /// </summary>
    public sealed class OnlineRoomRecoveryTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Player = "11111111111111111111111111111111";
        private const string Other = "22222222222222222222222222222222";
        private const string Run = "33333333333333333333333333333333";
        private const string Room = "44444444444444444444444444444444";
        private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private GameObject _root;
        private OnlineAccountClient _account;
        private OnlineRoomClient _client;
        private Scene _menu, _original;
        private OnlineLobbyChangedEvent _lobby;
        private CursorLockMode _cursorLock;
        private bool _cursorVisible;
        private float _timeScale;

        [SetUp] public void CreateInactiveApplicationServices()
        {
            ResetScope();
            _cursorLock = Cursor.lockState; _cursorVisible = Cursor.visible; _timeScale = Time.timeScale;
            _original = SceneManager.GetActiveScene(); _menu = EditorSceneManager.NewPreviewScene();
            _menu.name = "RecoveryMenuFixture";
            _root = new GameObject("Inactive cold recovery services"); _root.SetActive(false);
            _account = _root.AddComponent<OnlineAccountClient>(); _client = _root.AddComponent<OnlineRoomClient>();
            Set(_account, "_token", "fixture-session-no-credential");
            Set(_account, "_expiresUnixMilliseconds", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds());
            Set(_account, "_manifest", new ContentManifest { Hash = Hash, ProtocolVersion = ProtocolCompatibility.CurrentVersion });
            Set(_account, "_view", Profile()); Set(_client, "_account", _account);
            Set(_client, "_profile", Resources.Load<OnlineRoomProfile>("Online/RoomClientProfile"));
            EventBus<OnlineLobbyChangedEvent>.Subscribe(CaptureLobby);
        }
        [TearDown] public void ReleaseOnlyFixtureState()
        {
            EventBus<OnlineLobbyChangedEvent>.Unsubscribe(CaptureLobby);
            if (_original.IsValid() && _original.isLoaded) SceneManager.SetActiveScene(_original);
            if (_menu.IsValid()) EditorSceneManager.ClosePreviewScene(_menu);
            if (_root != null) Object.DestroyImmediate(_root);
            Cursor.lockState = _cursorLock; Cursor.visible = _cursorVisible; Time.timeScale = _timeScale;
            ResetScope();
        }
        private void CaptureLobby(OnlineLobbyChangedEvent view) => _lobby = view;
        private static void ResetScope() => typeof(OnlineAccountInventoryHubScope)
            .GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        private static OnlineProfileReply Profile(string player = Player, string run = Run, bool locked = true, uint version = 7)
            => new OnlineProfileReply { PlayerId = player, ActiveRunId = run, AssetsLocked = locked,
                Version = version, Gold = 1200, ContentManifestHash = Hash, Items = Array.Empty<OnlineItemReply>() };
        private static RoomReply Current(bool single = false) => new RoomReply
        {
            RoomId = Room, RunId = Run, RoomEpoch = 3, DungeonId = "p7", State = "Running",
            SinglePlayer = single, LeaderPlayerId = Player,
            Members = single ? new[] { new RoomMemberReply { PlayerId = Player, Seat = 0 } }
                : new[] { new RoomMemberReply { PlayerId = Player, Seat = 0 }, new RoomMemberReply { PlayerId = Other, Seat = 1 } }
        };
        private static void Set(object target, string field, object value)
        {
            var found = target.GetType().GetField(field, Private); Assert.That(found, Is.Not.Null, field);
            found.SetValue(target, value);
        }
        private static T Get<T>(object target, string field)
        {
            var found = target.GetType().GetField(field, Private); Assert.That(found, Is.Not.Null, field);
            return (T)found.GetValue(target);
        }
        private static object Call(object target, string method, params object[] args)
        {
            var found = target.GetType().GetMethod(method, Private);
            Assert.That(found, Is.Not.Null, "缺少正式冷恢复边界：" + method);
            return found.Invoke(target, args);
        }
        private bool Capture(OnlineProfileReply profile = null, int owner = 81)
            => (bool)Call(_client, "TryCaptureAccountRecovery", profile ?? Profile(), owner, _menu, null);
        private bool Accept(RoomReply room, out bool released)
        {
            object[] args = { room, false, null };
            bool accepted = (bool)Call(_client, "TryAcceptRecoveryRoom", args); released = (bool)args[1]; return accepted;
        }

        [Test] public void ColdActiveProfileCapturesOriginalRunWithoutArmingHubOrMintingEntry()
        {
            Assert.That(Capture(), Is.True);
            Assert.That(Get<string>(_client, "_recoveryPlayerId"), Is.EqualTo(Player));
            Assert.That(Get<string>(_client, "_recoveryRunId"), Is.EqualTo(Run));
            Assert.That(Get<Scene>(_client, "_recoveryMenu"), Is.EqualTo(_menu));
            Assert.That(Get<Scene>(_client, "_suspendedHub").IsValid(), Is.False,
                "菜单没有Hub consumer，不能借假Hub放行PrepareMemberExitHub。");
            Assert.That(Get<object>(_client, "_singleEntryCommand"), Is.Null);
            Assert.That((bool)Call(_account, "TryBeginHubEntry", "SinglePlayer_Hub", null), Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void TrustedCurrentRoomRestoresBothModesWithoutANewEntryCommand(bool single)
        {
            Assert.That(Capture(), Is.True);
            Assert.That(Accept(Current(single), out var released), Is.True);
            Assert.That(released, Is.False);
            Assert.That(_client.RoomId, Is.EqualTo(Room));
            Assert.That(Get<bool>(_client, "_singlePlayerEntry"), Is.EqualTo(single));
            Assert.That(Get<object>(_client, "_singleEntryCommand"), Is.Null,
                "恢复只接受服务器已有Run，不伪造单人开局命令绕过ReadRoom。");
            Assert.That(Get<string>(_client, "_singleEntryBody"), Is.Null);
        }

        [TestCase("different-run")] [TestCase("missing-self")] [TestCase("duplicate-self")]
        [TestCase("zero-epoch")] [TestCase("unknown-dungeon")] [TestCase("single-other-leader")]
        [TestCase("single-extra-member")] [TestCase("different-player-session")]
        public void CurrentReplyCannotReplaceTheOriginalRecoveryIdentity(string corruption)
        {
            Assert.That(Capture(), Is.True); var room = Current();
            switch (corruption)
            {
                case "different-run": room.RunId = Other; break;
                case "missing-self": room.Members = new[] { new RoomMemberReply { PlayerId = Other } }; break;
                case "duplicate-self": room.Members[1].PlayerId = Player; break;
                case "zero-epoch": room.RoomEpoch = 0; break;
                case "unknown-dungeon": room.DungeonId = "unknown-dungeon"; break;
                case "single-other-leader": room = Current(true); room.LeaderPlayerId = Other; break;
                case "single-extra-member": room.SinglePlayer = true; break;
                case "different-player-session": Set(_account, "_view", Profile(Other)); break;
            }
            Assert.That(Accept(room, out _), Is.False);
            Assert.That(_client.RoomId, Is.Null);
            Assert.That(Get<string>(_client, "_recoveryPlayerId"), Is.EqualTo(Player));
            Assert.That(Get<string>(_client, "_recoveryRunId"), Is.EqualTo(Run));
        }

        [Test] public void RetryCannotCaptureAnotherMenuOrReplaceTheOriginalPlayerRun()
        {
            Assert.That(Capture(), Is.True); Assert.That(Capture(), Is.True);
            Assert.That(Capture(Profile(Other)), Is.False); Assert.That(Capture(Profile(run: Other)), Is.False);
            Assert.That(Capture(owner: 82), Is.False);
            Assert.That(Get<string>(_client, "_recoveryRunId"), Is.EqualTo(Run));
            Assert.That(Get<Scene>(_client, "_suspendedHub").IsValid(), Is.False);
        }

        [TestCase("room")] [TestCase("seat")] [TestCase("mode")]
        public void RepeatedCurrentReplyKeepsTheOriginalRoomAndActorSeat(string changed)
        {
            Assert.That(Capture(), Is.True); Assert.That(Accept(Current(), out _), Is.True);
            var next = Current();
            if (changed == "room") next.RoomId = Other;
            else if (changed == "seat") { next.Members[0].Seat = 1; next.Members[1].Seat = 0; }
            else next = Current(true);
            Assert.That(Accept(next, out _), Is.False);
            Assert.That(_client.RoomId, Is.EqualTo(Room));
        }

        [Test] public void AccountFinallyCannotUnlockOtherMutationsWhileRoomRequestIsRunning()
        {
            Assert.That(Capture(), Is.True);
            Call(_account, "SetRoomRecoveryState", true, true, "恢复中");
            Call(_account, "Publish", "Account请求结束", false);
            Assert.That(_lobby.Busy, Is.True, "Room HTTP仍在途时Account finally的false不能启用重复Enter/Logout。");
            Call(_account, "SetRoomRecoveryState", true, false, "可以重试");
            Assert.That(_lobby.Busy, Is.False, "未知结果允许原Enter重试，不能永久busy。");
            object boxed = _lobby;
            Assert.That((bool)boxed.GetType().GetField("RecoveryPending").GetValue(boxed), Is.True);
            Assert.That((bool)Call(_account, "CanBeginServerRun", new object[] { null }), Is.False);
        }

        [Test] public void SessionGenerationRejectsLateHttpWithoutInvalidatingTheNewLogin()
        {
            Assert.That(Capture(), Is.True);
            Call(_client, "BeginRecoveryAttempt");
            uint attempt = Get<uint>(_client, "_recoveryAttempt");
            uint session = Get<uint>(_account, "_sessionGeneration");
            Assert.That((bool)Call(_client, "MatchesRecoveryAttempt", attempt, session), Is.True);
            Set(_account, "_sessionGeneration", session + 1);
            Assert.That((bool)Call(_client, "MatchesRecoveryAttempt", attempt, session), Is.False);
            Call(_account, "InvalidateSessionIfCurrent", session);
            Assert.That(_account.Authenticated, Is.True, "旧请求401不能清除刚登录的新Session。");
        }

        [Test] public void DisablePreservesColdCandidateAndInvalidatesTheInFlightAttempt()
        {
            Assert.That(Capture(), Is.True); Assert.That(Accept(Current(), out _), Is.True);
            Call(_client, "BeginRecoveryAttempt"); uint attempt = Get<uint>(_client, "_recoveryAttempt");
            // Preview不能成为activeScene；本例只验证停用的候选，不测试菜单根激活。
            Set(_client, "_recoveryMenu", _original);
            Call(_client, "OnDisable");
            Assert.That(Get<string>(_client, "_recoveryPlayerId"), Is.EqualTo(Player));
            Assert.That(Get<string>(_client, "_recoveryRunId"), Is.EqualTo(Run));
            Assert.That(_client.RoomId, Is.EqualTo(Room), "临时停用不能丢掉旧Run再走创建入口。");
            Assert.That(Get<uint>(_client, "_recoveryAttempt"), Is.GreaterThan(attempt));
            Assert.That(Get<Scene>(_client, "_suspendedHub").IsValid(), Is.False);
        }

        [Test] public void StartingFailureRequiresFreshUnlockedSamePlayerProfileWithoutExitReceipt()
        {
            Assert.That(Capture(), Is.True);
            var starting = Current(true); starting.State = "Starting";
            Assert.That(Accept(starting, out _), Is.True);
            var reopened = Current(true); reopened.State = "Open"; reopened.RunId = ""; reopened.RoomEpoch = 4;
            Assert.That(Accept(reopened, out var released), Is.True); Assert.That(released, Is.True);
            Assert.That((bool)Call(_client, "TryConfirmRecoveryReleased", Profile(), null), Is.False);
            Assert.That((bool)Call(_client, "TryConfirmRecoveryReleased", Profile(Other, "", false, 8), null), Is.False);
            Set(_client, "_recoveryMenu", _original);
            Assert.That((bool)Call(_client, "TryConfirmRecoveryReleased", Profile(run: "", locked: false, version: 8), null), Is.True);
            Assert.That(Get<bool>(_client, "_exitCommitted"), Is.False, "启动失败不伪造个人退出收据。");
            Assert.That((bool)Call(_account, "TryBeginHubEntry", "SinglePlayer_Hub", null), Is.True);
        }

        [TestCase(false, Player, false, 8u)]
        [TestCase(true, Other, false, 8u)]
        [TestCase(true, Player, true, 8u)]
        [TestCase(true, Player, false, 6u)]
        public void MenuReturnCannotBeConfirmedByIncompleteOrForeignExit(bool committed, string player, bool locked, uint version)
        {
            Assert.That(Capture(), Is.True); Assert.That(Accept(Current(), out _), Is.True);
            Set(_client, "_exitPlayerId", Player); Set(_client, "_exitRequested", true);
            Set(_client, "_exitCommitted", committed); Set(_client, "_exitCommittedVersion", 8u);
            Assert.That((bool)Call(_client, "TryConfirmRecoveryExitProfile", Profile(player, locked ? Run : "", locked, version), null), Is.False);
            Assert.That(Get<bool>(_client, "_exitMenuReady"), Is.False);
            Assert.That(Get<bool>(_client, "_exitHubReady"), Is.False);
        }

        [Test] public void ConfirmedExitCanPrepareMenuWithoutClaimingItHasHubConsumers()
        {
            Assert.That(Capture(), Is.True); Assert.That(Accept(Current(), out _), Is.True);
            Set(_client, "_exitPlayerId", Player); Set(_client, "_exitRequested", true);
            Set(_client, "_exitCommitted", true); Set(_client, "_exitCommittedVersion", 8u);
            Assert.That((bool)Call(_client, "TryConfirmRecoveryExitProfile", Profile(run: "", locked: false, version: 8), null), Is.True);
            Assert.That(Get<bool>(_client, "_exitHubReady"), Is.False);
            Assert.That(Get<Scene>(_client, "_suspendedHub").IsValid(), Is.False);
        }

        [Test] public void ColdMenuRoundTripPreservesRootActivationAndNeverCallsItAHub()
        {
            var visible = new GameObject("Visible menu root"); var hidden = new GameObject("Hidden menu root");
            SceneManager.MoveGameObjectToScene(visible, _menu); SceneManager.MoveGameObjectToScene(hidden, _menu);
            hidden.SetActive(false);
            Assert.That(Capture(), Is.True); Call(_client, "SuspendRecoveryMenu");
            Assert.That(visible.activeSelf, Is.False); Assert.That(hidden.activeSelf, Is.False);
            // Preview Scene不能作为Editor activeScene；仅替换恢复目标，根快照仍来自真实菜单挂起。
            Set(_client, "_recoveryMenu", _original); Call(_client, "RestoreRecoveryMenu");
            Assert.That(visible.activeSelf, Is.True); Assert.That(hidden.activeSelf, Is.False);
            Assert.That(Get<Scene>(_client, "_suspendedHub").IsValid(), Is.False);
        }

        [Test] public void SceneLoadedAcrossDisableCannotTakeTheStartingFailureShortcut()
        {
            Assert.That(Capture(), Is.True); Assert.That(Accept(Current(), out _), Is.True);
            // LoadSceneAsync跨Disable已完成，但旧Coroutine尚未写_inConnectionScene。
            Set(_client, "_connectionScene", _menu); Set(_client, "_inConnectionScene", false);
            Assert.That((bool)Call(_client, "TryConfirmRecoveryReleased", Profile(run: "", locked: false, version: 8), null), Is.False,
                "真实副本Scene仍在时必须查退出收据并卸载，不能当作尚未进图的启动失败清candidate。");
            Assert.That(Get<string>(_client, "_recoveryRunId"), Is.EqualTo(Run));
        }

        [Test] public void SecondColdRecoveryCannotInheritThePreviousMenuExitSuccess()
        {
            Assert.That(Capture(), Is.True); Set(_client, "_exitMenuReady", true);
            Call(_client, "FinishAccountRecovery", "已保存");
            Assert.That(Capture(), Is.True);
            Assert.That(Get<bool>(_client, "_exitMenuReady"), Is.False,
                "下一局的Connected不能跳过receipt/profile确认直接结束。");
        }

        [Test] public void RetryReopensOnlyTheQueryBudgetAndKeepsTheCommittedExitIdentity()
        {
            Assert.That(Capture(), Is.True);
            Set(_client, "_exitCommitted", true); Set(_client, "_exitRequested", true);
            Set(_client, "_exitCommandId", Other); Set(_client, "_exitCommittedVersion", 8u);
            Set(_client, "_exitQueries", 3); Set(_client, "_exitProfileAttempts", 3);
            Call(_client, "BeginRecoveryAttempt");
            Assert.That(Get<int>(_client, "_exitProfileAttempts"), Is.EqualTo(0));
            Assert.That(Get<string>(_client, "_exitCommandId"), Is.EqualTo(Other));
            Assert.That(Get<bool>(_client, "_exitCommitted"), Is.True);
            Assert.That(Get<uint>(_client, "_exitCommittedVersion"), Is.EqualTo(8u));
        }

        [Test] public void ConnectFailureAfterDisableReturnsToMenuRetryWhileKeepingTheLoadedReplica()
        {
            var replica = EditorSceneManager.NewPreviewScene();
            System.Collections.IEnumerator recovery = null;
            try
            {
                Assert.That(Capture(), Is.True);
                // OnDisable已经恢复菜单，但为保留原Run现场没有卸载Replica。
                // Scene存在不等于副本Dialog拥有输入；此时恢复入口仍是账号菜单的Enter。
                Set(_client, "_connectionScene", replica); Set(_client, "_inConnectionScene", true);
                Set(_client, "_recoveryMenuSuspended", false);
                Call(_client, "BeginRecoveryAttempt");
                recovery = (System.Collections.IEnumerator)Call(_client, "RecoverAccountRoom");

                // 推进真实父Coroutine，只在外部I/O的yield边界提供完成结果。
                // 不执行子IEnumerator，因此这个EditMode回归不会发HTTP、申请Ticket或建立KCP。
                Assert.That(recovery.MoveNext(), Is.True, "应先查询本人已有current room。");
                Set(_client, "_success", true); Set(_client, "_httpStatus", 200L);
                Set(_client, "_body", JsonUtility.ToJson(Current()));
                Assert.That(recovery.MoveNext(), Is.True, "已核验原Run后应查询个人终态。");
                Assert.That(_client.RoomId, Is.EqualTo(Room));
                Set(_client, "_terminalExitProbeAllowsReconnect", true);
                Assert.That(recovery.MoveNext(), Is.True, "明确无终态才可尝试连接。");
                Set(_client, "_auth", null); Set(_client, "_code", "AdmissionFailed");

                Assert.That(recovery.MoveNext(), Is.False,
                    "菜单仍可见时连接失败必须结束恢复并开放原Enter重试；不能等待不可见Dialog的Reconnect命令。");
                Assert.That(_account.Busy, Is.False);
                Assert.That(_lobby.RecoveryPending, Is.True);
                Assert.That(_lobby.RecoveryMenuVisible, Is.True);
                Assert.That(Get<string>(_client, "_recoveryPlayerId"), Is.EqualTo(Player));
                Assert.That(Get<string>(_client, "_recoveryRunId"), Is.EqualTo(Run));
                Assert.That(Get<Scene>(_client, "_connectionScene"), Is.EqualTo(replica));
                Assert.That(replica.isLoaded, Is.True, "连接失败不丢弃已加载的原Run现场。");
                Assert.That(Get<object>(_client, "_singleEntryCommand"), Is.Null);
            }
            finally
            {
                (recovery as IDisposable)?.Dispose();
                if (replica.IsValid()) EditorSceneManager.ClosePreviewScene(replica);
            }
        }
    }
}
