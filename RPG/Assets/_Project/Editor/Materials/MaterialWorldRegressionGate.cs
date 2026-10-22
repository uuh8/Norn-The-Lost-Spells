using Game.Core;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// Task 18 跨程序集 EditMode 回归入口。使用正在运行的 Unity Editor 执行，才能覆盖
    /// ScriptableObject、SerializedObject、AssetDatabase 与真实 ComputeShader 导入，而非只做 C# 编译。
    /// </summary>
    [InitializeOnLoad]
    internal static class MaterialWorldRegressionGate
    {
        private const string RunningKey = "Game.Materials.Task18.GateRunning";
        private static TestRunnerApi _api;
        private static Callbacks _callbacks;

        static MaterialWorldRegressionGate()
        {
            if (SessionState.GetBool(RunningKey, false))
                RegisterCallbacks();
        }

        [MenuItem("Tools/Material World/Run Full Regression Gate")]
        private static void Start()
        {
            if (SessionState.GetBool(RunningKey, false))
                return;
            SessionState.SetBool(RunningKey, true);
            RegisterCallbacks();
            EditorApplication.delayCall += Run;
        }

        private static void RegisterCallbacks()
        {
            _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            _callbacks = new Callbacks();
            _api.RegisterCallbacks(_callbacks);
        }

        private static void Run()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += Run;
                return;
            }

            var settings = new ExecutionSettings(new Filter
            {
                testMode = TestMode.EditMode,
                assemblyNames = new[]
                {
                    "Game.Materials.Tests",
                    "Game.Combat.Tests",
                    "Game.ElementField.Tests",
                    "Game.Rendering.Tests"
                }
            });
            GameLog.Info("[MATERIAL-TASK18-TESTS] full regression EditMode tests started.", "Editor");
            _api.Execute(settings);
        }

        private sealed class Callbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun)
            {
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                SessionState.SetBool(RunningKey, false);
                GameLog.Info(
                    $"[MATERIAL-TASK18-TESTS] FINISHED Passed={result.PassCount} Failed={result.FailCount} "
                    + $"Skipped={result.SkipCount} Inconclusive={result.InconclusiveCount} "
                    + $"Duration={result.Duration:0.###}s",
                    "Editor");
                if (_api != null && _callbacks != null)
                    _api.UnregisterCallbacks(_callbacks);
                if (_api != null)
                    Object.DestroyImmediate(_api);
                _api = null;
                _callbacks = null;
            }

            public void TestStarted(ITestAdaptor test)
            {
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.TestStatus == TestStatus.Failed)
                    GameLog.Error($"[MATERIAL-TASK18-FAIL] {result.FullName}: {result.Message}", "Editor");
            }
        }
    }
}
