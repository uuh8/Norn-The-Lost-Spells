#if UNITY_EDITOR
using System;
using System.Reflection;
using NUnit.Framework;

namespace Game.Run.Tests
{
    public sealed class EditorScenePreviewTests
    {
        [Test] public void PreviewFilesShareWithinPlayAndResetAtNextPlayBoundary()
        {
            var type=typeof(SinglePlayerInventorySession).Assembly.GetType("Game.Run.EditorScenePreviewFileSystem");
            Assert.That(type,Is.Not.Null,"Editor场景试玩必须经过独立文件边界，不能读写正式档。");
            var reset=type.GetMethod("ResetForPlay",BindingFlags.Public|BindingFlags.Static);reset.Invoke(null,null);
            var first=(IProfileFileSystem)Activator.CreateInstance(type);var second=(IProfileFileSystem)Activator.CreateInstance(type);
            const string path="Temp/SinglePlayer/PreviewContract/profile.json";
            first.WriteFlushed(path,"one play");Assert.That(second.Read(path),Is.EqualTo("one play"));
            reset.Invoke(null,null);Assert.That(second.Exists(path),Is.False,"Domain Reload关闭时也必须清除上一轮的临时资产。");
            Assert.That(System.IO.File.Exists(path),Is.False,"临时文件接口不能落盘。");
        }
    }
}
#endif
