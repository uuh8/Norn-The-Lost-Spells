using System;
using System.IO;
using UnityEditor;

namespace Game.Project.Editor
{
    /// <summary>单变量构建对照：暂放 Editor 文件夹排除纯 Client Resources，保留 GUID 并在 finally 恢复。</summary>
    internal sealed class ServerClientResourcesBuildScope : IDisposable
    {
        private const string Source = "Assets/_Project/Resources";
        private const string Temporary = "Assets/_Project/Editor/ServerExcludedResources";
        private const string Recovery = "Temp/OnlineLab/resources-build-recovery.txt";
        private bool _moved;
        public ServerClientResourcesBuildScope()
        {
            if (!AssetDatabase.IsValidFolder(Source) || AssetDatabase.IsValidFolder(Temporary))
                throw new InvalidOperationException("Client Resources 原路径或暂存路径异常；拒绝覆盖。");
            Directory.CreateDirectory("Temp/OnlineLab");
            File.WriteAllText(Recovery, Temporary + " -> " + Source);
            string error = AssetDatabase.MoveAsset(Source, Temporary);
            if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
            _moved = true;
        }
        public void Dispose()
        {
            if (!_moved) return;
            string error = AssetDatabase.MoveAsset(Temporary, Source);
            if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException("恢复 Client Resources 失败：" + error);
            _moved = false;
            File.Delete(Recovery);
        }
    }
}
