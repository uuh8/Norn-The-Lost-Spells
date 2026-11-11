using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Run
{
    /// <summary>
    /// 将账号服务器确认的供货投影到原商店IShopReadOnly。这里只解释值和美术，不创建Profile/ShopState，
    /// 也不持有Repository或交易服务。报价必须与本次冻结的原ShopDefinition完整相符后才整批发布。
    /// </summary>
    public sealed class AccountShopReadModel : IShopReadOnly
    {
        private readonly ShopOfferSnapshot[] _offers;
        public string PlayerId { get; }
        public string InventoryId { get; }
        public string ShopId { get; }
        public string ConfigHash { get; }
        public int SceneHandle { get; }
        public uint InventoryVersion { get; }
        public long Version { get; }
        public long Currency { get; }
        public long RemainingBudget { get; }
        public long ProgressUntilRefresh { get; }
        public int Count => _offers.Length;
        public ShopOfferSnapshot GetOffer(int index) => _offers[index];

        private AccountShopReadModel(OnlineAccountShopPresentationSnapshot snapshot, ShopOfferSnapshot[] offers)
        {
            PlayerId = snapshot.PlayerId; InventoryId = snapshot.InventoryId; ShopId = snapshot.ShopId;
            ConfigHash = snapshot.ConfigHash; SceneHandle = snapshot.SceneHandle; InventoryVersion = snapshot.InventoryVersion;
            Version = snapshot.ShopVersion; Currency = snapshot.Gold; RemainingBudget = snapshot.RemainingBudget;
            ProgressUntilRefresh = snapshot.ProgressUntilRefresh; _offers = offers;
        }

        public static bool TryCreate(ItemCatalogIndex catalog, ShopConfiguration configuration,
            OnlineAccountShopPresentationSnapshot snapshot, out AccountShopReadModel model, out string error)
        {
            model = null; error = null;
            if (catalog == null || configuration == null || snapshot == null
                || string.IsNullOrWhiteSpace(snapshot.PlayerId) || string.IsNullOrWhiteSpace(snapshot.InventoryId)
                || snapshot.ShopId != configuration.ShopId || !ValidHash(snapshot.ConfigHash) || snapshot.SceneHandle == 0
                || snapshot.InventoryVersion == 0 || snapshot.ShopVersion <= 0 || snapshot.Gold < 0
                || snapshot.RemainingBudget < 0 || snapshot.RemainingBudget > configuration.Budget
                || snapshot.ProgressUntilRefresh < 0 || snapshot.ProgressUntilRefresh > configuration.RefreshEveryProgress
                || snapshot.Stocks == null || snapshot.Stocks.Length != configuration.Count)
                return Fail("账号商店身份、配置来源、版本或完整供货非法。", out error);

            var stocks = new Dictionary<string, OnlineAccountShopStock>(snapshot.Stocks.Length, StringComparer.Ordinal);
            for (int i = 0; i < snapshot.Stocks.Length; i++)
            {
                var stock = snapshot.Stocks[i];
                if (stock == null || string.IsNullOrWhiteSpace(stock.OfferId) || !stocks.TryAdd(stock.OfferId, stock))
                    return Fail("账号商店供货包含空项或重复报价。", out error);
            }
            var offers = new ShopOfferSnapshot[configuration.Count];
            for (int i = 0; i < offers.Length; i++)
            {
                var offer = configuration.At(i);
                if (!stocks.TryGetValue(offer.OfferId, out var stock)
                    || (stock.ContentId ?? string.Empty) != (offer.ContentId ?? string.Empty) || stock.MaterialKey != offer.MaterialKey
                    || stock.BuyPrice != offer.BuyPrice || stock.SellPrice != offer.SellPrice
                    || stock.InitialCharges != offer.InitialCharges || stock.Unlimited != offer.Unlimited
                    || stock.InitialMaterialQuantity != offer.InitialMaterialQuantity || stock.InitialStock != offer.Stock
                    || stock.Remaining > stock.InitialStock)
                    return Fail("服务器报价与当前冻结商店配置不一致，未开放交易。", out error);
                // ShopOfferSnapshot自身是值快照：复制数字/显示值和原资源引用，不保留可变Stock/Offer数组。
                // ConfigHash的算法属于Shared/账户适配；这里逐字段核对真正影响玩家报价的规则，不复制第二套Hash算法。
                offers[i] = new ShopOfferSnapshot(offer, stock.Remaining, catalog);
            }
            model = new AccountShopReadModel(snapshot, offers); return true;
        }
        private static bool ValidHash(string hash)
        {
            if (hash == null || hash.Length != 64) return false;
            for (int i = 0; i < hash.Length; i++) if (!(hash[i] >= '0' && hash[i] <= '9' || hash[i] >= 'a' && hash[i] <= 'f')) return false;
            return true;
        }
        private static bool Fail(string message, out string error) { error = message; return false; }
    }
}
