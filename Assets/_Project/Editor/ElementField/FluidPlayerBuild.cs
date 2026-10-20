using System;
using System.Collections.Generic;
using System.IO;
using Game.Core;
using UnityEditor;
using UnityEditor.Build.Reporting;

namespace Game.EditorTools
{
    /// <summary>
    /// Task 13 的一次性 Windows Development Build Gate。只读取 EditorBuildSettings 中启用的 Scene，
    /// 输出到独立验证目录，避免覆盖开发者已有 Player；BuildReport 是 Player 打包成功与否的唯一判据。
    /// </summary>
    internal static class FluidPlayerBuild
    {
        private const string OutputPath =
            ".superpowers/sdd/2026-08-09 GPU PBF流体模拟实施计划/build/PBF_Task13_Windows/PBF_Task13.exe";

        [MenuItem("Tools/PBF Fluid/Build Fluid Windows Development")]
        public static void BuildWindowsDevelopment()
        {
            if (EditorApplication.isCompiling
                || EditorApplication.isUpdating
                || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.delayCall += BuildWindowsDevelopment;
                return;
            }

            try
            {
                string projectRoot = Directory.GetParent(UnityEngine.Application.dataPath)?.FullName
                    ?? throw new InvalidOperationException("无法解析 Unity Project Root。");
                string absoluteOutputPath = Path.GetFullPath(Path.Combine(projectRoot, OutputPath));
                Directory.CreateDirectory(Path.GetDirectoryName(absoluteOutputPath)
                    ?? throw new InvalidOperationException("无法解析 Build 输出目录。"));

                var enabledScenes = new List<string>(EditorBuildSettings.scenes.Length);
                EditorBuildSettingsScene[] configuredScenes = EditorBuildSettings.scenes;
                for (int index = 0; index < configuredScenes.Length; index++)
                {
                    if (configuredScenes[index].enabled)
                        enabledScenes.Add(configuredScenes[index].path);
                }

                var options = new BuildPlayerOptions
                {
                    scenes = enabledScenes.ToArray(),
                    locationPathName = absoluteOutputPath,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.Development | BuildOptions.AllowDebugging
                };
                GameLog.Info($"[PBF-TASK13-BUILD] START Scenes={enabledScenes.Count} Output={absoluteOutputPath}", "Editor");
                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;
                if (summary.result != BuildResult.Succeeded)
                {
                    GameLog.Error(
                        $"[PBF-TASK13-BUILD] FAILED Result={summary.result} Errors={summary.totalErrors} "
                        + $"Warnings={summary.totalWarnings} Duration={summary.totalTime}",
                        "Editor");
                    return;
                }

                GameLog.Info(
                    $"[PBF-TASK13-BUILD] SUCCESS Size={summary.totalSize} Errors={summary.totalErrors} "
                    + $"Warnings={summary.totalWarnings} Duration={summary.totalTime} Output={absoluteOutputPath}",
                    "Editor");
            }
            catch (Exception exception)
            {
                GameLog.Error($"[PBF-TASK13-BUILD] EXCEPTION {exception}", "Editor");
            }
        }
    }
}
