using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Game.NetworkProtocol
{
    [Serializable] public sealed class ContentManifest
    {
        public uint SchemaVersion = 1;
        public uint ProtocolVersion = 1;
        public string BuildVersion = "";
        public string Hash = "";
        public ContentManifestEntry[] Items = Array.Empty<ContentManifestEntry>();
    }
    [Serializable] public sealed class ContentManifestEntry
    {
        public string ContentId = "";
        public int Kind; // 与 ItemKind 的稳定值一致；Shared 不能反向引用 Game.Run。
        public string DisplayName = "";
    }

    /// <summary>三端共用的物品身份清单校验；SHA256 验证版本一致，不是密码认证或完整 Gameplay 内容签名。</summary>
    public static class ContentManifestRules
    {
        public static string CalculateHash(ContentManifest manifest)
        {
            if (manifest == null || manifest.SchemaVersion != 1 || manifest.ProtocolVersion != ProtocolCompatibility.CurrentVersion ||
                string.IsNullOrEmpty(manifest.BuildVersion) || manifest.BuildVersion.Length > 64 ||
                manifest.Items == null || manifest.Items.Length == 0 || manifest.Items.Length > 4096)
                throw new ArgumentException("Manifest 版本或容量非法。");
            var sorted = new List<ContentManifestEntry>(manifest.Items);
            sorted.Sort((a, b) => string.CompareOrdinal(a == null ? null : a.ContentId, b == null ? null : b.ContentId));
            // 长度前缀避免显示名中的分隔符制造歧义；Ordinal 排序与 UTF-8 让不同机器得到相同摘要。
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(manifest.SchemaVersion); writer.Write(manifest.ProtocolVersion); writer.Write(manifest.BuildVersion);
            writer.Write(sorted.Count);
            string previous = null;
            foreach (ContentManifestEntry item in sorted)
            {
                if (item == null || !IsContentId(item.ContentId) || item.ContentId == previous || item.Kind < 0 || item.Kind > 6 ||
                    string.IsNullOrEmpty(item.DisplayName) || item.DisplayName.Length > 128)
                    throw new ArgumentException("Manifest 包含空值、重复 ContentId 或非法物品定义。");
                previous = item.ContentId;
                writer.Write(item.ContentId); writer.Write(item.Kind); writer.Write(item.DisplayName);
            }
            writer.Flush();
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(stream.ToArray());
            var text = new StringBuilder(64);
            foreach (byte value in hash) text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        public static bool IsContentId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
            foreach (char c in value)
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '.' || c == '_' || c == '-')) return false;
            return true;
        }
    }
}
