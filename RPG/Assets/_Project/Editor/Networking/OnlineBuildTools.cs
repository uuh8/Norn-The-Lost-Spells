using System;
using System.IO;
using Game.Core;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Game.Project.Editor
{
    /// <summary>从同一实验场景输出两种程序；不用 Headless Client 冒充 Dedicated Server。</summary>
    public static class OnlineBuildTools
    {
        [MenuItem("Tools/Norn/Online/Build Lab Windows Client")]
        public static void BuildClient() => Build(false);

        [MenuItem("Tools/Norn/Online/Build Lab Windows Dedicated Server")]
        public static void BuildServer() => Build(true);

        [MenuItem("Tools/Norn/Online/Restore Saved Rendering References")]
        public static void RestoreSavedRenderingReferences()
        {
            OnlineSceneSetup.RequireCleanEditor();
            using (var settings = new ServerRenderingBuildScope(false)) { }
            string[] pipelines = new string[QualitySettings.names.Length];
            for (int i = 0; i < pipelines.Length; i++) pipelines[i] = AssetDatabase.GetAssetPath(QualitySettings.GetRenderPipelineAssetAt(i));
            Directory.CreateDirectory("Tools/Networking/.artifacts");
            File.WriteAllText("Tools/Networking/.artifacts/rendering-references.json", JsonUtility.ToJson(new RenderingEvidence
            { DefaultPipeline = AssetDatabase.GetAssetPath(UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline),
                QualityPipelines = pipelines, CurrentQuality = QualitySettings.GetQualityLevel(), RecordedAtUtc = DateTime.UtcNow.ToString("O") }, true));
        }

        [MenuItem("Tools/Norn/Online/Build Headless Grid Windows Server")]
        public static void BuildGridServer() => Build(true, HeadlessGridSceneSetup.ScenePath,
            "Builds/OnlineLab/HeadlessGrid/NornGridServer.exe", "grid-server");

        [MenuItem("Tools/Norn/Online/Build Grid Server Rendering Comparison")]
        public static void BuildGridServerRenderingComparison() => Build(true, HeadlessGridSceneSetup.ScenePath,
            "Builds/OnlineLab/HeadlessGrid/NornGridServer.exe", "grid-server", true);

        [MenuItem("Tools/Norn/Online/Build Grid Server Without Client Resources")]
        public static void BuildGridServerWithoutClientResources() => Build(true, HeadlessGridSceneSetup.ScenePath,
            "Builds/OnlineLab/HeadlessGrid/NornGridServer.exe", "grid-server", false, true);

        [MenuItem("Tools/Norn/Online/Verify Lab Shader Import")]
        public static void VerifyLabShaderImport()
        {
            OnlineSceneSetup.RequireCleanEditor();
            const string shaderPath = "Assets/ANGRY MESH/Stylized Pack - Common/Shaders/ASE_Template_URPLit.shader";
            // 增量 Build 可能复用资产缓存；只强制导入这份已知问题资产，避免删除整个 Library。
            // 此入口仅用于 Editor 离散诊断，不改变场景、材质引用或正式构建设置。
            AssetDatabase.ImportAsset(shaderPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            if (shader == null) throw new InvalidOperationException("未找到实验构建涉及的第三方 Shader。");

            var messages = ShaderUtil.GetShaderMessages(shader);
            var details = new string[messages.Length];
            int errors = 0;
            for (int i = 0; i < messages.Length; i++)
            {
                var message = messages[i];
                string severity = message.severity.ToString();
                if (severity == "Error") errors++;
                details[i] = severity + ": " + message.message + " (line " + message.line + ")";
            }
            // 报告先落盘再抛错：失败证据必须保留；这里的数组/JSON 分配不属于 Runtime 热路径。
            bool hasErrors = ShaderUtil.ShaderHasError(shader);
            Directory.CreateDirectory("Tools/Networking/.artifacts");
            File.WriteAllText("Tools/Networking/.artifacts/shader-import.json", JsonUtility.ToJson(new ShaderEvidence
            {
                ShaderPath = shaderPath, UnityVersion = Application.unityVersion,
                RecordedAtUtc = DateTime.UtcNow.ToString("O"), HasErrors = hasErrors,
                Errors = errors, Messages = details
            }, true));
            if (hasErrors || errors > 0) throw new InvalidOperationException("Shader 重新导入仍有错误，详见 Console 与 shader-import.json。");
            GameLog.Info("实验 Shader 重新导入验证通过。", "OnlineLab");
        }

        internal static void Build(bool server, string scenePath = OnlineSceneSetup.ScenePath,
            string requestedOutput = null, string requestedEvidenceName = null, bool isolateServerRendering = false,
            bool excludeClientResources = false, string[] explicitScenes = null)
        {
            OnlineSceneSetup.RequireCleanEditor();
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) == null)
                throw new InvalidOperationException("请先通过对应 Online 菜单创建实验场景：" + scenePath);
            if (explicitScenes != null)
                foreach (string path in explicitScenes)
                    if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null) throw new InvalidOperationException("构建 Scene 不存在：" + path);
            if (server && !HasWindowsServerModule())
                throw new InvalidOperationException("缺少 Windows Dedicated Server Build Support；用 Unity Hub 为当前 6000.3.16f1 安装后重试。");

            string output = requestedOutput ?? (server ? "Builds/OnlineLab/Server/NornServer.exe" : "Builds/OnlineLab/Client/NornClient.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            // 实验默认只含一个 Scene；N1 主菜单完整路径可显式给出多 Scene，仍不修改全局 Scene List。
            BuildReport report;
            // 普通 Build 也可能自动升级/保存 ProjectSettings；保护磁盘字节，同时恢复有效的内存引用。
            using (var rendering = new ServerRenderingBuildScope(server && isolateServerRendering))
            using (var resources = server && excludeClientResources ? new ServerClientResourcesBuildScope() : null)
            report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = explicitScenes ?? new[] { scenePath },
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)(server ? StandaloneBuildSubtarget.Server : StandaloneBuildSubtarget.Player),
                // Server 是独立 Named Build Target，不继承 Standalone 的 Editor Only 宏。
                // 仅在本次 Server 编译中排除 App UI Runtime；Editor 插件与 Client 的现有配置保留。
                extraScriptingDefines = server ? new[] { "APP_UI_EDITOR_ONLY" } : Array.Empty<string>(),
                options = BuildOptions.Development
            });
            Directory.CreateDirectory("Tools/Networking/.artifacts");
            File.WriteAllText("Tools/Networking/.artifacts/" + (requestedEvidenceName ?? (server ? "server" : "client")) + "-build.json",
                JsonUtility.ToJson(new Evidence { Result = report.summary.result.ToString(), UnityVersion = Application.unityVersion,
                    DedicatedServer = server, RenderingInputsIsolated = server && isolateServerRendering, Output = output,
                    ClientResourcesExcluded = server && excludeClientResources,
                    Errors = report.summary.totalErrors, RecordedAtUtc = DateTime.UtcNow.ToString("O") }, true));
            if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("实验构建失败，请查看 Console 与 Build Report。");
            // Unity 可能在仍报告 Shader 错误项时生成产物；不能把 Succeeded 误报成零错误。
            if (report.summary.totalErrors > 0)
                GameLog.Warn("已生成实验产物，但 Build Report 仍有错误项，请审阅构建报告：" + output, "OnlineLab");
            else
                GameLog.Info("实验构建成功：" + output, "OnlineLab");
        }

        public static bool HasWindowsServerModule()
        {
            string variations = Path.Combine(EditorApplication.applicationContentsPath, "PlaybackEngines", "WindowsStandaloneSupport", "Variations");
            return Directory.Exists(variations) && Directory.GetDirectories(variations, "*server*").Length > 0;
        }

        [Serializable]
        private sealed class Evidence
        {
            public string Result, UnityVersion, Output, RecordedAtUtc;
            public bool DedicatedServer, RenderingInputsIsolated, ClientResourcesExcluded;
            public int Errors;
        }
        [Serializable] private sealed class RenderingEvidence
        { public string DefaultPipeline, RecordedAtUtc; public string[] QualityPipelines; public int CurrentQuality; }

        [Serializable]
        private sealed class ShaderEvidence
        {
            public string ShaderPath, UnityVersion, RecordedAtUtc;
            public bool HasErrors;
            public int Errors;
            public string[] Messages;
        }
    }
}
