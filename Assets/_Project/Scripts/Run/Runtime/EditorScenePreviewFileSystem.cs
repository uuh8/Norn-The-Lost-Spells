#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Game.Run
{
    public enum EditorInventoryProfileMode : byte { ScenePreview=0, SavedProfile=1 }
    /// <summary>Editor专用临时档：复用真实Repository原子提交，所有“文件”只存在当前Play的内存中。</summary>
    public sealed class EditorScenePreviewFileSystem:IProfileFileSystem
    {
        private static readonly Dictionary<string,string> Files=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        // SubsystemRegistration每次Play都执行，即使关闭Domain Reload；Scene往返不调用它。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void ResetForPlay()=>Files.Clear();
        private static string Key(string path)=>Path.GetFullPath(path);
        public bool Exists(string path)=>Files.ContainsKey(Key(path));
        public string Read(string path)=>Files[Key(path)];
        public void WriteFlushed(string path,string text)=>Files[Key(path)]=text;
        public void Move(string source,string destination)
        {
            string from=Key(source),to=Key(destination);
            if(Files.ContainsKey(to))throw new IOException("临时档目标已存在。");
            string value=Files[from];Files.Add(to,value);Files.Remove(from);
        }
        public void Replace(string source,string destination,string backup)
        {
            string from=Key(source),to=Key(destination),old=Key(backup);
            string replacement=Files[from],original=Files[to];
            Files[old]=original;Files[to]=replacement;Files.Remove(from);
        }
    }
}
#endif
