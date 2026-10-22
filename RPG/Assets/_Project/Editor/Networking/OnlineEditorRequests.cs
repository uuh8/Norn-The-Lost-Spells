using System;
using System.IO;
using Game.Core;
using UnityEditor;
using UnityEngine;

namespace Game.Project.Editor
{
    /// <summary>复用已打开 Editor 的显式验收请求；不双开原工程，不自动进入 Play 或保存用户场景。</summary>
    [InitializeOnLoad]
    internal static class OnlineEditorRequests
    {
        private const string Root = "Temp/OnlineLab";
        private static double _nextPoll;
        static OnlineEditorRequests() => EditorApplication.update += Poll;

        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + .5d;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            // 测试器可能暂时更换活动场景；等其完成再创建资产或构建，避免两个工具同时接管 Editor。
            if (SessionState.GetBool("Norn.SinglePlayer.Tests.Running", false)) return;
            string path = Root + "/command.request";
            if (!File.Exists(path)) return;
            string command = File.ReadAllText(path).Trim();
            File.Delete(path);
            try
            {
                OnlineSceneSetup.RequireCleanEditor();
                // 固定白名单，只调用与菜单相同的正式工具，不接受任意方法或路径。
                switch (command)
                {
                    case "create-lab": OnlineSceneSetup.CreateLab(); break;
                    case "create-room-lab": RoomLifecycleTools.Create(); break;
                    case "setup-portal-room": RoomLifecycleTools.SetupPortal(); break;
                    case "list-native-profilers": PortalProfilerTools.List(); break;
                    case "capture-native-allocations": PortalProfilerTools.Capture(); break;
                    case "build-room-server": RoomLifecycleTools.BuildServer(); break;
                    case "build-room-client": RoomLifecycleTools.BuildClient(); break;
                    case "start-room-business": OnlineLocalServices.StartRoomBusiness(); break;
                    case "verify-shared": OnlineFoundationTools.VerifySharedProtocol(); break;
                    // 外部编辑源码后先显式Refresh，不能用尚未导入的旧Assembly/0测试当作本轮验证。
                    case "refresh-source": AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport); break;
                    case "export-content": ContentManifestExporter.Export(); break;
                    case "create-account-ui": OnlineAccountTools.Create(); break;
                    case "verify-online-workspace": OnlineAccountTools.VerifyWorkspace(); break;
                    case "build-account-client": OnlineAccountTools.BuildLab(); break;
                    case "build-account-menu-client": OnlineAccountTools.BuildMenu(); break;
                    case "start-online-business": OnlineLocalServices.StartBusiness(); break;
                    case "stop-online-business": OnlineLocalServices.StopBusiness(); break;
                    case "start-online-database": OnlineLocalServices.StartDatabase(); break;
                    case "stop-online-database": OnlineLocalServices.StopDatabase(); break;
                    case "restore-rendering": OnlineBuildTools.RestoreSavedRenderingReferences(); break;
                    case "verify-lab-shader": OnlineBuildTools.VerifyLabShaderImport(); break;
                    case "build-client": OnlineBuildTools.BuildClient(); break;
                    case "build-server": OnlineBuildTools.BuildServer(); break;
                    case "create-grid-lab": HeadlessGridSceneSetup.CreateLab(); break;
                    case "build-grid-server": OnlineBuildTools.BuildGridServer(); break;
                    case "build-grid-server-no-ui": OnlineBuildTools.BuildGridServerWithoutClientResources(); break;
                    case "verify-grid-server": HeadlessGridVerification.Run(); break;
                    case "create-load-lab": TransportLoadTools.CreateLab(); break;
                    case "build-load-server": TransportLoadTools.BuildServer(); break;
                    case "build-load-client": TransportLoadTools.BuildClient(); break;
                    case "verify-transport-load": TransportLoadTools.Run(); break;
                    case "create-optimized-load-lab": TransportLoadTools.CreateOptimizedLab(); break;
                    case "build-optimized-load-server": TransportLoadTools.BuildOptimizedServer(); break;
                    case "build-optimized-load-client": TransportLoadTools.BuildOptimizedClient(); break;
                    case "create-auth-lab": TransportLoadTools.CreateAuthLab(); break;
                    case "build-auth-server": TransportLoadTools.BuildAuthServer(); break;
                    case "build-auth-client": TransportLoadTools.BuildAuthClient(); break;
                    case "verify-auth-snapshots": TransportLoadTools.RunAuth(); break;
                    case "verify-encrypted-load": TransportLoadTools.RunEncrypted(); break;
                    case "verify-loss-load": TransportLoadTools.RunLoss(); break;
                    case "capture-loss-allocations": TransportLoadTools.CaptureLoss(); break;
                    case "read-loss-allocations": TransportAllocationTraceTools.Read(); break;
                    case "capture-numeric-allocations": TransportLoadTools.CaptureNumeric(); break;
                    case "read-numeric-allocations": TransportAllocationTraceTools.ReadNumeric(); break;
                    default: throw new ArgumentException("未知 OnlineLab 请求。");
                }
                WriteResult(command, true, null);
            }
            catch (Exception exception)
            {
                WriteResult(command, false, exception.ToString());
                GameLog.Error(exception.Message, "OnlineLab");
            }
        }

        private static void WriteResult(string command, bool passed, string error)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Root + "/command-result.json", JsonUtility.ToJson(new Result
            { Command = command, Passed = passed, Error = error, RecordedAtUtc = DateTime.UtcNow.ToString("O") }, true));
        }

        [Serializable]
        private sealed class Result { public string Command, Error, RecordedAtUtc; public bool Passed; }
    }
}
