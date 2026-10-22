using System;
using System.Collections.Generic;
using System.IO;
using Game.Core;
using Game.NetworkProtocol;
using Game.Run;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Game.Project.Editor
{
    /// <summary>从正式物品定义导出身份清单；不读取玩家 Profile，业务服也不需要加载 Unity SO。</summary>
    public sealed class ContentManifestExporter : IPreprocessBuildWithReport
    {
        public const string Output = "Assets/_Project/Resources/Online/ContentManifest.json";
        public int callbackOrder => 0;

        [MenuItem("Tools/Norn/Online/Export Content Manifest")]
        public static void Export()
        {
            OnlineSceneSetup.RequireCleanEditor();
            ContentManifest manifest = CreateManifest();
            Directory.CreateDirectory(Path.GetDirectoryName(Output));
            File.WriteAllText(Output, JsonUtility.ToJson(manifest, true));
            Directory.CreateDirectory("Tools/Networking/.artifacts");
            File.WriteAllText("Tools/Networking/.artifacts/content-manifest.json", JsonUtility.ToJson(manifest, true));
            AssetDatabase.ImportAsset(Output);
            GameLog.Info("物品身份清单已导出：" + manifest.Items.Length + " 项，Hash=" + manifest.Hash, "OnlineContent");
        }

        public static ContentManifest CreateManifest()
        {
            // Compatibility/Tests 不属于正式物品输入；不能用全工程 FindAssets 把旧 GUID 副本算成新配置。
            string[] roots = { "Assets/_Project/ScriptableObjects/Spell/Items", "Assets/_Project/ScriptableObjects/Spell/Wands/Items", "Assets/_Project/ScriptableObjects/Items" };
            string[] guids = AssetDatabase.FindAssets("t:ItemDefinition", roots);
            var entries = new List<ContentManifestEntry>(guids.Length);
            foreach (string guid in guids)
            {
                var definition = AssetDatabase.LoadAssetAtPath<ItemDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                entries.Add(new ContentManifestEntry { ContentId = definition.ContentId, Kind = (int)definition.Kind, DisplayName = definition.DisplayName });
            }
            entries.Sort((a, b) => string.CompareOrdinal(a.ContentId, b.ContentId));
            var manifest = new ContentManifest { BuildVersion = Application.version, ProtocolVersion = ProtocolCompatibility.CurrentVersion, Items = entries.ToArray() };
            manifest.Hash = ContentManifestRules.CalculateHash(manifest);
            return manifest;
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (!File.Exists(Output)) return; // N0 老实验没有业务内容依赖；导出后所有 Client/Server 构建都核对。
            var saved = JsonUtility.FromJson<ContentManifest>(File.ReadAllText(Output));
            if (saved == null || saved.Hash != ContentManifestRules.CalculateHash(saved) || saved.Hash != CreateManifest().Hash)
                throw new BuildFailedException("ContentManifest 已过期或损坏，请先 Export Content Manifest。");
        }
    }
}
