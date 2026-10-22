using Game.Core;

namespace Game.Run
{
    /// <summary>只消费已经可靠结算的经济进展；开店/进入场景本身不增加任何进展。</summary>
    public static class ShopProgressTracker
    {
        public static bool TryPrepare(SinglePlayerProfile profile, ShopConfiguration config, out SinglePlayerProfile next, out bool changed)
        {
            next = null; changed = false;
            if (profile == null || config == null || profile.PendingRaid != null || profile.RaidActionsFrozen || profile.Items.IsResourceBusy
                || profile.PendingMaterialOutputs != 0 || profile.Items.HasWorldTransit) return false;
            var state = profile.Shop;
            if (state != null)
            {
                if (state.ShopId != config.ShopId || state.Stocks==null || state.Stocks.Length > config.Count || state.RemainingBudget > config.Budget) return false;
                for (int i = 0; i < state.Stocks.Length; i++)
                {var stock=state.Stocks[i];var offer=stock==null?null:config.Find(stock.OfferId);if(offer==null||stock.Remaining>offer.Stock)return false;}
                if (profile.AdventureProgress - state.AppliedProgress < config.RefreshEveryProgress)
                {
                    if(state.Stocks.Length==config.Count){next=profile;return true;}
                    if(state.Version==long.MaxValue)return false;
                    // 内容升级只补新增报价；旧库存/预算/进度/收据保留，开店不能顺便刷新已买空的商品。
                    next=RaidProfileOps.Clone(profile);next.Shop.Version++;next.Shop.Stocks=new ShopStock[config.Count];
                    for(int i=0;i<config.Count;i++)
                    {var offer=config.At(i);var old=state.Find(offer.OfferId);next.Shop.Stocks[i]=new ShopStock{OfferId=offer.OfferId,Remaining=old?.Remaining??offer.Stock};}
                    changed=true;return true;
                }
            }
            if (state != null && state.Version == long.MaxValue) return false;
            next = RaidProfileOps.Clone(profile);
            if (state == null) next.Shop = new ShopRuntimeState { ShopId = config.ShopId, Version = 1, Stocks = new ShopStock[config.Count], AppliedProgress = profile.AdventureProgress };
            else { next.Shop.Version = checked(next.Shop.Version + 1); next.Shop.Stocks=new ShopStock[config.Count];next.Shop.AppliedProgress += (profile.AdventureProgress - state.AppliedProgress) / config.RefreshEveryProgress * config.RefreshEveryProgress; }
            next.Shop.RemainingBudget = config.Budget;
            for (int i = 0; i < config.Count; i++) next.Shop.Stocks[i] = new ShopStock { OfferId = config.At(i).OfferId, Remaining = config.At(i).Stock };
            changed = true; return true;
        }
    }
}
