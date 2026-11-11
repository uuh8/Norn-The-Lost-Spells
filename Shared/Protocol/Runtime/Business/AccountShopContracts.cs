using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Game.NetworkProtocol
{
    /// <summary>
    /// 原正式商店的受控配置值。价格、初始次数和 GMU 来自离线导出的 SO，
    /// 请求只携带 OfferId 与数量；业务服从这份配置重算，绝不接受客户端报价。
    /// </summary>
    [Serializable] public sealed class ContentShopDefinition
    {
        public uint SchemaVersion = 1;
        public string ShopId = "", Hash = "";
        public long PurchaseBudget;
        public int RefreshEveryProgress;
        public ContentShopOffer[] Offers = Array.Empty<ContentShopOffer>();
    }
    [Serializable] public sealed class ContentShopOffer
    {
        public string OfferId = "", ContentId = "";
        public byte MaterialKey;
        public long BuyPrice, SellPrice;
        public ulong InitialStock, InitialMaterialQuantity;
        public int InitialCharges;
        public bool Unlimited;
    }
    /// <summary>只在导出/加载边界计算，不能用于每帧热路径。长度前缀和 Ordinal 排序保持三端一致。</summary>
    public static class ContentShopRules
    {
        public static string CalculateHash(ContentShopDefinition value)
        {
            if (value == null || value.SchemaVersion != 1 || !ContentManifestRules.IsContentId(value.ShopId)
                || value.PurchaseBudget < 0 || value.RefreshEveryProgress < 1 || value.Offers == null
                || value.Offers.Length < 1 || value.Offers.Length > 128)
                throw new ArgumentException("商店配置版本、身份或容量非法。");
            var offers = new List<ContentShopOffer>(value.Offers);
            offers.Sort((a, b) => string.CompareOrdinal(a?.OfferId, b?.OfferId));
            var contents = new HashSet<string>(StringComparer.Ordinal);
            ushort materials = 0;
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(value.SchemaVersion); writer.Write(value.ShopId); writer.Write(value.PurchaseBudget);
            writer.Write(value.RefreshEveryProgress); writer.Write(offers.Count);
            string previous = null;
            foreach (var offer in offers)
            {
                if (offer == null || !ContentManifestRules.IsContentId(offer.OfferId) || offer.OfferId == previous
                    || offer.BuyPrice <= 0 || offer.SellPrice < 0 || offer.SellPrice > offer.BuyPrice
                    || offer.InitialStock == 0 || offer.InitialCharges < 0)
                    throw new ArgumentException("商店报价重复、缺失或数值非法。");
                if (offer.MaterialKey != 0)
                {
                    if (offer.MaterialKey > 8 || (materials & (1 << offer.MaterialKey)) != 0 || !string.IsNullOrEmpty(offer.ContentId))
                        throw new ArgumentException("材料报价必须具有唯一材料身份，不能夹带物品 ContentId。");
                    materials |= (ushort)(1 << offer.MaterialKey);
                }
                else if (!ContentManifestRules.IsContentId(offer.ContentId) || !contents.Add(offer.ContentId))
                    throw new ArgumentException("物品报价缺少唯一 ContentId。");
                previous = offer.OfferId;
                writer.Write(offer.OfferId); writer.Write(offer.ContentId ?? ""); writer.Write(offer.MaterialKey);
                writer.Write(offer.BuyPrice); writer.Write(offer.SellPrice); writer.Write(offer.InitialStock);
                writer.Write(offer.InitialCharges); writer.Write(offer.Unlimited); writer.Write(offer.InitialMaterialQuantity);
            }
            writer.Flush();
            using var sha = SHA256.Create();
            var text = new StringBuilder(64);
            foreach (byte part in sha.ComputeHash(stream.ToArray())) text.Append(part.ToString("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }
    }
    /// <summary>店铺状态与本次真实 Profile 同快照返回，避免报价 UI 与钱包显示属于不同版本。</summary>
    [Serializable] public sealed class AccountShopReply
    {
        public OnlineProfileReply Profile;
        public AccountShopSnapshot Shop;
    }
    [Serializable] public sealed class AccountShopSnapshot
    {
        public uint SchemaVersion = 1;
        public string PlayerId = "", ShopId = "", ConfigHash = "";
        public uint InventoryVersion;
        public long Version, RemainingBudget, ProgressUntilRefresh;
        public AccountShopStock[] Stocks = Array.Empty<AccountShopStock>();
    }
    [Serializable] public sealed class AccountShopStock { public string OfferId = ""; public ulong Remaining; }
    /// <summary>
    /// 一笔交易的不可变意图。Kind 与既有 BuyItem/SellItem/BuyMaterial/SellMaterial 的 0..3 一致。
    /// CommandId 仍进入账号现有幂等账本，ExpectedVersion 只认真实账号版本，不使用 UI 的局部修订。
    /// </summary>
    [Serializable] public sealed class AccountShopTradeRequest
    {
        public string CommandId = "", ShopId = "", ConfigHash = "";
        public string OfferId = "", ItemInstanceId = "", ContainerId = "", BackpackId = "";
        public uint ExpectedVersion;
        public long ExpectedShopVersion;
        public int Kind;
        public ulong Quantity;
        public bool ToBackpack, AllowWarehouseFallback;
    }
    /// <summary>
    /// 固定收据仅描述原交易。重放后必须重新读取完整 Shop/Profile 并确认水位；
    /// 不能把历史收据的钱包或快照安装回后来已经变更的账号。
    /// </summary>
    [Serializable] public sealed class AccountShopTradeReply
    {
        public string CommandId = "", PlayerId = "";
        public bool Success, Replayed;
        public uint InventoryVersion;
        public long ShopVersion, Currency, CurrencyDelta;
        public ulong Quantity, WarehouseQuantity;
        public OnlineOwnedMaterialAmountSnapshot[] Materials = Array.Empty<OnlineOwnedMaterialAmountSnapshot>();
    }
}
