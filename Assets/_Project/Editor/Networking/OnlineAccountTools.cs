using System;
using System.Collections.Generic;
using System.IO;
using Game.Core;
using Game.Multiplayer;
using Game.NetworkProtocol;
using Game.UI;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.Project.Editor
{
    /// <summary>显式装配线上 UI/组合 Prefab；只增主菜单入口，不改变原单机按钮和全局 Build Scene List。</summary>
    public static class OnlineAccountTools
    {
        public const string PrefabPath = "Assets/_Project/Prefabs/UI/Online/OnlineLobby.prefab";
        public const string ProfilePath = "Assets/_Project/ScriptableObjects/World/Multiplayer/OnlineServiceProfile.asset";
        public const string ScenePath = "Assets/_Project/Scenes/Tests/OnlineAccountLab.unity";
        public const string AccountMenuPath = "Assets/_Project/Prefabs/UI/Online/AccountMenu.prefab";
        public const string EntryDialogPath = "Assets/_Project/Resources/Online/DungeonEntryDialog.prefab";
        private const string MainMenu = "Assets/_Project/Scenes/P7_MainMenu.unity";
        [MenuItem("Tools/Norn/Online/Create Online Account UI")]
        public static void Create()
        {
            OnlineSceneSetup.RequireCleanEditor();
            if (!File.Exists(ContentManifestExporter.Output)) throw new InvalidOperationException("先 Export Content Manifest。");
            var profile = AssetDatabase.LoadAssetAtPath<OnlineServiceProfile>(ProfilePath);
            if (profile == null) { profile = ScriptableObject.CreateInstance<OnlineServiceProfile>(); AssetDatabase.CreateAsset(profile, ProfilePath); }
            Scene original = SceneManager.GetActiveScene();
            Scene lab = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(lab);
                SaveEntryPrefab(AccountMenuPath, () => CreateAccountMenu(profile));
                SaveEntryPrefab(EntryDialogPath, CreateEntryDialog);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
                if (prefab == null)
                {
                    GameObject root = CreatePanel(profile);
                    Directory.CreateDirectory(Path.GetDirectoryName(PrefabPath)); AssetDatabase.Refresh();
                    prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                    UnityEngine.Object.DestroyImmediate(root);
                }
                if (!File.Exists(ScenePath))
                {
                    PrefabUtility.InstantiatePrefab(prefab, lab);
                    new GameObject("ExplicitAccountProbe").AddComponent<OnlineAccountProbe>();
                    var camera = new GameObject("AccountLabCamera").AddComponent<Camera>(); camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = new Color(.025f, .04f, .065f);
                    var events = new GameObject("EventSystem", typeof(EventSystem));
                    events.AddComponent<InputSystemUIInputModule>().AssignDefaultActions();
                    EditorSceneManager.SaveScene(lab, ScenePath);
                }
            }
            finally { SceneManager.SetActiveScene(original); EditorSceneManager.CloseScene(lab, true); }
            InstallMenuEntry();
            EnsureProbe();
            AssetDatabase.SaveAssets();
            GameLog.Info("线上账户 Prefab/独立检查场景/主菜单入口已建立。", "OnlineAccounts");
        }

        private static GameObject CreatePanel(OnlineServiceProfile profile)
        {
            var root = new GameObject("OnlineLobby", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 100;
            var scaler = root.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 800); scaler.matchWidthOrHeight = .5f;
            var panel = Box("Panel", root.transform, Vector2.zero, new Vector2(1140, 740), new Color(.035f, .065f, .105f, .99f));
            Label("Title", panel, "联机 · 线上账号与仓库", new Vector2(0, 316), new Vector2(1000, 48), 30);
            Label("UsernameLabel", panel, "用户名（3–24 位字母/数字/下划线）", new Vector2(-335, 226), new Vector2(390, 32));
            var username = Field("Username", panel, new Vector2(-335, 180));
            Label("PasswordLabel", panel, "密码（10–128 字符）", new Vector2(-335, 120), new Vector2(390, 32));
            var password = Field("Password", panel, new Vector2(-335, 74)); password.contentType = InputField.ContentType.Password;
            var register = Button("Register", panel, "注册", new Vector2(-425, -2), new Vector2(170, 46));
            var login = Button("Login", panel, "登录", new Vector2(-245, -2), new Vector2(170, 46));
            var status = Label("Status", panel, "正在准备…", new Vector2(-335, -120), new Vector2(390, 140));
            var close = Button("Close", panel, "返回主菜单", new Vector2(-335, -292), new Vector2(350, 46));
            var summary = Label("Summary", panel, "登录后读取线上角色", new Vector2(215, 214), new Vector2(610, 140));
            var refresh = Button("Refresh", panel, "刷新资产", new Vector2(120, 112), new Vector2(170, 42));
            var logout = Button("Logout", panel, "退出账号", new Vector2(310, 112), new Vector2(170, 42));
            var scrollRoot = Box("Items", panel, new Vector2(215, -100), new Vector2(610, 360), new Color(.07f, .1f, .15f));
            scrollRoot.gameObject.AddComponent<RectMask2D>();
            var scroll = scrollRoot.gameObject.AddComponent<ScrollRect>(); scroll.horizontal = false; scroll.viewport = scrollRoot;
            var content = Box("Content", scrollRoot, Vector2.zero, new Vector2(610, 24 * 48), Color.clear);
            content.anchorMin = content.anchorMax = new Vector2(.5f, 1); content.pivot = new Vector2(.5f, 1); content.anchoredPosition = Vector2.zero;
            scroll.content = content;
            var labels = new Text[24]; var buttons = new UnityEngine.UI.Button[24];
            for (int i = 0; i < 24; i++)
            {
                var row = Box("Item" + i, content, new Vector2(0, -i * 48 - 24), new Vector2(600, 44), new Color(.08f, .13f, .2f));
                row.anchorMin = row.anchorMax = new Vector2(.5f, 1);
                labels[i] = Label("Name", row, "", new Vector2(-80, 0), new Vector2(420, 40));
                buttons[i] = Button("Move", row, "移动", new Vector2(220, 0), new Vector2(145, 38));
                row.gameObject.SetActive(false);
            }
            root.AddComponent<OnlineAccountClient>().SetProfile(profile);
            var ui = root.AddComponent<OnlineLobbyController>(); var serialized = new SerializedObject(ui);
            Set(serialized, "_username", username); Set(serialized, "_password", password);
            Set(serialized, "_register", register); Set(serialized, "_login", login); Set(serialized, "_refresh", refresh);
            Set(serialized, "_logout", logout); Set(serialized, "_close", close); Set(serialized, "_status", status); Set(serialized, "_summary", summary);
            var labelArray = serialized.FindProperty("_itemLabels"); var buttonArray = serialized.FindProperty("_moveButtons");
            labelArray.arraySize = buttonArray.arraySize = 24;
            for (int i = 0; i < 24; i++) { labelArray.GetArrayElementAtIndex(i).objectReferenceValue = labels[i]; buttonArray.GetArrayElementAtIndex(i).objectReferenceValue = buttons[i]; }
            serialized.ApplyModifiedPropertiesWithoutUndo(); return root;
        }

        private static void SaveEntryPrefab(string path, Func<GameObject> create)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var root = create(); PrefabUtility.SaveAsPrefabAsset(root, path); UnityEngine.Object.DestroyImmediate(root);
        }
        private static GameObject CanvasRoot(string name, int order)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay; root.GetComponent<Canvas>().sortingOrder = order;
            var scaler = root.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 800); scaler.matchWidthOrHeight = .5f; return root;
        }
        private static GameObject CreateAccountMenu(OnlineServiceProfile profile)
        {
            var root = CanvasRoot("AccountMenu", 20);
            var panel = Box("Panel", root.transform, Vector2.zero, new Vector2(600, 700), new Color(.035f, .055f, .09f, .98f));
            Label("Title", panel, "Norn · 登录与注册", new Vector2(0, 290), new Vector2(540, 52), 30);
            var identity = Label("Identity", panel, "请先登录，单机与匹配均需要账号。", new Vector2(0, 232), new Vector2(540, 38), 20);
            Label("UsernameLabel", panel, "用户名（3–24 位字母/数字/下划线）", new Vector2(0, 196), new Vector2(500, 32), 20);
            var username = Field("Username", panel, new Vector2(0, 154));
            Label("PasswordLabel", panel, "密码（10–128 字符）", new Vector2(0, 102), new Vector2(500, 32), 20);
            var password = Field("Password", panel, new Vector2(0, 60)); password.contentType = InputField.ContentType.Password;
            var register = Button("Register", panel, "注册", new Vector2(-110, -20), new Vector2(200, 44));
            var login = Button("Login", panel, "登录", new Vector2(110, -20), new Vector2(200, 44));
            var status = Label("Status", panel, "请输入账号和密码。", new Vector2(0, -90), new Vector2(500, 90), 20);
            var enter = Button("EnterHub", panel, "进入据点", new Vector2(0, -174), new Vector2(420, 48));
            var logout = Button("Logout", panel, "注销账号", new Vector2(0, -228), new Vector2(420, 42));
            var settings = Button("Settings", panel, "设置", new Vector2(-110, -286), new Vector2(200, 40));
            var quit = Button("Quit", panel, "退出游戏", new Vector2(110, -286), new Vector2(200, 40));
            root.AddComponent<OnlineAccountBootstrap>().SetProfile(profile);
            var data = new SerializedObject(root.AddComponent<AccountMenuController>());
            Set(data, "_username", username); Set(data, "_password", password); Set(data, "_register", register); Set(data, "_login", login);
            Set(data, "_enter", enter); Set(data, "_logout", logout); Set(data, "_settings", settings); Set(data, "_quit", quit);
            Set(data, "_status", status); Set(data, "_identity", identity); data.ApplyModifiedPropertiesWithoutUndo(); return root;
        }
        private static GameObject CreateEntryDialog()
        {
            var root = CanvasRoot("DungeonEntryDialog", 200);
            var backdrop = Box("Backdrop", root.transform, Vector2.zero, new Vector2(1280, 800), new Color(0, 0, 0, .65f));
            backdrop.anchorMin = Vector2.zero; backdrop.anchorMax = Vector2.one; backdrop.sizeDelta = Vector2.zero;
            var panel = Box("Panel", backdrop, Vector2.zero, new Vector2(720, 550), new Color(.035f, .06f, .1f));
            var title = Label("Title", panel, "选择副本玩法", new Vector2(0, 216), new Vector2(660, 48), 28);
            var status = Label("Status", panel, "请选择本次副本的进入方式。", new Vector2(0, 140), new Vector2(660, 92), 20);
            var members = Label("Members", panel, "", new Vector2(0, 14), new Vector2(660, 150), 20);
            var single = Button("SinglePlayer", panel, "单机进入", new Vector2(-155, -124), new Vector2(280, 52));
            var match = Button("Matchmaking", panel, "匹配", new Vector2(155, -124), new Vector2(280, 52));
            var ready = Button("Ready", panel, "准备", new Vector2(-155, -124), new Vector2(280, 52));
            var start = Button("StartRoom", panel, "开始（队长）", new Vector2(155, -124), new Vector2(280, 52));
            var reconnect = Button("Reconnect", panel, "重连", new Vector2(0, -124), new Vector2(280, 52));
            ready.gameObject.SetActive(false); start.gameObject.SetActive(false); reconnect.gameObject.SetActive(false);
            var cancel = Button("Cancel", panel, "取消", new Vector2(0, -214), new Vector2(400, 48));
            var data = new SerializedObject(root.AddComponent<DungeonEntryDialogController>());
            Set(data, "_panel", backdrop.gameObject); Set(data, "_title", title); Set(data, "_status", status);
            Set(data, "_single", single); Set(data, "_match", match); Set(data, "_cancel", cancel);
            Set(data, "_members", members); Set(data, "_ready", ready); Set(data, "_start", start); Set(data, "_reconnect", reconnect);
            data.ApplyModifiedPropertiesWithoutUndo(); return root;
        }

        internal static void UpdateRoomDialog()
        {
            // 通用Create只补缺失资产；这个显式升级入口才重建已知UI，并保留原Prefab GUID。
            var root = CreateEntryDialog();
            try { PrefabUtility.SaveAsPrefabAsset(root, EntryDialogPath); }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static void InstallMenuEntry()
        {
            var setup = EditorSceneManager.GetSceneManagerSetup();
            Scene menu = SceneManager.GetSceneByPath(MainMenu); bool opened = !menu.IsValid() || !menu.isLoaded;
            if (opened) menu = EditorSceneManager.OpenScene(MainMenu, OpenSceneMode.Additive);
            try
            {
                DemoMainMenuController controller = null;
                foreach (var root in menu.GetRootGameObjects())
                    if ((controller = root.GetComponentInChildren<DemoMainMenuController>(true)) != null) break;
                if (controller == null) throw new InvalidOperationException("主菜单缺少 Controller。");
                var serialized = new SerializedObject(controller);
                Set(serialized, "_accountMenuPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(AccountMenuPath));
                var panel = (GameObject)serialized.FindProperty("_mainPanel").objectReferenceValue;
                // 用户更正入口：删除本阶段新增的旧按钮，不改变原Play/Settings/Quit资产和绑定。
                var oldEntry = panel.transform.Find("OnlineButton");
                if (oldEntry != null) UnityEngine.Object.DestroyImmediate(oldEntry.gameObject);
                serialized.ApplyModifiedPropertiesWithoutUndo();
                if (!EditorSceneManager.SaveScene(menu)) throw new InvalidOperationException("主菜单入口保存失败。");
            }
            finally { if (opened) EditorSceneManager.CloseScene(menu, true); EditorSceneManager.RestoreSceneManagerSetup(setup); }
        }

        private static void EnsureProbe()
        {
            var setup = EditorSceneManager.GetSceneManagerSetup();
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            try
            {
                foreach (var root in scene.GetRootGameObjects()) if (root.GetComponent<OnlineAccountProbe>() != null) return;
                var probe = new GameObject("ExplicitAccountProbe"); SceneManager.MoveGameObjectToScene(probe, scene);
                probe.AddComponent<OnlineAccountProbe>(); EditorSceneManager.SaveScene(scene);
            }
            finally { EditorSceneManager.CloseScene(scene, true); EditorSceneManager.RestoreSceneManagerSetup(setup); }
        }

        [MenuItem("Tools/Norn/Online/Build Online Account Windows Client")]
        public static void BuildLab() => OnlineBuildTools.Build(false, ScenePath, "Builds/OnlineAccounts/Lab/NornAccountClient.exe", "account-client");
        [MenuItem("Tools/Norn/Online/Build Online Main Menu Windows Client")]
        public static void BuildMenu()
        {
            var scenes = new List<string> { MainMenu };
            foreach (var scene in EditorBuildSettings.scenes) if (scene.enabled && !scenes.Contains(scene.path)) scenes.Add(scene.path);
            if (!scenes.Contains(RoomLifecycleTools.PortalScenePath)) scenes.Add(RoomLifecycleTools.PortalScenePath);
            OnlineBuildTools.Build(false, MainMenu, "Builds/OnlineAccounts/Menu/NornMenuClient.exe", "account-menu-client", explicitScenes: scenes.ToArray());
        }

        [MenuItem("Tools/Norn/Online/Verify Online Workspace")]
        public static void VerifyWorkspace()
        {
            OnlineSceneSetup.RequireCleanEditor();
            // TestRunner 完成后可能暂留空Scene；只恢复已保存的原实验场景，不保存/丢弃任何Dirty内容。
            if (string.IsNullOrEmpty(SceneManager.GetActiveScene().path))
                EditorSceneManager.OpenScene("Assets/_Project/Scenes/Tests/MultiplayerAuthorityLab.unity");
            var scenes = new string[SceneManager.sceneCount];
            for (int i = 0; i < scenes.Length; i++) scenes[i] = SceneManager.GetSceneAt(i).path;
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(ContentManifestExporter.Output);
            var manifest = JsonUtility.FromJson<ContentManifest>(asset.text);
            if (manifest.Hash != ContentManifestRules.CalculateHash(manifest)) throw new InvalidOperationException("Manifest 摘要不匹配。");
            Directory.CreateDirectory("Tools/Networking/.artifacts");
            File.WriteAllText("Tools/Networking/.artifacts/n1-editor-handoff.json", JsonUtility.ToJson(new WorkspaceReport
            { Passed = true, ScenePaths = scenes, ManifestHash = manifest.Hash, ItemCount = manifest.Items.Length,
                RecordedAtUtc = DateTime.UtcNow.ToString("O") }, true));
            GameLog.Info("Editor 非Play、场景干净，线上清单校验通过。", "OnlineAccounts");
        }
        [Serializable] private sealed class WorkspaceReport
        {
            public bool Passed; public string[] ScenePaths; public string ManifestHash, RecordedAtUtc; public int ItemCount;
        }

        private static void Set(SerializedObject serialized, string name, UnityEngine.Object value) => serialized.FindProperty(name).objectReferenceValue = value;
        private static RectTransform Box(string name, Transform parent, Vector2 position, Vector2 size, Color color)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(Image)); var rect = (RectTransform)obj.transform;
            rect.SetParent(parent, false); rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f); rect.sizeDelta = size; rect.anchoredPosition = position;
            obj.GetComponent<Image>().color = color; return rect;
        }
        private static Text Label(string name, Transform parent, string text, Vector2 position, Vector2 size, int fontSize = 20)
        {
            var rect = Box(name, parent, position, size, Color.clear); UnityEngine.Object.DestroyImmediate(rect.GetComponent<Image>());
            var label = rect.gameObject.AddComponent<Text>(); label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.text = text; label.fontSize = fontSize; label.color = new Color(.9f, .94f, 1); label.alignment = TextAnchor.MiddleLeft; label.raycastTarget = false; return label;
        }
        private static UnityEngine.UI.Button Button(string name, Transform parent, string text, Vector2 position, Vector2 size)
        {
            var rect = Box(name, parent, position, size, new Color(.12f, .29f, .43f)); var button = rect.gameObject.AddComponent<UnityEngine.UI.Button>();
            var label = Label("Label", rect, text, Vector2.zero, size); label.alignment = TextAnchor.MiddleCenter; return button;
        }
        private static InputField Field(string name, Transform parent, Vector2 position)
        {
            var rect = Box(name, parent, position, new Vector2(390, 46), new Color(.13f, .18f, .25f));
            var field = rect.gameObject.AddComponent<InputField>(); field.textComponent = Label("Text", rect, "", Vector2.zero, new Vector2(366, 44));
            return field;
        }
    }
}
