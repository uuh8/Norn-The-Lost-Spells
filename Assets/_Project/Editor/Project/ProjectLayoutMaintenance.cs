using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Game.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>显式目录维护入口。GUID是Unity引用身份，字符串路径必须由迁移清单另行更新。</summary>
    [InitializeOnLoad]
    public static class ProjectLayoutMaintenance
    {
        private const string Root="Temp/ArchitectureCleanup/";
        [Serializable] public sealed class Move { public string From,To,Guid; }
        [Serializable] public sealed class Retired { public string Path,Guid,Sha256; }
        [Serializable] public sealed class Manifest { public Move[] Moves; public string[] EmptyDirectories;public Retired[] Retired;public string Archive,ArchiveSha256; }
        [Serializable] private sealed class State { public bool Playing,Compiling,Dirty;public string Scene;public string[] Dependencies; }
        static ProjectLayoutMaintenance(){EditorApplication.update+=Poll;}
        private static void Poll()
        {
            string request=Root+"command.request";
            if(!File.Exists(request)||EditorApplication.isCompiling||EditorApplication.isUpdating)return;
            string command=File.ReadAllText(request).Trim();
            // 维护任务允许结束临时Play；绝不保存或丢弃Edit Mode中尚未保存的作者修改。
            if(command=="stop"&&EditorApplication.isPlayingOrWillChangePlaymode)
            {EditorApplication.isPlaying=false;return;}
            File.Delete(request);
            try
            {
                if(command=="inspect")Inspect();
                else if(command=="stop")EnsureEditable();
                else if(command=="empty")
                {EnsureEditable();EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);}
                else if(command=="move")ExecuteMoves();
                else if(command=="retire-hub") { EnsureEditable(); HubGameplayMigration.RemoveLegacyBindings(); }
                else throw new InvalidOperationException("未知目录维护命令："+command);
                File.WriteAllText(Root+"result.txt","OK: "+command);
            }
            catch(Exception e){File.WriteAllText(Root+"result.txt",e.ToString());GameLog.Error(e.Message,"ProjectLayout");}
        }
        private static void EnsureEditable()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)throw new InvalidOperationException("请先退出Play Mode。");
            for(int i=0;i<EditorSceneManager.sceneCount;i++)
                if(EditorSceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("有未保存Scene，保留现场："+EditorSceneManager.GetSceneAt(i).path);
        }
        private static void Inspect()
        {
            var scenes=new List<string>();foreach(var scene in EditorBuildSettings.scenes)if(scene.enabled)scenes.Add(scene.path);
            File.WriteAllText(Root+"editor-state.json",JsonUtility.ToJson(new State{Playing=EditorApplication.isPlayingOrWillChangePlaymode,
                Compiling=EditorApplication.isCompiling,Dirty=EditorSceneManager.GetActiveScene().isDirty,Scene=EditorSceneManager.GetActiveScene().path,
                Dependencies=AssetDatabase.GetDependencies(scenes.ToArray(),true)},true));
        }
        private static void EnsureFolder(string folder)
        {
            if(AssetDatabase.IsValidFolder(folder))return;
            string parent=Path.GetDirectoryName(folder).Replace('\\','/');EnsureFolder(parent);
            if(string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent,Path.GetFileName(folder))))throw new IOException("创建目录失败："+folder);
        }
        private static void ValidatePath(string path)
        {
            string workspace=Path.GetFullPath("Assets/_Project")+Path.DirectorySeparatorChar;
            if(!Path.GetFullPath(path).StartsWith(workspace,StringComparison.OrdinalIgnoreCase)||path.Contains(".."))
                throw new InvalidOperationException("迁移目标越出_Project："+path);
        }
        private static void ExecuteMoves()
        {
            EnsureEditable();var manifest=JsonUtility.FromJson<Manifest>(File.ReadAllText(Root+"moves.json"));
            if(manifest?.Moves==null)throw new InvalidOperationException("缺少迁移清单。");
            if(!File.Exists(manifest.Archive)||Hash(manifest.Archive)!=manifest.ArchiveSha256)
                throw new InvalidOperationException("缺少完整、可核验的回退归档。");
            // 全量预检通过再移动；重复执行已到位的条目只核对GUID，不重新生成资产。
            var targets=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var move in manifest.Moves)
            {
                ValidatePath(move.From);ValidatePath(move.To);
                if(!targets.Add(move.To))throw new InvalidOperationException("目标路径重复："+move.To);
                string oldGuid=AssetDatabase.AssetPathToGUID(move.From),newGuid=AssetDatabase.AssetPathToGUID(move.To);
                if(newGuid==move.Guid&&string.IsNullOrEmpty(oldGuid))continue;
                if(oldGuid!=move.Guid||!string.IsNullOrEmpty(newGuid))throw new InvalidOperationException("源身份或目标冲突："+move.From+" → "+move.To);
            }
            var roots=new List<string>();foreach(var scene in EditorBuildSettings.scenes)if(scene.enabled)roots.Add(scene.path);
            var production=new HashSet<string>(AssetDatabase.GetDependencies(roots.ToArray(),true),StringComparer.Ordinal);
            if(manifest.Retired!=null)foreach(var retired in manifest.Retired)
            {
                ValidatePath(retired.Path);
                if(!File.Exists(retired.Path))continue;
                if(production.Contains(retired.Path)||AssetDatabase.AssetPathToGUID(retired.Path)!=retired.Guid||Hash(retired.Path)!=retired.Sha256)
                    throw new InvalidOperationException("退役对象引用或快照已变化："+retired.Path);
            }
            string progress=Root+"completed-moves.txt";
            // StartAssetEditing会延迟导入：必须先让所有目标目录进入AssetDatabase，
            // 否则磁盘上已存在的目录仍可能被MoveAsset视为不存在。
            foreach(var move in manifest.Moves)EnsureFolder(Path.GetDirectoryName(move.To).Replace('\\','/'));
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach(var move in manifest.Moves)
                {
                    if(AssetDatabase.AssetPathToGUID(move.To)==move.Guid)continue;
                    string error=AssetDatabase.MoveAsset(move.From,move.To);
                    if(!string.IsNullOrEmpty(error))throw new IOException(error);
                    File.AppendAllText(progress,move.From+" → "+move.To+Environment.NewLine);
                }
                if(manifest.Retired!=null)foreach(var retired in manifest.Retired)
                    if(File.Exists(retired.Path)&&!AssetDatabase.DeleteAsset(retired.Path))throw new IOException("移除退役对象失败："+retired.Path);
                if(manifest.EmptyDirectories!=null)foreach(string folder in manifest.EmptyDirectories)
                {
                    ValidatePath(folder);
                    // 非空目录绝不递归删除；旧父目录仅在清单范围内且确实为空时退出。
                    if(AssetDatabase.IsValidFolder(folder)&&Directory.GetFileSystemEntries(folder).Length==0&&!AssetDatabase.DeleteAsset(folder))
                        throw new IOException("移除空目录失败："+folder);
                }
            }
            finally{AssetDatabase.StopAssetEditing();AssetDatabase.Refresh();}
        }
        private static string Hash(string path)
        {using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
    }
}
