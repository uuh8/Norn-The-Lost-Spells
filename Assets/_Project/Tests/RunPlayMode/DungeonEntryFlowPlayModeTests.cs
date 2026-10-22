#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Game.Character;
using Game.Core;
using Game.Multiplayer;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.Networking;
using Game.NetworkProtocol;
using Mirror;

namespace Game.Run.Tests
{
    /// <summary>真实业务登录+生产场景/F输入/UGUI/单机转场；只把单机文件系统注入内存，不替换账号或游戏规则。</summary>
    public sealed class DungeonEntryFlowPlayModeTests
    {
        private const string Menu = "Assets/_Project/Scenes/P7_MainMenu.unity";
        private const string Hub = "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private InputTestFixture _input;
        private Keyboard _keyboard;
        private bool _seedHub, _background;
        private readonly List<Scene> _scenes = new List<Scene>();
        private SinglePlayerInventorySession _hubSession;
        private string _memoryPath;
        private readonly MemoryFiles _files = new MemoryFiles();
        [Serializable] private sealed class Credentials { public string Username, Password; }
        [Serializable] private sealed class NativeInstanceState { public int ProcessId; public long ProcessStartTicks; public string InstanceId; }
        private string _ownInstance;
        private string _testRoomId, _testCompanionToken;
        private string _expiredLobbyRoom;
        private OnlineRoomChangedEvent _roomView;

