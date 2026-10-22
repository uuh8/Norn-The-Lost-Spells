// Release 构建中所有 GameLog 调用由编译器在调用方 IL 层面整体移除，
// 参数表达式（字符串插值、装箱等）不会被求值，GC Alloc 为零。

using UnityEngine;

namespace Game.Core
{
    public static class GameLog
    {
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        public static void Info(string message, string category = "")
        {
            Debug.Log(string.IsNullOrEmpty(category) ? message : $"[{category}] {message}");
        }

        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        public static void Warn(string message, string category = "")
        {
            Debug.LogWarning(string.IsNullOrEmpty(category) ? message : $"[{category}] {message}");
        }

        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        public static void Error(string message, string category = "")
        {
            Debug.LogError(string.IsNullOrEmpty(category) ? message : $"[{category}] {message}");
        }
    }
}
