using System;

namespace Game.Run
{
    [Serializable] public sealed class ShopStock { public string OfferId; public ulong Remaining; }
    /// <summary>商店资产写入Profile；重开窗口/重启只读这一份，不重新发放供货与预算。</summary>
    [Serializable]
    public sealed class ShopRuntimeState
    {
        public string ShopId;
        public long Version, RemainingBudget, AppliedProgress;
        public ShopStock[] Stocks;
        public bool HasLastTrade;
        public ShopTradeReceipt LastTrade;
        internal ShopRuntimeState Copy()
        {
            var copy = new ShopRuntimeState { ShopId = ShopId, Version = Version, RemainingBudget = RemainingBudget, AppliedProgress = AppliedProgress,
                Stocks = new ShopStock[Stocks.Length], HasLastTrade = HasLastTrade, LastTrade = HasLastTrade ? LastTrade?.Copy() : null };
            for (int i = 0; i < Stocks.Length; i++) copy.Stocks[i] = new ShopStock { OfferId = Stocks[i].OfferId, Remaining = Stocks[i].Remaining };
            return copy;
        }
        internal ShopStock Find(string id) { for (int i = 0; i < Stocks.Length; i++) if (Stocks[i].OfferId == id) return Stocks[i]; return null; }
        internal bool IsValid(long progress, long generation)
        {
            if (string.IsNullOrWhiteSpace(ShopId) || Version <= 0 || RemainingBudget < 0 || AppliedProgress < 0 || AppliedProgress > progress || Stocks == null || Stocks.Length == 0) return false;
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < Stocks.Length; i++) if (Stocks[i] == null || string.IsNullOrWhiteSpace(Stocks[i].OfferId) || !seen.Add(Stocks[i].OfferId)) return false;
            // JsonUtility会把inline null恢复成默认对象；显式Presence区分“尚无收据”和损坏收据。
            return HasLastTrade ? LastTrade != null && LastTrade.IsValid(Version, generation)
                : LastTrade == null || string.IsNullOrEmpty(LastTrade.RequestId) && string.IsNullOrEmpty(LastTrade.Fingerprint)
                    && LastTrade.Quantity == 0 && LastTrade.Generation == 0 && LastTrade.Currency == 0 && LastTrade.CurrencyDelta == 0;
        }
    }
}