        [UnitySetUp] public IEnumerator SetUp()
        {
            _background = Application.runInBackground; Application.runInBackground = true; Time.timeScale = 1;
            _input = new InputTestFixture(); _input.Setup(); _keyboard = InputSystem.AddDevice<Keyboard>();
            _seedHub = true; _scenes.Clear(); _files.Clear(); _hubSession = null;
            _memoryPath = "Temp/SinglePlayer/N1EntryMemory/" + Guid.NewGuid().ToString("N");
            SceneManager.sceneLoaded += OnLoaded;
            _ownInstance = _testRoomId = _testCompanionToken = _expiredLobbyRoom = null; _roomView = default; EventBus<OnlineRoomChangedEvent>.Subscribe(OnRoom);
            EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            yield return EditorSceneManager.LoadSceneInPlayMode(Menu, new LoadSceneParameters(LoadSceneMode.Single));
            yield return null; yield return null;
        }
        private void OnRoom(OnlineRoomChangedEvent view) => _roomView = view;
        private void OnLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.path != Menu && scene.path != Hub && !scene.path.Contains("P7_DemoRun") && !scene.path.Contains("P8_BossField") && !scene.path.EndsWith("OnlineRoom.unity", StringComparison.Ordinal)) return;
            _scenes.Add(scene);
            if (scene.path != Hub) return;
            _hubSession = Find<SinglePlayerInventorySession>(scene);
            if (!_seedHub || _hubSession == null) return;
            _seedHub = false;
            // sceneLoaded早于Start；注入MemoryFiles后Start不能转去Application.persistentDataPath。
            var data = new SerializedObject(_hubSession);
            var catalog = (ItemCatalog)data.FindProperty("_catalog").objectReferenceValue;
            var seeds = (SinglePlayerStartingInventory)data.FindProperty("_startingInventory").objectReferenceValue;
            Assert.That(_hubSession.ConfigureAndInitialize(catalog, seeds, _memoryPath, _files), Is.True);
        }
        [UnityTest] public IEnumerator Login_P7ChoiceCancelMatchAndSinglePlayer_ReturnsWithSession() => RunPath("P7");
        [UnityTest] public IEnumerator Login_P8ChoiceCancelMatchAndSinglePlayer_ReturnsWithSession() => RunPath("P8");
        [UnityTest] public IEnumerator PortalMatch_AuthenticatedAdmissionReconnectAndReturn_PreservesLocalHub()
        {
            var account = UnityEngine.Object.FindFirstObjectByType<AccountMenuController>();
            var credentials = JsonUtility.FromJson<Credentials>(File.ReadAllText(".tools/local-online/account-probe.private.json"));
            account.transform.Find("Panel/Username").GetComponent<InputField>().text = credentials.Username;
            account.transform.Find("Panel/Password").GetComponent<InputField>().text = credentials.Password;
            Button(account, "Login").onClick.Invoke(); yield return WaitIdle(OnlineAccountClient.ApplicationSession);
            Assert.That(OnlineAccountClient.ApplicationSession.Authenticated, Is.True);
            Button(account, "EnterHub").onClick.Invoke(); yield return WaitScene(Hub); yield return null; yield return null;
            int localSession = _hubSession.GetInstanceID(); uint localVersion = _hubSession.ReadOnly.Version;
            var hubScene = SceneManager.GetActiveScene(); var player = Find<PlayerControllerBase>(hubScene);
            HubPracticePortal portal = null;
            foreach (var root in hubScene.GetRootGameObjects()) foreach (var p in root.GetComponentsInChildren<HubPracticePortal>(true)) if (p.ScenePath.Contains("P7")) portal = p;
            Assert.That(portal, Is.Not.Null); player.CharacterController.enabled = false; player.transform.position = portal.transform.position;
            player.CharacterController.enabled = true; Physics.SyncTransforms(); yield return PressF();
            var dialog = UnityEngine.Object.FindFirstObjectByType<DungeonEntryDialogController>();
            dialog.transform.Find("Backdrop/Panel/Matchmaking").GetComponent<Button>().onClick.Invoke();
            yield return WaitRoomPhase(OnlineRoomPhase.Lobby, 20);
            string room = _roomView.RoomId;
            _testRoomId = room;
            // 第二位只是此PlayMode的业务成员，真实双KCP Client另由原生验收；不在一个Mirror静态上下文伪建两个Client。
            var companion = new Credentials { Username = "pf_" + Guid.NewGuid().ToString("N").Substring(0, 16), Password = "Portal_flow_test_2026!" };
            yield return Api("POST", "/api/online/accounts", JsonUtility.ToJson(new RegisterAccountRequest { Username = companion.Username, Password = companion.Password }), null);
            var manifest = JsonUtility.FromJson<ContentManifest>(Resources.Load<TextAsset>("Online/ContentManifest").text);
            yield return Api("POST", "/api/online/sessions", JsonUtility.ToJson(new LoginAccountRequest { Username = companion.Username, Password = companion.Password, ProtocolVersion = manifest.ProtocolVersion, ContentManifestHash = manifest.Hash }), null);
            string token = JsonUtility.FromJson<LoginAccountReply>(_apiBody).Token;
            _testCompanionToken = token;
            yield return Api("GET", "/api/online/profile", null, token); var profile = JsonUtility.FromJson<OnlineProfileReply>(_apiBody);
            yield return Api("POST", "/api/online/rooms/match", JsonUtility.ToJson(new CreateRoomRequest { DungeonId = "p7" }), token);
            Assert.That(JsonUtility.FromJson<RoomReply>(_apiBody).RoomId, Is.EqualTo(room));
            double until = Time.realtimeSinceStartupAsDouble + 10; while ((_roomView.Members?.Length ?? 0) != 2 || _roomView.Busy) { Assert.That(Time.realtimeSinceStartupAsDouble, Is.LessThan(until)); yield return null; }
            dialog.transform.Find("Backdrop/Panel/Ready").GetComponent<Button>().onClick.Invoke();
            until = Time.realtimeSinceStartupAsDouble + 10; while (!_roomView.SelfReady || _roomView.Busy) { Assert.That(Time.realtimeSinceStartupAsDouble, Is.LessThan(until)); yield return null; }
            yield return Api("GET", "/api/online/rooms/" + room, null, token); var current = JsonUtility.FromJson<RoomReply>(_apiBody);
            yield return Api("POST", "/api/online/rooms/" + room + "/ready", JsonUtility.ToJson(new RoomReadyRequest { ExpectedRevision = current.ReadyRevision, ExpectedInventoryVersion = profile.Version, Ready = true }), token);
            until = Time.realtimeSinceStartupAsDouble + 10; while (!_roomView.CanStart || _roomView.Busy) { Assert.That(Time.realtimeSinceStartupAsDouble, Is.LessThan(until)); yield return null; }
            dialog.transform.Find("Backdrop/Panel/StartRoom").GetComponent<Button>().onClick.Invoke();
            // 保存本测试创建的实例身份，TearDown只清理它；不按进程名扫描。
            until = Time.realtimeSinceStartupAsDouble + 50;
            while (_roomView.Phase != OnlineRoomPhase.Connected)
            {
                Assert.That(Time.realtimeSinceStartupAsDouble, Is.LessThan(until), _roomView.Status);
                if (_ownInstance == null)
                {
                    yield return Api("GET", "/api/online/rooms/" + room, null, token);
                    var view = JsonUtility.FromJson<RoomReply>(_apiBody);
                    if (view.State == "Starting" || view.State == "Running")
                    { yield return Api("GET", "/api/online/rooms/" + room + "/instance", null, token); _ownInstance = JsonUtility.FromJson<RoomInstanceReply>(_apiBody).InstanceId; }
                }
                yield return null;
            }
            yield return WaitScene("Assets/_Project/Scenes/Online/OnlineRoom.unity");
            Assert.That(hubScene.isLoaded, Is.True); Assert.That(_hubSession.gameObject.activeInHierarchy, Is.False);
            Assert.That(_hubSession.GetInstanceID(), Is.EqualTo(localSession)); Assert.That(_hubSession.ReadOnly.Version, Is.EqualTo(localVersion));
            var client = OnlineAccountClient.ApplicationSession.GetComponent<OnlineRoomClient>(); ulong actor = client.ActorId; uint epoch = client.ConnectionEpoch;
            Assert.That(actor, Is.GreaterThan(0)); Assert.That(Time.timeScale, Is.EqualTo(1));
            NetworkClient.Disconnect(); yield return WaitRoomPhase(OnlineRoomPhase.Disconnected, 10); yield return new WaitForSecondsRealtime(5.1f);
            dialog.transform.Find("Backdrop/Panel/Reconnect").GetComponent<Button>().onClick.Invoke(); yield return WaitRoomPhase(OnlineRoomPhase.Connected, 20);
            Assert.That(client.ActorId, Is.EqualTo(actor)); Assert.That(client.ConnectionEpoch, Is.GreaterThan(epoch));
            dialog.transform.Find("Backdrop/Panel/Cancel").GetComponent<Button>().onClick.Invoke(); yield return WaitScene(Hub); yield return null; yield return null;
            Assert.That(_hubSession.GetInstanceID(), Is.EqualTo(localSession)); Assert.That(_hubSession.gameObject.activeInHierarchy, Is.True);
            Assert.That(_hubSession.ReadOnly.Version, Is.EqualTo(localVersion)); Assert.That(Time.timeScale, Is.EqualTo(1));
            Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.Locked)); Assert.That(HubPracticeSession.Current, Is.Null);
        }
        [UnityTest] public IEnumerator PortalMatch_UnknownHttpOutcome_KeepsChoiceUntilCancellationConfirmed()
        {
            var account = UnityEngine.Object.FindFirstObjectByType<AccountMenuController>();
            var credentials = JsonUtility.FromJson<Credentials>(File.ReadAllText(".tools/local-online/account-probe.private.json"));
            account.transform.Find("Panel/Username").GetComponent<InputField>().text = credentials.Username;
            account.transform.Find("Panel/Password").GetComponent<InputField>().text = credentials.Password;
            Button(account, "Login").onClick.Invoke(); yield return WaitIdle(OnlineAccountClient.ApplicationSession);
            Button(account, "EnterHub").onClick.Invoke(); yield return WaitScene(Hub); yield return null; yield return null;
            var player = Find<PlayerControllerBase>(SceneManager.GetActiveScene()); HubPracticePortal portal = null;
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects()) foreach (var candidate in root.GetComponentsInChildren<HubPracticePortal>(true)) if (candidate.ScenePath.Contains("P7")) portal = candidate;
            Assert.That(portal, Is.Not.Null); player.CharacterController.enabled = false; player.transform.position = portal.transform.position; player.CharacterController.enabled = true; Physics.SyncTransforms();
            yield return PressF(); var dialog = UnityEngine.Object.FindFirstObjectByType<DungeonEntryDialogController>();
            var data = new SerializedObject(OnlineAccountClient.ApplicationSession); var original = (OnlineServiceProfile)data.FindProperty("_profile").objectReferenceValue;
            var isolated = UnityEngine.Object.Instantiate(original);
            try
            {
                var profile = new SerializedObject(isolated); profile.FindProperty("_baseUrl").stringValue = "http://127.0.0.1:9"; profile.ApplyModifiedPropertiesWithoutUndo();
                data.FindProperty("_profile").objectReferenceValue = isolated; data.ApplyModifiedPropertiesWithoutUndo();
                dialog.transform.Find("Backdrop/Panel/Matchmaking").GetComponent<Button>().onClick.Invoke(); yield return WaitRoomPhase(OnlineRoomPhase.Error, 15);
                Assert.That(dialog.transform.Find("Backdrop/Panel/SinglePlayer").gameObject.activeSelf, Is.False); Assert.That(Time.timeScale, Is.Zero);
                dialog.transform.Find("Backdrop/Panel/Cancel").GetComponent<Button>().onClick.Invoke(); yield return new WaitForSecondsRealtime(2);
                Assert.That(dialog.transform.Find("Backdrop").gameObject.activeSelf, Is.True, "无法确认业务结果时不能假装退出。");
                data.FindProperty("_profile").objectReferenceValue = original; data.ApplyModifiedPropertiesWithoutUndo();
                dialog.transform.Find("Backdrop/Panel/Cancel").GetComponent<Button>().onClick.Invoke(); yield return WaitRoomPhase(OnlineRoomPhase.None, 15);
                Assert.That(Time.timeScale, Is.EqualTo(1)); Assert.That(SceneManager.GetActiveScene().path, Is.EqualTo(Hub));
            }
            finally { data.FindProperty("_profile").objectReferenceValue = original; data.ApplyModifiedPropertiesWithoutUndo(); UnityEngine.Object.DestroyImmediate(isolated); }
        }
        [UnityTest] public IEnumerator PortalMatch_SessionExpiry_ClosesLobbyAndRestoresInputWithoutSinglePlayer()
        {
            var account = UnityEngine.Object.FindFirstObjectByType<AccountMenuController>();
            var credentials = JsonUtility.FromJson<Credentials>(File.ReadAllText(".tools/local-online/account-probe.private.json"));
            account.transform.Find("Panel/Username").GetComponent<InputField>().text = credentials.Username;
            account.transform.Find("Panel/Password").GetComponent<InputField>().text = credentials.Password;
            Button(account, "Login").onClick.Invoke(); yield return WaitIdle(OnlineAccountClient.ApplicationSession);
            Button(account, "EnterHub").onClick.Invoke(); yield return WaitScene(Hub); yield return null; yield return null;
            var player = Find<PlayerControllerBase>(SceneManager.GetActiveScene()); HubPracticePortal portal = null;
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects()) foreach (var p in root.GetComponentsInChildren<HubPracticePortal>(true)) if (p.ScenePath.Contains("P7")) portal = p;
            Assert.That(portal, Is.Not.Null); player.CharacterController.enabled = false; player.transform.position = portal.transform.position; player.CharacterController.enabled = true; Physics.SyncTransforms(); yield return PressF();
            var dialog = UnityEngine.Object.FindFirstObjectByType<DungeonEntryDialogController>();
            dialog.transform.Find("Backdrop/Panel/Matchmaking").GetComponent<Button>().onClick.Invoke(); yield return WaitRoomPhase(OnlineRoomPhase.Lobby, 15);
            _expiredLobbyRoom = _roomView.RoomId;
            typeof(OnlineAccountClient).GetField("_expiresUnixMilliseconds", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(OnlineAccountClient.ApplicationSession, 0L);
            yield return WaitRoomPhase(OnlineRoomPhase.None, 10);
            Assert.That(OnlineAccountClient.ApplicationSession.Authenticated, Is.False); Assert.That(Time.timeScale, Is.EqualTo(1));
            Assert.That(SceneManager.GetActiveScene().path, Is.EqualTo(Hub)); Assert.That(HubPracticeSession.Current, Is.Null);
        }

        private IEnumerator WaitRoomPhase(OnlineRoomPhase phase, int seconds)
        { double end = Time.realtimeSinceStartupAsDouble + seconds; while (_roomView.Phase != phase || _roomView.Busy) { Assert.That(Time.realtimeSinceStartupAsDouble, Is.LessThan(end), "等待" + phase + "，当前" + _roomView.Phase + "/Busy=" + _roomView.Busy + "：" + _roomView.Status); yield return null; } }
        private string _apiBody;
        private IEnumerator Api(string method, string path, string json, string token)
        {
            var manifest = JsonUtility.FromJson<ContentManifest>(Resources.Load<TextAsset>("Online/ContentManifest").text);
            using (var request = new UnityWebRequest("http://127.0.0.1:5080" + path, method))
            {
                request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 10;
                if (json != null) { request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(json)); request.SetRequestHeader("Content-Type", "application/json"); }
                if (token != null) request.SetRequestHeader("Authorization", "Bearer " + token);
                request.SetRequestHeader("X-Norn-Protocol", "1"); request.SetRequestHeader("X-Norn-Manifest", manifest.Hash);
                yield return request.SendWebRequest(); Assert.That(request.result, Is.EqualTo(UnityWebRequest.Result.Success), method + " " + path + " HTTP " + request.responseCode);
                _apiBody = request.downloadHandler.text;
            }
        }
        [UnityTest] public IEnumerator CachedLogin_BusinessUnavailable_DoesNotEnterHub()
        {
            var account = UnityEngine.Object.FindFirstObjectByType<AccountMenuController>();
            var session = OnlineAccountClient.ApplicationSession;
            var credentials = JsonUtility.FromJson<Credentials>(File.ReadAllText(".tools/local-online/account-probe.private.json"));
            account.transform.Find("Panel/Username").GetComponent<InputField>().text = credentials.Username;
            account.transform.Find("Panel/Password").GetComponent<InputField>().text = credentials.Password;
            Button(account, "Login").onClick.Invoke(); yield return WaitIdle(session);
            Assert.That(session.Authenticated, Is.True);
            var data = new SerializedObject(session);
            var original = (OnlineServiceProfile)data.FindProperty("_profile").objectReferenceValue;
            var isolated = UnityEngine.Object.Instantiate(original);
            try
            {
                // 只改临时SO副本指向未监听的本机端口；不停止真实业务服，也不污染原Profile资产。
                var profile = new SerializedObject(isolated); profile.FindProperty("_baseUrl").stringValue = "http://127.0.0.1:9";
                profile.ApplyModifiedPropertiesWithoutUndo(); data.FindProperty("_profile").objectReferenceValue = isolated; data.ApplyModifiedPropertiesWithoutUndo();
                Button(account, "EnterHub").onClick.Invoke(); yield return WaitIdle(session); yield return null;
                Assert.That(session.Authenticated, Is.True, "缓存Token仍有效不能成为本次业务确认的替代。");
                Assert.That(session.LastHttpStatus, Is.LessThanOrEqualTo(0));
                Assert.That(SceneManager.GetActiveScene().path, Is.EqualTo(Menu));
            }
            finally
            {
                data.FindProperty("_profile").objectReferenceValue = original; data.ApplyModifiedPropertiesWithoutUndo();
                UnityEngine.Object.DestroyImmediate(isolated);
            }
        }
        private IEnumerator RunPath(string kind)
        {
            var menu = Find<DemoMainMenuController>(SceneManager.GetActiveScene());
            var account = UnityEngine.Object.FindFirstObjectByType<AccountMenuController>();
            Assert.That(account, Is.Not.Null); Assert.That(OnlineAccountClient.ApplicationSession, Is.Not.Null);
            var session = OnlineAccountClient.ApplicationSession;
            Assert.That(Button(account, "EnterHub").interactable, Is.False);
            menu.StartGame(); yield return null; yield return null;
            Assert.That(SceneManager.GetActiveScene().path, Is.EqualTo(Menu), "旧Play回调也不能绕过登录。");

            string path = Path.GetFullPath(".tools/local-online/account-probe.private.json");
            Assert.That(File.Exists(path), Is.True, "需要N1本机私有测试账号，不输出凭据。");
            var credentials = JsonUtility.FromJson<Credentials>(File.ReadAllText(path));
            var username = account.transform.Find("Panel/Username").GetComponent<InputField>();
            var password = account.transform.Find("Panel/Password").GetComponent<InputField>();
            username.text = credentials.Username; password.text = "wrong-password-for-n1";
            Button(account, "Login").onClick.Invoke(); yield return WaitIdle(session);
            Assert.That(session.Authenticated, Is.False); Assert.That(session.LastHttpStatus, Is.EqualTo(401));
            password.text = credentials.Password; Button(account, "Login").onClick.Invoke(); yield return WaitIdle(session);
            Assert.That(session.Authenticated, Is.True); Assert.That(password.text, Is.Empty);
            Assert.That(Button(account, "EnterHub").interactable, Is.True);
            int sessionId = session.GetInstanceID(); Button(account, "EnterHub").onClick.Invoke();
            yield return WaitScene(Hub); yield return null; yield return null;
            Assert.That(_hubSession.ProfileStoragePath.Replace('\\', '/'), Does.Contain("N1EntryMemory"));
            Assert.That(OnlineAccountClient.ApplicationSession.GetInstanceID(), Is.EqualTo(sessionId));
            Assert.That(UnityEngine.Object.FindFirstObjectByType<AccountMenuController>(), Is.Null, "UI随菜单卸载，身份独立保留。");
            var player = Find<PlayerControllerBase>(SceneManager.GetActiveScene());
            HubPracticePortal portal = null;
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                foreach (var candidate in root.GetComponentsInChildren<HubPracticePortal>(true))
                    if (candidate.ScenePath != null && candidate.ScenePath.Contains(kind)) portal = candidate;
            Assert.That(portal, Is.Not.Null);
            bool enabled = player.CharacterController.enabled; player.CharacterController.enabled = false;
            player.transform.position = portal.transform.position; player.CharacterController.enabled = enabled; Physics.SyncTransforms();
            yield return PressF();
            var dialog = UnityEngine.Object.FindFirstObjectByType<DungeonEntryDialogController>();
            Assert.That(dialog.transform.Find("Backdrop").gameObject.activeSelf, Is.True);
            Assert.That(Time.timeScale, Is.Zero); Assert.That(HubPracticeSession.Current, Is.Null);
            var run = Find<SinglePlayerRunCoordinator>(SceneManager.GetActiveScene());
            uint version = _hubSession.ReadOnly.Version;
            dialog.transform.Find("Backdrop/Panel/Matchmaking").GetComponent<Button>().onClick.Invoke(); yield return null;
            yield return WaitRoomPhase(OnlineRoomPhase.Lobby, 20);
            Assert.That(_roomView.Members.Length, Is.GreaterThanOrEqualTo(1));
            Assert.That(dialog.transform.Find("Backdrop/Panel/SinglePlayer").gameObject.activeSelf, Is.False, "房间中必须先取消，不能同时进入单机。");
            Assert.That(run.State.Phase, Is.EqualTo(RaidPhase.HubReady)); Assert.That(_hubSession.ReadOnly.Version, Is.EqualTo(version));
            dialog.transform.Find("Backdrop/Panel/Cancel").GetComponent<Button>().onClick.Invoke(); yield return WaitRoomPhase(OnlineRoomPhase.None, 15); yield return null;
            Assert.That(Time.timeScale, Is.EqualTo(1)); Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.Locked));
            yield return PressF();
            dialog.transform.Find("Backdrop/Panel/SinglePlayer").GetComponent<Button>().onClick.Invoke();
            yield return WaitScene(portal.ScenePath); yield return null; yield return null;
            Assert.That(OnlineAccountClient.ApplicationSession.GetInstanceID(), Is.EqualTo(sessionId));
            Assert.That(session.Authenticated, Is.True); Assert.That(HubPracticeSession.Current, Is.Not.Null);
            var guide = Find<GameplayGuideController>(SceneManager.GetActiveScene()); if (guide != null) guide.Close();
            Assert.That(Find<RunPauseCoordinator>(SceneManager.GetActiveScene()).HasReason(RunPauseReason.DungeonEntryChoice), Is.False);
            // P8未胜利不生成回程门；暂停菜单的原返回入口最终也调用此权威方法，不修改Boss/重试。
            Assert.That(HubPracticeSession.Current.ReturnToHub(), Is.True);
            yield return WaitScene(Hub); yield return null; yield return null;
            Assert.That(OnlineAccountClient.ApplicationSession.GetInstanceID(), Is.EqualTo(sessionId));
            Assert.That(session.Authenticated, Is.True); Assert.That(HubPracticeSession.Current, Is.Null);
            Assert.That(_hubSession.ProfileStoragePath.Replace('\\', '/'), Does.Contain("N1EntryMemory"));
        }
        private static Button Button(AccountMenuController menu, string name) => menu.transform.Find("Panel/" + name).GetComponent<Button>();
        private IEnumerator PressF()
        {
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState()); InputSystem.Update(); yield return null; yield return null;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.F)); InputSystem.Update(); yield return null;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState()); InputSystem.Update();
        }
        private static IEnumerator WaitIdle(OnlineAccountClient client)
        {
            double end = Time.realtimeSinceStartupAsDouble + 20;
            do { yield return null; Assert.That(Time.realtimeSinceStartupAsDouble, Is.LessThan(end), "业务请求超时。"); } while (client.Busy);
        }
        private static IEnumerator WaitScene(string path)
        {
            double end = Time.realtimeSinceStartupAsDouble + 60;
            while (SceneManager.GetActiveScene().path != path)
            { yield return null; Assert.That(Time.realtimeSinceStartupAsDouble, Is.LessThan(end), "目标Scene未加载。"); }
        }
        private static T Find<T>(Scene scene) where T : Component
        {
            foreach (var root in scene.GetRootGameObjects()) { var found = root.GetComponentInChildren<T>(true); if (found != null) return found; }
            return null;
        }
        [UnityTearDown] public IEnumerator TearDown()
        {
            EventBus<OnlineRoomChangedEvent>.Unsubscribe(OnRoom);
            if (_expiredLobbyRoom != null)
            {
                // 本测试只推进Client的展示到期；业务Session仍真实有效，显式重新登录后清理自己的测试席位。
                var credentials = JsonUtility.FromJson<Credentials>(File.ReadAllText(".tools/local-online/account-probe.private.json"));
                var manifest = JsonUtility.FromJson<ContentManifest>(Resources.Load<TextAsset>("Online/ContentManifest").text);
                yield return Api("POST", "/api/online/sessions", JsonUtility.ToJson(new LoginAccountRequest { Username = credentials.Username, Password = credentials.Password, ProtocolVersion = manifest.ProtocolVersion, ContentManifestHash = manifest.Hash }), null);
                string token = JsonUtility.FromJson<LoginAccountReply>(_apiBody).Token;
                yield return Api("DELETE", "/api/online/rooms/" + _expiredLobbyRoom + "/members/me", null, token);
            }
            if (!string.IsNullOrEmpty(_ownInstance))
            {
                string state = Path.GetFullPath("../Server/.artifacts/instances/" + _ownInstance + "/state.json");
                if (File.Exists(state))
                {
                    var identity = JsonUtility.FromJson<NativeInstanceState>(File.ReadAllText(state));
                    Assert.That(identity.InstanceId, Is.EqualTo(_ownInstance));
                    try
                    {
                        using (var p = System.Diagnostics.Process.GetProcessById(identity.ProcessId))
                        {
                            Assert.That(p.StartTime.ToUniversalTime().Ticks, Is.EqualTo(identity.ProcessStartTicks));
                            Assert.That(Path.GetFullPath(p.MainModule.FileName), Is.EqualTo(Path.GetFullPath("Builds/OnlineRooms/Server/NornRoomServer.exe")).IgnoreCase);
                            _ = p.Handle; p.Kill();
                        }
                    }
                    catch (ArgumentException) { } // 只允许该实例已经自行退出，不回退成按名字Kill。
                }
                if (_testCompanionToken != null && _testRoomId != null)
                {
                    double deadline = Time.realtimeSinceStartupAsDouble + 10; bool open = false;
                    do
                    {
                        yield return Api("GET", "/api/online/rooms/" + _testRoomId, null, _testCompanionToken);
                        open = JsonUtility.FromJson<RoomReply>(_apiBody).State == "Open";
                        if (!open) yield return new WaitForSecondsRealtime(.5f);
                    } while (!open && Time.realtimeSinceStartupAsDouble < deadline);
                    Assert.That(open, Is.True, "本测试DS退出后应恢复大厅，再清理自己的两个席位。");
                    yield return Api("DELETE", "/api/online/rooms/" + _testRoomId + "/members/me", null, _testCompanionToken);
                    var credentials = JsonUtility.FromJson<Credentials>(File.ReadAllText(".tools/local-online/account-probe.private.json"));
                    var manifest = JsonUtility.FromJson<ContentManifest>(Resources.Load<TextAsset>("Online/ContentManifest").text);
                    yield return Api("POST", "/api/online/sessions", JsonUtility.ToJson(new LoginAccountRequest { Username = credentials.Username, Password = credentials.Password, ProtocolVersion = manifest.ProtocolVersion, ContentManifestHash = manifest.Hash }), null);
                    string token = JsonUtility.FromJson<LoginAccountReply>(_apiBody).Token;
                    yield return Api("DELETE", "/api/online/rooms/" + _testRoomId + "/members/me", null, token);
                }
            }
            SceneManager.sceneLoaded -= OnLoaded;
            if (OnlineAccountClient.ApplicationSession != null) UnityEngine.Object.Destroy(OnlineAccountClient.ApplicationSession.gameObject);
            if (HubPracticeSession.Current != null) UnityEngine.Object.Destroy(HubPracticeSession.Current.gameObject);
            Time.timeScale = 1;
            Scene cleanup = SceneManager.CreateScene("N1 entry cleanup"); SceneManager.SetActiveScene(cleanup);
            foreach (var scene in _scenes) if (scene.IsValid() && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
            yield return null;
            _input?.TearDown(); _input = null; Application.runInBackground = _background; Time.timeScale = 1;
        }
        private sealed class MemoryFiles : IProfileFileSystem
        {
            private readonly Dictionary<string, string> _data = new Dictionary<string, string>();
            public void Clear() => _data.Clear(); public bool Exists(string path) => _data.ContainsKey(path);
            public string Read(string path) => _data[path]; public void WriteFlushed(string path, string content) => _data[path] = content;
            public void Move(string source, string target) { _data[target] = _data[source]; _data.Remove(source); }
            public void Replace(string source, string target, string backup) { _data[backup] = _data[target]; Move(source, target); }
        }
    }
}
#endif
