using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Game.NetworkProtocol
{
    /// <summary>
    /// 可信DS和平连续性的压缩壳。JSON序列化由各自适配器完成，Shared不依赖Unity或ASP.NET。
    /// 此壳不接受物品增减，也不是认证凭据；业务端还须核同Session Scope、原Run和最终收据。
    /// </summary>
    [Serializable] public sealed class OnlinePracticeContinuityEnvelope
    {
        public uint SchemaVersion = 1;
        public string PlayerId = "", GameplayConfigHash = "", Encoding = "gzip-base64", Data = "";
        public int UncompressedBytes;
    }
    /// <summary>
    /// 压缩只发生于研究/转场的离散边界。声明长度、压缩正文、解压实际写入分别受限，
    /// 不能先信任gzip长度分配大数组，也不能把解码失败静默替换成满Mana的新工作区。
    /// </summary>
    public static class PracticeContinuityEnvelopeRules
    {
        public const int MaximumRawBytes = 24 * 1024 * 1024;
        public const int MaximumEnvelopeBytes = 32 * 1024 * 1024;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        public static bool TryPack(string playerId, string gameplayHash, string canonicalJson,
            out OnlinePracticeContinuityEnvelope envelope, out string error)
        {
            envelope = null; error = null;
            if (!Hex(playerId, 32) || !Hex(gameplayHash, 64) || string.IsNullOrEmpty(canonicalJson))
                return Fail("和平连续性身份或正文缺失。", out error);
            try
            {
                int count = StrictUtf8.GetByteCount(canonicalJson);
                if (count < 2 || count > MaximumRawBytes) return Fail("和平连续性正文超过原始预算。", out error);
                byte[] raw = StrictUtf8.GetBytes(canonicalJson);
                using var compressed = new MemoryStream();
                using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, true)) gzip.Write(raw, 0, raw.Length);
                string data = Convert.ToBase64String(compressed.ToArray());
                // 给固定元数据留下明确空间；外层完整退出正文仍受原64MiB总门检查，不扩大旧预算。
                if (data.Length > MaximumEnvelopeBytes - 1024) return Fail("和平连续性压缩壳超过传输预算。", out error);
                envelope = new OnlinePracticeContinuityEnvelope { PlayerId = playerId, GameplayConfigHash = gameplayHash,
                    UncompressedBytes = count, Data = data };
                return true;
            }
            catch (ArgumentException) { return Fail("和平连续性正文编码非法。", out error); }
            catch (IOException) { return Fail("和平连续性压缩失败。", out error); }
        }
        public static bool TryExpand(OnlinePracticeContinuityEnvelope envelope, string expectedPlayer,
            string expectedGameplayHash, out string canonicalJson, out string error)
        {
            canonicalJson = null; error = null;
            if (envelope == null || envelope.SchemaVersion != 1 || !Hex(expectedPlayer, 32) || !Hex(expectedGameplayHash, 64)
                || envelope.PlayerId != expectedPlayer || envelope.GameplayConfigHash != expectedGameplayHash
                || envelope.Encoding != "gzip-base64" || envelope.UncompressedBytes < 2
                || envelope.UncompressedBytes > MaximumRawBytes || string.IsNullOrEmpty(envelope.Data)
                || envelope.Data.Length > MaximumEnvelopeBytes - 1024)
                return Fail("和平连续性压缩壳身份、版本或长度非法。", out error);
            try
            {
                byte[] compressed = Convert.FromBase64String(envelope.Data);
                if (Convert.ToBase64String(compressed) != envelope.Data) return Fail("和平连续性Base64表示不规范。", out error);
                using var input = new MemoryStream(compressed, false);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var raw = new MemoryStream(Math.Min(envelope.UncompressedBytes, 64 * 1024));
                byte[] buffer = new byte[32 * 1024]; int total = 0, read;
                while ((read = gzip.Read(buffer, 0, buffer.Length)) != 0)
                {
                    // 先核对再写，谎报长度的流不能先膨胀到无界MemoryStream后才被拒绝。
                    if (read > envelope.UncompressedBytes - total || read > MaximumRawBytes - total)
                        return Fail("和平连续性实际解压量超过声明。", out error);
                    raw.Write(buffer, 0, read); total += read;
                }
                if (total != envelope.UncompressedBytes) return Fail("和平连续性实际长度与声明不一致。", out error);
                canonicalJson = StrictUtf8.GetString(raw.GetBuffer(), 0, total);
                return true;
            }
            catch (ArgumentException) { return Fail("和平连续性编码非法。", out error); }
            // InvalidDataException继承SystemException而非IOException；损坏压缩壳也必须返回失败并保留现场。
            catch (InvalidDataException) { return Fail("和平连续性压缩正文损坏。", out error); }
            catch (IOException) { return Fail("和平连续性压缩正文损坏。", out error); }
        }
        private static bool Hex(string value, int count)
        {
            if (value == null || value.Length != count) return false;
            for (int i = 0; i < value.Length; i++)
                if (!(value[i] >= '0' && value[i] <= '9' || value[i] >= 'a' && value[i] <= 'f')) return false;
            return true;
        }
        private static bool Fail(string message, out string error) { error = message; return false; }
    }
}
