using System;
using System.IO;
using System.Reflection;
using System.Text;
using Game.Core;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;

namespace Game.EditorTools
{
    /// <summary>
    /// 少量同步 Test 方法的 Editor 原生检查。官方 TestRunner 会先询问保存并重载 Dirty Scene；
    /// 这里直接调用明确无 SetUp/UnityTest 的断言，保留当前场景，不冒称官方 TestRunner XML。
    /// Reflection 和报告分配只在显式 Editor 请求执行，不属于 Gameplay 热路径。
    /// </summary>
    [InitializeOnLoad]
    internal static class MaterialPhaseFocusedChecks
    {
        private const string Request = "Temp/ElementPerformance/phase-native-checks.request";
        private const string Result = "Temp/ElementPerformance/phase-native-checks.txt";
        static MaterialPhaseFocusedChecks() => EditorApplication.update += Poll;
        private static void Poll()
        {
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating
                || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (!SessionState.GetBool("MaterialPhaseChecks.Refreshed", false))
            {
                // 请求可能紧跟源码写入；先 Refresh 并跨 Domain Reload 等待，不能执行仍装载的旧断言。
                SessionState.SetBool("MaterialPhaseChecks.Refreshed", true);
                SessionState.SetFloat("MaterialPhaseChecks.ReadyAt", (float)EditorApplication.timeSinceStartup + 5);
                AssetDatabase.Refresh(); return;
            }
            if (EditorApplication.timeSinceStartup < SessionState.GetFloat("MaterialPhaseChecks.ReadyAt", 0)) return;
            if(EditorUtility.scriptCompilationFailed)
            { File.WriteAllText(Result,"BLOCKED: current source compilation failed; old loaded assemblies are not validation evidence.\n"); return; }
            string mode=File.ReadAllText(Request).Trim();
            bool conversionsOnly = mode == "conversions";
            File.Delete(Request);
            SessionState.SetBool("MaterialPhaseChecks.Refreshed", false);
            var report = new StringBuilder(DateTime.UtcNow.ToString("O") + "\nMode=Direct synchronous NUnit assertions in Unity Editor; no Scene reload\n");
            report.AppendLine("UnityVersion="+UnityEngine.Application.unityVersion);
            report.AppendLine("ScriptCompilationFailed="+EditorUtility.scriptCompilationFailed);
            bool running = (bool)typeof(TestRunnerApi).GetMethod("IsRunActive", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            report.AppendLine("OfficialTestRunActive=" + running);
            if (running) { File.WriteAllText(Result, report + "Waiting: another test job is active.\n"); return; }
            SessionState.SetBool("ElementPerformance.GateRunning", false);
            // 旧版测试构造器断言失败时未进入 using.Dispose；只清理本检查创建的临时宿主及内存 Profile。
            foreach(var host in UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.GameObject>())
                if(host.name=="Conversion Test World" && !EditorUtility.IsPersistent(host))
                {
                    var world=host.GetComponent<Game.ElementField.ElementWorldRuntime>();
                    if(world==null) continue;
                    var profile=new SerializedObject(world).FindProperty("_profile").objectReferenceValue;
                    if(profile!=null && !EditorUtility.IsPersistent(profile)) UnityEngine.Object.DestroyImmediate(host);
                }
            int passed = 0, failed = 0;
            string[] fixtures = mode=="grain-gas"?new[]{"Game.ElementField.Tests.MaterialPhaseRuntimeTests","Game.ElementField.Tests.MaterialSolidGasWorldTests","Game.ElementField.Tests.FixedSolidGeometryTests","Game.Rendering.Tests.GasFieldVolumePerformanceTests"}
                :mode=="lifecycle"?new[]{"Game.Rendering.Tests.MaterialPhaseAssetTests","Game.ElementField.Tests.MaterialConversionTests","Game.ElementField.Tests.MaterialWorldSolidGasPerformanceTests","Game.Rendering.Tests.FallingLiquidPresentationTests","Game.Rendering.Tests.GridLiquidFieldSamplerTests"}
                :mode=="phase-e"?new[]{"Game.ElementField.Tests.MaterialWorldSolidGasPerformanceTests","Game.Rendering.Tests.GasFieldVolumePerformanceTests"}
                :mode=="liquid-regression"?new[]{"Game.ElementField.Tests.StableLiquidRegionCandidateTests"}
                :conversionsOnly ? new[] { "Game.ElementField.Tests.MaterialConversionTests" } : new[] { "Game.ElementField.Tests.MaterialSolidGasWorldTests", "Game.ElementField.Tests.FixedSolidGeometryTests",
                "Game.ElementField.Tests.MaterialPhaseRuntimeTests", "Game.ElementField.Tests.MaterialPhaseIntegrationTests",
                "Game.ElementField.Tests.MaterialPhaseSolidMaskTests",
                "Game.Rendering.Tests.MaterialPhaseAssetTests", "Game.Rendering.Tests.MaterialWorldSolidGasSceneContractTests", "Game.ElementField.Tests.MaterialConversionTests" };
            foreach (string fixture in fixtures)
            {
                Type type = null;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) if ((type = assembly.GetType(fixture)) != null) break;
                if (type == null) { report.AppendLine("FAIL missing compiled fixture " + fixture); failed++; continue; }
                object instance = Activator.CreateInstance(type);
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    foreach (CustomAttributeData attribute in method.GetCustomAttributesData())
                    {
                        string name = attribute.AttributeType.FullName;
                        if (name != "NUnit.Framework.TestAttribute" && name != "NUnit.Framework.TestCaseAttribute") continue;
                        object[] arguments = Array.Empty<object>();
                        if (name.EndsWith("TestCaseAttribute", StringComparison.Ordinal))
                        {
                            // params object[] 在 Metadata 中为一个数组；枚举参数需恢复强类型。
                            if (attribute.ConstructorArguments.Count == 1 && attribute.ConstructorArguments[0].ArgumentType.IsArray)
                            {
                                var values = (System.Collections.Generic.IList<CustomAttributeTypedArgument>)attribute.ConstructorArguments[0].Value;
                                arguments = new object[values.Count]; for (int i = 0; i < values.Count; i++) arguments[i] = values[i].Value;
                            }
                            else { arguments = new object[attribute.ConstructorArguments.Count]; for (int i = 0; i < arguments.Length; i++) arguments[i] = attribute.ConstructorArguments[i].Value; }
                            var parameters = method.GetParameters();
                            for (int i = 0; i < arguments.Length; i++) if (parameters[i].ParameterType.IsEnum) arguments[i] = Enum.ToObject(parameters[i].ParameterType, arguments[i]);
                        }
                        try { method.Invoke(instance, arguments); report.AppendLine("PASS " + fixture + "." + method.Name); passed++; }
                        catch (Exception error) { report.AppendLine("FAIL " + fixture + "." + method.Name + "\n" + (error.InnerException ?? error)); failed++; }
                    }
            }
            report.AppendLine("Passed=" + passed + " Failed=" + failed);
            File.WriteAllText(Result, report.ToString());
            GameLog.Info("Material Phase native checks: " + passed + " passed, " + failed + " failed.", "Editor");
        }
    }
}
