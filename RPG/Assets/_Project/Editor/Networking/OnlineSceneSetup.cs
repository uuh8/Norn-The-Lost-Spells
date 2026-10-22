using System;
using Game.Core;
using Game.Multiplayer;
using Game.Networking;
using kcp2k;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Project.Editor
{
    /// <summary>显式生成独立实验资产；不改正式 Hub、Prefab 或全局 Build Scene List。</summary>
    public static class OnlineSceneSetup
    {
        public const string ScenePath = "Assets/_Project/Scenes/Tests/MultiplayerAuthorityLab.unity";
        public const string ProfilePath = "Assets/_Project/ScriptableObjects/World/Multiplayer/OnlineNetworkProfile.asset";

        [MenuItem("Tools/Norn/Online/Create Local Transport Lab")]
        public static void CreateLab()
        {
            RequireCleanEditor();
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
            {
                GameLog.Info("实验场景已存在，请在 Project 中打开；没有覆盖已有资产。", "OnlineLab");
                return;
            }
            EnsureFolder("Assets/_Project/Scenes/Tests");
            EnsureFolder("Assets/_Project/ScriptableObjects/World/Multiplayer");
            var profile = AssetDatabase.LoadAssetAtPath<OnlineNetworkProfile>(ProfilePath);
            if (profile == null)
            {
                if (AssetDatabase.LoadMainAssetAtPath(ProfilePath) != null)
                    throw new InvalidOperationException("Profile 路径已有其他资产，拒绝覆盖。");
                profile = ScriptableObject.CreateInstance<OnlineNetworkProfile>();
                AssetDatabase.CreateAsset(profile, ProfilePath);
            }

            Scene original = SceneManager.GetActiveScene();
            // Unity 禁止在未命名 Scene 旁创建 Additive Scene。隔离 batch 工程的纯空初始场景
            // 没有用户内容，可以替换；交互 Editor 的未命名场景则要求用户先保存，绝不擅自替换。
            bool emptyBatchScene = Application.isBatchMode && SceneManager.sceneCount == 1 &&
                string.IsNullOrEmpty(original.path) && original.rootCount == 0;
            if (string.IsNullOrEmpty(original.path) && !emptyBatchScene)
                throw new InvalidOperationException("请先手动保存当前未命名场景，再创建实验场景。");
            Scene lab = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                emptyBatchScene ? NewSceneMode.Single : NewSceneMode.Additive);
            try
            {
                // 用临时 Additive 场景装配并保存，再关闭；当前场景不保存、不丢弃、不换内容。
                SceneManager.SetActiveScene(lab);
                var root = new GameObject("OnlineLabRoot");
                var kcp = root.AddComponent<KcpTransport>();
                kcp.DualMode = false;
                var manager = root.AddComponent<NornNetworkManager>();
                manager.transport = kcp;
                manager.autoCreatePlayer = false;
                manager.dontDestroyOnLoad = false;
                manager.runInBackground = true;
                manager.headlessStartMode = Mirror.HeadlessStartOptions.DoNothing;
                manager.editorAutoStart = false;
                root.AddComponent<DedicatedServerBootstrap>().SetProfile(profile);
                root.AddComponent<OnlineClientBootstrap>();
                root.AddComponent<OnlineLabProbe>();
                var cameraObject = new GameObject("LabCamera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.04f, .05f, .07f);
                if (!EditorSceneManager.SaveScene(lab, ScenePath)) throw new InvalidOperationException("实验场景保存失败。");
                GameLog.Info("本地实验场景和 SO 已建立；请打开实验场景审阅。", "OnlineLab");
            }
            finally
            {
                if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
                if (!emptyBatchScene) EditorSceneManager.CloseScene(lab, true);
            }
        }

        internal static void RequireCleanEditor()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorUtility.scriptCompilationFailed)
                throw new InvalidOperationException("请先退出 Play Mode 并解决编译问题。");
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty)
                    throw new InvalidOperationException("存在未保存场景，请自行保存或处理后重试；工具不会代为保存或丢弃。");
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int slash = path.LastIndexOf('/');
            EnsureFolder(path.Substring(0, slash));
            AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
        }
    }
}
