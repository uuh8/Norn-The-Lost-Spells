using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    /// <summary>
    /// 保留未保存关卡的显式诊断：直接调用 NUnit 方法，不冒充官方 TestRunner XML。
    /// 根对象状态先持久化，再临时隔离；取消、异常、超时、Domain Reload 都经过同一恢复入口。
    /// </summary>
    [InitializeOnLoad]
    internal static class ElementLoadedSceneGate
    {
        private const string RequestPath = "Temp/ElementPerformance/run-loaded-gate.request";
        private const string CancelPath = "Temp/ElementPerformance/cancel-loaded-gate.request";
        private const string ResultPath = "Temp/ElementPerformance/loaded-scene-gate.json";
        // Unity 重启会清空项目 Temp。恢复证据放在 Assets 外的持久目录，
        // Instance ID 只在同一 Editor 进程内有效，跨进程仅供人工核对名字和开关。
        private const string StatePath = "Backups/ElementPerformance/gate-saved-root-state.json";
        private const string StateKey = "Liquid.LoadedGate.Restore";
        private static readonly List<Invocation> Calls = new List<Invocation>(256);
        private static readonly List<Entry> Results = new List<Entry>(256);
        private static readonly Stack<IEnumerator> Routines = new Stack<IEnumerator>(16);
        private static int _next;
        private static Entry _entry;
        private static Invocation _call;
        private static Snapshot _snapshot;
        private static Scene _temporary;
        private static bool _running;
        private static string _start;
        private static double _startedAt, _caseStartedAt;
        private static object _wait;
        private static object _logScope;
        private static readonly Type LogScopeType = Type.GetType(
            "UnityEngine.TestTools.Logging.LogScope, UnityEngine.TestRunner", true);
        private static readonly MethodInfo EvaluateLogs = LogScopeType.GetMethod("EvaluateLogScope");

        static ElementLoadedSceneGate()
        {
            EditorApplication.update += Poll;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorApplication.quitting += BeforeReload;
            EditorApplication.playModeStateChanged += OnPlayStateChanged;
            // Reload 后仅恢复有本次 SessionState 证明的孤儿状态，绝不猜测所有对象应激活。
            EditorApplication.delayCall += RestoreOrphan;
        }

        private static void BeforeReload() { if (_running) Finish("Cancelled before assembly reload or Editor quit."); }
        private static void OnPlayStateChanged(PlayModeStateChange state)
        { if (_running && state == PlayModeStateChange.ExitingEditMode) Finish("Cancelled before entering Play Mode."); }

        private static void Poll()
        {
            if (File.Exists(CancelPath))
            {
                File.Delete(CancelPath);
                if (_running) Finish("Cancelled by explicit request.");
            }
            if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode) return;
            try
            {
                if (!_running)
                {
                    if (!File.Exists(RequestPath)) return;
                    Request request = JsonUtility.FromJson<Request>(File.ReadAllText(RequestPath));
                    File.Delete(RequestPath);
                    Begin(request);
                }
                if (EditorApplication.timeSinceStartup - _startedAt > 180)
                { Finish("Suite exceeded 180 second diagnostic limit."); return; }
                // 同步 NUnit 方法也会创建/释放 GraphicsBuffer。EditMode 没有持续 PlayerLoop；
                // 每个案例推进一次，让 Render Thread 处理提交和延迟释放，避免只在 Coroutine 时推进。
                EditorApplication.QueuePlayerLoopUpdate();
                if (Routines.Count > 0)
                {
                    if (EditorApplication.timeSinceStartup - _caseStartedAt > 60)
                        throw new TimeoutException("Coroutine exceeded 60 seconds.");
                    // EditMode 没有持续 Game Loop；请求一次更新和 Repaint，使 GPU readback 能实际前进。
                    EditorApplication.QueuePlayerLoopUpdate();
                    SceneView.RepaintAll();
                    if (_wait is AsyncOperation asyncOperation && !asyncOperation.isDone) return;
                    _wait = null;
                    IEnumerator routine = Routines.Peek();
                    if (routine.MoveNext())
                    {
                        object yielded = routine.Current;
                        if (yielded is IEnumerator nested) Routines.Push(nested);
                        else if (yielded is AsyncOperation) _wait = yielded;
                        else if (yielded != null)
                            throw new NotSupportedException("Direct gate does not support yield " + yielded.GetType().FullName);
                        return;
                    }
                    (Routines.Pop() as IDisposable)?.Dispose();
                    if (Routines.Count > 0) return;
                    CompleteCase(null);
                }
                if (_next == Calls.Count) { Finish(null); return; }
                _call = Calls[_next++];
                _entry = new Entry
                {
                    Name = _call.Method.DeclaringType.FullName + "." + _call.Method.Name,
                    Arguments = _call.Arguments == null ? "" : string.Join(",", _call.Arguments)
                };
                Results.Add(_entry);
                _caseStartedAt = EditorApplication.timeSinceStartup;
                // LogAssert.Expect 依赖官方包的作用域；复用它并检查遗漏/意外日志，
                // 避免显式诊断因为缺少 Runner 上下文误报，或把真正的 Error 当作通过。
                _logScope = Activator.CreateInstance(LogScopeType);
                InvokeLifecycle(_call.Instance, "NUnit.Framework.SetUpAttribute");
                IEnumerator result = _call.Method.Invoke(_call.Instance, _call.Arguments) as IEnumerator;
                if (result == null) CompleteCase(null);
                else Routines.Push(result);
            }
            catch (Exception exception)
            {
                if (!_running) { GameLog.Error(exception.ToString(), "LoadedLiquidGate"); return; }
                if (_entry == null) Finish(exception.ToString());
                else CompleteCase(exception);
            }
        }

        private static void Begin(Request request)
        {
            if (request == null || request.Fixtures == null || request.Fixtures.Length == 0)
                throw new ArgumentException("Fixtures must contain explicit test fixtures.");
            Calls.Clear(); Results.Clear(); Routines.Clear(); _next = 0; _entry = null;
            foreach (string name in request.Fixtures)
            {
                string assembly = name.StartsWith("Game.Rendering.", StringComparison.Ordinal)
                    ? "Game.Rendering.Tests" : "Game.ElementField.Tests";
                Type type = Type.GetType(name + ", " + assembly, true);
                object instance = Activator.CreateInstance(type);
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    bool simple = false, parameterized = false;
                    foreach (object attribute in method.GetCustomAttributes(false))
                    {
                        string kind = attribute.GetType().FullName;
                        if (kind == "NUnit.Framework.TestCaseAttribute")
                        {
                            parameterized = true;
                            object[] args = (object[])attribute.GetType().GetProperty("Arguments").GetValue(attribute);
                            ParameterInfo[] parameters = method.GetParameters();
                            for (int i = 0; i < args.Length; i++)
                                if (args[i] != null && !parameters[i].ParameterType.IsInstanceOfType(args[i]))
                                    args[i] = parameters[i].ParameterType.IsEnum
                                        ? Enum.ToObject(parameters[i].ParameterType, args[i])
                                        : Convert.ChangeType(args[i], parameters[i].ParameterType);
                            Calls.Add(new Invocation { Instance = instance, Method = method, Arguments = args });
                        }
                        else if (kind == "NUnit.Framework.TestAttribute" || kind == "UnityEngine.TestTools.UnityTestAttribute")
                            simple = true;
                    }
                    // 同时标注 [Test] 和 [TestCase] 时只执行参数案例，不能再虚构一个无参数调用。
                    if (simple && !parameterized)
                    {
                        if (method.GetParameters().Length != 0)
                            throw new NotSupportedException("Direct gate requires explicit TestCase arguments: " + method.Name);
                        Calls.Add(new Invocation { Instance = instance, Method = method });
                    }
                }
            }
            Scene original = SceneManager.GetActiveScene();
            var roots = new List<RootState>();
            for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
                foreach (GameObject root in SceneManager.GetSceneAt(sceneIndex).GetRootGameObjects())
                    roots.Add(new RootState { Id = root.GetInstanceID(), SceneHandle = root.scene.handle, Name = root.name, ScenePath = root.scene.path, Active = root.activeSelf });
            _snapshot = new Snapshot { ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id, OriginalScenePath = original.path, OriginalHandle = original.handle, OriginalDirty = original.isDirty, Roots = roots.ToArray() };
            string json = JsonUtility.ToJson(_snapshot, true);
            Directory.CreateDirectory("Temp/ElementPerformance");
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
            // 在第一个 SetActive 前写入两份证据；SessionState 跨 Domain Reload，磁盘用于人工审计。
            File.WriteAllText(StatePath, json);
            SessionState.SetString(StateKey, json);
            _running = true; _start = DateTime.UtcNow.ToString("O"); _startedAt = EditorApplication.timeSinceStartup;
            foreach (RootState root in _snapshot.Roots)
                if (EditorUtility.InstanceIDToObject(root.Id) is GameObject gameObject) gameObject.SetActive(false);
            _temporary = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            _snapshot.TemporaryHandle = _temporary.handle;
            json = JsonUtility.ToJson(_snapshot, true);
            File.WriteAllText(StatePath, json);
            SessionState.SetString(StateKey, json);
            SceneManager.SetActiveScene(_temporary);
        }

        private static void InvokeLifecycle(object instance, string attributeName)
        {
            foreach (MethodInfo method in instance.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                foreach (object attribute in method.GetCustomAttributes(true))
                    if (attribute.GetType().FullName == attributeName) method.Invoke(instance, null);
        }

        private static void CompleteCase(Exception exception)
        {
            while (Routines.Count > 0)
                try { (Routines.Pop() as IDisposable)?.Dispose(); }
                catch (Exception cleanup) { if (exception == null) exception = cleanup; }
            try { InvokeLifecycle(_call.Instance, "NUnit.Framework.TearDownAttribute"); }
            catch (Exception cleanup) { if (exception == null) exception = cleanup; }
            try { if (_logScope != null) EvaluateLogs.Invoke(_logScope, new object[] { true }); }
            catch (Exception logs) { if (exception == null) exception = logs; }
            finally { (_logScope as IDisposable)?.Dispose(); _logScope = null; }
            Exception error = exception is TargetInvocationException invocation ? invocation.InnerException : exception;
            _entry.Skipped = error != null && error.GetType().FullName == "NUnit.Framework.IgnoreException";
            _entry.Passed = error == null;
            _entry.Error = error?.ToString();
            _entry.DurationSeconds = EditorApplication.timeSinceStartup - _caseStartedAt;
            _entry = null; _call = null; _wait = null;
        }

        private static void Finish(string reason)
        {
            if (!_running) return;
            _running = false;
            if (_entry != null) CompleteCase(new OperationCanceledException(reason ?? "Interrupted."));
            bool restored = Restore(_snapshot);
            EditorApplication.QueuePlayerLoopUpdate();
            int passed = 0, skipped = 0;
            foreach (Entry entry in Results) { if (entry.Passed) passed++; if (entry.Skipped) skipped++; }
            File.WriteAllText(ResultPath, JsonUtility.ToJson(new Report
            {
                Kind = "Direct NUnit method invocation; not Unity TestRunner XML",
                StartUtc = _start, EndUtc = DateTime.UtcNow.ToString("O"), Passed = passed, Skipped = skipped,
                Failed = Results.Count - passed - skipped, Planned = Calls.Count, Completed = Results.Count,
                Aborted = reason != null, Reason = reason, SceneRestored = restored, Cases = Results.ToArray()
            }, true));
        }

        private static bool Restore(Snapshot snapshot)
        {
            if (snapshot == null) return false;
            if (snapshot.ProcessId != 0 && snapshot.ProcessId != System.Diagnostics.Process.GetCurrentProcess().Id) return false;
            bool restored = true;
            Scene original = FindLoadedScene(snapshot.OriginalHandle);
            if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
            else restored = false;
            foreach (RootState saved in snapshot.Roots)
            {
                if (!(EditorUtility.InstanceIDToObject(saved.Id) is GameObject root)
                    || root.scene.handle != saved.SceneHandle) { restored = false; continue; }
                root.SetActive(saved.Active);
            }
            if (original.IsValid() && original.isLoaded && snapshot.OriginalDirty)
                EditorSceneManager.MarkSceneDirty(original);
            Scene temporary = FindLoadedScene(snapshot.TemporaryHandle);
            // 只关闭本工具自己创建的场景；无论成功与否都不保存/替换用户关卡。
            if (snapshot.TemporaryHandle != 0 && temporary.IsValid() && temporary.isLoaded)
                EditorSceneManager.CloseScene(temporary, true);
            if (restored) SessionState.EraseString(StateKey);
            return restored;
        }

        private static void RestoreOrphan()
        {
            string json = SessionState.GetString(StateKey, "");
            if (!_running && !string.IsNullOrEmpty(json))
            {
                bool restored = Restore(JsonUtility.FromJson<Snapshot>(json));
                GameLog.Warn("Interrupted liquid gate recovery: " + restored, "LoadedLiquidGate");
            }
        }

        private static Scene FindLoadedScene(int handle)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            { Scene scene = SceneManager.GetSceneAt(i); if (scene.handle == handle) return scene; }
            return default;
        }

        private sealed class Invocation { public object Instance; public MethodInfo Method; public object[] Arguments; }
        [Serializable] private sealed class Request { public string[] Fixtures; }
        [Serializable] private sealed class RootState { public int Id, SceneHandle; public string Name, ScenePath; public bool Active; }
        [Serializable] private sealed class Snapshot { public int ProcessId, OriginalHandle, TemporaryHandle; public string OriginalScenePath; public bool OriginalDirty; public RootState[] Roots; }
        [Serializable] private sealed class Entry { public string Name, Arguments, Error; public bool Passed, Skipped; public double DurationSeconds; }
        [Serializable] private sealed class Report
        { public string Kind, StartUtc, EndUtc, Reason; public int Passed, Failed, Skipped, Planned, Completed; public bool Aborted, SceneRestored; public Entry[] Cases; }
    }
}
