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
        public uint SchemaVersion = 2;
        public uint ProtocolVersion = ProtocolCompatibility.CurrentVersion;
        public string BuildVersion = "";
        public string Hash = "";
        public ContentManifestEntry[] Items = Array.Empty<ContentManifestEntry>();
        // Schema3把原正式商店报价纳入同一内容身份。Schema2历史正文不包含此段，不能夹带未Hash的报价。
        public ContentShopDefinition Shop;
    }
    [Serializable] public sealed class ContentManifestEntry
    {
        public string ContentId = "";
        public int Kind; // 与 ItemKind 的稳定值一致；Shared 不能反向引用 Game.Run。
        public string DisplayName = "";
        // 由 Unity 真实 InventoryItemRules 导出。缺失版本不能靠零值猜出合法空间/容器规则。
        public int RulesSchemaVersion;
        public int UseKind, ShapeWidth, ShapeHeight, MaxStack;
        public ContentShapeCell[] OccupiedCells = Array.Empty<ContentShapeCell>();
        public int BackpackWidth, BackpackHeight, WandProgramCapacity;
        public ulong ContainerCapacityGmu;
        public ushort ContainerMaterialsMask;
        public byte MaterialKey;
        public ulong MaterialItemCapacityGmu;
    }
    [Serializable] public sealed class ContentShapeCell { public int X, Y; }

    /// <summary>三端共用的物品身份清单校验；SHA256 验证版本一致，不是密码认证或完整 Gameplay 内容签名。</summary>
    public static class ContentManifestRules
    {
        public static string CalculateHash(ContentManifest manifest)
        {
            if (manifest == null || (manifest.SchemaVersion != 2 && manifest.SchemaVersion != 3) || manifest.ProtocolVersion != ProtocolCompatibility.CurrentVersion ||
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
                WriteRules(writer, item);
            }
            if (manifest.SchemaVersion == 2)
            {
                // 历史物品清单仍按原字节计算；不能悄悄接收未进入摘要的商店段。
                if (manifest.Shop != null) throw new ArgumentException("Schema2不能携带未纳入摘要的商店配置。");
            }
            else
            {
                string shopHash = ContentShopRules.CalculateHash(manifest.Shop);
                if (manifest.Shop.Hash != shopHash) throw new ArgumentException("商店报价正文与摘要不一致。");
                foreach (var offer in manifest.Shop.Offers)
                {
                    if (offer.MaterialKey != 0) continue;
                    ContentManifestEntry item = null;
                    foreach (var candidate in sorted) if (candidate.ContentId == offer.ContentId) { item = candidate; break; }
                    if (item == null || item.Kind == 0 && !offer.Unlimited && offer.InitialCharges == 0
                        || item.Kind == 4 && (offer.InitialMaterialQuantity == 0 || offer.InitialMaterialQuantity > item.MaterialItemCapacityGmu))
                        throw new ArgumentException("商店引用未知物品、零次有限卡或非法材料数量。");
                }
                writer.Write("AccountShop1"); writer.Write(shopHash);
            }
            writer.Flush();
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(stream.ToArray());
            var text = new StringBuilder(64);
            foreach (byte value in hash) text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        private static void WriteRules(BinaryWriter writer, ContentManifestEntry item)
        {
            // 有界的导入配置防止坏目录制造巨大占格数组；不是扩大现有背包或玩法容量。
            if (item.RulesSchemaVersion != 1 || item.UseKind < 0 || item.UseKind > 2 ||
                item.UseKind == 1 && item.Kind != 3 || item.UseKind == 2 && item.Kind != 4 && item.Kind != 5 && item.Kind != 6 ||
                item.ShapeWidth < 1 || item.ShapeWidth > 64 || item.ShapeHeight < 1 || item.ShapeHeight > 64 ||
                item.MaxStack < 1 || (item.Kind == 1 || item.Kind == 2 || item.Kind == 3) && item.MaxStack != 1 ||
                item.BackpackWidth < 1 || item.BackpackWidth > 64 || item.BackpackHeight < 1 || item.BackpackHeight > 64 ||
                item.WandProgramCapacity < 1 || item.WandProgramCapacity > 32 ||
                item.ContainerCapacityGmu == 0 || item.MaterialItemCapacityGmu == 0 ||
                item.ContainerMaterialsMask == 0 || (item.ContainerMaterialsMask & ~0x1fe) != 0 ||
                item.MaterialKey < 1 || item.MaterialKey > 8 || item.OccupiedCells == null ||
                item.OccupiedCells.Length < 1 || item.OccupiedCells.Length > item.ShapeWidth * item.ShapeHeight)
                throw new ArgumentException("Manifest 空间或容器规则非法。 ");
            var cells = new List<ContentShapeCell>(item.OccupiedCells);
            // 形状是格集合，不依赖作者数组顺序；排序仅在导出/加载离散路径发生。
            cells.Sort((a, b) => a == null ? -1 : b == null ? 1 : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
            writer.Write(item.RulesSchemaVersion); writer.Write(item.UseKind);
            writer.Write(item.ShapeWidth); writer.Write(item.ShapeHeight); writer.Write(item.MaxStack);
            writer.Write(cells.Count);
            ContentShapeCell previous = null;
            foreach (var cell in cells)
            {
                if (cell == null || cell.X < 0 || cell.X >= item.ShapeWidth || cell.Y < 0 || cell.Y >= item.ShapeHeight ||
                    previous != null && previous.X == cell.X && previous.Y == cell.Y)
                    throw new ArgumentException("Manifest 占格为空、重复或越界。");
                writer.Write(cell.X); writer.Write(cell.Y); previous = cell;
            }
            writer.Write(item.BackpackWidth); writer.Write(item.BackpackHeight); writer.Write(item.WandProgramCapacity);
            writer.Write(item.ContainerCapacityGmu); writer.Write(item.ContainerMaterialsMask);
            writer.Write(item.MaterialKey); writer.Write(item.MaterialItemCapacityGmu);
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
