using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using MySqlConnector;
using Norn.Business.Content;
using Norn.Business.Features.Inventory;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Trading;

/// <summary>
/// 原商店的账号权威适配。HTTP只表达报价ID、原实例和数量，价格/容量来自受控内容；
/// 角色锁串行化钱包、库存和店铺，候选通过原父链/网格规则后才一次提交。
/// 不读取本地Profile文件、不运行Unity Gameplay，也不把开店/ESC当成功补货。
/// 全部复制/JSON工作都属于离散控制面，不进入Unity每帧或DS Tick。
/// </summary>
public sealed class AccountShopService(OnlineDatabase database, ContentManifestValidator content, InventoryService inventory)
{
    private const string ReceiptKind = "AccountShopTrade";
    private static readonly JsonSerializerOptions Json = ContentManifestValidator.Json;

    public async Task<AccountShopReply> GetAsync(string player, CancellationToken ct)
    {
        var config = Configuration();
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await LockCharacter(db, tx, player, ct); await RequireUnleased(db, tx, player, ct);
        var profile = await inventory.ReadSnapshotAsync(db, tx, player, ct);
        profile = await inventory.EnsureEntryBackpackAsync(db, tx, profile, null, ct);
        var state = await LoadState(db, tx, player, config, ct);
        var reply = new AccountShopReply { Profile = profile, Shop = Snapshot(player, profile.Version, config, state) };
        await tx.CommitAsync(ct); return reply;
    }

    public async Task<AccountShopTradeReply> TradeAsync(string player, AccountShopTradeRequest request, CancellationToken ct)
    {
        if (request == null) throw new OnlineFailure(400, "InvalidTrade");
        // await前冻结完整正文；相同CommandId改变任何字段都不是重试。
        var move = JsonSerializer.Deserialize<AccountShopTradeRequest>(JsonSerializer.Serialize(request, Json), Json)!;
        RequireRequest(move);
        string hash = Hash(JsonSerializer.Serialize(new { Kind = ReceiptKind, Request = move }, Json));
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        uint version = await LockCharacter(db, tx, player, ct);
        var prior = await db.QuerySingleOrDefaultAsync<ReceiptRow>(new CommandDefinition(
            "SELECT RequestHash,ResultJson FROM InventoryCommands WHERE CharacterId=@Player AND CommandId=@Id",
            new { Player = player, Id = move.CommandId }, tx, cancellationToken: ct));
        if (prior != null)
        {
            if (prior.RequestHash != hash) throw Failure("CommandConflict");
            return DecodeReceipt(prior.ResultJson, true); // 收据先于新版本/租约，避免未知成功被误报成失败。
        }
        await RequireUnleased(db, tx, player, ct);
        if (version != move.ExpectedVersion || version == uint.MaxValue) throw Failure("ProfileVersionConflict");
        var config = Configuration();
        if (move.ShopId != config.ShopId || move.ConfigHash != config.Hash) throw Failure("ShopConfigurationMismatch");
        var state = await LoadState(db, tx, player, config, ct);
        if (state.Row.Version != move.ExpectedShopVersion) throw Failure("ShopVersionConflict");
        var profile = await inventory.ReadSnapshotAsync(db, tx, player, ct);
        var candidate = AccountInventoryMoves.AccountSnapshot(profile, content);
        OwnedCombatAssetRules.RequireAccount(candidate, player, content.GameplayConfigHash, content);
        var before = candidate.Items.ToDictionary(i => i.InstanceId, i => JsonSerializer.Serialize(i, Json), StringComparer.Ordinal);
        var retired = new Dictionary<string, string>(StringComparer.Ordinal);
        ulong warehouse = 0; OnlineOwnedMaterialAmountSnapshot[] amounts = [];
        long delta;
        try
        {
            switch (move.Kind)
            {
                case 0: delta = BuyItems(candidate, config, state, move, out warehouse); break;
                case 1: delta = SellItem(candidate, config, state, move, retired); break;
                case 2: delta = BuyMaterial(candidate, config, state, move, out amounts); break;
                case 3: delta = SellMaterial(candidate, config, state, move, retired, out amounts); break;
                default: throw new OnlineFailure(400, "InvalidTrade");
            }
            candidate.Gold = checked(candidate.Gold + delta);
            if (delta > 0) state.Row.RemainingBudget = checked(state.Row.RemainingBudget - delta);
            state.Row.Version = checked(state.Row.Version + 1);
            if (!candidate.Items.Any(i => i.LocationKind == 4)) throw Failure("MinimumBackpackRequired");
            if (!candidate.Items.Any(i => i.InstanceId == candidate.SelectedWandInstanceId && i.LocationKind == 3))
                candidate.SelectedWandInstanceId = candidate.Items.Where(i => i.LocationKind == 3).OrderBy(i => i.Slot).Select(i => i.InstanceId).FirstOrDefault() ?? "";
            OwnedCombatAssetRules.RequireAccount(candidate, player, content.GameplayConfigHash, content);
            var reply = new AccountShopTradeReply { CommandId = move.CommandId, PlayerId = player, Success = true,
                InventoryVersion = checked(version + 1), ShopVersion = state.Row.Version, Currency = candidate.Gold,
                CurrencyDelta = delta, Quantity = move.Quantity, WarehouseQuantity = warehouse, Materials = amounts };
            await PersistTrade(db, tx, player, config, state, candidate, before, retired, reply, hash, ct);
            await tx.CommitAsync(ct); return reply;
        }
        catch (OverflowException) { throw Failure("Overflow"); }
    }

