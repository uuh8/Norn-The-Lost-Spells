using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Norn.Business.Features.Runs;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>原商店规则经过正式鉴权HTTP与隔离MySQL；报价只取受控导出，不把fixture加币/材料当生产获取入口。</summary>
public sealed class AccountShopTests
{
    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task OpeningShopReturnsTheSameProfileAndNeverRefreshesExistingStocksOrBudget()
    {
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync(); var config = Config(f);
        var first = await Shop(a.Client); var second = await Shop(a.Client);
        Assert.Equal(a.Profile.Version, first.Profile.Version); Assert.Equal(a.Profile.Gold, first.Profile.Gold);
        Assert.Equal(first.Profile.Version, first.Shop.InventoryVersion); Assert.Equal(a.Profile.PlayerId, first.Shop.PlayerId);
        Assert.Equal(config.Hash, first.Shop.ConfigHash); Assert.Equal(config.PurchaseBudget, first.Shop.RemainingBudget);
        Assert.Equal(config.Offers.Length, first.Shop.Stocks.Length);
        Assert.All(config.Offers, offer => Assert.Equal(offer.InitialStock, first.Shop.Stocks.Single(s => s.OfferId == offer.OfferId).Remaining));
        Assert.Equal(JsonSerializer.Serialize(first, OnlineFactory.Json), JsonSerializer.Serialize(second, OnlineFactory.Json));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ItemPurchaseUsesTrustedPriceAndFixedReceiptWithoutDuplicateGoldStockOrInstances()
    {
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync(); var b = await f.NewPlayerAsync();
        var before = await Shop(a.Client); var offer = BottleOffer(f); var request = Buy(before, offer);
        var receipt = await Trade(a.Client, request); var after = await Shop(a.Client);
        Assert.True(receipt.Success); Assert.Equal(before.Profile.Gold - offer.BuyPrice, after.Profile.Gold);
        Assert.Equal(before.Profile.Version + 1, after.Profile.Version); Assert.Equal(before.Shop.Version + 1, after.Shop.Version);
        Assert.Equal(offer.InitialStock - 1, after.Shop.Stocks.Single(s => s.OfferId == offer.OfferId).Remaining);
        var item = Assert.Single(after.Profile.Items, i => before.Profile.Items.All(old => old.InstanceId != i.InstanceId));
        Assert.Equal(offer.ContentId, item.ContentId); Assert.Equal(1, item.LocationKind); Assert.Empty(item.ContainerState.Components);
        var repeated = await Trade(a.Client, request); Assert.Equal(receipt.InventoryVersion, repeated.InventoryVersion);
        Assert.Equal(receipt.CurrencyDelta, repeated.CurrencyDelta);
        using var known = await a.Client.GetAsync("/api/online/shop/trades/" + request.CommandId); Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        using var stranger = await b.Client.GetAsync("/api/online/shop/trades/" + request.CommandId); Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
        Assert.Equal(JsonSerializer.Serialize(after, OnlineFactory.Json), JsonSerializer.Serialize(await Shop(a.Client), OnlineFactory.Json));
        await using (var db = new MySqlConnection(f.Connection))
        {
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM EntryBackpackGrants WHERE CharacterId=@Player", new { Player = a.Profile.PlayerId }));
            Assert.Equal(a.Profile.Items.Single(i => i.EntryBoundBackpack).InstanceId,
                await db.QuerySingleAsync<string>("SELECT InstanceId FROM EntryBackpackGrants WHERE CharacterId=@Player", new { Player = a.Profile.PlayerId }));
        }
        request.Quantity = 2;
        using var altered = await a.Client.PostAsJsonAsync("/api/online/shop/trade", request, OnlineFactory.Json);
        await RoomAdmissionTests.Error(altered, HttpStatusCode.Conflict, "CommandConflict");
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task InsufficientCurrencyOrStaleShopVersionLeavesAllAssetsStocksAndReceiptsUntouched()
    {
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync();
        await using var db = new MySqlConnection(f.Connection);
        await db.ExecuteAsync("UPDATE Wallets SET Balance=0 WHERE CharacterId=@Player", new { Player = a.Profile.PlayerId });
        var before = await Shop(a.Client); var request = Buy(before, BottleOffer(f));
        using var rejected = await a.Client.PostAsJsonAsync("/api/online/shop/trade", request, OnlineFactory.Json);
        await RoomAdmissionTests.Error(rejected, HttpStatusCode.Conflict, "InsufficientCurrency");
        // 拒绝没有“已扣款但UI没看见”的隐含候选；同ID重送仍拒绝，不能生成收据/新包或消耗供货。
        using var failedReplay = await a.Client.PostAsJsonAsync("/api/online/shop/trade", request, OnlineFactory.Json);
        await RoomAdmissionTests.Error(failedReplay, HttpStatusCode.Conflict, "InsufficientCurrency");
        request.CommandId = Guid.NewGuid().ToString("N"); request.ExpectedShopVersion++;
        using var stale = await a.Client.PostAsJsonAsync("/api/online/shop/trade", request, OnlineFactory.Json);
        await RoomAdmissionTests.Error(stale, HttpStatusCode.Conflict, "ShopVersionConflict");
        Assert.Equal(JsonSerializer.Serialize(before, OnlineFactory.Json), JsonSerializer.Serialize(await Shop(a.Client), OnlineFactory.Json));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM InventoryCommands WHERE CharacterId=@Player", new { Player = a.Profile.PlayerId }));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM EntryBackpackGrants WHERE CharacterId=@Player", new { Player = a.Profile.PlayerId }));
        Assert.Equal(before.Profile.Items.Length, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Items WHERE OwnerCharacterId=@Player", new { Player = a.Profile.PlayerId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task MixedMaterialBuyAndSalePreserveOriginalLargestRemainderCompositionAndContainerIdentity()
    {
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync(); var config = Config(f);
        var state = await Shop(a.Client); await Trade(a.Client, Buy(state, BottleOffer(f))); state = await Shop(a.Client);
        var bottle = state.Profile.Items.Single(i => i.ContentId == BottleOffer(f).ContentId);
        var rules = f.Manifest.Items.Single(i => i.ContentId == bottle.ContentId);
        var materials = config.Offers.Where(o => o.MaterialKey != 0 && (rules.ContainerMaterialsMask & (1 << o.MaterialKey)) != 0).Take(2).ToArray();
        Assert.Equal(2, materials.Length);
        await using var db = new MySqlConnection(f.Connection);
        await db.ExecuteAsync("INSERT INTO ItemContainerComponents VALUES(@Id,@A,4),(@Id,@B,6)", new { Id = bottle.InstanceId, A = materials[0].MaterialKey, B = materials[1].MaterialKey });
        state = await Shop(a.Client); long gold = state.Profile.Gold;
        var buy = Buy(state, materials[0]); buy.Kind = 2; buy.ContainerId = bottle.InstanceId; buy.Quantity = 5;
        await Trade(a.Client, buy); state = await Shop(a.Client);
        var sell = Buy(state, materials[0]); sell.Kind = 3; sell.OfferId = ""; sell.ItemInstanceId = bottle.InstanceId; sell.Quantity = 7;
        var receipt = await Trade(a.Client, sell); var after = await Shop(a.Client);
        var components = after.Profile.Items.Single(i => i.InstanceId == bottle.InstanceId).ContainerState.Components;
        Assert.Equal(5UL, components.Single(p => p.MaterialKey == materials[0].MaterialKey).Quantity);
        Assert.Equal(3UL, components.Single(p => p.MaterialKey == materials[1].MaterialKey).Quantity);
        Assert.Equal(4UL, receipt.Materials.Single(p => p.MaterialKey == materials[0].MaterialKey).Quantity);
        Assert.Equal(3UL, receipt.Materials.Single(p => p.MaterialKey == materials[1].MaterialKey).Quantity);
        long proceeds = checked(4 * materials[0].SellPrice + 3 * materials[1].SellPrice);
        Assert.Equal(gold - 5 * materials[0].BuyPrice + proceeds, after.Profile.Gold);
        Assert.Equal(config.PurchaseBudget - proceeds, after.Shop.RemainingBudget);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AccountItemRetirements WHERE InstanceId=@Id", new { Id = bottle.InstanceId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task SellingAnEmptyOrdinaryItemRetiresItsOriginalIdAndCannotSellTheBoundBaselineBag()
    {
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync();
        var before = await Shop(a.Client); var offer = BottleOffer(f); await Trade(a.Client, Buy(before, offer));
        var bought = await Shop(a.Client); var bottle = Assert.Single(bought.Profile.Items, i => i.ContentId == offer.ContentId);
        var sell = Buy(bought, offer); sell.Kind = 1; sell.ItemInstanceId = bottle.InstanceId; sell.OfferId = "";
        var receipt = await Trade(a.Client, sell); var after = await Shop(a.Client);
        Assert.Equal(offer.SellPrice, receipt.CurrencyDelta); Assert.Equal(bought.Profile.Gold + offer.SellPrice, after.Profile.Gold);
        Assert.DoesNotContain(after.Profile.Items, i => i.InstanceId == bottle.InstanceId);
        await using var db = new MySqlConnection(f.Connection);
        Assert.Equal("Sold", await db.QuerySingleAsync<string>("SELECT Reason FROM AccountItemRetirements WHERE InstanceId=@Id", new { Id = bottle.InstanceId }));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Items WHERE InstanceId=@Id", new { Id = bottle.InstanceId }));
        var forbidden = Buy(after, offer); forbidden.Kind = 1; forbidden.OfferId = "";
        forbidden.ItemInstanceId = after.Profile.Items.Single(i => i.EntryBoundBackpack).InstanceId;
        using var rejected = await a.Client.PostAsJsonAsync("/api/online/shop/trade", forbidden, OnlineFactory.Json);
        await RoomAdmissionTests.Error(rejected, HttpStatusCode.Conflict, "NotSellable");
        Assert.Equal(JsonSerializer.Serialize(after, OnlineFactory.Json), JsonSerializer.Serialize(await Shop(a.Client), OnlineFactory.Json));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task FullRealBackpackRejectsOrFallsBackToOneWarehouseItemWithinTheSamePurchase()
    {
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync();
        var bag = a.Profile.Items.Single(i => i.LocationKind == 4); await using var db = new MySqlConnection(f.Connection);
        var filler = f.Manifest.Items.First(i => i.Kind == 0 && i.ShapeWidth == 1 && i.ShapeHeight == 1 && i.MaxStack == 1);
        var bagRules = f.Manifest.Items.Single(i => i.ContentId == bag.ContentId);
        for (int y = 0; y < bagRules.BackpackHeight; y++) for (int x = 0; x < bagRules.BackpackWidth; x++)
            await db.ExecuteAsync("INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges) VALUES(@Id,@Player,@Content,1,1,FALSE); INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind,ParentInstanceId,GridX,GridY) VALUES(@Id,@Player,NULL,-1,1,@Bag,@X,@Y)",
                new { Id = Guid.NewGuid().ToString("N"), Player = a.Profile.PlayerId, Content = filler.ContentId, Bag = bag.InstanceId, X = x, Y = y });
        var before = await Shop(a.Client); var request = Buy(before, BottleOffer(f)); request.AllowWarehouseFallback = false;
        using var full = await a.Client.PostAsJsonAsync("/api/online/shop/trade", request, OnlineFactory.Json);
        await RoomAdmissionTests.Error(full, HttpStatusCode.Conflict, "InventorySpace");
        Assert.Equal(JsonSerializer.Serialize(before, OnlineFactory.Json), JsonSerializer.Serialize(await Shop(a.Client), OnlineFactory.Json));
        request.CommandId = Guid.NewGuid().ToString("N"); request.AllowWarehouseFallback = true;
        var receipt = await Trade(a.Client, request); var after = await Shop(a.Client);
        Assert.Equal(1UL, receipt.WarehouseQuantity);
        Assert.Equal(before.Profile.Items.Length + 1, after.Profile.Items.Length);
        Assert.Equal(0, Assert.Single(after.Profile.Items, i => i.ContentId == BottleOffer(f).ContentId).LocationKind);
        Assert.Equal(before.Profile.Gold - BottleOffer(f).BuyPrice, after.Profile.Gold);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ActiveRunLeaseRejectsNewShopReadAndTradeWithoutChangingTheFrozenAccount()
    {
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync(); var before = await Shop(a.Client);
        var runs = f.Services.GetRequiredService<RunEntryService>();
        var run = await runs.PrepareSingleAsync(a.Profile.PlayerId, new SinglePlayerEntryRequest { CommandId = Guid.NewGuid().ToString("N"), DungeonId = "p7", ExpectedInventoryVersion = a.Profile.Version }, default);
        try
        {
            using var read = await a.Client.GetAsync("/api/online/shop"); await RoomAdmissionTests.Error(read, HttpStatusCode.Conflict, "AssetsLeased");
            using var trade = await a.Client.PostAsJsonAsync("/api/online/shop/trade", Buy(before, BottleOffer(f)), OnlineFactory.Json);
            await RoomAdmissionTests.Error(trade, HttpStatusCode.Conflict, "AssetsLeased");
            var frozen = (await a.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
            Assert.True(frozen.AssetsLocked); Assert.Equal(before.Profile.Gold, frozen.Gold);
            await using var db = new MySqlConnection(f.Connection);
            Assert.Equal(before.Shop.Version, await db.QuerySingleAsync<long>("SELECT Version FROM CharacterShopStates WHERE CharacterId=@Player AND ShopId=@Shop", new { Player = a.Profile.PlayerId, Shop = before.Shop.ShopId }));
        }
        finally { await runs.FailStartAsync(run.RunId, run.RoomEpoch, "Cancelled", default); }
    }

    private static ContentShopDefinition Config(OnlineFactory f)
    { Assert.NotNull(f.Manifest.Shop); Assert.Equal(ContentShopRules.CalculateHash(f.Manifest.Shop), f.Manifest.Shop.Hash); return f.Manifest.Shop; }
    private static ContentShopOffer BottleOffer(OnlineFactory f) => Config(f).Offers.Single(o => o.ContentId == "containertest.bottle");
    private static async Task<AccountShopReply> Shop(HttpClient client)
    {
        using var response = await client.GetAsync("/api/online/shop"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reply = await response.Content.ReadFromJsonAsync<AccountShopReply>(OnlineFactory.Json); Assert.NotNull(reply); Assert.NotNull(reply.Profile); Assert.NotNull(reply.Shop); return reply;
    }
    private static AccountShopTradeRequest Buy(AccountShopReply state, ContentShopOffer offer) => new()
    { CommandId = Guid.NewGuid().ToString("N"), ShopId = state.Shop.ShopId, ConfigHash = state.Shop.ConfigHash,
        ExpectedVersion = state.Profile.Version, ExpectedShopVersion = state.Shop.Version, Kind = 0, Quantity = 1,
        OfferId = offer.OfferId, ToBackpack = true, AllowWarehouseFallback = true,
        BackpackId = state.Profile.Items.Single(i => i.LocationKind == 4).InstanceId };
    private static async Task<AccountShopTradeReply> Trade(HttpClient client, AccountShopTradeRequest request)
    {
        using var response = await client.PostAsJsonAsync("/api/online/shop/trade", request, OnlineFactory.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<AccountShopTradeReply>(OnlineFactory.Json); Assert.NotNull(receipt); return receipt;
    }
}
