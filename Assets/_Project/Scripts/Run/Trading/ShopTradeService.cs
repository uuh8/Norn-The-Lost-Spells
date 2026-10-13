using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>Run权威整笔交易入口；先保存候选，调用者随后才采用，绝不让UI直接改资产。</summary>
    public sealed class ShopTradeService
    {
        public ShopTradeResult TryTrade(SinglePlayerProfile profile, ProfileSaveRepository repository, ShopConfiguration config,
            in ShopTradeRequestedEvent request, out SinglePlayerProfile next)
        {
            next = null;
            if (profile == null || repository == null || config == null || string.IsNullOrWhiteSpace(request.RequestId)
                || string.IsNullOrWhiteSpace(request.InventoryId) || request.Quantity == 0 || request.Kind > ShopTradeKind.SellMaterial) return Fail(ShopTradeFailure.InvalidRequest);
            if (profile.PendingRaid != null || profile.RaidActionsFrozen) return Fail(ShopTradeFailure.NotInHub);
            if (profile.Items.IsResourceBusy || profile.PendingMaterialOutputs != 0 || profile.Items.HasWorldTransit) return Fail(ShopTradeFailure.ResourceBusy);
            var state = profile.Shop;
            if (state == null || state.ShopId != config.ShopId) return Fail(ShopTradeFailure.ConfigurationMismatch);
            // 完整请求作为指纹；同ID改数量/目标不是重试。旧版本请求即使不再是最近收据也不能二次扣款。
            string fingerprint = JsonUtility.ToJson(request);
            if (state.HasLastTrade && state.LastTrade.RequestId == request.RequestId)
            {
                if (!SameRequest(state.LastTrade.Fingerprint, fingerprint)) return Fail(ShopTradeFailure.InvalidRequest);
                next = profile; return new ShopTradeResult(ShopTradeFailure.None, state.LastTrade.Copy(), true);
            }
            if (request.ShopVersion != state.Version || request.InventoryVersion != profile.Items.Version) return Fail(ShopTradeFailure.StaleVersion);
            try
            {
                if (!ShopProgressTracker.TryPrepare(profile, config, out _, out bool due) || due) return Fail(ShopTradeFailure.StaleVersion);
                var candidate = RaidProfileOps.Clone(profile);
                long delta; ulong warehouseQuantity = 0; ShopTradeFailure failure;
                switch (request.Kind)
                {
                    case ShopTradeKind.BuyItem: failure = BuyItem(candidate, config, request, out delta, out warehouseQuantity); break;
                    case ShopTradeKind.SellItem: failure = SellItem(ref candidate, config, request, out delta); break;
                    case ShopTradeKind.BuyMaterial: failure = BuyMaterial(candidate, config, request, out delta); break;
                    case ShopTradeKind.SellMaterial: failure = SellMaterial(candidate, config, request, out delta); break;
                    default: return Fail(ShopTradeFailure.InvalidRequest);
                }
                if (failure != ShopTradeFailure.None) return Fail(failure);
                candidate.Currency = checked(candidate.Currency + delta);
                if (delta > 0) candidate.Shop.RemainingBudget -= delta;
                candidate.Shop.Version = checked(candidate.Shop.Version + 1);
                var receipt = new ShopTradeReceipt { RequestId = request.RequestId, Fingerprint = fingerprint, Kind = request.Kind,
                    Currency = candidate.Currency, CurrencyDelta = delta, Quantity = request.Quantity, ShopVersion = candidate.Shop.Version,
                    Generation = checked(profile.SaveGeneration + 1), Materials = CaptureMaterials(profile,candidate,request), WarehouseQuantity = warehouseQuantity };
                candidate.Shop.LastTrade = receipt; candidate.Shop.HasLastTrade = true;
                // 最终Replace之前，candidate中的任何数量变化都不向原Profile或外部消费者暴露。
                if (!repository.TrySave(candidate).Success) return Fail(ShopTradeFailure.SaveFailed);
                next = candidate; return new ShopTradeResult(ShopTradeFailure.None, receipt.Copy());
            }
            catch (OverflowException) { return Fail(ShopTradeFailure.Overflow); }
        }
        private static ShopTradeResult Fail(ShopTradeFailure reason) => new ShopTradeResult(reason);
        private static bool SameRequest(string saved, string current)
        {
            if (saved == current) return true;
            // JsonUtility 会给旧 JSON 缺少的 bool 补 false。只兼容尚无新策略字段的旧收据，
            // 再比较完整请求；改变新策略/数量/目标仍然不是同一笔重试。
            if (string.IsNullOrEmpty(saved) || saved.IndexOf("\"AllowWarehouseFallback\"", StringComparison.Ordinal) >= 0) return false;
            try { return JsonUtility.ToJson(JsonUtility.FromJson<ShopTradeRequestedEvent>(saved)) == current; }
            catch (ArgumentException) { return false; }
        }
        private static MaterialAmount[] CaptureMaterials(SinglePlayerProfile original,SinglePlayerProfile next,in ShopTradeRequestedEvent request)
        {
            if(request.Kind!=ShopTradeKind.BuyMaterial && request.Kind!=ShopTradeKind.SellMaterial)return null;
            string id=request.Kind==ShopTradeKind.BuyMaterial?request.ContainerId:request.ItemInstanceId;
            original.Items.TryGet(id,out var before);next.Items.TryGet(id,out var after);
            var moved=new List<MaterialAmount>(8);
            for(byte key=1;key<=8;key++)
            {
                ulong a=before?.Composition?.GetQuantity(key)??(before?.Material?.MaterialKey==key?before.Material.Quantity:0);
                ulong b=after?.Composition?.GetQuantity(key)??(after?.Material?.MaterialKey==key?after.Material.Quantity:0);
                ulong quantity=a>b?a-b:b-a;if(quantity>0)moved.Add(new MaterialAmount(key,quantity));
            }
            return moved.ToArray();
        }
        private static long Price(long unit, ulong quantity) => checked(unit * checked((long)quantity));
        private static ShopTradeFailure CanBuy(SinglePlayerProfile profile, ShopOffer offer, ulong quantity, out long cost)
        {
            cost = 0;
            if (offer == null) return ShopTradeFailure.UnknownOffer;
            var stock = profile.Shop.Find(offer.OfferId);
            if (stock == null) return ShopTradeFailure.ConfigurationMismatch;
            if (quantity > stock.Remaining) return ShopTradeFailure.OutOfStock;
            cost = Price(offer.BuyPrice, quantity);
            return profile.Currency < cost ? ShopTradeFailure.InsufficientCurrency : ShopTradeFailure.None;
        }
        private static ShopTradeFailure BuyItem(SinglePlayerProfile profile, ShopConfiguration config, in ShopTradeRequestedEvent request, out long delta, out ulong warehouseQuantity)
        {
            delta = 0; warehouseQuantity = 0; var offer = config.Find(request.OfferId);
            if (offer != null && offer.IsMaterial) return ShopTradeFailure.UnknownOffer;
            var failure = CanBuy(profile, offer, request.Quantity, out long cost); if (failure != ShopTradeFailure.None) return failure;
            // 限制离散交易输出规模，避免恶意数量让UI一次创建百万实例；不限制材料GMU。
            if (request.Quantity > 128) return ShopTradeFailure.InvalidRequest;
            profile.Items.Catalog.TryGetRules(offer.ContentId, out var rules);
            for (ulong i = 0; i < request.Quantity; i++)
            {
                var material = rules.Kind == ItemKind.Material ? new MaterialItemState(rules.MaterialKey, offer.InitialMaterialQuantity) : null;
                var item = new RuntimeItemState(Guid.NewGuid().ToString("N"), offer.ContentId, rules.Kind == ItemKind.Spell ? offer.InitialCharges : 0,
                    rules.Kind == ItemKind.Spell && offer.Unlimited, material: material);
                // 同一实例只增加一次。位置验证由 Store 负责：嵌套背包与真实形状
                // 都可能拒绝入包；回退在未提交候选里完成，不能用 UI 发第二笔交易。
                bool legacyForbidden = !request.AllowWarehouseFallback && (rules.Kind == ItemKind.Wand || rules.Kind == ItemKind.Backpack);
                bool placed = request.ToBackpack && !legacyForbidden && PlaceInBag(profile, item, request.BackpackId);
                if (!placed)
                {
                    if (request.ToBackpack && !request.AllowWarehouseFallback) return ShopTradeFailure.InventorySpace;
                    if (!profile.Items.TryAdd(item, ItemLocation.Warehouse)) return ShopTradeFailure.InventorySpace;
                    warehouseQuantity++;
                }
            }
            profile.Shop.Find(offer.OfferId).Remaining -= request.Quantity; delta = -cost; return ShopTradeFailure.None;
        }
        private static bool PlaceInBag(SinglePlayerProfile profile, RuntimeItemState item, string bagId)
        {
            if (!profile.Items.TryGet(bagId, out var bag) || !profile.Items.TryGetLocation(bagId, out var root)
                || root.Kind != ItemLocationKind.EquippedBackpack || root.OwnerId != profile.ActorId
                || !profile.Items.Catalog.TryGetRules(bag.ContentId, out var rules)) return false;
            // 自动放置只用于购买；真实形状/堆叠/边界验证仍由唯一归属表裁决，不修改已有摆放。
            for (int turn = 0; turn < 4; turn++) for (int y = 0; y < rules.BackpackHeight; y++) for (int x = 0; x < rules.BackpackWidth; x++)
                if (profile.Items.TryAdd(item, new GridPlacement(x,y,turn).InBackpack(bagId))) return true;
            return false;
        }
        private static bool Accessible(SinglePlayerProfile profile, string id)
            => profile.Items.TryGetLocation(id, out var location) && (location.Kind == ItemLocationKind.Warehouse || RaidProfileOps.IsCarried(profile,id));
        private static ShopTradeFailure SellItem(ref SinglePlayerProfile profile, ShopConfiguration config, in ShopTradeRequestedEvent request, out long delta)
        {
            delta = 0;
            if (request.Quantity != 1 || !Accessible(profile, request.ItemInstanceId) || !profile.Items.TryGet(request.ItemInstanceId, out var item)
                || item.IsStarterEquipment || item.Material != null) return ShopTradeFailure.NotSellable;
            if (item.Composition != null && item.Composition.Total != 0) return ShopTradeFailure.ContainerNotEmpty;
            foreach (var e in profile.Items) if (e.Location.HasParentInstance && e.Location.OwnerId == item.InstanceId) return ShopTradeFailure.NotSellable;
            var offer = config.Item(item.ContentId); if (offer == null || offer.SellPrice <= 0) return ShopTradeFailure.NotSellable;
            profile.Items.Catalog.TryGetRules(item.ContentId, out var rules);
            long value = offer.SellPrice;
            if (rules.Kind == ItemKind.Spell && !item.HasUnlimitedCharges)
                value = offer.InitialCharges == 0 ? 0 : (long)decimal.Floor((decimal)value * Math.Min(item.RemainingCharges, offer.InitialCharges) / offer.InitialCharges);
            if (value == 0) return ShopTradeFailure.NotSellable;
            if (value > profile.Shop.RemainingBudget) return ShopTradeFailure.BudgetExceeded;
            checked { _ = profile.Currency + value; }
            var kept = new List<InventoryEntry>(profile.Items.Count - 1);
            foreach (var e in profile.Items) if (e.Item.InstanceId != item.InstanceId) kept.Add(e);
            if (!RaidProfileOps.RestoreEntries(profile, kept, profile.ActorId, out var next)) return ShopTradeFailure.NotSellable;
            profile = next; delta = value; return ShopTradeFailure.None;
        }
        private static ShopTradeFailure BuyMaterial(SinglePlayerProfile profile, ShopConfiguration config, in ShopTradeRequestedEvent request, out long delta)
        {
            delta = 0; var offer = config.Find(request.OfferId);
            if (offer == null || !offer.IsMaterial) return ShopTradeFailure.UnknownOffer;
            var failure = CanBuy(profile,offer,request.Quantity,out long cost); if (failure != ShopTradeFailure.None) return failure;
            if (!Accessible(profile,request.ContainerId) || !profile.Items.TryGet(request.ContainerId,out var item) || item.Composition == null
                || (item.Composition.AllowedMaterials & 1 << offer.MaterialKey) == 0) return ShopTradeFailure.IncompatibleMaterial;
            if (request.Quantity > item.Composition.FreeCapacity) return ShopTradeFailure.ContainerFull;
            var amounts = new[] { new MaterialAmount(offer.MaterialKey,request.Quantity) };
            if (!item.Composition.CanAdd(amounts,1)) return ShopTradeFailure.IncompatibleMaterial;
            item.Composition.AddReserved(amounts,1);
            if (!TouchMaterial(profile,item)) return ShopTradeFailure.InventorySpace;
            profile.Shop.Find(offer.OfferId).Remaining -= request.Quantity; delta = -cost; return ShopTradeFailure.None;
        }
        private static ShopTradeFailure SellMaterial(SinglePlayerProfile profile, ShopConfiguration config, in ShopTradeRequestedEvent request, out long delta)
        {
            delta = 0;
            if (!Accessible(profile,request.ItemInstanceId) || !profile.Items.TryGet(request.ItemInstanceId,out var item)) return ShopTradeFailure.NotSellable;
            MaterialAmount[] amounts; int count;
            if (item.Composition != null)
            {
                if (request.Quantity > item.Composition.Total) return ShopTradeFailure.InsufficientMaterial;
                var all = new MaterialAmount[8]; count = item.Composition.CopyTo(all); amounts = new MaterialAmount[count];
                // 整笔混样出售保持原比例，不能选择高价组分免费提纯；复用M3最大余数分配。
                MaterialAmountOps.SplitBudget(all,count,request.Quantity,amounts,new ulong[count]);
            }
            else if (item.Material != null)
            { if (request.Quantity > item.Material.Quantity) return ShopTradeFailure.InsufficientMaterial;
                amounts = new[] { new MaterialAmount(item.Material.MaterialKey,request.Quantity) }; count = 1; }
            else return ShopTradeFailure.NotSellable;
            long value = 0;
            for (int i = 0; i < count; i++)
            { if (amounts[i].Quantity == 0) continue; var offer = config.Material(amounts[i].MaterialKey);
                if (offer == null || offer.SellPrice <= 0) return ShopTradeFailure.NotSellable; value = checked(value + Price(offer.SellPrice,amounts[i].Quantity)); }
            if (value > profile.Shop.RemainingBudget) return ShopTradeFailure.BudgetExceeded;
            checked { _ = profile.Currency + value; }
            if (item.Composition != null) { item.Composition.RemoveReserved(amounts,count); if (!TouchMaterial(profile,item)) return ShopTradeFailure.InventorySpace; }
            else
            {
                var replacement = request.Quantity == item.Material.Quantity ? null : new RuntimeItemState(item.InstanceId,item.ContentId,item.RemainingCharges,
                    item.HasUnlimitedCharges,item.StackStateKey,material:new MaterialItemState(item.Material.MaterialKey,item.Material.Quantity-request.Quantity),attachment:item.Attachment);
                profile.Items.TryGetLocation(item.InstanceId,out var location);
                if (!profile.Items.TryPrepareMaterialChange(item.InstanceId,replacement,location,out var prepared)) return ShopTradeFailure.InventorySpace;
                profile.Items.CompletePrepared(prepared,true);
            }
            delta = value; return ShopTradeFailure.None;
        }
        private static bool TouchMaterial(SinglePlayerProfile profile,RuntimeItemState item)
        {
            profile.Items.TryGetLocation(item.InstanceId,out var location);
            if (!profile.Items.TryPrepareMaterialChange(item.InstanceId,item,location,out var prepared)) return false;
            return profile.Items.CompletePrepared(prepared,true);
        }
    }
}