    /// <summary>只按已认证账号查询原固定收据；不返回历史Profile，也不给其他命令种类伪造商店收据。</summary>
    public async Task<AccountShopTradeReply> FindReceiptAsync(string player, string command, CancellationToken ct)
    {
        if (!Id(command)) throw new OnlineFailure(400, "InvalidCommand");
        await using var db = await database.OpenAsync(ct);
        string? json = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT ResultJson FROM InventoryCommands WHERE CharacterId=@Player AND CommandId=@Id", new { Player = player, Id = command }, cancellationToken: ct));
        if (json == null) throw new OnlineFailure(404, "ShopTradeNotFound");
        var record = JsonSerializer.Deserialize<TradeReceipt>(json, Json);
        if (record?.Kind != ReceiptKind || record.Reply == null || record.Reply.PlayerId != player)
            throw new OnlineFailure(404, "ShopTradeNotFound");
        record.Reply.Replayed = true; return record.Reply;
    }

    /// <summary>
    /// 仅可信成功离局的原收据事务可调用。三个显式成功事实缺一不可，旧schema/ESC/死亡不会补进度；
    /// 在原RunMember唯一收据行留已应用标记，与进度同事务更新，重试不重复。这里只提供消费口，
    /// 并不宣称DS成功出局采集/鉴权/提交已经接通，当前没有从普通退出/开店调用它。
    /// </summary>
    internal async Task<bool> RecordTrustedSuccessProgressAsync(MySqlConnection db, MySqlTransaction tx,
        string player, string runId, CancellationToken ct)
    {
        var receipt = await db.QuerySingleOrDefaultAsync<ProgressReceipt>(new CommandDefinition(
            "SELECT RequestJson,ShopProgressApplied FROM RunMemberExitReceipts WHERE CharacterId=@Player AND RunId=@Run FOR UPDATE",
            new { Player = player, Run = runId }, tx, cancellationToken: ct));
        if (receipt == null || receipt.ShopProgressApplied || !TrustedSuccess(receipt.RequestJson)) return false;
        long progress = await ReadProgress(db, tx, player, ct);
        if (progress == long.MaxValue) throw Failure("ShopProgressExhausted");
        await db.ExecuteAsync(new CommandDefinition("UPDATE CharacterEconomyProgress SET AdventureProgress=AdventureProgress+1 WHERE CharacterId=@Player; UPDATE RunMemberExitReceipts SET ShopProgressApplied=TRUE WHERE CharacterId=@Player AND RunId=@Run",
            new { Player = player, Run = runId }, tx, cancellationToken: ct));
        return true;
    }

