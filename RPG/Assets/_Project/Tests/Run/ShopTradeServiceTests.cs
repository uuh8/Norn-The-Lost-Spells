using System;
using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class ShopTradeServiceTests : InventoryTestFixture
    {
        private SinglePlayerProfile _profile;
        private ProfileSaveRepository _repository;
        private MemoryRaidFiles _files;
        private ShopConfiguration _config;
        private readonly ShopTradeService _service = new ShopTradeService();
        private void Build(long money = 100, int bottleMaxStack = 1)
        {
            var store = Store(Define("spell", ItemKind.Spell), Define("bottle", ItemKind.Container, maxStack: bottleMaxStack), Define("wand", ItemKind.Wand),
                Define("bag", ItemKind.Backpack, bagWidth: 2, bagHeight: 2), Define("solid", ItemKind.Material));
            Add(store, "bag", "bag"); LoadoutOps.TryReplaceBackpack(store, "bag", "player", store.Version);
            Add(store, "card", "spell", 6); Add(store, "bottle", "bottle"); Add(store, "wand", "wand");
            _profile = new SinglePlayerProfile("player", store, currency: money); _files = new MemoryRaidFiles();
            _repository = new ProfileSaveRepository("Temp/SinglePlayer/M5-Memory", store.Catalog, _files);
            var definition = Track(ScriptableObject.CreateInstance<ShopDefinition>());
            definition.PurchaseBudget = 50;
            definition.Offers = new[] {
                new ShopOffer { OfferId="card", ContentId="spell", BuyPrice=10, SellPrice=6, InitialCharges=12, Stock=2 },
                new ShopOffer { OfferId="bottle", ContentId="bottle", BuyPrice=6, SellPrice=2, Stock=3 },
                new ShopOffer { OfferId="wand", ContentId="wand", BuyPrice=12, SellPrice=6, Stock=2 },
                new ShopOffer { OfferId="water", MaterialKey=1, BuyPrice=2, SellPrice=1, Stock=100 },
                new ShopOffer { OfferId="poison", MaterialKey=3, BuyPrice=3, SellPrice=2, Stock=100 }
            };
            Assert.That(definition.TryFreeze(store.Catalog, out _config), Is.True);
            Assert.That(ShopProgressTracker.TryPrepare(_profile, _config, out var next, out bool changed), Is.True);
            Assert.That(changed, Is.True); Assert.That(_repository.TrySave(next).Success, Is.True); _profile = next;
        }
        private ShopTradeRequestedEvent Request(ShopTradeKind kind, string offer = "card", ulong quantity = 1) => new ShopTradeRequestedEvent {
            InventoryId="inventory", RequestId=Guid.NewGuid().ToString("N"), InventoryVersion=_profile.Items.Version, ShopVersion=_profile.Shop.Version,
            Kind=kind, OfferId=offer, Quantity=quantity, ItemInstanceId="card", ContainerId="bottle", BackpackId="bag"
        };
        private ShopTradeResult Trade(ShopTradeRequestedEvent request)
        { var result = _service.TryTrade(_profile, _repository, _config, request, out var next); if (result.Success) _profile = next; return result; }
        private void Fill(ulong water, ulong poison = 0)
        {
            _profile.Items.TryGet("bottle", out var item);
            var amounts = poison == 0 ? new[] { new MaterialAmount(1, water) } : new[] { new MaterialAmount(1, water), new MaterialAmount(3, poison) };
            Assert.That(item.Composition.CanAdd(amounts, amounts.Length), Is.True); item.Composition.AddReserved(amounts, amounts.Length);
        }
        private void RejectUnchanged(ShopTradeRequestedEvent request, ShopTradeFailure expected)
        {
            string before = JsonUtility.ToJson(ProfileSaveMapper.ToDto(_profile, _profile.SaveGeneration));
            var result = _service.TryTrade(_profile, _repository, _config, request, out var next);
            Assert.That(result.Failure, Is.EqualTo(expected)); Assert.That(next, Is.Null);
            Assert.That(JsonUtility.ToJson(ProfileSaveMapper.ToDto(_profile, _profile.SaveGeneration)), Is.EqualTo(before));
        }
        private static ShopTradeRequestedEvent WithFallback(ShopTradeRequestedEvent request)
        {
            request.AllowWarehouseFallback = true; return request;
        }
        private static ulong WarehouseQuantity(ShopTradeReceipt receipt) => receipt.WarehouseQuantity;
        private void FillBag(int count)
        { for (int i = 0; i < count; i++) Assert.That(_profile.Items.TryAdd(new RuntimeItemState("fill" + i, "spell", 1), Bag(i % 2, i / 2)), Is.True); }
        private int PurchasedAt(ItemLocationKind kind,string contentId="spell")
        {
            int count = 0;
            foreach (var entry in _profile.Items)
                if (entry.Item.ContentId == contentId && entry.Item.InstanceId != "card" && !entry.Item.InstanceId.StartsWith("fill") && entry.Location.Kind == kind) count++;
            return count;
        }
        [Test] public void QuickPurchaseUsesBackpackBeforeWarehouse()
        {
            Build(); var request = WithFallback(Request(ShopTradeKind.BuyItem)); request.ToBackpack = true;
            var result = Trade(request); Assert.That(result.Success, Is.True);
            Assert.That(PurchasedAt(ItemLocationKind.Backpack), Is.EqualTo(1));
            Assert.That(WarehouseQuantity(result.Receipt), Is.Zero); Assert.That(_profile.Currency, Is.EqualTo(90));
        }
        [Test] public void FullBackpackQuickPurchaseCommitsOneItemToWarehouse()
        {
            Build(); FillBag(4); var request = WithFallback(Request(ShopTradeKind.BuyItem)); request.ToBackpack = true;
            var result = Trade(request); Assert.That(result.Success, Is.True);
            Assert.That(PurchasedAt(ItemLocationKind.Warehouse), Is.EqualTo(1)); Assert.That(WarehouseQuantity(result.Receipt), Is.EqualTo(1));
            Assert.That(_profile.Currency, Is.EqualTo(90)); Assert.That(_profile.Shop.Find("card").Remaining, Is.EqualTo(1));
        }
        [Test] public void PartialSpaceQuickPurchaseSplitsDestinationsWithoutMovingExistingItems()
        {
            Build(); FillBag(3); var request = WithFallback(Request(ShopTradeKind.BuyItem, quantity: 2)); request.ToBackpack = true;
            var result = Trade(request); Assert.That(result.Success, Is.True);
            Assert.That(PurchasedAt(ItemLocationKind.Backpack), Is.EqualTo(1)); Assert.That(PurchasedAt(ItemLocationKind.Warehouse), Is.EqualTo(1));
            Assert.That(WarehouseQuantity(result.Receipt), Is.EqualTo(1)); Assert.That(_profile.Currency, Is.EqualTo(80));
            for (int i = 0; i < 3; i++) { _profile.Items.TryGetLocation("fill" + i, out var location); Assert.That(location, Is.EqualTo(Bag(i % 2, i / 2))); }
        }
        [Test] public void MissingBackpackQuickPurchaseUsesWarehouse()
        {
            Build(); var request = WithFallback(Request(ShopTradeKind.BuyItem)); request.ToBackpack = true; request.BackpackId = "missing";
            var result = Trade(request); Assert.That(result.Success, Is.True); Assert.That(WarehouseQuantity(result.Receipt), Is.EqualTo(1));
        }
        [Test] public void FailedFallbackSaveRetainsWalletStockAndItemsThenRetryCommitsOnce()
        {
            Build(); FillBag(4); var request = WithFallback(Request(ShopTradeKind.BuyItem)); request.ToBackpack = true;
            _files.FailReplace = true; RejectUnchanged(request, ShopTradeFailure.SaveFailed);
            _files.FailReplace = false; Assert.That(Trade(request).Success, Is.True);
            Assert.That(Trade(request).Replayed, Is.True); Assert.That(PurchasedAt(ItemLocationKind.Warehouse), Is.EqualTo(1));
            Assert.That(_profile.Currency, Is.EqualTo(90));
        }
        [Test] public void FallbackReceiptKeepsActualDestinationThroughReloadAndReplay()
        {
            Build(); FillBag(4); var request = WithFallback(Request(ShopTradeKind.BuyItem)); request.ToBackpack = true;
            Assert.That(Trade(request).Success, Is.True); Assert.That(_repository.TryLoad(out _profile).Success, Is.True);
            var result = Trade(request); Assert.That(result.Replayed, Is.True); Assert.That(WarehouseQuantity(result.Receipt), Is.EqualTo(1));
            Assert.That(_profile.Currency, Is.EqualTo(90)); Assert.That(PurchasedAt(ItemLocationKind.Warehouse), Is.EqualTo(1));
        }
        [Test] public void FullMaterialContainerDoesNotTurnGmuIntoWarehouseItems()
        {
            Build(); _profile.Items.TryGet("bottle", out var bottle); Fill(bottle.Composition.FreeCapacity);
            RejectUnchanged(WithFallback(Request(ShopTradeKind.BuyMaterial, "water")), ShopTradeFailure.ContainerFull);
        }
        [Test] public void OldReceiptMissingPolicyFieldReplaysButCannotChangePolicy()
        {
            Build(); var request = Request(ShopTradeKind.BuyItem); Assert.That(Trade(request).Success, Is.True);
            _profile.Shop.LastTrade.Fingerprint = _profile.Shop.LastTrade.Fingerprint.Replace(",\"AllowWarehouseFallback\":false", "");
            Assert.That(_repository.TrySave(_profile).Success, Is.True); Assert.That(_repository.TryLoad(out _profile).Success, Is.True);
            Assert.That(Trade(request).Replayed, Is.True);
            RejectUnchanged(WithFallback(request), ShopTradeFailure.InvalidRequest);
        }
        [Test] public void QuickPurchasePlacesSpareWandInBackpackWithThreeEquippedWands()
        {
            Build(); Assert.That(_profile.Items.TryMove("wand", new ItemLocation(ItemLocationKind.EquippedWand, "player", 0, 0, 0, 0)).Success, Is.True);
            for (int i = 1; i < 3; i++) Assert.That(_profile.Items.TryAdd(new RuntimeItemState("w" + i, "wand", 0), new ItemLocation(ItemLocationKind.EquippedWand, "player", 0, 0, 0, i)), Is.True);
            var request = WithFallback(Request(ShopTradeKind.BuyItem, "wand")); request.ToBackpack = true;
            var result = Trade(request); Assert.That(result.Success, Is.True); Assert.That(result.Receipt.WarehouseQuantity, Is.Zero);
            Assert.That(PurchasedAt(ItemLocationKind.Backpack,"wand"),Is.EqualTo(1));
            Assert.That(_profile.Currency, Is.EqualTo(88));
        }
        [Test] public void FallbackCannotBypassMoneyOrStockChecks()
        {
            Build(0); FillBag(4); var request = WithFallback(Request(ShopTradeKind.BuyItem)); request.ToBackpack = true;
            RejectUnchanged(request, ShopTradeFailure.InsufficientCurrency);
            Build(); request = WithFallback(Request(ShopTradeKind.BuyItem, quantity: 3)); request.ToBackpack = true;
            RejectUnchanged(request, ShopTradeFailure.OutOfStock);
        }
        [Test]
        public void BuyingPreservesRealToolReferenceThroughCandidateDiskAndAdopt()
        {
            Build(); Assert.That(_profile.Items.TryMove("bottle", Bag()).Success, Is.True);
            var tools = new ToolbeltService(_profile, "inventory"); Assert.That(tools.TryBind(0, "bottle", tools.ConfigurationVersion), Is.True);
            var original = _profile;
            Assert.That(Trade(Request(ShopTradeKind.BuyItem)).Success, Is.True);
            Assert.That(_repository.TryLoad(out var restored).Success, Is.True);
            Assert.That(new ToolbeltService(restored, "restored").TryGetBinding(0, out var id), Is.True); Assert.That(id, Is.EqualTo("bottle"));
            original.Adopt(_profile); Assert.That(new ToolbeltService(original, "inventory").TryGetBinding(0, out id), Is.True);
            Assert.That(id, Is.EqualTo("bottle")); Assert.That(original.Currency, Is.EqualTo(90));
        }
        [Test]
        public void SellingBoundContainerClearsOnlyItsReferenceAfterReliableCommit()
        {
            Build(); Assert.That(_profile.Items.TryMove("bottle", Bag()).Success, Is.True);
            var tools = new ToolbeltService(_profile, "inventory"); Assert.That(tools.TryBind(0, "bottle", tools.ConfigurationVersion), Is.True);
            var request = Request(ShopTradeKind.SellItem); request.ItemInstanceId = "bottle";
            _files.FailReplace = true; RejectUnchanged(request, ShopTradeFailure.SaveFailed);
            Assert.That(tools.TryGetBinding(0, out _), Is.True);
            _files.FailReplace = false; Assert.That(Trade(request).Success, Is.True);
            Assert.That(_profile.Items.TryGet("bottle", out _), Is.False); Assert.That(_profile.Currency, Is.EqualTo(102));
            Assert.That(_repository.TryLoad(out var restored).Success, Is.True);
            Assert.That(new ToolbeltService(restored, "restored").TryGetBinding(0, out _), Is.False);
        }
        [Test]
        public void CommittedCandidateAdoptsItsWalletTogetherWithItems()
        {
            var store = Store(Define("bottle", ItemKind.Container));
            var current = new SinglePlayerProfile("player", store, currency: 100);
            var committed = new SinglePlayerProfile("player", store, currency: 70);
            current.Adopt(committed);
            Assert.That(current.Currency, Is.EqualTo(70), "可靠候选切换必须同时采用钱包，不能只刷新物品。");
        }

        [Test]
        public void TradingHasAnAtomicAuthorityRatherThanUiMutatingAssets()
        {
            Assert.That(typeof(SinglePlayerProfile).Assembly.GetType("Game.Run.ShopTradeService"), Is.Not.Null,
                "商店必须有Run层整笔事务入口，UI不能自行扣钱/增加物品。");
        }

        [Test] public void BuyItemCommitsWalletStockAndNewIdentityToDisk()
        { Build(); Assert.That(Trade(Request(ShopTradeKind.BuyItem)).Success, Is.True); Assert.That(_profile.Currency, Is.EqualTo(90));
            Assert.That(_profile.Shop.Find("card").Remaining, Is.EqualTo(1)); Assert.That(_profile.Items.Count, Is.EqualTo(5));
            Assert.That(_repository.TryLoad(out var restored).Success, Is.True); Assert.That(restored.Currency, Is.EqualTo(90)); Assert.That(restored.Items.Count, Is.EqualTo(5)); }
        [Test] public void InsufficientMoneyDoesNotConsumeStock()
        { Build(0); RejectUnchanged(Request(ShopTradeKind.BuyItem), ShopTradeFailure.InsufficientCurrency); }
        [Test] public void InsufficientStockRejectsWholeQuantity()
        { Build(); RejectUnchanged(Request(ShopTradeKind.BuyItem, quantity: 3), ShopTradeFailure.OutOfStock); }
        [Test] public void FullBackpackRejectsPurchaseWithoutCharging()
        { Build(); for (int y=0;y<2;y++)for(int x=0;x<2;x++) { var item=RuntimeItemState.Create("spell",1); Assert.That(_profile.Items.TryAdd(item, Bag(x,y)), Is.True); }
            var request=Request(ShopTradeKind.BuyItem);request.ToBackpack=true;RejectUnchanged(request,ShopTradeFailure.InventorySpace); }
        [Test] public void MultiplePurchasedItemsUseDistinctIdentitiesAndBackpackCells()
        { Build(); var request=Request(ShopTradeKind.BuyItem,quantity:2);request.ToBackpack=true;Assert.That(Trade(request).Success,Is.True);
            int count=0;foreach(var e in _profile.Items)if(e.Location.Kind==ItemLocationKind.Backpack)count++;Assert.That(count,Is.EqualTo(2)); }
        [Test] public void FiniteCardIsSoldByActualRemainingCharges()
        { Build(); Assert.That(Trade(Request(ShopTradeKind.SellItem)).Success,Is.True); Assert.That(_profile.Currency,Is.EqualTo(103));
            Assert.That(_profile.Shop.RemainingBudget,Is.EqualTo(47));Assert.That(_profile.Items.TryGet("card",out _),Is.False); }
        [Test] public void FreeStarterEquipmentCannotBeSold()
        { Build();Assert.That(_profile.Items.TryAdd(new RuntimeItemState("free","spell",0,true,StarterKitService.FreeStateKey),ItemLocation.Warehouse),Is.True);
            var r=Request(ShopTradeKind.SellItem);r.ItemInstanceId="free";RejectUnchanged(r,ShopTradeFailure.NotSellable); }
        [Test] public void FilledBottleShellCannotSilentlySellItsMaterials()
        { Build();Fill(10);var r=Request(ShopTradeKind.SellItem);r.ItemInstanceId="bottle";RejectUnchanged(r,ShopTradeFailure.ContainerNotEmpty); }
        [Test] public void EmptyBottleShellSaleRemovesOnlyTheShell()
        { Build();var r=Request(ShopTradeKind.SellItem);r.ItemInstanceId="bottle";Assert.That(Trade(r).Success,Is.True);
            Assert.That(_profile.Currency,Is.EqualTo(102));Assert.That(_profile.Items.TryGet("bottle",out _),Is.False); }
        [Test] public void LoadedWandCannotSellItsChildProgram()
        { Build();Assert.That(_profile.Items.TryMove("card",new ItemLocation(ItemLocationKind.WandSlot,"wand",0,0,0,0)).Success,Is.True);
            var r=Request(ShopTradeKind.SellItem);r.ItemInstanceId="wand";RejectUnchanged(r,ShopTradeFailure.NotSellable); }
        [Test] public void BuyingMaterialTransfersOnlyPaidAmountIntoExistingBottle()
        { Build();Fill(6,4);Assert.That(Trade(Request(ShopTradeKind.BuyMaterial,"water",5)).Success,Is.True);
            _profile.Items.TryGet("bottle",out var item);Assert.That(item.Composition.GetQuantity(1),Is.EqualTo(11));Assert.That(item.Composition.GetQuantity(3),Is.EqualTo(4));Assert.That(_profile.Currency,Is.EqualTo(90)); }
        [Test] public void FullContainerRejectsBuyingMaterials()
        { Build();Fill(1024);RejectUnchanged(Request(ShopTradeKind.BuyMaterial,"water",1),ShopTradeFailure.ContainerFull); }
        [Test] public void WrongTargetRejectsBuyingMaterial()
        { Build();var r=Request(ShopTradeKind.BuyMaterial,"water",1);r.ContainerId="card";RejectUnchanged(r,ShopTradeFailure.IncompatibleMaterial); }
        [Test] public void MixedSalePreservesProportionAndPricesEachActualComponent()
        { Build();Fill(36,24);var r=Request(ShopTradeKind.SellMaterial,quantity:10);r.ItemInstanceId="bottle";Assert.That(Trade(r).Success,Is.True);
            _profile.Items.TryGet("bottle",out var item);Assert.That(item.Composition.GetQuantity(1),Is.EqualTo(30));Assert.That(item.Composition.GetQuantity(3),Is.EqualTo(20));
            Assert.That(_profile.Currency,Is.EqualTo(114));Assert.That(_profile.Items.Count,Is.EqualTo(4)); }
        [Test] public void SaleCannotExceedActualMaterialQuantity()
        { Build();Fill(3);var r=Request(ShopTradeKind.SellMaterial,quantity:4);r.ItemInstanceId="bottle";RejectUnchanged(r,ShopTradeFailure.InsufficientMaterial); }
        [Test] public void BudgetFailureDoesNotPartiallyRemoveMixture()
        { Build();Fill(36,24);var r=Request(ShopTradeKind.SellMaterial,quantity:60);r.ItemInstanceId="bottle";RejectUnchanged(r,ShopTradeFailure.BudgetExceeded); }
        [Test] public void BuyDoesNotReplenishFinitePurchaseBudget()
        { Build();Assert.That(Trade(Request(ShopTradeKind.BuyItem)).Success,Is.True);Assert.That(_profile.Shop.RemainingBudget,Is.EqualTo(50)); }
        [Test] public void SameRequestReturnsReceiptWithoutRepeatingTransactionAfterReload()
        { Build();var r=Request(ShopTradeKind.BuyItem);Assert.That(Trade(r).Success,Is.True);long generation=_profile.SaveGeneration;
            Assert.That(_repository.TryLoad(out _profile).Success,Is.True);var again=Trade(r);Assert.That(again.Success,Is.True);Assert.That(again.Replayed,Is.True);
            Assert.That(_profile.Currency,Is.EqualTo(90));Assert.That(_profile.SaveGeneration,Is.EqualTo(generation)); }
        [Test] public void ReusingRequestIdForDifferentQuantityIsRejected()
        { Build();var r=Request(ShopTradeKind.BuyItem);Assert.That(Trade(r).Success,Is.True);r.Quantity=2;RejectUnchanged(r,ShopTradeFailure.InvalidRequest); }
        [Test] public void OlderRequestCannotBeReplayedAfterAnotherTrade()
        { Build();var old=Request(ShopTradeKind.BuyItem);Trade(old);Trade(Request(ShopTradeKind.BuyItem));RejectUnchanged(old,ShopTradeFailure.StaleVersion); }
        [Test] public void FinalFileReplacementFailureKeepsAllAssetsAndRetryWorks()
        { Build();Fill(6,4);var r=Request(ShopTradeKind.SellMaterial,quantity:5);r.ItemInstanceId="bottle";_files.FailReplace=true;
            RejectUnchanged(r,ShopTradeFailure.SaveFailed);_files.FailReplace=false;Assert.That(Trade(r).Success,Is.True);Assert.That(_profile.Currency,Is.EqualTo(107)); }
        [Test] public void RaidCannotUseShopEvenForWarehouseItems()
        { Build();_profile.RaidActionsFrozen=true;RejectUnchanged(Request(ShopTradeKind.BuyItem),ShopTradeFailure.NotInHub); }
        [Test] public void PendingOutputCannotTradeOrOrphanWorldMaterial()
        { Build();_profile.PendingMaterialOutputs=1;var result=_service.TryTrade(_profile,_repository,_config,Request(ShopTradeKind.BuyItem),out var next);
            Assert.That(result.Failure,Is.EqualTo(ShopTradeFailure.ResourceBusy));Assert.That(next,Is.Null);Assert.That(_profile.Currency,Is.EqualTo(100)); }
        [Test] public void WalletOverflowRejectsSaleWithoutDeletingAsset()
        { Build(long.MaxValue);RejectUnchanged(Request(ShopTradeKind.SellItem),ShopTradeFailure.Overflow); }
        [Test] public void EmptyOrUnknownRequestIsRejected()
        { Build();var r=Request(ShopTradeKind.BuyItem);r.RequestId="";RejectUnchanged(r,ShopTradeFailure.InvalidRequest);
            r=Request(ShopTradeKind.BuyItem,"missing");RejectUnchanged(r,ShopTradeFailure.UnknownOffer); }
        [Test] public void UnpricedComponentCannotBeRemovedOrPurifiedBySale()
        { Build();_profile.Items.TryGet("bottle",out var bottle);var amounts=new[]{new MaterialAmount(1,6),new MaterialAmount(7,4)};bottle.Composition.AddReserved(amounts,2);
            var r=Request(ShopTradeKind.SellMaterial,quantity:10);r.ItemInstanceId="bottle";RejectUnchanged(r,ShopTradeFailure.NotSellable); }
        [Test] public void MaterialReceiptListsActualComponentsRatherThanOnlyTotal()
        { Build();Fill(36,24);var r=Request(ShopTradeKind.SellMaterial,quantity:10);r.ItemInstanceId="bottle";var result=Trade(r);
            Assert.That(result.Success,Is.True);Assert.That(result.Receipt.Materials.Length,Is.EqualTo(2));Assert.That(result.Receipt.Materials[0].Quantity,Is.EqualTo(6));Assert.That(result.Receipt.Materials[1].Quantity,Is.EqualTo(4)); }
        [Test] public void SolidMaterialSaleReducesQuantityAndRemovesOnlyWhenEmpty()
        {
            Build();var offers=new ShopOffer[_config.Count+1];for(int i=0;i<_config.Count;i++)offers[i]=_config.At(i).Copy();offers[offers.Length-1]=new ShopOffer{OfferId="ice",MaterialKey=5,BuyPrice=4,SellPrice=2,Stock=10};
            _config=new ShopConfiguration(_config.ShopId,_config.Budget,1,offers);var stocks=new ShopStock[offers.Length];Array.Copy(_profile.Shop.Stocks,stocks,stocks.Length-1);stocks[stocks.Length-1]=new ShopStock{OfferId="ice",Remaining=10};_profile.Shop.Stocks=stocks;
            Assert.That(_profile.Items.TryAdd(new RuntimeItemState("solid","solid",0,material:new MaterialItemState(5,7)),ItemLocation.Warehouse),Is.True);
            var r=Request(ShopTradeKind.SellMaterial,quantity:3);r.ItemInstanceId="solid";Assert.That(Trade(r).Success,Is.True);
            Assert.That(_profile.Items.TryGet("solid",out var item),Is.True);Assert.That(item.Material.Quantity,Is.EqualTo(4));Assert.That(_profile.Currency,Is.EqualTo(106));
            r=Request(ShopTradeKind.SellMaterial,quantity:4);r.ItemInstanceId="solid";Assert.That(Trade(r).Success,Is.True);Assert.That(_profile.Items.TryGet("solid",out _),Is.False);
        }
        [Test] public void FillingSelectedBottleDoesNotChangeAnotherBottle()
        {
            Build(bottleMaxStack:2);Add(_profile.Items,"bottle2","bottle");
            Assert.That(_profile.Items.TryMove("bottle",Bag()).Success,Is.True);Assert.That(_profile.Items.TryMove("bottle2",Bag(1)).Success,Is.True);
            Assert.That(Trade(Request(ShopTradeKind.BuyMaterial,"water",1)).Success,Is.True);
            _profile.Items.TryGet("bottle",out var selected);_profile.Items.TryGet("bottle2",out var other);
            Assert.That(selected.Composition.Total,Is.EqualTo(1));Assert.That(other.Composition.Total,Is.Zero);
            At(_profile.Items,"bottle",Bag());At(_profile.Items,"bottle2",Bag(1));
        }
    }
}
