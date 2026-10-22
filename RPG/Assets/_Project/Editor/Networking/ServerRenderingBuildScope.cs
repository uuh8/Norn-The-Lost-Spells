using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Project.Editor
{
    /// <summary>所有 Lab Build 保护原设置；只有显式诊断才隔离 RP，按 GUID 重新加载后恢复，避免失效引用。</summary>
    internal sealed class ServerRenderingBuildScope : IDisposable
    {
        private readonly byte[] _graphicsBytes, _qualityBytes;
        private readonly byte[] _playerBytes;
        private readonly StackTraceLogType _errorStack;
        private readonly string _defaultPath;
        private readonly string[] _qualityPaths;
        private readonly int _qualityLevel;
        private bool _disposed;
        public ServerRenderingBuildScope(bool isolateRendering)
        {
            _graphicsBytes = File.ReadAllBytes("ProjectSettings/GraphicsSettings.asset");
            _qualityBytes = File.ReadAllBytes("ProjectSettings/QualitySettings.asset");
            _playerBytes = File.ReadAllBytes("ProjectSettings/ProjectSettings.asset");
            _errorStack = PlayerSettings.GetStackTraceLogType(LogType.Error);
            _qualityLevel = QualitySettings.GetQualityLevel();
            _defaultPath = AssetDatabase.GetAssetPath(GraphicsSettings.defaultRenderPipeline);
            MatchCollection pipelines = Regex.Matches(Encoding.UTF8.GetString(_qualityBytes), @"customRenderPipeline: \{([^}]+)\}");
            if (pipelines.Count != QualitySettings.names.Length) throw new InvalidOperationException("Quality YAML 配置数量不匹配，拒绝修改设置。");
            _qualityPaths = new string[pipelines.Count];
            // 普通托管引用不能阻止 Unity 卸载 native Asset；保存磁盘 GUID，恢复时重新 LoadAssetAtPath。
            for (int i = 0; i < pipelines.Count; i++)
            {
                Match guid = Regex.Match(pipelines[i].Value, @"guid: ([0-9a-f]{32})");
                _qualityPaths[i] = guid.Success ? AssetDatabase.GUIDToAssetPath(guid.Groups[1].Value) : string.Empty;
                if (guid.Success && string.IsNullOrEmpty(_qualityPaths[i])) throw new InvalidOperationException("Quality RP GUID 找不到资产。");
            }
            if (!isolateRendering) return;
            try
            {
                GraphicsSettings.defaultRenderPipeline = null;
                // native 启动错误发生在实验脚本之前：构建时开启完整 Error stack，不能靠脚本稍后过滤。
                PlayerSettings.SetStackTraceLogType(LogType.Error, StackTraceLogType.Full);
                // 保留 URP Global Settings：移除它既未解决 native 报错，还会使 RenderGraph 构建检查失败。
                for (int i = 0; i < _qualityPaths.Length; i++)
                { QualitySettings.SetQualityLevel(i, false); QualitySettings.renderPipeline = null; }
                QualitySettings.SetQualityLevel(_qualityLevel, false);
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                for (int i = 0; i < _qualityPaths.Length; i++)
                { QualitySettings.SetQualityLevel(i, false); QualitySettings.renderPipeline = LoadPipeline(_qualityPaths[i]); }
                GraphicsSettings.defaultRenderPipeline = LoadPipeline(_defaultPath);
                QualitySettings.SetQualityLevel(_qualityLevel, false);
                PlayerSettings.SetStackTraceLogType(LogType.Error, _errorStack);
            }
            finally
            {
                // Project Settings 可能在构建期间被 Unity 重写；恢复精确字节，不批量 Save 用户资产/Scene。
                File.WriteAllBytes("ProjectSettings/GraphicsSettings.asset", _graphicsBytes);
                File.WriteAllBytes("ProjectSettings/QualitySettings.asset", _qualityBytes);
                File.WriteAllBytes("ProjectSettings/ProjectSettings.asset", _playerBytes);
            }
        }
        private static RenderPipelineAsset LoadPipeline(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            RenderPipelineAsset asset = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(path);
            if (asset == null) throw new InvalidOperationException("无法重新加载原 RP：" + path);
            return asset;
        }
    }
}
