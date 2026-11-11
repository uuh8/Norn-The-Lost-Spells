using System;
using Game.Core;
using Game.Multiplayer;
using Game.Networking;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

namespace Game.Project.Editor
{
    /// <summary>隔离的真实准入诊断Scene/Build，不复用N0凭据、不改正式单机场景或全局Build列表。</summary>
    public static class RoomLifecycleTools
    {
        public const string ScenePath = "Assets/_Project/Scenes/Tests/RoomLifecycleLab.unity";
        public const string ServerPath = "Builds/OnlineRooms/Server/NornRoomServer.exe";
        public const string ClientPath = "Builds/OnlineRooms/Client/NornRoomClient.exe";
        public const string PortalScenePath = "Assets/_Project/Scenes/Online/OnlineRoom.unity";
        public const string ConnectionPrefabPath = "Assets/_Project/Prefabs/Online/RoomConnection.prefab";
        private const string InventoryRegressionScenePath = "Assets/_Project/Scenes/Tests/SinglePlayerInventoryRegression.unity";
        [MenuItem("Tools/Norn/Online/Setup Portal Room Entry")]
        public static void SetupPortal()
        {
            OnlineSceneSetup.RequireCleanEditor();
            System.IO.Directory.CreateDirectory("Assets/_Project/Scenes/Online");
            System.IO.Directory.CreateDirectory("Assets/_Project/Prefabs/Online"); AssetDatabase.Refresh();
            var original = SceneManager.GetActiveScene(); var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                var root = new GameObject("RoomConnection"); var raw = root.AddComponent<NornKcpTransport>(); raw.DualMode = false;
                var manager = root.AddComponent<RoomNetworkManager>(); manager.authenticator = root.AddComponent<JoinTicketAuthenticator>();
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, ConnectionPrefabPath); UnityEngine.Object.DestroyImmediate(root);
                OnlineAccountTools.UpdateRoomDialog();
                var profile = AssetDatabase.LoadAssetAtPath<OnlineRoomProfile>("Assets/_Project/Resources/Online/RoomClientProfile.asset");
                if (profile == null) { profile = ScriptableObject.CreateInstance<OnlineRoomProfile>(); AssetDatabase.CreateAsset(profile, "Assets/_Project/Resources/Online/RoomClientProfile.asset"); }
                var data = new SerializedObject(profile); data.FindProperty("_connectionPrefab").objectReferenceValue = prefab;
                data.FindProperty("_connectionScene").stringValue = PortalScenePath;
                data.FindProperty("_hubScene").stringValue = "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
                var portals = data.FindProperty("_portals"); portals.arraySize = 4;
                string[] paths = { "Assets/_Project/Scenes/P7_DemoRun.unity", "Assets/_Project/Scenes/P8_BossField.unity", "Assets/_Project/Scenes/Tests/RaidWorldSandbox.unity", "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity" };
                string[] keys = { "p7", "p8", "raid", "home" };
                for (int i = 0; i < paths.Length; i++) { var binding = portals.GetArrayElementAtIndex(i); binding.FindPropertyRelative("PortalScenePath").stringValue = paths[i]; binding.FindPropertyRelative("DungeonId").stringValue = keys[i]; }
                data.ApplyModifiedPropertiesWithoutUndo();
                // 正式连接Scene只负责N2入场展示；不复制P7/P8客户端角色到服务器伪称权威玩法。
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(PortalScenePath) == null)
                {
                    var camera = new GameObject("ConnectionCamera").AddComponent<Camera>(); camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.025f,.04f,.065f);
                    new GameObject("EventSystem", typeof(FocusAwareEventSystem), typeof(InputSystemUIInputModule)).GetComponent<InputSystemUIInputModule>().AssignDefaultActions();
                    if (!EditorSceneManager.SaveScene(scene, PortalScenePath)) throw new InvalidOperationException("连接Scene保存失败。");
                }
                AssetDatabase.SaveAssets();
            }
            finally { if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original); EditorSceneManager.CloseScene(scene, true); }
            UpgradeLabTransport();
        }
        private static void UpgradeLabTransport()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null) return;
            var lab = SceneManager.GetSceneByPath(ScenePath); bool opened = !lab.IsValid() || !lab.isLoaded;
            if (opened) lab = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            try
            {
                foreach (var root in lab.GetRootGameObjects())
                {
                    var raw = root.GetComponentInChildren<NornKcpTransport>(true); if (raw == null || !raw.DualMode) continue;
                    // Raw IO在Awake决定路径；必须在序列化场景里设IPv4，不能等Bootstrap.Start再改。
                    var data = new SerializedObject(raw); data.FindProperty("DualMode").boolValue = false; data.ApplyModifiedPropertiesWithoutUndo();
                    if (!EditorSceneManager.SaveScene(lab)) throw new InvalidOperationException("Room Lab IPv4配置保存失败。");
                }
            }
            finally { if (opened) EditorSceneManager.CloseScene(lab, true); }
        }
        [MenuItem("Tools/Norn/Online/Create Room Lifecycle Lab")]
        public static void Create()
        {
            OnlineSceneSetup.RequireCleanEditor(); if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null) { GameLog.Info("Room Lab已存在，未覆盖。", "OnlineRooms"); return; }
            var original = SceneManager.GetActiveScene(); var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene); var root = new GameObject("OnlineRoom"); root.AddComponent<NornKcpTransport>().DualMode = false;
                var manager = root.AddComponent<RoomNetworkManager>(); manager.authenticator = root.AddComponent<JoinTicketAuthenticator>();
                root.AddComponent<RoomInstanceBootstrap>(); root.AddComponent<RoomAdmissionProbe>();
                var camera = new GameObject("DiagnosticCamera").AddComponent<Camera>(); camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.04f,.05f,.08f);
                if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new InvalidOperationException("Room Lab保存失败。");
            }
            finally { if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original); EditorSceneManager.CloseScene(scene, true); }
        }
        [MenuItem("Tools/Norn/Online/Build Room Client (Windows)")]
        public static void BuildClient() { OnlineMovementTools.Verify(); OnlineBuildTools.Build(false, ScenePath, ClientPath, "room-client"); }
        [MenuItem("Tools/Norn/Online/Build Room Dedicated Server (Windows)")]
        public static void BuildServer() { OnlineMovementTools.Verify(); OnlineBuildTools.Build(true, ScenePath, ServerPath, "room-server"); }
        /// <summary>
        /// 独立验证源码的Dedicated分支，沿正式Build工具与原Rendering保护Scope；
        /// 每次输出到唯一验证目录，不覆盖业务菜单正在使用的ServerPath，不启动任何玩家或服务。
        /// </summary>
        public static void BuildServerVerification()
        {
            OnlineMovementTools.Verify();
            string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            string output = "Builds/OnlineValidation/RoomServer/" + stamp + "/NornRoomServer.exe";
            OnlineBuildTools.Build(true, ScenePath, output, "room-server-verification-" + stamp);
        }
        /// <summary>
        /// 显式Native库存回归复用正式OnlineRoom的Scope、Camera和原UI，再加私有Ticket准入探针。
        /// 历史Lab只有技术HUD，不能据它声称测试过Tab。独立副本不保存原Scene或修改全局Build列表。
        /// </summary>
        public static void BuildInventoryRegressionClient()
        {
            OnlineSceneSetup.RequireCleanEditor();
            OnlineMovementTools.Verify();
            CreateInventoryRegressionScene();
            string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            OnlineBuildTools.Build(false, InventoryRegressionScenePath, "Builds/OnlineValidation/InventoryClient/" + stamp + "/NornInventoryClient.exe",
                "inventory-client-verification-" + stamp);
        }

        private static void CreateInventoryRegressionScene()
        {
            // 先由Unity生成完整Scene副本与.meta，不能手写GUID或在正式场景插入诊断组件。
            // 不覆盖用户正在查看/编辑的旧验证场景；正常构建从MainMenu执行，原Scene始终不保存。
            var existing = SceneManager.GetSceneByPath(InventoryRegressionScenePath);
            if (existing.IsValid() && existing.isLoaded)
                throw new InvalidOperationException("请先关闭已打开的库存验证场景，再从干净Editor构建。");
            var template = SceneManager.GetSceneByPath(PortalScenePath);
            bool openedTemplate = !template.IsValid() || !template.isLoaded;
            if (openedTemplate) template = EditorSceneManager.OpenScene(PortalScenePath, OpenSceneMode.Additive);
            try
            {
                if (!EditorSceneManager.SaveScene(template, InventoryRegressionScenePath, true))
                    throw new InvalidOperationException("正式OnlineRoom的独立验证副本保存失败。");
            }
            finally { if (openedTemplate) EditorSceneManager.CloseScene(template, true); }
            AssetDatabase.ImportAsset(InventoryRegressionScenePath, ImportAssetOptions.ForceSynchronousImport);
            var scene = EditorSceneManager.OpenScene(InventoryRegressionScenePath, OpenSceneMode.Additive);
            try
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ConnectionPrefabPath);
                if (prefab == null || prefab.GetComponent<RoomNetworkManager>() == null || prefab.GetComponent<JoinTicketAuthenticator>() == null)
                    throw new InvalidOperationException("正式RoomConnection准入Prefab缺失，不能用替代认证构建验收。");
                foreach (var root in scene.GetRootGameObjects())
                    if (root.GetComponentInChildren<RoomNetworkManager>(true) != null)
                        throw new InvalidOperationException("OnlineRoom模板已含连接Owner，拒绝额外生成第二个Manager。");
                var connection = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                connection.AddComponent<RoomAdmissionProbe>();
                CombatPresentationTools.InstallOnline(scene);
                if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("库存验证场景装配保存失败。");
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }
    }
}
