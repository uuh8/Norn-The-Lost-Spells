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
                // Refresh本身用于修复尚未导入的外部Shared源码；不能先以旧编译失败标记将它锁死。
                // 它不保存/切换Scene。构建、资产装配及测试仍须通过下方严格干净检查。
                if (command == "refresh-source")
                {
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    WriteResult(command, true, null);
                    return;
                }
                if (command == "inspect-safety")
                {
                    InspectSafety();
                    WriteResult(command, true, null);
                    return;
                }
                OnlineSceneSetup.RequireCleanEditor();
                // 固定白名单，只调用与菜单相同的正式工具，不接受任意方法或路径。
                switch (command)
                {
                    case "reclaim-unreferenced-memory": ReclaimUnreferencedMemory(); break;
                    case "create-lab": OnlineSceneSetup.CreateLab(); break;
                    case "create-room-lab": RoomLifecycleTools.Create(); break;
                    case "setup-portal-room": RoomLifecycleTools.SetupPortal(); break;
                    case "setup-online-movement": OnlineMovementTools.Setup(); break;
                    case "verify-online-movement": OnlineMovementTools.Verify(); break;
                    case "align-wizard-feet": CharacterGroundingTools.AlignWizardFeet(); break;
                    case "setup-online-combat": OnlineCombatTools.Setup(); break;
                    case "refresh-online-hashes": OnlineCombatTools.RefreshRuntimeCompatibilityHash(); break;
                    case "create-world-replication-profile": OnlineCombatTools.CreateWorldReplicationProfile(); break;
                    case "verify-online-combat": OnlineCombatTools.Verify(); break;
                    case "setup-shared-hud": SharedGameplayHudTools.Setup(); break;
                    case "verify-shared-hud": SharedGameplayHudTools.VerifyMenu(); break;
                    case "setup-shared-camera": SharedGameplayCameraTools.Setup(); break;
                    case "verify-shared-camera": SharedGameplayCameraTools.VerifyMenu(); break;
                    case "upgrade-ui-focus": EventSystemFocusTools.Upgrade(); break;
                    case "export-combat-hud": SharedGameplayHudTools.ExportResource(); break;
                    case "verify-combat-hud": SharedGameplayHudTools.VerifyResource(); break;
                    case "export-combat-inventory": SharedInventoryPresentationTools.ExportResource(); break;
                    case "verify-combat-inventory": SharedInventoryPresentationTools.VerifyResource(); break;
                    case "create-early-presentation-fixture": CombatPresentationLifecycleFixtureTools.Create(); break;
                    case "export-online-dungeons": OnlineDungeonTools.Export(); break;
                    case "export-online-home": OnlineDungeonTools.ExportHome(); break;
                    case "verify-online-home": OnlineDungeonTools.VerifyHome(); break;
                    case "refresh-online-return-portals": OnlineDungeonTools.RefreshReturnBindings(); break;
                    case "verify-online-return-portals": OnlineDungeonTools.VerifyReturnBindings(); break;
                    // 仅同步原Loot配置/来源基线，不重导世界Prefab；仍经过同一干净Editor与正式Verify门。
                    case "refresh-online-dungeon-loot": OnlineDungeonTools.RefreshLootConfiguration(); break;
                    case "verify-online-dungeons": OnlineDungeonTools.Verify(); break;
                    case "list-native-profilers": PortalProfilerTools.List(); break;
                    case "capture-native-allocations": PortalProfilerTools.Capture(); break;
                    case "build-room-server": RoomLifecycleTools.BuildServer(); break;
                    case "build-room-server-verification": RoomLifecycleTools.BuildServerVerification(); break;
                    case "build-inventory-regression-client": RoomLifecycleTools.BuildInventoryRegressionClient(); break;
                    case "build-room-client": RoomLifecycleTools.BuildClient(); break;
                    case "start-room-business": OnlineLocalServices.StartRoomBusiness(); break;
                    case "verify-shared": OnlineFoundationTools.VerifySharedProtocol(); break;
                    // 外部编辑源码后先显式Refresh，不能用尚未导入的旧Assembly/0测试当作本轮验证。
                    case "refresh-source": AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport); break;
                    // Player构建可能已导入Test源码却尚未更新Editor测试程序集；显式请求后仍需核DLL时间与实际发现数。
                    case "recompile-scripts": UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation(); break;
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

        /// <summary>
        /// Native QA前仅回收Editor中真正未被引用的RAM。沿用RequireCleanEditor门禁，
        /// 不保存/关闭Scene，不清磁盘Library/Shader缓存，不结束用户进程或修改渲染/系统参数。
        /// false保留脚本字段和static引用；不能用true强行卸载用户仍在使用的对象。
        /// </summary>
        private static void ReclaimUnreferencedMemory()
        {
            if(BuildPipeline.isBuildingPlayer)throw new InvalidOperationException("构建期间不能回收Editor资产内存。");
            long before=GC.GetTotalMemory(false);
            string scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
            GC.Collect();
            EditorUtility.UnloadUnusedAssetsImmediate(false);
            GC.Collect();
            File.WriteAllText(Root+"/memory-reclaim.json",JsonUtility.ToJson(new MemoryReclaim
                {ManagedHeapBefore=before,ManagedHeapAfter=GC.GetTotalMemory(false),Scene=scene,RecordedUtc=DateTime.UtcNow.ToString("O")},true));
        }
        [Serializable] private sealed class MemoryReclaim{public long ManagedHeapBefore,ManagedHeapAfter;public string Scene,RecordedUtc;}

        /// <summary>
        /// 正常退出前的只读快照：场景和持久资源分别检查，不代为保存或丢弃。
        /// 不把Git未提交等同于Editor未保存；也不将Inspector等临时Editor对象误当生产资产。
        /// 外部必须核对本次请求时间，并仍通过正常关闭入口保留Unity自身的未保存提示。
        /// </summary>
        private static void InspectSafety()
        {
            var scenes = new System.Collections.Generic.List<string>();
            bool dirtyScene = false;
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                scenes.Add(scene.path);
                dirtyScene |= scene.isDirty;
            }
            var assets = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
            foreach (UnityEngine.Object asset in Resources.FindObjectsOfTypeAll<UnityEngine.Object>())
                if (asset != null && EditorUtility.IsPersistent(asset) && EditorUtility.IsDirty(asset))
                {
                    string path = AssetDatabase.GetAssetPath(asset);
                    if (!string.IsNullOrEmpty(path)) assets.Add(path);
                }
            File.WriteAllText(Root + "/editor-safety.json", JsonUtility.ToJson(new Safety
            {
                Playing = EditorApplication.isPlayingOrWillChangePlaymode,
                Compiling = EditorApplication.isCompiling, Updating = EditorApplication.isUpdating,
                CompilationFailed = EditorUtility.scriptCompilationFailed,
                Building = BuildPipeline.isBuildingPlayer,
                TestsRunning = SessionState.GetBool("Norn.SinglePlayer.Tests.Running", false),
                DirtyScene = dirtyScene, Scenes = scenes.ToArray(),
                DirtyAssets = new System.Collections.Generic.List<string>(assets).ToArray(),
                RecordedUtc = DateTime.UtcNow.ToString("O")
            }, true));
        }
        [Serializable] private sealed class Safety
        {
            public bool Playing, Compiling, Updating, CompilationFailed, Building, TestsRunning, DirtyScene;
            public string[] Scenes, DirtyAssets;
            public string RecordedUtc;
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
