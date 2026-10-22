using System;
using Game.Core;
using Game.Multiplayer;
using Game.Networking;
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
                var portals = data.FindProperty("_portals"); portals.arraySize = 3;
                string[] paths = { "Assets/_Project/Scenes/P7_DemoRun.unity", "Assets/_Project/Scenes/P8_BossField.unity", "Assets/_Project/Scenes/Tests/RaidWorldSandbox.unity" };
                string[] keys = { "p7", "p8", "raid" };
                for (int i = 0; i < 3; i++) { var binding = portals.GetArrayElementAtIndex(i); binding.FindPropertyRelative("PortalScenePath").stringValue = paths[i]; binding.FindPropertyRelative("DungeonId").stringValue = keys[i]; }
                data.ApplyModifiedPropertiesWithoutUndo();
                // 正式连接Scene只负责N2入场展示；不复制P7/P8客户端角色到服务器伪称权威玩法。
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(PortalScenePath) == null)
                {
                    var camera = new GameObject("ConnectionCamera").AddComponent<Camera>(); camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.025f,.04f,.065f);
                    new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule)).GetComponent<InputSystemUIInputModule>().AssignDefaultActions();
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
        public static void BuildClient() => OnlineBuildTools.Build(false, ScenePath, ClientPath, "room-client");
        [MenuItem("Tools/Norn/Online/Build Room Dedicated Server (Windows)")]
        public static void BuildServer() => OnlineBuildTools.Build(true, ScenePath, ServerPath, "room-server");
    }
}
