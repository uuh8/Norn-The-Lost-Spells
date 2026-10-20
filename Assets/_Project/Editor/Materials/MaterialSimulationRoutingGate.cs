using Game.Core;
using Game.ElementField;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// Task 19 运行整个 ElementField EditMode Assembly，防止新 Router 只通过新增测试，
    /// 却破坏既有 Impact、Initial Deposit、PBF Layout 或 Lifecycle Contract。
    /// </summary>
    [InitializeOnLoad]
    internal static class MaterialSimulationRoutingGate
    {
        private const string RunningKey = "Game.Materials.Task19.GateRunning";
        private const string RoutingPath =
            "Assets/_Project/ScriptableObjects/Materials/MaterialSimulationRouting_Default.asset";
        private static TestRunnerApi _api;
        private static Callbacks _callbacks;

        static MaterialSimulationRoutingGate()
        {
            if (SessionState.GetBool(RunningKey, false)) RegisterCallbacks();
        }

        [MenuItem("Tools/Material World/Run ElementField Gate")]
        private static void Start()
        {
            if (SessionState.GetBool(RunningKey, false)) return;
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
            if (EditorApplication.isCompiling
                || EditorApplication.isUpdating
                || AssetDatabase.LoadAssetAtPath<MaterialSimulationRoutingProfile>(RoutingPath) == null)
            {
                EditorApplication.delayCall += Run;
                return;
            }

            GameLog.Info("[MATERIAL-TASK19-TESTS] ElementField EditMode gate started.", "Editor");
            _api.Execute(new ExecutionSettings(new Filter
            {
                testMode = TestMode.EditMode,
                assemblyNames = new[] { "Game.ElementField.Tests" }
            }));
        }

        private sealed class Callbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { }

            public void RunFinished(ITestResultAdaptor result)
            {
                SessionState.SetBool(RunningKey, false);
                GameLog.Info(
                    $"[MATERIAL-TASK19-TESTS] FINISHED Passed={result.PassCount} Failed={result.FailCount} "
                    + $"Skipped={result.SkipCount} Inconclusive={result.InconclusiveCount} Duration={result.Duration:0.###}s",
                    "Editor");
                if (_api != null && _callbacks != null) _api.UnregisterCallbacks(_callbacks);
                if (_api != null) Object.DestroyImmediate(_api);
                _api = null;
                _callbacks = null;
            }

            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.TestStatus == TestStatus.Failed)
                    GameLog.Error($"[MATERIAL-TASK19-FAIL] {result.FullName}: {result.Message}", "Editor");
            }
        }
    }
}