    private static bool TrustedSuccess(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json); var root = document.RootElement;
            return root.TryGetProperty("SuccessfulExtraction", out var success) && success.ValueKind == JsonValueKind.True
                && root.TryGetProperty("CollectedGmu", out var collected) && collected.TryGetUInt64(out ulong gmu)
                && root.TryGetProperty("DefeatedEnemies", out var defeated) && defeated.TryGetUInt32(out uint kills)
                && (gmu > 0 || kills > 0);
        }
        catch (JsonException) { return false; }
    }

    private ContentShopDefinition Configuration()
    {
        var config = content.Manifest.Shop;
        if (content.Manifest.SchemaVersion != 3 || config == null) throw new OnlineFailure(503, "ShopNotConfigured");
        if (config.Hash != ContentShopRules.CalculateHash(config)) throw Failure("ShopConfigurationMismatch");
        foreach (var offer in config.Offers)
        {
            if (offer.MaterialKey != 0) continue;
            var item = content.RequireItem(offer.ContentId);
            if (item.Kind == 0 && !offer.Unlimited && offer.InitialCharges == 0
                || item.Kind == 4 && (offer.InitialMaterialQuantity == 0 || offer.InitialMaterialQuantity > item.MaterialItemCapacityGmu))
                throw Failure("ShopConfigurationMismatch");
        }
        return config;
    }

    private static async Task<uint> LockCharacter(MySqlConnection db, MySqlTransaction tx, string player, CancellationToken ct)
        => await db.QuerySingleOrDefaultAsync<uint?>(new CommandDefinition("SELECT Version FROM Characters WHERE CharacterId=@Player FOR UPDATE", new { Player = player }, tx, cancellationToken: ct))
            ?? throw new OnlineFailure(404, "ProfileNotFound");
    private static async Task RequireUnleased(MySqlConnection db, MySqlTransaction tx, string player, CancellationToken ct)
    {
        if (await db.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM AssetLeases WHERE CharacterId=@Player", new { Player = player }, tx, cancellationToken: ct)) != 0)
            throw Failure("AssetsLeased");
    }
    private static async Task<long> ReadProgress(MySqlConnection db, MySqlTransaction tx, string player, CancellationToken ct)
    {
        await db.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO CharacterEconomyProgress(CharacterId,AdventureProgress) VALUES(@Player,0)", new { Player = player }, tx, cancellationToken: ct));
        long progress = await db.QuerySingleAsync<long>(new CommandDefinition("SELECT AdventureProgress FROM CharacterEconomyProgress WHERE CharacterId=@Player", new { Player = player }, tx, cancellationToken: ct));
        if (progress < 0) throw Failure("ShopStateInvalid"); return progress;
    }

    /// <summary>沿原ShopProgressTracker：开店只初始化一次；可靠进度到阈值才整批补，内容升级只补新报价。</summary>
    private static async Task<ShopState> LoadState(MySqlConnection db, MySqlTransaction tx, string player, ContentShopDefinition config, CancellationToken ct)
    {
        long progress = await ReadProgress(db, tx, player, ct);
        var row = await db.QuerySingleOrDefaultAsync<ShopRow>(new CommandDefinition("SELECT Version,ConfigHash,RemainingBudget,AppliedProgress,StocksJson FROM CharacterShopStates WHERE CharacterId=@Player AND ShopId=@Shop", new { Player = player, Shop = config.ShopId }, tx, cancellationToken: ct));
        if (row == null)
        {
            row = new ShopRow { Version = 1, ConfigHash = config.Hash, RemainingBudget = config.PurchaseBudget, AppliedProgress = progress };
            var initial = new ShopState(row, config.Offers.Select(o => new AccountShopStock { OfferId = o.OfferId, Remaining = o.InitialStock }).ToArray(), progress);
            await db.ExecuteAsync(new CommandDefinition("INSERT INTO CharacterShopStates(CharacterId,ShopId,Version,ConfigHash,RemainingBudget,AppliedProgress,StocksJson) VALUES(@Player,@Shop,@Version,@Hash,@Budget,@Applied,@Stocks)",
                new { Player = player, Shop = config.ShopId, row.Version, Hash = config.Hash, Budget = row.RemainingBudget, Applied = row.AppliedProgress, Stocks = JsonSerializer.Serialize(initial.Stocks, Json) }, tx, cancellationToken: ct));
            return initial;
        }
        AccountShopStock[] stocks;
        try { stocks = JsonSerializer.Deserialize<AccountShopStock[]>(row.StocksJson, Json) ?? throw Failure("ShopStateInvalid"); }
        catch (JsonException) { throw Failure("ShopStateInvalid"); }
        if (row.Version <= 0 || row.RemainingBudget < 0 || row.RemainingBudget > config.PurchaseBudget || row.AppliedProgress < 0
            || row.AppliedProgress > progress || stocks.Length > config.Offers.Length || stocks.Length == 0)
            throw Failure("ShopStateInvalid");
        var known = config.Offers.ToDictionary(o => o.OfferId, StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stock in stocks)
            if (stock == null || stock.OfferId == null || !seen.Add(stock.OfferId) || !known.TryGetValue(stock.OfferId, out var offer) || stock.Remaining > offer.InitialStock)
                throw Failure("ShopStateInvalid");
        bool due = progress - row.AppliedProgress >= config.RefreshEveryProgress;
        var state = new ShopState(row, stocks, progress);
        if (due || stocks.Length != config.Offers.Length || row.ConfigHash != config.Hash)
        {
            if (row.Version == long.MaxValue) throw Failure("ShopVersionExhausted");
            state.Stocks = config.Offers.Select(o => new AccountShopStock { OfferId = o.OfferId,
                Remaining = due ? o.InitialStock : stocks.FirstOrDefault(s => s.OfferId == o.OfferId)?.Remaining ?? o.InitialStock }).ToArray();
            if (due)
            {
                row.AppliedProgress += (progress - row.AppliedProgress) / config.RefreshEveryProgress * config.RefreshEveryProgress;
                row.RemainingBudget = config.PurchaseBudget;
            }
            row.ConfigHash = config.Hash; row.Version++;
            await SaveState(db, tx, player, config, state, ct);
        }
        return state;
    }
    private static Task<int> SaveState(MySqlConnection db, MySqlTransaction tx, string player, ContentShopDefinition config, ShopState state, CancellationToken ct)
        => db.ExecuteAsync(new CommandDefinition("UPDATE CharacterShopStates SET Version=@Version,ConfigHash=@Hash,RemainingBudget=@Budget,AppliedProgress=@Applied,StocksJson=@Stocks WHERE CharacterId=@Player AND ShopId=@Shop",
            new { Player = player, Shop = config.ShopId, state.Row.Version, Hash = config.Hash, Budget = state.Row.RemainingBudget,
                Applied = state.Row.AppliedProgress, Stocks = JsonSerializer.Serialize(state.Stocks, Json) }, tx, cancellationToken: ct));
    private static AccountShopSnapshot Snapshot(string player, uint version, ContentShopDefinition config, ShopState state) => new()
    { PlayerId = player, ShopId = config.ShopId, ConfigHash = config.Hash, InventoryVersion = version, Version = state.Row.Version,
        RemainingBudget = state.Row.RemainingBudget, ProgressUntilRefresh = Math.Max(0, config.RefreshEveryProgress - (state.Progress - state.Row.AppliedProgress)), Stocks = state.Stocks };

    /// <summary>普通物品沿原先入背包/放不下整笔退仓策略，不把一次购买拆成两笔扣款。</summary>
    private long BuyItems(OnlineOwnedAssetSnapshot candidate, ContentShopDefinition config, ShopState state,
        AccountShopTradeRequest request, out ulong warehouse)
    {
        warehouse = 0; var offer = Offer(config, request.OfferId);
        if (offer.MaterialKey != 0) throw Failure("UnknownOffer");
        long cost = CanBuy(candidate, state, offer, request.Quantity);
        if (request.Quantity > 128) throw new OnlineFailure(400, "InvalidTrade");
        var rules = content.RequireItem(offer.ContentId);
        for (ulong n = 0; n < request.Quantity; n++)
        {
            // 原商店创建模板本体，未按掉落表随机roll；保持Mode0，不给现有实例重Roll或转模式。
            var item = new OnlineOwnedItemSnapshot { InstanceId = Guid.NewGuid().ToString("N"), ContentId = offer.ContentId,
                Revision = 1, UseTemplateWandStats = rules.Kind == 1,
                RemainingCharges = rules.Kind == 0 ? offer.InitialCharges : 0, HasUnlimitedCharges = rules.Kind == 0 && offer.Unlimited,
                ContainerState = rules.Kind == 3 ? new OnlineOwnedContainerStateSnapshot { SchemaVersion = 1, Components = [] } : null!,
                MaterialState = rules.Kind == 4 ? new OnlineOwnedMaterialStateSnapshot { SchemaVersion = 1, MaterialKey = rules.MaterialKey, Quantity = offer.InitialMaterialQuantity } : null! };
            bool legacyForbidden = !request.AllowWarehouseFallback && rules.Kind is 1 or 2;
            bool placed = request.ToBackpack && !legacyForbidden && TryPlaceInBag(candidate, item, request.BackpackId);
            if (placed) continue;
            if (request.ToBackpack && !request.AllowWarehouseFallback) throw Failure("InventorySpace");
            int slot = 0;
            while (slot < OnlineInventoryAreas.WarehouseCapacity && candidate.Items.Any(i => i.LocationKind == 0 && i.Slot == slot)) slot++;
            if (slot == OnlineInventoryAreas.WarehouseCapacity) throw Failure("InventorySpace");
            item.LocationKind = 0; item.ParentInstanceId = ""; item.Slot = slot; item.X = item.Y = item.QuarterTurns = 0;
            candidate.Items = [.. candidate.Items, item]; warehouse++;
        }
        Stock(state, offer.OfferId).Remaining -= request.Quantity; return -cost;
    }

    private bool TryPlaceInBag(OnlineOwnedAssetSnapshot candidate, OnlineOwnedItemSnapshot item, string bagId)
    {
        var bag = candidate.Items.SingleOrDefault(i => i.InstanceId == bagId && i.LocationKind == 4);
        if (bag == null || content.RequireItem(item.ContentId).Kind == 2) return false;
        var rules = content.RequireItem(bag.ContentId); var previous = candidate.Items;
        item.LocationKind = 1; item.ParentInstanceId = bagId; item.Slot = -1;
        candidate.Items = [.. previous, item];
        for (int turn = 0; turn < 4; turn++) for (int y = 0; y < rules.BackpackHeight; y++) for (int x = 0; x < rules.BackpackWidth; x++)
        {
            item.X = x; item.Y = y; item.QuarterTurns = turn;
            // 来源已完整验证；逐候选借唯一原网格/旋转/同格堆叠规则判断，不能另写一套近似矩形碰撞。
            try { OwnedCombatAssetRules.RequireAccount(candidate, candidate.PlayerId, content.GameplayConfigHash, content); return true; }
            catch (OnlineFailure) { /* 本候选不能放置；原数组仍未提交，继续原顺序找下一格。 */ }
        }
        candidate.Items = previous; return false;
    }

    private long SellItem(OnlineOwnedAssetSnapshot candidate, ContentShopDefinition config, ShopState state,
        AccountShopTradeRequest request, Dictionary<string, string> retired)
    {
        var item = candidate.Items.SingleOrDefault(i => i.InstanceId == request.ItemInstanceId);
        if (request.Quantity != 1 || item == null || item.EntryBoundBackpack || item.MaterialState != null || !Accessible(candidate, item)
            || item.ContainerState != null && Total(item.ContainerState.Components) != 0
            || candidate.Items.Any(i => i.ParentInstanceId == item.InstanceId)) throw Failure("NotSellable");
        var offer = config.Offers.SingleOrDefault(o => o.MaterialKey == 0 && o.ContentId == item.ContentId);
        if (offer == null || offer.SellPrice <= 0) throw Failure("NotSellable");
        long value = offer.SellPrice;
        if (content.RequireItem(item.ContentId).Kind == 0 && !item.HasUnlimitedCharges)
            value = offer.InitialCharges == 0 ? 0 : (long)decimal.Floor((decimal)value * Math.Min(item.RemainingCharges, offer.InitialCharges) / offer.InitialCharges);
        if (value == 0) throw Failure("NotSellable");
        if (value > state.Row.RemainingBudget) throw Failure("BudgetExceeded");
        candidate.Items = candidate.Items.Where(i => i.InstanceId != item.InstanceId).ToArray(); retired.Add(item.InstanceId, "Sold"); return value;
    }

    private long BuyMaterial(OnlineOwnedAssetSnapshot candidate, ContentShopDefinition config, ShopState state,
        AccountShopTradeRequest request, out OnlineOwnedMaterialAmountSnapshot[] amounts)
    {
        var offer = Offer(config, request.OfferId);
        if (offer.MaterialKey == 0) throw Failure("UnknownOffer");
        long cost = CanBuy(candidate, state, offer, request.Quantity);
        var item = candidate.Items.SingleOrDefault(i => i.InstanceId == request.ContainerId);
        if (item == null || item.ContainerState == null || !Accessible(candidate, item)) throw Failure("IncompatibleMaterial");
        var rules = content.RequireItem(item.ContentId);
        if ((rules.ContainerMaterialsMask & (1 << offer.MaterialKey)) == 0) throw Failure("IncompatibleMaterial");
        ulong total = Total(item.ContainerState.Components);
        if (request.Quantity > rules.ContainerCapacityGmu - total) throw Failure("ContainerFull");
        var parts = item.ContainerState.Components.ToDictionary(p => p.MaterialKey, p => p.Quantity);
        parts[offer.MaterialKey] = checked(parts.GetValueOrDefault(offer.MaterialKey) + request.Quantity);
        item.ContainerState.Components = parts.OrderBy(p => p.Key).Select(p => new OnlineOwnedMaterialAmountSnapshot { MaterialKey = p.Key, Quantity = p.Value }).ToArray();
        Stock(state, offer.OfferId).Remaining -= request.Quantity;
        amounts = [new OnlineOwnedMaterialAmountSnapshot { MaterialKey = offer.MaterialKey, Quantity = request.Quantity }]; return -cost;
    }

    private static long SellMaterial(OnlineOwnedAssetSnapshot candidate, ContentShopDefinition config, ShopState state,
        AccountShopTradeRequest request, Dictionary<string, string> retired, out OnlineOwnedMaterialAmountSnapshot[] amounts)
    {
        var item = candidate.Items.SingleOrDefault(i => i.InstanceId == request.ItemInstanceId);
        if (item == null || !Accessible(candidate, item)) throw Failure("NotSellable");
        if (item.ContainerState != null)
        {
            if (request.Quantity > Total(item.ContainerState.Components)) throw Failure("InsufficientMaterial");
            amounts = Split(item.ContainerState.Components, request.Quantity);
        }
        else if (item.MaterialState != null)
        {
            if (request.Quantity > item.MaterialState.Quantity) throw Failure("InsufficientMaterial");
            amounts = [new OnlineOwnedMaterialAmountSnapshot { MaterialKey = item.MaterialState.MaterialKey, Quantity = request.Quantity }];
        }
        else throw Failure("NotSellable");
        long value = 0;
        foreach (var part in amounts)
        {
            var offer = config.Offers.SingleOrDefault(o => o.MaterialKey == part.MaterialKey);
            if (offer == null || offer.SellPrice <= 0) throw Failure("NotSellable");
            value = checked(value + Price(offer.SellPrice, part.Quantity));
        }
        if (value > state.Row.RemainingBudget) throw Failure("BudgetExceeded");
        if (item.ContainerState != null)
        {
            var removed = amounts.ToDictionary(p => p.MaterialKey, p => p.Quantity);
            item.ContainerState.Components = item.ContainerState.Components.Select(p => new OnlineOwnedMaterialAmountSnapshot {
                MaterialKey = p.MaterialKey, Quantity = p.Quantity - removed.GetValueOrDefault(p.MaterialKey) }).Where(p => p.Quantity != 0).ToArray();
        }
        else
        {
            item.MaterialState!.Quantity -= request.Quantity;
            if (item.MaterialState.Quantity == 0)
            { candidate.Items = candidate.Items.Where(i => i.InstanceId != item.InstanceId).ToArray(); retired.Add(item.InstanceId, "Consumed"); }
        }
        return value;
    }

    /// <summary>
    /// 与原MaterialAmountOps的最大余数法同序：按材料Key排列，先整数下限，再把差额给最大余数。
    /// 业务端.NET有UInt128，直接精确乘除覆盖完整ulong GMU；不能用double损失末位，或只卖高价组分。
    /// </summary>
    private static OnlineOwnedMaterialAmountSnapshot[] Split(OnlineOwnedMaterialAmountSnapshot[] source, ulong budget)
    {
        var ordered = source.OrderBy(p => p.MaterialKey).ToArray(); ulong total = Total(ordered), used = 0;
        var result = new OnlineOwnedMaterialAmountSnapshot[ordered.Length]; var remainders = new ulong[ordered.Length];
        for (int i = 0; i < ordered.Length; i++)
        {
            UInt128 product = (UInt128)ordered[i].Quantity * budget;
            ulong quantity = (ulong)(product / total); remainders[i] = (ulong)(product % total); used += quantity;
            result[i] = new OnlineOwnedMaterialAmountSnapshot { MaterialKey = ordered[i].MaterialKey, Quantity = quantity };
        }
        while (used < budget)
        {
            int best = -1;
            for (int i = 0; i < result.Length; i++)
                if (remainders[i] != ulong.MaxValue && result[i].Quantity < ordered[i].Quantity && (best < 0 || remainders[i] > remainders[best])) best = i;
            if (best < 0) throw Failure("InvalidMaterialSplit");
            result[best].Quantity++; remainders[best] = ulong.MaxValue; used++;
        }
        return result.Where(p => p.Quantity > 0).ToArray();
    }
    private static ulong Total(OnlineOwnedMaterialAmountSnapshot[] parts)
    { ulong sum = 0; foreach (var part in parts) sum = checked(sum + part.Quantity); return sum; }
    private static bool Accessible(OnlineOwnedAssetSnapshot candidate, OnlineOwnedItemSnapshot item)
    {
        var root = item;
        for (int depth = 0; root.ParentInstanceId.Length != 0; depth++)
        {
            if (depth >= candidate.Items.Length) return false;
            var parent = candidate.Items.SingleOrDefault(i => i.InstanceId == root.ParentInstanceId);
            if (parent == null) return false; root = parent;
        }
        return root.LocationKind is 0 or 3 or 4 or 7 or 8; // 地面及其子闭包不参加远程交易。
    }
    private static ContentShopOffer Offer(ContentShopDefinition config, string id) => config.Offers.SingleOrDefault(o => o.OfferId == id) ?? throw Failure("UnknownOffer");
    private static AccountShopStock Stock(ShopState state, string id) => state.Stocks.SingleOrDefault(s => s.OfferId == id) ?? throw Failure("ShopStateInvalid");
    private static long Price(long unit, ulong quantity) => checked(unit * checked((long)quantity));
    private static long CanBuy(OnlineOwnedAssetSnapshot candidate, ShopState state, ContentShopOffer offer, ulong quantity)
    {
        if (quantity > Stock(state, offer.OfferId).Remaining) throw Failure("OutOfStock");
        long cost = Price(offer.BuyPrice, quantity); if (candidate.Gold < cost) throw Failure("InsufficientCurrency"); return cost;
    }

    /// <summary>
    /// 只把验证后的差异写入原表：活物原ID/roll/附件不改，出售/耗尽只移走位置并记永久退休。
    /// 收据先插入满足退休FK，但直到最后Commit之前钱包、店铺、物品与收据全都不可见。
    /// 不删除Items或历史侧表，不更新保留物的Revision；完整旧状态进入退休SnapshotJson。
    /// </summary>
    private static async Task PersistTrade(MySqlConnection db, MySqlTransaction tx, string player,
        ContentShopDefinition config, ShopState state, OnlineOwnedAssetSnapshot candidate,
        Dictionary<string, string> before, Dictionary<string, string> retired, AccountShopTradeReply reply, string hash, CancellationToken ct)
    {
        var changed = candidate.Items.Where(i => !before.TryGetValue(i.InstanceId, out var old) || JsonSerializer.Serialize(i, Json) != old).ToArray();
        var removed = before.Keys.Except(candidate.Items.Select(i => i.InstanceId), StringComparer.Ordinal).ToArray();
        if (removed.Any(id => !retired.ContainsKey(id)) || retired.Keys.Any(id => !removed.Contains(id))) throw Failure("ShopCandidateInvalid");
        foreach (var item in changed)
        {
            if (before.ContainsKey(item.InstanceId))
            {
                if (item.Revision == uint.MaxValue) throw Failure("InventoryRevisionExhausted"); item.Revision++;
                if (await db.ExecuteAsync(new CommandDefinition("UPDATE Items SET Revision=@Revision WHERE InstanceId=@Id AND OwnerCharacterId=@Player",
                    new { item.Revision, Id = item.InstanceId, Player = player }, tx, cancellationToken: ct)) != 1) throw Failure("ItemNotOwned");
            }
            else
            {
                await db.ExecuteAsync(new CommandDefinition("INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges,WandStatsMode) VALUES(@Id,@Player,@Content,@Revision,@Charges,@Unlimited,0); INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind,ParentInstanceId,GridX,GridY,QuarterTurns) VALUES(@Id,@Player,@Area,@Slot,@Kind,@Parent,@X,@Y,@Turns)",
                    new { Id = item.InstanceId, Player = player, Content = item.ContentId, item.Revision, Charges = item.RemainingCharges, Unlimited = item.HasUnlimitedCharges,
                        Area = item.LocationKind == 0 ? "Warehouse" : null, item.Slot, Kind = item.LocationKind == 0 ? (int?)null : item.LocationKind,
                        Parent = item.ParentInstanceId.Length == 0 ? null : item.ParentInstanceId, item.X, item.Y, Turns = item.QuarterTurns }, tx, cancellationToken: ct));
            }
            if (item.ContainerState != null)
            {
                if (!before.ContainsKey(item.InstanceId)) await db.ExecuteAsync(new CommandDefinition("INSERT INTO ItemContainerStates VALUES(@Id,1)", new { Id = item.InstanceId }, tx, cancellationToken: ct));
                await db.ExecuteAsync(new CommandDefinition("DELETE FROM ItemContainerComponents WHERE InstanceId=@Id", new { Id = item.InstanceId }, tx, cancellationToken: ct));
                foreach (var part in item.ContainerState.Components) await db.ExecuteAsync(new CommandDefinition("INSERT INTO ItemContainerComponents VALUES(@Id,@Key,@Quantity)",
                    new { Id = item.InstanceId, Key = part.MaterialKey, part.Quantity }, tx, cancellationToken: ct));
            }
            if (item.MaterialState is { } material)
            {
                if (before.ContainsKey(item.InstanceId))
                    await db.ExecuteAsync(new CommandDefinition("UPDATE ItemMaterialStates SET Quantity=@Quantity WHERE InstanceId=@Id", new { Id = item.InstanceId, material.Quantity }, tx, cancellationToken: ct));
                else await db.ExecuteAsync(new CommandDefinition("INSERT INTO ItemMaterialStates VALUES(@Id,1,@Key,@Quantity)", new { Id = item.InstanceId, Key = material.MaterialKey, material.Quantity }, tx, cancellationToken: ct));
            }
        }
        await db.ExecuteAsync(new CommandDefinition("INSERT INTO InventoryCommands(CharacterId,CommandId,RequestHash,ResultJson,CommittedAtUtc) VALUES(@Player,@Command,@Hash,@Result,UTC_TIMESTAMP(6))",
            new { Player = player, Command = reply.CommandId, Hash = hash, Result = JsonSerializer.Serialize(new TradeReceipt { Reply = reply }, Json) }, tx, cancellationToken: ct));
        foreach (string id in removed)
        {
            var old = JsonSerializer.Deserialize<OnlineOwnedItemSnapshot>(before[id], Json)!;
            if (old.Revision == uint.MaxValue) throw Failure("InventoryRevisionExhausted");
            await db.ExecuteAsync(new CommandDefinition("DELETE FROM ItemLocations WHERE InstanceId=@Id AND OwnerCharacterId=@Player; UPDATE Items SET Revision=Revision+1 WHERE InstanceId=@Id AND OwnerCharacterId=@Player; INSERT INTO AccountItemRetirements(InstanceId,OwnerCharacterId,CommandId,ContentId,Revision,Reason,SnapshotJson,RecordedAtUtc) VALUES(@Id,@Player,@Command,@Content,@Revision,@Reason,@Snapshot,UTC_TIMESTAMP(6))",
                new { Id = id, Player = player, Command = reply.CommandId, Content = old.ContentId, Revision = old.Revision + 1,
                    Reason = retired[id], Snapshot = before[id] }, tx, cancellationToken: ct));
        }
        await db.ExecuteAsync(new CommandDefinition("UPDATE Wallets SET Balance=@Gold WHERE CharacterId=@Player; INSERT INTO CharacterCombatLoadouts(CharacterId,SelectedWandInstanceId) VALUES(@Player,@Selected) ON DUPLICATE KEY UPDATE SelectedWandInstanceId=@Selected",
            new { Player = player, Gold = candidate.Gold, Selected = candidate.SelectedWandInstanceId.Length == 0 ? null : candidate.SelectedWandInstanceId }, tx, cancellationToken: ct));
        if (await db.ExecuteAsync(new CommandDefinition("UPDATE Characters SET Version=@Version WHERE CharacterId=@Player AND Version=@Before",
            new { Player = player, Version = reply.InventoryVersion, Before = candidate.InventoryVersion }, tx, cancellationToken: ct)) != 1) throw Failure("ProfileVersionConflict");
        await SaveState(db, tx, player, config, state, ct);
    }

    private static void RequireRequest(AccountShopTradeRequest request)
    {
        if (!Id(request.CommandId) || request.ExpectedVersion == 0 || request.ExpectedShopVersion < 1 || request.Kind is < 0 or > 3 || request.Quantity == 0
            || request.ShopId == null || request.ConfigHash == null || request.OfferId == null || request.ItemInstanceId == null || request.ContainerId == null || request.BackpackId == null
            || request.Kind is 0 or 2 && !ContentManifestRules.IsContentId(request.OfferId)
            || request.Kind is 1 or 3 && !Id(request.ItemInstanceId) || request.Kind == 2 && !Id(request.ContainerId))
            throw new OnlineFailure(400, "InvalidTrade");
    }
    private static AccountShopTradeReply DecodeReceipt(string json, bool replayed)
    {
        var receipt = JsonSerializer.Deserialize<TradeReceipt>(json, Json);
        if (receipt?.Kind != ReceiptKind || receipt.Reply == null || !receipt.Reply.Success) throw Failure("ShopReceiptInvalid");
        receipt.Reply.Replayed = replayed; return receipt.Reply;
    }
    private static bool Id(string value) => value != null && Guid.TryParseExact(value, "N", out var id) && id.ToString("N") == value;
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static OnlineFailure Failure(string code) => new(409, code);
    private sealed class ShopRow { public long Version { get; set; } public string ConfigHash { get; set; } = ""; public long RemainingBudget { get; set; } public long AppliedProgress { get; set; } public string StocksJson { get; set; } = ""; }
    private sealed class ShopState(ShopRow row, AccountShopStock[] stocks, long progress) { public ShopRow Row = row; public AccountShopStock[] Stocks = stocks; public long Progress = progress; }
    private sealed class ReceiptRow { public string RequestHash { get; set; } = ""; public string ResultJson { get; set; } = ""; }
    private sealed class TradeReceipt { public string Kind = ReceiptKind; public AccountShopTradeReply? Reply; }
    private sealed class ProgressReceipt { public string RequestJson { get; set; } = ""; public bool ShopProgressApplied { get; set; } }
}
