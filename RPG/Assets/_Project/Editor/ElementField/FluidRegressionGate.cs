using Game.Core;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// 当前授权轮次的一次性 EditMode Gate。测试由正在运行的 Unity Editor 执行，
    /// 因此结果能覆盖 ComputeShader/GPU fixture，而不是把 dotnet build 冒充 Test Runner。
    /// </summary>
    [InitializeOnLoad]
    internal static class FluidRegressionGate
    {
        private const string RunningSessionKey = "Game.PbfTask13.PbfFocusedGateRunning";
        private static TestRunnerApi _api;
        private static GateCallbacks _callbacks;

        static FluidRegressionGate()
        {
            // EditMode 测试可以触发 Play Mode 切换与 Domain Reload。静态字段会被清空，
            // 所以运行期间每次 Reload 都必须重新注册 callback；没有手动启动时不做任何自动工作。
            if (SessionState.GetBool(RunningSessionKey, false))
                RegisterCallbacks();
        }

        [MenuItem("Tools/PBF Fluid/Run Fluid Focused EditMode Gate")]
        private static void StartFocusedGate()
        {
            if (SessionState.GetBool(RunningSessionKey, false))
                return;
            SessionState.SetBool(RunningSessionKey, true);
            RegisterCallbacks();
            EditorApplication.delayCall += Run;
        }

        private static void RegisterCallbacks()
        {
            _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            _callbacks = new GateCallbacks();
            _api.RegisterCallbacks(_callbacks);
        }

        private static void Run()
        {
            if (EditorApplication.isCompiling
                || EditorApplication.isUpdating
                || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.delayCall += Run;
                return;
            }

            FluidWorldSetup.ValidateSetup();
            var settings = new ExecutionSettings(new Filter
            {
                testMode = TestMode.EditMode,
                assemblyNames = new[] { "Game.ElementField.Tests", "Game.Rendering.Tests" }
            });
            GameLog.Info("[PBF-PHASE-F-TESTS] Phase F focused EditMode 测试开始。", "Editor");
            _api.Execute(settings);
        }

        private sealed class GateCallbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun)
            {
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                SessionState.SetBool(RunningSessionKey, false);
                GameLog.Info(
                    $"[PBF-PHASE-F-TESTS] FINISHED Passed={result.PassCount} Failed={result.FailCount} "
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
                if (result.TestStatus != TestStatus.Failed)
                    return;
                GameLog.Error(
                    $"[PBF-PHASE-F-TEST-FAIL] {result.FullName}: {result.Message}\n{result.StackTrace}",
                    "Editor");
            }
        }
    }
}
