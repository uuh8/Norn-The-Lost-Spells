using System;
using System.IO;
using Game.Core;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    /// <summary>
    /// 单机资产的显式 EditMode/PlayMode 入口。复用已打开 Editor 的官方 TestRunner，不开第二个进程。
    /// 非 Play Mode、全部场景干净才接受；不执行保存/丢弃，也不生成或打开测试场景。
    /// </summary>
    [InitializeOnLoad]
    internal static class SinglePlayerInventoryTestGate
    {
        private const string DirectoryPath = "Temp/SinglePlayer";
        private const string RequestPath = DirectoryPath + "/run-editmode.request.json";
        private const string PlayRequestPath = DirectoryPath + "/run-playmode.request.json";
        private const string RestoreHubPath = DirectoryPath + "/open-saved-hub.request";
        private const string StateKey = "Norn.SinglePlayer.Tests.";
        private static double _nextPoll;

        static SinglePlayerInventoryTestGate()
        {
            EditorApplication.update += Poll;
            // Play Mode 会重载 AppDomain；纯 static 回调会丢失。SessionState 保留测试上下文，
            // 每次 Editor Domain 建立后向官方 API 重新注册，仅接收本入口明确发起的结果。
            TestRunnerApi.RegisterTestCallback(new Callbacks());
        }

        private static void Poll()
        {
            // 显式停止了本工具发起的Play测试后，旧Runner可能不回调RunFinished。
            // 仅接受人工恢复请求，且必须已退出Play；不把取消当作通过或覆盖Dirty场景。
            const string recover = "Temp/SinglePlayer/recover-stopped-test.request";
            if (File.Exists(recover) && !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling)
            {
                File.Delete(recover);
                WriteReport("Cancelled", 0, 0, "测试显式停止，恢复工具入口；不记通过。", null);
                SessionState.SetBool(StateKey + "Running", false);
            }
            if (EditorApplication.timeSinceStartup < _nextPoll || SessionState.GetBool(StateKey + "Running", false)) return;
            _nextPoll = EditorApplication.timeSinceStartup + .5d;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if(File.Exists(RestoreHubPath))
            {
                File.Delete(RestoreHubPath);
                // Editor崩溃后TestRunner可能恢复到空Scene；只打开已保存Hub，拒绝覆盖任何Dirty Scene。
                if(!EditorApplication.isPlayingOrWillChangePlaymode && !HasDirtyScene())
                    UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity");
                else GameLog.Warn("存在Play或未保存Scene，没有恢复据点。","InventoryTests");
                return;
            }
            bool play = File.Exists(PlayRequestPath);
            string requestPath = play ? PlayRequestPath : RequestPath;
            if (!File.Exists(requestPath)) return;

            string json = File.ReadAllText(requestPath);
            File.Delete(requestPath);
            SessionState.SetString(StateKey + "Mode", play ? "playmode" : "editmode");
            Directory.CreateDirectory(DirectoryPath);
            // Unity保留上一份可加载Assembly；编译失败时不能用旧测试结果冒充最新代码验证。
            if (EditorUtility.scriptCompilationFailed)
            {
                WriteReport("Blocked", 0, 0, "当前脚本编译失败；拒绝运行旧Assembly。先修复Console编译错误。", null);
                return;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode || HasDirtyScene())
            {
                WriteReport("Blocked", 0, 0, "当前 Play Mode 或有未保存场景；没有自动保存/丢弃，也没有运行测试。", null);
                return;
            }

            try
            {
                Request request = JsonUtility.FromJson<Request>(json);
                if (request == null || request.TestNames == null || request.TestNames.Length == 0)
                    throw new ArgumentException("必须指定本次验证的 TestNames。");
                for (int i = 0; i < request.TestNames.Length; i++)
                    if (string.IsNullOrWhiteSpace(request.TestNames[i]) ||
                        (!request.TestNames[i].StartsWith("Game.Run.Tests.", StringComparison.Ordinal) &&
                         !request.TestNames[i].StartsWith("Game.Skills.Tests.", StringComparison.Ordinal) &&
                         !request.TestNames[i].StartsWith("Game.Character.Tests.", StringComparison.Ordinal) &&
                         !request.TestNames[i].StartsWith("Game.ElementField.Tests.", StringComparison.Ordinal)))
                        throw new ArgumentException("此入口只允许 Run/Skills/Character/ElementField 的明确 fixture 或测试名。");

                SessionState.SetString(StateKey + "Started", DateTime.UtcNow.ToString("O"));
                SessionState.SetString(StateKey + "Original", SceneManager.GetActiveScene().path);
                SessionState.SetBool(StateKey + "Running", true);
                WriteReport("Running", 0, 0, null, request.TestNames);
                var api = ScriptableObject.CreateInstance<TestRunnerApi>();
                try { api.Execute(new ExecutionSettings(new Filter
                {
                    testMode = play ? TestMode.PlayMode : TestMode.EditMode,
                    assemblyNames = play ? new[] { "Game.Run.PlayMode.Tests" } : new[] { "Game.Run.Tests", "Game.Skills.Tests", "Game.Character.Tests", "Game.ElementField.Tests" },
                    testNames = request.TestNames
                })); }
                finally { UnityEngine.Object.DestroyImmediate(api); }
            }
            catch (Exception exception)
            {
                WriteReport("Error", 0, 0, exception.ToString(), null);
                SessionState.SetBool(StateKey + "Running", false);
            }
        }

        private static bool HasDirtyScene()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty) return true;
            return false;
        }

        private static void WriteReport(string state, int passed, int failed, string error, string[] tests)
        {
            File.WriteAllText(DirectoryPath + "/" + SessionState.GetString(StateKey + "Mode", "editmode") + "-report.json", JsonUtility.ToJson(new Report
            {
                State = state,
                StartedUtc = SessionState.GetString(StateKey + "Started", ""),
                UpdatedUtc = DateTime.UtcNow.ToString("O"),
                OriginalScene = SessionState.GetString(StateKey + "Original", ""),
                CurrentScene = SceneManager.GetActiveScene().path,
                SceneDirty = HasDirtyScene(),
                Passed = passed,
                Failed = failed,
                Error = error,
                TestNames = tests
            }, true));
        }

        private sealed class Callbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { }
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }
            public void RunFinished(ITestResultAdaptor result)
            {
                if (!SessionState.GetBool(StateKey + "Running", false)) return;
                try
                {
                    string mode = SessionState.GetString(StateKey + "Mode", "editmode");
                    TestRunnerApi.SaveResultToFile(result, DirectoryPath + "/" + mode + "-results.xml");
                    WriteReport("Finished", result.PassCount, result.FailCount, null, null);
                    GameLog.Info($"单机资产 {mode}：通过 {result.PassCount}，失败 {result.FailCount}。", "InventoryTests");
                }
                catch (Exception exception)
                {
                    WriteReport("Error", result.PassCount, result.FailCount, exception.ToString(), null);
                }
                finally { SessionState.SetBool(StateKey + "Running", false); }
            }
        }

        [Serializable]
        private sealed class Request { public string[] TestNames; }
        [Serializable]
        private sealed class Report
        {
            public string State, StartedUtc, UpdatedUtc, OriginalScene, CurrentScene, Error;
            public bool SceneDirty;
            public int Passed, Failed;
            public string[] TestNames;
        }
    }
}
