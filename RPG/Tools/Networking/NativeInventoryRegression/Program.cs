using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Game.NetworkProtocol;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Norn.Business.Features.Rooms;
using Norn.Business.Infrastructure;

namespace Norn.NativeInventoryRegression;

/// <summary>
/// 显式Native端到端验收，不是默认测试。HTTP注册/入场、DS裁决、可靠库存页、原UI命令、
/// 最终个人退出都是产品路径；本工具只编排并保存值证据，不写资产表、不上传最终Assets。
/// </summary>
internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = true };
    public static async Task<int> Main(string[] args)
    {
        try { return await new NativeCase(Options.Parse(args)).RunAsync(); }
        catch (CaseFailure failure) { Console.Error.WriteLine(failure.Code); return 1; }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name); return 1; }
    }
}

internal sealed record Options(string ServerExe, string ClientExe, string Output, string Root, int TimeoutSeconds, bool CoreOnly, string Dungeon)
{
    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        bool core = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--core-only") { core = true; continue; }
            if (args[i] is not ("--server-exe" or "--client-exe" or "--output" or "--repo" or "--timeout-seconds" or "--dungeon") || i + 1 == args.Length)
                throw new CaseFailure("ArgumentsInvalid");
            values.Add(args[i], args[++i]);
        }
        string Need(string key) => values.TryGetValue(key, out var value) ? Path.GetFullPath(value) : throw new CaseFailure("Missing" + key);
        string root = values.TryGetValue("--repo", out var explicitRoot) ? Path.GetFullPath(explicitRoot) : FindRoot();
        int timeout = values.TryGetValue("--timeout-seconds", out var seconds) && int.TryParse(seconds, out int parsed) ? parsed : 240;
        if (timeout is < 60 or > 280) throw new CaseFailure("TimeoutOutside60To280Seconds");
        string dungeon = values.GetValueOrDefault("--dungeon", "p7");
        if (dungeon is not ("p7" or "home")) throw new CaseFailure("DungeonMustBeP7OrHome");
        return new(Need("--server-exe"), Need("--client-exe"), Need("--output"), root, timeout, core, dungeon);
    }
    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Server", "Norn.Business", "Norn.Business.csproj"))) return directory.FullName;
        throw new CaseFailure("MonorepoRootNotFound");
    }
}

internal sealed class NativeCase(Options options)
{
    private readonly List<object> _checks = [];
    private readonly string _caseId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
    private string _private = "", _command = "", _clientReport = "", _workRoot = "", _connection = "", _player = "", _run = "", _instance = "";
    private NativeFactory? _factory;
    private Process? _client;
    private HttpClient? _http;
    private JsonObject? _lastClient, _lastDs;
    private string _stage = "Initialize", _failure = "";
    private bool _passed;
    private int _artifact;
    private int _instanceCapacity = 1;
    private int _entryOrdinal;
    private string _practiceScope = "";
    private string[] _accountFilledContainers = [];
    private readonly List<(string Id, int Entry)> _nativeInstances = [];
    private CancellationToken _deadline;

    public async Task<int> RunAsync()
    {
        if (Directory.Exists(options.Output) && Directory.EnumerateFileSystemEntries(options.Output).Any())
            throw new CaseFailure("OutputDirectoryMustBeNewOrEmpty");
        Directory.CreateDirectory(options.Output);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds)); _deadline = timeout.Token;
        string? previousProbe = Environment.GetEnvironmentVariable("NORN_N5_PLAYER_RESOURCE_PROBE");
        try
        {
            Require(File.Exists(options.ServerExe) && File.Exists(options.ClientExe), "NativeExecutablesMissing");
            _private = Path.Combine(options.Root, "RPG", ".tools", "native-inventory-regression", _caseId);
            Directory.CreateDirectory(_private); _workRoot = Path.Combine(_private, "instances");
            _command = Path.Combine(_private, "command.json"); _clientReport = Path.Combine(_private, "inventory-report.json");
            string settingsPath = Environment.GetEnvironmentVariable("NORN_N1_CONFIG") ?? throw new CaseFailure("PrivateN1ConfigMissing");
            using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, _deadline));
            _connection = settings.RootElement.GetProperty("Test").GetString() ?? "";
            string migration = settings.RootElement.GetProperty("TestMigration").GetString() ?? "";
            NativeFactory.RequireTestConnection(_connection); NativeFactory.RequireTestConnection(migration);
            string manifestPath = Environment.GetEnvironmentVariable("NORN_CONTENT_MANIFEST")
                ?? Path.Combine(options.Root, "RPG", "Assets", "_Project", "Resources", "Online", "ContentManifest.json");
            var manifest = JsonSerializer.Deserialize<ContentManifest>(await File.ReadAllTextAsync(manifestPath, _deadline), Program.Json)
                ?? throw new CaseFailure("ManifestMissing");
            using var gameplay = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(options.Root, "RPG", "Assets", "_Project", "Resources", "Online", "GameplayManifest.json"), _deadline));
            string hash = gameplay.RootElement.GetProperty("GameplayConfigHash").GetString() ?? "";
            int udp = await FreeUdpPort();
            Environment.SetEnvironmentVariable("NORN_N5_PLAYER_RESOURCE_PROBE", "1");
            _factory = new NativeFactory(new()
            {
                ["Online:Enabled"] = "true", ["Online:AutoMigrate"] = "true", ["Online:RoomsEnabled"] = "true",
                ["ConnectionStrings:Online"] = _connection, ["Online:MigrationConnectionString"] = migration,
                ["Online:ManifestPath"] = Path.GetFullPath(manifestPath), ["Online:GameplayConfigHash"] = hash,
                ["Online:AuthRequestsPerMinute"] = "120", ["Online:SessionMinutes"] = "30",
                ["Online:Instances:Enabled"] = "true", ["Online:Instances:BuildPath"] = options.ServerExe,
                ["Online:Instances:WorkRoot"] = _workRoot, ["Online:Instances:FirstPort"] = udp.ToString(),
                ["Online:Instances:LastPort"] = udp.ToString(), ["Online:Instances:MaximumInstances"] = _instanceCapacity.ToString()
            }, Path.Combine(options.Root, "Server", "Norn.Business"));
            // Package构建目标会为ProjectReference生成MvcTestingAppManifest，不依赖从工具目录搜索.sln。
            _factory.UseKestrel(0); _http = _factory.CreateClient();
            Require(_http.BaseAddress != null && _http.BaseAddress.IsLoopback && _http.BaseAddress.Port is not (5080 or 13384), "KestrelLoopbackOwnershipFailed");
            _factory.Services.GetRequiredService<OnlineOptions>().Instances.BusinessBaseUrl = _http.BaseAddress!.AbsoluteUri;
            await RegisterAndEnter(manifest);
            await RunGameplay();
            _passed = true;
        }
        catch (Exception error)
        {
            _failure = error is CaseFailure known ? known.Code : error is OperationCanceledException ? "CaseDeadlineExceeded" : error.GetType().Name;
            Console.Error.WriteLine(_stage + ": " + _failure);
        }
        finally
        {
            // 失败也只尝试原退出意图；不能靠数据库解锁、伪造结算或删Run掩盖失败。
            if (!_passed && _client != null && !_client.HasExited && _lastClient != null && Flag(_lastClient, "ClientInventoryReady"))
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    _deadline = cleanup.Token;
                    await Issue(new JsonObject { ["Action"] = "exit" });
                    await WaitClient(r => Text(r["LastRunExitStatus"], "Phase") == "Committed", "CleanupExit", 18);
                }
                catch (Exception error) { _checks.Add(new { Stage = "CleanupExit", Passed = false, Failure = error.GetType().Name }); }
            }
            await SaveLastEvidence();
            StopOwnedClient();
            await StopOwnedDs();
            if (_factory != null)
            {
                try { await _factory.DisposeAsync(); }
                catch (Exception error) { _checks.Add(new { Stage = "DisposeOwnedHost", Passed = false, Failure = error.GetType().Name }); _passed = false; }
            }
            _http?.Dispose();
            Environment.SetEnvironmentVariable("NORN_N5_PLAYER_RESOURCE_PROBE", previousProbe);
            // 一次性Ticket/控制文件不作为公开证据；账号密码/Token从未写入文件。
            if (!string.IsNullOrEmpty(_private))
                foreach (string file in new[] { "ticket.json", "probe-config.json", "command.json", "command.tmp" })
                {
                    try { string path = Path.Combine(_private, file); if (File.Exists(path)) File.Delete(path); }
                    catch (IOException) { _checks.Add(new { Stage = "RemoveOwnPrivateControl", Passed = false }); }
                }
            if (!_passed && string.IsNullOrEmpty(_failure)) _failure = "CleanupFailed";
            await File.WriteAllTextAsync(Path.Combine(options.Output, "evidence.json"), JsonSerializer.Serialize(new
            {
                CaseId = _caseId, Passed = _passed, Failure = _failure, Stage = _stage, CoreOnly = options.CoreOnly,
                DungeonId = options.Dungeon, EntryCount = _entryOrdinal,
                PlayerId = _player, RunId = _run, InstanceId = _instance, Checks = _checks,
                HostErrors = _factory?.Logs.Errors.ToArray(), RecordedAtUtc = DateTime.UtcNow,
                Scope = "Real loopback Kestrel/MySQL/Native DS/Native Client and original UI callbacks; pointer Raycast and subjective combat feel are not covered"
            }, Program.Json));
        }
        Console.WriteLine(_passed ? "NativeInventoryRegressionPassed" : "NativeInventoryRegressionFailed");
        return _passed ? 0 : 1;
    }

    private async Task RegisterAndEnter(ContentManifest manifest)
    {
        Stage("RegisterAndLogin");
        string username = "reg_" + Guid.NewGuid().ToString("N")[..16];
        string password = Guid.NewGuid().ToString("N") + "!Native";
        await Post<RegisterAccountRequest, JsonObject>("/api/online/accounts", new() { Username = username, Password = password });
        var login = await Post<LoginAccountRequest, LoginAccountReply>("/api/online/sessions", new()
        { Username = username, Password = password, ProtocolVersion = manifest.ProtocolVersion, ContentManifestHash = manifest.Hash });
        _http!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        _http.DefaultRequestHeaders.Add("X-Norn-Manifest", manifest.Hash);
        _http.DefaultRequestHeaders.Add("X-Norn-Protocol", manifest.ProtocolVersion.ToString());
        var profile = await Get<OnlineProfileReply>("/api/online/profile"); _player = profile.PlayerId;
        _practiceScope = profile.PracticeSessionScope;
        if (options.Dungeon == "home") Require(!string.IsNullOrEmpty(_practiceScope), "HomeSessionScopeMissing");
        if (!options.CoreOnly && options.Dungeon == "p7") profile = await PrepareTwoWandsThreeAccountContainers(manifest, profile);
        await EnterAndStart(profile);
    }

    /// <summary>
    /// 账号先经正式商店准备两杖三瓶，P7原入局奖励再提供槽0的第四只空瓶。
    /// 注册本来没有容器，不能在这里预期四瓶或补发；逐笔读取Profile确认版本、身份和三瓶各17 GMU水。
    /// 这里只编排原HTTP命令，不向数据库植入物品，也不改Starter、入局奖励或游戏目录。
    /// </summary>
    private async Task<OnlineProfileReply> PrepareTwoWandsThreeAccountContainers(ContentManifest manifest, OnlineProfileReply profile)
    {
        Stage("AccountPrepareTwoWandsThreeContainers");
        Require(profile.Items.All(item => item.ContainerState == null), "RegistrationUnexpectedContainer");
        long initialGold = profile.Gold;
        string starterWand = profile.CombatAssets.SelectedWandInstanceId;
        string bag = profile.Items.Single(item => item.LocationKind == 4).InstanceId;
        var purchases = new[] { "wandtest.wand.b", "containertest.bottle", "containertest.bottle", "containertest.bottle" };
        for (int index = 0; index < purchases.Length; index++)
        {
            var offer = manifest.Shop.Offers.Single(item => item.ContentId == purchases[index]);
            Stage("AccountBuy" + index + ":" + offer.ContentId);
            var shop = await Get<AccountShopReply>("/api/online/shop");
            var knownIds = shop.Profile.Items.Select(item => item.InstanceId).ToHashSet(StringComparer.Ordinal);
            var bought = await Post<AccountShopTradeRequest, AccountShopTradeReply>("/api/online/shop/trade", new()
            { CommandId = Guid.NewGuid().ToString("N"), ShopId = shop.Shop.ShopId, ConfigHash = shop.Shop.ConfigHash,
                ExpectedVersion = shop.Profile.Version, ExpectedShopVersion = shop.Shop.Version, Kind = 0, Quantity = 1,
                // 正式Hub的quickPurchase同时允许先入包和放不下退仓；false属于旧选项语义，
                // 对Wand/Backpack会明确禁止入包，不能把它当成“更严格的正常购买”使用。
                OfferId = offer.OfferId, ToBackpack = true, AllowWarehouseFallback = true, BackpackId = bag });
            Require(bought.Success, "AccountLoadoutPurchaseRejected");
            profile = await Get<OnlineProfileReply>("/api/online/profile");
            Require(profile.Version >= bought.InventoryVersion, "AccountPurchaseProfileNotConfirmed");
            var added = profile.Items.Single(item => !knownIds.Contains(item.InstanceId) && item.ContentId == offer.ContentId);
            _checks.Add(new { Stage = "AccountLoadoutPurchase", Passed = true, added.InstanceId, added.ContentId,
                ActualLocationKind = added.LocationKind, ActualParentId = added.ParentInstanceId,
                bought.InventoryVersion, bought.WarehouseQuantity });
            int destination = index == 0 ? 3 : 8;
            int slot = index == 0 ? 1 : index;
            Stage("AccountEquip" + index + ":" + offer.ContentId);
            await Post<InventoryMoveRequest, InventoryMoveReply>("/api/online/inventory/move", new()
            { CommandId = Guid.NewGuid().ToString("N"), ExpectedVersion = profile.Version, InstanceId = added.InstanceId,
                OperationKind = 1, TargetLocationKind = destination, Slot = slot });
            profile = await Get<OnlineProfileReply>("/api/online/profile");
            Require(profile.Items.Any(item => item.InstanceId == added.InstanceId && item.LocationKind == destination && item.Slot == slot),
                "AccountLoadoutEquipNotConfirmed");
        }
        var bottles = profile.Items.Where(item => item.LocationKind == 8 && item.ContainerState != null).OrderBy(item => item.Slot).ToArray();
        Require(profile.Items.Count(item => item.LocationKind == 3) == 2 && bottles.Length == 3
            && bottles.Select(item => item.Slot).SequenceEqual(new[] { 1, 2, 3 }), "TwoWandsThreeAccountContainersNotPrepared");
        var water = manifest.Shop.Offers.Single(offer => offer.MaterialKey == 1);
        foreach (var bottle in bottles)
        {
            Stage("AccountFillWaterToolSlot" + bottle.Slot);
            var shop = await Get<AccountShopReply>("/api/online/shop");
            var filled = await Post<AccountShopTradeRequest, AccountShopTradeReply>("/api/online/shop/trade", new()
            { CommandId = Guid.NewGuid().ToString("N"), ShopId = shop.Shop.ShopId, ConfigHash = shop.Shop.ConfigHash,
                ExpectedVersion = shop.Profile.Version, ExpectedShopVersion = shop.Shop.Version,
                Kind = 2, Quantity = 17, OfferId = water.OfferId, ContainerId = bottle.InstanceId });
            Require(filled.Success && filled.Quantity == 17, "AccountBottleWaterPurchaseRejected");
        }
        profile = await Get<OnlineProfileReply>("/api/online/profile");
        Require(bottles.All(bottle =>
        {
            var parts = profile.Items.Single(item => item.InstanceId == bottle.InstanceId).ContainerState.Components;
            return parts.Length == 1 && parts[0].MaterialKey == 1 && parts[0].Quantity == 17;
        }), "AccountBottleWaterProfileNotConfirmed");
        Stage("AccountSelectStarterWand");
        // 与OnlineAccountClient.Inventory的SelectWand正文一致：选择虽然不移动资产，
        // 仍须声明TargetLocationKind=EquippedWand(3)，不能让默认0被解释成仓库目标。
        var selected = await Post<InventoryMoveRequest, InventoryMoveReply>("/api/online/inventory/move", new()
        { CommandId = Guid.NewGuid().ToString("N"), ExpectedVersion = profile.Version, InstanceId = starterWand,
            OperationKind = 2, TargetLocationKind = 3, TargetParentInstanceId = "", Slot = 0,
            GridX = 0, GridY = 0, QuarterTurns = 0, WholeStack = false });
        profile = await Get<OnlineProfileReply>("/api/online/profile");
        Require(selected.InstanceId == starterWand && selected.OperationKind == 2 && selected.TargetLocationKind == 3
            && selected.Slot == 0 && profile.Version == selected.CommittedVersion
            && profile.CombatAssets.SelectedWandInstanceId == starterWand, "StarterWandSelectionNotConfirmed");
        _accountFilledContainers = bottles.Select(item => item.InstanceId).ToArray();
        _checks.Add(new { Stage = "AccountTwoWandsThreeContainers", Passed = true, InitialGold = initialGold, FinalGold = profile.Gold,
            Bottles = _accountFilledContainers, FilledContainers = 3, EmptyContainers = 0, AddedWaterGmu = 51,
            Covered = "Original shop HTTP purchases and confirmed Profile; the fourth empty bottle belongs to the later P7 entry reward" });
        return profile;
    }

    private async Task EnterAndStart(OnlineProfileReply profile)
    {
        _entryOrdinal++;
        _lastClient = null; _lastDs = null; _instance = ""; _run = "";
        foreach (string path in new[] { _clientReport, Path.Combine(_private, "admission-report.json") })
            if (File.Exists(path)) File.Delete(path); // 只清本工具上一次Client的报告，防止把旧Ready当成新连接。
        Stage("SingleEntry");
        var room = await Post<SinglePlayerEntryRequest, RoomReply>("/api/online/rooms/single-entry", new()
        { CommandId = Guid.NewGuid().ToString("N"), DungeonId = options.Dungeon, ExpectedInventoryVersion = profile.Version });
        Require(room.SinglePlayer && room.DungeonId == options.Dungeon && !string.IsNullOrEmpty(room.RunId), "SingleEntryIdentityMismatch");
        _run = room.RunId;
        var started = Stopwatch.StartNew(); RoomInstanceReply instance;
        while (true)
        {
            instance = await Get<RoomInstanceReply>($"/api/online/rooms/{room.RoomId}/instance"); _instance = instance.InstanceId;
            if (!string.IsNullOrEmpty(_instance) && _nativeInstances.All(value => value.Id != _instance)) _nativeInstances.Add((_instance, _entryOrdinal));
            if (instance.State == "Ready") break;
            Require(instance.State is not ("Failed" or "Completed") && started.Elapsed.TotalSeconds < 55, "InstanceReadyFailedOrTimedOut");
            await Task.Delay(250, _deadline);
        }
        Require(instance.RunId == _run && instance.DungeonId == options.Dungeon && instance.RoomEpoch == room.RoomEpoch, "ReadyIdentityMismatch");
        var ticket = await Post<object, JoinTicketReply>($"/api/online/rooms/{room.RoomId}/ticket", null);
        Require(ticket.RunId == _run && ticket.InstanceId == _instance && ticket.PlayerId == _player, "TicketIdentityMismatch");
        await WritePrivate("ticket.json", new { Ticket = ticket, Output = Path.Combine(_private, "admission-report.json"), DurationSeconds = 300, ExpectRejection = false });
        await WritePrivate("probe-config.json", new { Output = _clientReport, Command = _command });
        Directory.CreateDirectory(Path.Combine(options.Output, "native"));
        var start = new ProcessStartInfo(options.ClientExe)
        { WorkingDirectory = Path.GetDirectoryName(options.ClientExe), UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        string clientLog = _entryOrdinal == 1 ? "client.log" : $"client-{_entryOrdinal:00}.log";
        foreach (string argument in new[] { "-batchmode", "-force-d3d11", "-logFile", Path.Combine(options.Output, "native", clientLog),
            "--norn-room-client", Path.Combine(_private, "ticket.json"), "--norn-inventory-regression", Path.Combine(_private, "probe-config.json") }) start.ArgumentList.Add(argument);
        // Native Client不需要数据库、migration或Host环境凭据。Ticket仅从当前私有文件进入原握手。
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase)
            || k.StartsWith("Online__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("NORN_N1_", StringComparison.OrdinalIgnoreCase)
            || k == "NORN_CONTENT_MANIFEST").ToArray()) start.Environment.Remove(key);
        start.Environment.Remove("NORN_INSTANCE_SECRET");
        _client = Process.Start(start) ?? throw new CaseFailure("ClientStartFailed");
        _ = _client.Handle;
        Stage("OwnerReady");
        var ready = await WaitClient(r => Flag(r, "ClientInventoryReady") && Flag(r, "InventoryCaptured") && Flag(r, "WorldReady")
            && Flag(r, "CanAdvance") && Text(r, "DungeonId") == options.Dungeon, "OwnerReady", 55);
        Require(Number(ready, "ActorId") == 1UL, "SingleOwnerActorMismatch");
        Record("OwnerReady", ready);
    }

    private async Task RunGameplay()
    {
        var thrownContainers = new List<string>();
        var initial = _lastClient ?? throw new CaseFailure("OwnerReportMissing");
        if (!options.CoreOnly && options.Dungeon == "p7")
        {
            var bottles = Objects(initial["Inventory"], "Items").Where(item => Flag(item, "HasContainerState") && Number(item, "LocationKind") == 8).ToArray();
            Require(bottles.Length == 4 && Objects(initial["Inventory"], "Items").Count(item => Number(item, "LocationKind") == 3) == 2,
                "NativeOwnerDidNotInstallTwoWandsFourContainers");
            var reward = bottles.SingleOrDefault(item => Number(item, "Slot") == 0);
            Require(reward != null && Number(reward, "ContainerTotal") == 0 && !_accountFilledContainers.Contains(Text(reward, "InstanceId")),
                "OriginalP7EmptyContainerRewardNotInstalled");
            Require(_accountFilledContainers.Length == 3 && _accountFilledContainers.All(id => bottles.Any(item => Text(item, "InstanceId") == id
                && Number(item, "ContainerTotal") == 17 && Number(item, "Slot") is >= 1 and <= 3)), "AccountFilledContainersChangedAtEntry");
            _checks.Add(new { Stage = "NativeTwoWandsFourContainers", Passed = true, FilledContainers = 3, EmptyContainers = 1,
                AddedWaterGmu = 51, AccountBottles = _accountFilledContainers, EntryRewardBottle = Text(reward, "InstanceId"),
                Covered = "Three purchased filled containers plus the original P7 slot-zero empty reward" });
        }
        string wand = Text(initial["Inventory"], "SelectedWandInstanceId");
        var card = Objects(initial["Inventory"], "Cards").SingleOrDefault(c => Text(c, "ParentWandInstanceId") == wand)
            ?? throw new CaseFailure("StarterCardNotFound");
        string cardId = Text(card, "InstanceId");
        Require(Number(card, "RemainingCharges") == 30, "StarterCardIsNot30");
        await Badge(cardId, 30);
        for (int count = 29; count >= 28; count--)
        {
            Stage("CastTo" + count); await Issue(new JsonObject { ["Action"] = "ui-close" });
            ulong priorPages = Number(await ReadDs(), "InventorySentPages");
            await Issue(new JsonObject { ["Action"] = "cast" });
            var cast = await WaitClient(r => CardCount(r, cardId) == count
                && Text(r["LastProbeCast"], "Result") == "Released", "CastInstalled" + count, 15);
            // Resource开关只附加WriteState内容，并不增加写盘时钟。未启用其他N5诊断时，
            // DS从真实15秒Heartbeat更新state；留25秒避免把正常采样间隔判成Owner发送故障。
            await WaitDs(r => DsCardCount(r, cardId) == count && Number(r, "InventorySentPages") > priorPages, "DsCastOwnerDelivery" + count, 25);
            Record("CastInstalled" + count, cast); await Badge(cardId, count);
        }
        bool canMoveCards = options.Dungeon != "home" || await HomeResearchCommand(16, true);
        if (canMoveCards)
        {
            await UiMove(cardId, "SpellStorage", null, 0, null, r => Location(r, cardId) == 7);
            await UiMove(cardId, "ProgramSlot", wand, 0, null, r => Location(r, cardId) == 2 && Owner(r, cardId) == wand);
            if (options.Dungeon == "home") await HomeResearchCommand(17, false);
        }
        _checks.Add(new { Stage = "CardMovementCoverage", Covered = canMoveCards,
            Reason = canMoveCards ? "Original UI callbacks and real receipts" : "Home spawn is outside the original research area; no movement or permission bypass" });
        await Badge(cardId, 28);
        if (!options.CoreOnly && options.Dungeon == "p7")
        {
            string bag = Text(Objects(_lastClient!["Inventory"], "Items").Single(i => Number(i, "LocationKind") == 4), "InstanceId");
            var equipped = Objects(_lastClient!["Inventory"], "Items").Where(i => Flag(i, "HasContainerState") && Number(i, "LocationKind") == 8)
                .OrderBy(i => Number(i, "Slot")).Select(i => (Id: Text(i, "InstanceId"), Slot: (int)Number(i, "Slot"))).ToArray();
            Require(equipped.Length == 4 && Objects(_lastClient!["Inventory"], "Items").Count(i => Number(i, "LocationKind") == 3) == 2,
                "NativeOwnerDidNotInstallTwoWandsFourContainers");
            string bottle = equipped[0].Id;
            string secondWand = Text(Objects(_lastClient!["Inventory"], "Items").Single(i => Number(i, "LocationKind") == 3 && Number(i, "Slot") == 1), "InstanceId");
            // 用户现场的关键差异是0号杖程序为空、1号杖装有Fire。先完成原两次施法与卡片往返，
            // 再沿原UI迁移同一张剩28次的真实卡，避免只用“首杖可施法”的组合掩盖切换/回退问题。
            await UiMove(cardId, "ProgramSlot", secondWand, 0, null, r => Location(r, cardId) == 2
                && Owner(r, cardId) == secondWand && CardCount(r, cardId) == 28
                && !Objects(r["Inventory"], "Cards").Any(value => Text(value, "ParentWandInstanceId") == wand));
            _checks.Add(new { Stage = "EmptyFirstWandProgramWithFireOnSecond", Passed = true, EmptyProgramWandId = wand,
                FireWandId = secondWand, FireCardId = cardId, RemainingCharges = 28,
                Covered = "Original UI moved the same consumed Fire card from wand slot zero to wand slot one; no new card or charge refill" });
            Record("SecondWandFireProgramInstalled", _lastClient!);
            await UiMove(wand, "Backpack", bag, 0, new JsonObject { ["GridX"] = 0, ["GridY"] = 0 }, r => Location(r, wand) == 1 && Owner(r, wand) == bag);
            await UiMove(wand, "EquippedWand", null, 0, null, r => Location(r, wand) == 3);
            await UiMove(bottle, "Backpack", bag, 0, new JsonObject { ["GridX"] = 4, ["GridY"] = 4 }, r => Location(r, bottle) == 1);
            await UiMove(bottle, "Shortcut", null, 0, null, r => Location(r, bottle) == 8);
            await UiMove(bottle, "Ground", null, 0, null, r => Location(r, bottle) == -1);
            await UiMove(bottle, "Backpack", bag, 0, new JsonObject { ["GridX"] = 4, ["GridY"] = 4 }, r => Location(r, bottle) == 1);
            await UiMove(bottle, "Shortcut", null, 0, null, r => Location(r, bottle) == 8);
            await Issue(new JsonObject { ["Action"] = "ui-close" });
            // 移走再装备0号杖可能合法保留1号杖选择；明确通过Driver选择0号空程序杖，
            // 才能把前三次Throw的回退断言绑定到实际事前选择，而不是工具自己假定的槽号。
            if (Text(_lastClient!["Inventory"], "SelectedItemInstanceId") != wand)
                await Inventory(new JsonObject { ["Kind"] = 1, ["ItemId"] = wand, ["TargetSlot"] = 0 },
                    r => Text(r["Inventory"], "SelectedItemInstanceId") == wand && Signed(r["Inventory"], "SelectedHeldSlot") == 0);
            for (int index = 0; index < equipped.Length; index++)
            {
                // 前三次保留两根装备杖，第四次让0号槽为空且持1号杖，覆盖用户配置中的非首槽回退。
                if (index == equipped.Length - 1)
                {
                    await UiMove(wand, "Backpack", bag, 0, new JsonObject { ["GridX"] = 0, ["GridY"] = 0 }, r => Location(r, wand) == 1);
                    await Issue(new JsonObject { ["Action"] = "ui-close" });
                    if (Text(_lastClient!["Inventory"], "SelectedItemInstanceId") != secondWand)
                        await Inventory(new JsonObject { ["Kind"] = 1, ["ItemId"] = secondWand, ["TargetSlot"] = 1 },
                            r => Text(r["Inventory"], "SelectedItemInstanceId") == secondWand);
                }
                var current = equipped[index];
                await Inventory(new JsonObject { ["Kind"] = 3, ["ItemId"] = current.Id, ["TargetSlot"] = current.Slot },
                    r => Signed(r["Inventory"], "SelectedHeldSlot") == current.Slot + 3 && Text(r["Inventory"], "SelectedItemInstanceId") == current.Id);
                await ThrowSelectedContainer(current.Id, current.Slot, index == equipped.Length - 1 ? secondWand : wand,
                    index == equipped.Length - 1 ? 1 : 0);
                thrownContainers.Add(current.Id);
            }
            await WaitForContainerImpacts(thrownContainers);
            await UiMove(wand, "EquippedWand", null, 0, null, r => Location(r, wand) == 3);
            await Issue(new JsonObject { ["Action"] = "ui-close" });
            if (Text(_lastClient!["Inventory"], "SelectedItemInstanceId") == wand)
                await Inventory(new JsonObject { ["Kind"] = 1, ["ItemId"] = secondWand, ["TargetSlot"] = 1 },
                    r => Text(r["Inventory"], "SelectedItemInstanceId") == secondWand);
            await Inventory(new JsonObject { ["Kind"] = 1, ["ItemId"] = wand, ["TargetSlot"] = 0 },
                r => Signed(r["Inventory"], "SelectedHeldSlot") == 0 && Text(r["Inventory"], "SelectedItemInstanceId") == wand
                    && Flag(r["Inventory"], "SelectedWandResourcesAvailable"));
            await Badge(cardId, 28);
        }
        var exitedProfile = await ExitAndVerify(cardId, 28);
        if (thrownContainers.Count != 0)
        {
            Require(exitedProfile.Items.All(item => !thrownContainers.Contains(item.InstanceId)), "ThrownContainerRevivedAfterExit");
            _checks.Add(new { Stage = "ThrownContainerFinalProfile", Passed = true, ItemIds = thrownContainers.ToArray(),
                Covered = "Original Driver throw intent, confirmed Owner removal and reliable exit Profile; no hardware G input" });
        }
        if (options.Dungeon == "home") await ReenterHome(cardId, wand, exitedProfile);
    }

    private async Task ThrowSelectedContainer(string bottle, int toolSlot, string wand, int wandSlot)
    {
        Stage("ContainerThrowOwnerInstall");
        var before = _lastClient ?? throw new CaseFailure("OwnerReportMissingBeforeThrow");
        Require(Text(before["Inventory"], "SelectedItemInstanceId") == bottle, "ContainerNotSelectedBeforeThrow");
        ulong sequence = Number(before, "ClientInventorySequence");
        ulong priorCommand = Number(before["LastInventoryIntent"], "CommandId");
        await Issue(new JsonObject { ["Action"] = "container-throw" });
        var after = await WaitClient(r => CommandCommitted(r, priorCommand, sequence, value =>
            Location(value, bottle) == -1 && Signed(value["Inventory"], "SelectedHeldSlot") == wandSlot
            && Text(value["Inventory"], "SelectedItemInstanceId") == wand
            && Flag(value["Inventory"], "SelectedWandResourcesAvailable")
            && HudSlotCleared(value, toolSlot + 3) && HudSelectionInstalled(value, wandSlot, wand)), "ContainerThrowOwnerAndHudInstalled", 15);
        Require(Number(after["LastInventoryIntent"], "Kind") == 9 && Text(after["LastInventoryIntent"], "ItemId") == bottle,
            "ContainerThrowDidNotUseTheOriginalInstanceIntent");
        Require(Signed(after, "LocalInventoryDriverId") != 0 && Signed(after, "LastInventoryCallerId") == Signed(after, "LocalInventoryDriverId"),
            "ContainerThrowDidNotUseTheOriginalDriverCaller");
        Require(!Objects(after["Inventory"], "Items").Any(item => Number(item, "LocationKind") == 8 && Number(item, "Slot") == (ulong)toolSlot),
            "ThrownToolSlotStillContainsAnOwnerItem");
        Require(Flag(after, "CanAdvance"), "ThrowFaultedTheAuthorityWorld");
        Record("ContainerThrowOwnerInstalled", after);
    }

    private static bool HudSelectionInstalled(JsonObject report, int slot, string itemId)
    {
        var hud = report["HeldHud"];
        if (!Flag(hud, "Found") || !Flag(hud, "CanvasVisible") || !Flag(hud, "RenderedSourceMatches")
            || !Flag(hud, "SelectedFramePresent")) return false;
        var cells = Objects(hud, "Cells").ToArray();
        var selected = cells.SingleOrDefault(cell => Signed(cell, "Slot") == slot);
        return selected != null && Text(selected, "ItemId") == itemId && Flag(selected, "IconFound")
            && Flag(selected, "IconEnabled") && Flag(selected, "IconActive") && Flag(selected, "IconSpritePresent")
            && Flag(selected, "FrameFound") && Flag(selected, "FrameEnabled") && Flag(selected, "FrameActive") && Flag(selected, "FrameSelected")
            && cells.Count(cell => Flag(cell, "FrameSelected")) == 1;
    }

    private static bool HudSlotCleared(JsonObject report, int slot)
    {
        var hud = report["HeldHud"];
        if (!Flag(hud, "Found") || !Flag(hud, "CanvasVisible") || !Flag(hud, "RenderedSourceMatches")) return false;
        var cell = Objects(hud, "Cells").SingleOrDefault(value => Signed(value, "Slot") == slot);
        return cell != null && Text(cell, "ItemId").Length == 0 && Flag(cell, "IconFound")
            && !Flag(cell, "IconEnabled") && !Flag(cell, "IconSpritePresent") && !Flag(cell, "FrameSelected");
    }

    /// <summary>
    /// Owner移除只证明离手，不能冒充已经落地破碎。以业务已经接受的完整Head与退役索引作只读证据，
    /// 等四瓶都离开Flights且有可靠退役，再等Client安装该Head后的库存版本；超时保留现场并失败。
    /// RetiredItems只含尚未确认的本次墓碑，因此跨多个Head必须查询持久身份索引，不能要求四瓶同批出现。
    /// </summary>
    private async Task WaitForContainerImpacts(List<string> bottles)
    {
        Stage("ContainerImpactsConfirmed");
        await using var database = new MySqlConnection(_connection);
        await database.OpenAsync(_deadline);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < 40)
        {
            _deadline.ThrowIfCancellationRequested();
            string? raw = await database.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                "SELECT PayloadJson FROM RunAssetCheckpointHeads WHERE RunId=@Run AND InstanceId=@Instance",
                new { Run = _run, Instance = _instance }, cancellationToken: _deadline));
            var head = raw == null ? null : JsonSerializer.Deserialize<OnlineRunAssetCheckpoint>(raw, Program.Json);
            int retired = await database.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM RunAssetCheckpointIdentities WHERE RunId=@Run AND InstanceId IN @Ids AND IsRetired=TRUE",
                new { Run = _run, Ids = bottles.ToArray() }, cancellationToken: _deadline));
            if (retired == bottles.Count && head?.Flights != null && head.Flights.All(flight => !bottles.Contains(flight.Item.InstanceId)))
            {
                Require(head.LostMaterials != null && !head.LostMaterials.Any(lost => lost.PlayerId == _player
                    && lost.Source == OnlineRunAssetCheckpointLostMaterial.ContainerTransitSource && lost.Quantity != 0),
                    "ContainerExpiredOrLostInsteadOfDepositing");
                var owner = head.Members.Single(member => member.PlayerId == _player);
                Require(owner.Assets.Items.All(item => !bottles.Contains(item.InstanceId)), "RetiredContainerStillInCheckpointOwner");
                var report = await WaitClient(r => Number(r, "ClientInventoryVersion") >= owner.Assets.InventoryVersion
                    && bottles.All(id => Location(r, id) == -1), "AfterImpactOwnerInstalled", 15);
                _checks.Add(new { Stage = "ContainerImpactCheckpoint", Passed = true, head.CheckpointId, head.Sequence,
                    owner.Assets.InventoryVersion, RetiredCount = retired, NoContainerTransitLoss = true });
                Record("AfterImpactOwnerInstalled", report); return;
            }
            await Task.Delay(250, _deadline);
        }
        throw new CaseFailure("ContainerImpactCheckpointUnconfirmed");
    }

    private async Task<OnlineProfileReply> ExitAndVerify(string cardId, int charges)
    {
        Stage("NormalEscExit"); await Issue(new JsonObject { ["Action"] = "ui-close" });
        await Issue(new JsonObject { ["Action"] = "exit" });
        var finalClient = await WaitClient(r => Text(r["LastRunExitStatus"], "Phase") == "Committed", "ExitCommitted", 35);
        Require(Text(finalClient["LastRunExitStatus"], "Reason") == "VoluntaryExit", "ExitWasNotVoluntary");
        var receipt = await Get<OnlineRunMemberExitReceipt>($"/api/online/runs/{_run}/member-exit");
        Require(receipt.Exited && receipt.Reason == OnlineRunMemberExitReason.VoluntaryExit && receipt.RunId == _run
            && receipt.InstanceId == _instance && receipt.PlayerId == _player && !receipt.SuccessfulExtraction
            && receipt.SettlementId == Text(finalClient["LastRunExitStatus"], "CommandId")
            && receipt.PayloadHash == Text(finalClient["LastRunExitStatus"], "PayloadHash"), "FinalExitReceiptMismatch");
        var profile = await Get<OnlineProfileReply>("/api/online/profile");
        Require(profile.Version == receipt.CommittedVersion, "FinalProfileVersionMismatch");
        Require(profile.Items.SingleOrDefault(i => i.InstanceId == cardId)?.RemainingCharges == charges, "FinalProfileDidNotPersistCharges");
        if (options.Dungeon == "home")
            Require(profile.PracticeSessionScope == _practiceScope && receipt.PracticeSessionScope == _practiceScope
                && !string.IsNullOrEmpty(receipt.PracticeContinuityHash), "HomeExitSessionScopeOrContinuityMismatch");
        await using (var database = new MySqlConnection(_connection))
        {
            await database.OpenAsync(_deadline);
            var persisted = await database.QuerySingleAsync<ExitDatabaseState>(new CommandDefinition("""
                SELECT m.State MemberState,c.Version PersistentVersion,x.RequestHash,
                  (SELECT COUNT(*) FROM AssetLeases a WHERE a.RunId=m.RunId AND a.CharacterId=m.CharacterId) RemainingLeases
                FROM RunMembers m JOIN Characters c ON c.CharacterId=m.CharacterId
                JOIN RunMemberExitReceipts x ON x.RunId=m.RunId AND x.CharacterId=m.CharacterId
                WHERE m.RunId=@Run AND m.CharacterId=@Player
                """, new { Run = _run, Player = _player }, cancellationToken: _deadline));
            Require(persisted.MemberState == "Exited" && persisted.RemainingLeases == 0
                && persisted.PersistentVersion == receipt.CommittedVersion && persisted.RequestHash == receipt.PayloadHash, "PersistedExitStateMismatch");
            await Save("exit-database-state", JsonSerializer.SerializeToNode(persisted, Program.Json)!);
        }
        await Save("final-receipt", JsonSerializer.SerializeToNode(receipt, Program.Json)!);
        Record("NormalEscExit", finalClient);
        return profile;
    }

    private async Task ReenterHome(string cardId, string wand, OnlineProfileReply exitedProfile)
    {
        Stage("HomeSameSessionReentry");
        Require(exitedProfile.PracticeSessionScope == _practiceScope, "HomeReentryScopeChanged");
        string previousRun = _run, previousInstance = _instance;
        await SaveLastEvidence();
        // 前一个Client是本工具直接持有句柄的子进程；其DS仍走原终态Head/Complete流程。
        // 不注销/重登，不更换Bearer，不调用数据库解锁或给第二局补发任何物品。
        Require(StopOwnedClient(), "OwnedPriorClientDidNotStop");
        var profile = await Get<OnlineProfileReply>("/api/online/profile");
        Require(profile.PracticeSessionScope == _practiceScope && profile.PlayerId == _player
            && profile.Items.SingleOrDefault(i => i.InstanceId == cardId)?.RemainingCharges == 28, "HomeReentryProfileMismatch");
        // 第一局可能还在发布终态Head。只换到另一个真正空闲UDP端口，并按既有预留数留一席；
        // 不抢占旧DS端口、不停旧实例、不回收其他测试记录。
        int port = await FreeUdpPort();
        var instances = _factory!.Services.GetRequiredService<OnlineOptions>().Instances;
        instances.FirstPort = instances.LastPort = port; instances.MaximumInstances = _instanceCapacity;
        await EnterAndStart(profile);
        Require(_run != previousRun && _instance != previousInstance, "HomeReentryDidNotCreateNewRun");
        Require(CardCount(_lastClient!, cardId) == 28 && Text(_lastClient!["Inventory"], "SelectedWandInstanceId") == wand
            && Owner(_lastClient!, cardId) == wand, "HomeReentryRefilledOrReplacedCard");
        await WaitDs(r => DsCardCount(r, cardId) == 28 && Number(r, "InventorySentPages") > 0, "HomeReentryOwnerDelivery", 25);
        await Badge(cardId, 28);
        _checks.Add(new { Stage = "HomeSameSessionReentry", Passed = true, SameScope = true, SameCard = true,
            Charges = 28, ManaCooldownContinuityAsserted = false });
        Record("HomeSameSessionReentry", _lastClient!);
        await ExitAndVerify(cardId, 28);
    }

    private async Task Badge(string id, int count)
    {
        Stage("RenderedBadge" + count); await Issue(new JsonObject { ["Action"] = "ui-open" });
        var report = await WaitClient(r => Objects(r["Ui"], "RenderedCardBadges").Any(b => Text(b, "ItemId") == id
            && Flag(b, "Visible") && Text(b, "ActualCaption") == count.ToString() && Number(b, "BoundRemainingCharges") == (ulong)count), "RenderedBadge" + count, 10);
        Require(Objects(report["Ui"], "CardBadges").Any(b => Text(b, "ItemId") == id && Text(b, "ExpectedCaption") == count.ToString()), "ExpectedBadgeMismatch");
        Record("RenderedBadge" + count, report);
    }

    private async Task UiMove(string id, string target, string? targetId, int slot, JsonObject? intent, Func<JsonObject, bool> installed)
    {
        Stage("UiMove" + target);
        // 先走原OpenBackpack，再取本次移动的水位。投掷会关闭UI，后续移杖/重装备不能借上一次
        // Badge碰巧打开的Canvas；Probe的IsOpen Gate保持原样，真实拖放仍必须从已打开的UI发起。
        await Issue(new JsonObject { ["Action"] = "ui-open" });
        // 正式根目标由View传ReadOnly.ActorId（业务PlayerId），不是网络Actor席号。
        // Probe复用相同公开回调时也必须带这份真实Owner；父实例目标与地面语义保持原样。
        if (string.IsNullOrEmpty(targetId) && (target is "SpellStorage" or "EquippedWand" or "EquippedBackpack")) targetId = _player;
        var before = _lastClient!; ulong sequence = Number(before, "ClientInventorySequence");
        ulong previousCommand = Number(before["LastInventoryIntent"], "CommandId");
        await Issue(new JsonObject { ["Action"] = "ui-drag", ["ItemId"] = id, ["TargetKind"] = target,
            ["TargetId"] = targetId, ["TargetSlot"] = slot, ["Intent"] = intent });
        var completed = await WaitClient(r => CommandCommitted(r, previousCommand, sequence, installed), "UiMove" + target, 15);
        Require(Text(completed["LastInventoryIntent"], "ItemId") == id, "UiMovedDifferentInstance");
        Record("UiMove" + target, completed);
    }
    private async Task Inventory(JsonObject intent, Func<JsonObject, bool> installed)
    {
        Stage("InventorySelection"); ulong sequence = Number(_lastClient, "ClientInventorySequence");
        ulong previousCommand = Number(_lastClient?["LastInventoryIntent"], "CommandId");
        await Issue(new JsonObject { ["Action"] = "inventory", ["Intent"] = intent });
        bool selection = Number(intent, "Kind") is 1 or 3;
        int heldSlot = (int)Number(intent, "TargetSlot") + (Number(intent, "Kind") == 3 ? 3 : 0);
        var report = await WaitClient(r => CommandCommitted(r, previousCommand, sequence,
            value => installed(value) && (!selection || HudSelectionInstalled(value, heldSlot, Text(intent, "ItemId")))), "InventorySelectionAndHud", 15);
        if (selection)
            Require(Signed(report, "LocalInventoryDriverId") != 0 && Signed(report, "LastInventoryCallerId") == Signed(report, "LocalInventoryDriverId"),
                "HeldSelectionDidNotUseTheOriginalDriverCaller");
        Record("InventorySelection", report);
    }
    private async Task<bool> HomeResearchCommand(int kind, bool allowNotOwned)
    {
        Stage(kind == 16 ? "HomeOriginalResearchGate" : "HomeEndResearch");
        ulong previous = Number(_lastClient?["LastInventoryIntent"], "CommandId");
        ulong sequence = Number(_lastClient, "ClientInventorySequence");
        await Issue(new JsonObject { ["Action"] = "inventory", ["Intent"] = new JsonObject { ["Kind"] = kind } });
        var report = await WaitClient(r => Number(r["LastInventoryIntent"], "CommandId") != previous
            && Number(r["LastInventoryIntent"], "CommandId") != 0 && r["LastMatchingInventoryReceipt"] != null
            && Number(r["LastMatchingInventoryReceipt"], "CommandId") == Number(r["LastInventoryIntent"], "CommandId"), "HomeResearchReceipt", 15);
        var receipt = report["LastMatchingInventoryReceipt"];
        if (!Flag(receipt, "Success"))
        {
            if (!allowNotOwned || Text(receipt, "Failure") != "NotOwned") throw new CaseFailure("HomeResearchReceipt" + Text(receipt, "Failure"));
            // 原Home首席出生x=0；作者研究Box中心x=6、尺寸4，未移动时应由DS拒绝。
            // 这里只把NotOwned当空间Gate控制例，绝不能把它记作卡片已装拆。
            _checks.Add(new { Stage = "HomeOriginalResearchAreaGate", Passed = true, ExpectedFailure = "NotOwned", CardMovementCovered = false });
            Record("HomeResearchNotOwnedControl", report); return false;
        }
        report = await WaitClient(r => Number(r, "ClientInventorySequence") > sequence, "HomeResearchStateInstalled", 15);
        Record(kind == 16 ? "HomeResearchOpened" : "HomeResearchClosed", report); return true;
    }
    private static bool CommandCommitted(JsonObject report, ulong previous, ulong sequence, Func<JsonObject, bool> installed)
    {
        var intent = report["LastInventoryIntent"]; var receipt = report["LastMatchingInventoryReceipt"];
        ulong command = Number(intent, "CommandId");
        if (command == 0 || command == previous || receipt == null || Number(receipt, "CommandId") != command) return false;
        if (!Flag(receipt, "Success")) throw new CaseFailure("InventoryReceipt" + Text(receipt, "Failure"));
        return Number(report, "ClientInventorySequence") > sequence && Number(report, "ClientInventoryVersion") >= Number(receipt, "InventoryVersion") && installed(report);
    }

    private async Task<JsonObject> Issue(JsonObject command)
    {
        string before = Text(_lastClient, "LastActionAtUtc");
        Require(!File.Exists(_command), "PreviousCommandStillPending");
        string temp = Path.Combine(_private, "command.tmp");
        await File.WriteAllTextAsync(temp, command.ToJsonString(Program.Json), _deadline); File.Move(temp, _command);
        var report = await WaitClient(r => Text(r, "LastAction") == Text(command, "Action") && Text(r, "LastActionAtUtc") != before && !File.Exists(_command), "Command" + Text(command, "Action"), 8);
        if (Text(report, "LastActionStatus").StartsWith("Rejected:", StringComparison.Ordinal)) throw new CaseFailure("ProbeRejected" + Text(command, "Action"));
        return report;
    }

    private async Task<JsonObject> WaitClient(Func<JsonObject, bool> predicate, string name, int seconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            _deadline.ThrowIfCancellationRequested();
            if (_client != null && _client.HasExited) throw new CaseFailure("NativeClientExited" + _client.ExitCode);
            var report = await ReadJson(_clientReport);
            if (report != null)
            {
                _lastClient = report;
                if (Text(report["LastRunExitStatus"], "Reason") == "FinalDeath") throw new CaseFailure("ActualPlayerDiedDuringRegression");
                if (predicate(report)) return report;
            }
            await Task.Delay(200, _deadline);
        }
        throw new CaseFailure(name + "TimedOut");
    }
    private async Task<JsonObject> WaitDs(Func<JsonObject, bool> predicate, string name, int seconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            var state = await ReadDs(); if (predicate(state)) return state;
            await Task.Delay(200, _deadline);
        }
        throw new CaseFailure(name + "TimedOut");
    }
    private async Task<JsonObject> ReadDs()
    {
        var state = await ReadJson(Path.Combine(_workRoot, _instance, "state.json"));
        if (state != null) _lastDs = state;
        return state ?? _lastDs ?? new JsonObject();
    }
    private static async Task<JsonObject?> ReadJson(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(await File.ReadAllTextAsync(path)) as JsonObject : null; }
        catch (IOException) { return null; }
        catch (JsonException) { return null; } // Unity原报告是覆盖写，读到写入间隙不算一次协议失败。
    }
    private async Task<T> Post<TRequest, T>(string route, TRequest? request)
    {
        using var response = request is null ? await _http!.PostAsync(route, null, _deadline)
            : await _http!.PostAsJsonAsync(route, request, Program.Json, _deadline);
        await RequireHttp(response);
        return (await response.Content.ReadFromJsonAsync<T>(Program.Json, _deadline))!;
    }
    private async Task<T> Get<T>(string route)
    {
        using var response = await _http!.GetAsync(route, _deadline); await RequireHttp(response);
        return (await response.Content.ReadFromJsonAsync<T>(Program.Json, _deadline))!;
    }
    private static async Task RequireHttp(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        string code = "";
        try { code = (await response.Content.ReadFromJsonAsync<OnlineErrorReply>(Program.Json))?.Code ?? ""; } catch (JsonException) { }
        throw new CaseFailure("Http" + (int)response.StatusCode + code);
    }
    private async Task<int> FreeUdpPort()
    {
        await using var database = new MySqlConnection(_connection); await database.OpenAsync(_deadline);
        bool hasInstances = await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='ServerInstances'") != 0;
        int reserved = hasInstances ? await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ServerInstances WHERE ActivePort IS NOT NULL") : 0;
        Require(reserved < 32, "ExistingTestReservationsFillInstanceCapacity");
        // 分配器容量统计整个test Schema。只给本轮多留一席，不扫描/回收历史预留，不启动历史Run。
        _instanceCapacity = reserved + 1;
        for (int attempt = 0; attempt < 50; attempt++)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); int port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            if (port is 5080 or 13384 || port < 1024) continue;
            if (!hasInstances || await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ServerInstances WHERE ActivePort=@Port", new { Port = port }) == 0) return port;
        }
        throw new CaseFailure("NoUnclaimedLoopbackUdpPort");
    }
    private Task WritePrivate(string name, object value) => File.WriteAllTextAsync(Path.Combine(_private, name), JsonSerializer.Serialize(value, Program.Json), _deadline);
    private async Task Save(string name, JsonNode value)
        => await File.WriteAllTextAsync(Path.Combine(options.Output, $"{++_artifact:00}-{name}.json"), value.ToJsonString(Program.Json));
    private void Record(string name, JsonObject client)
    {
        _checks.Add(new { Stage = name, Passed = true, InventoryVersion = Number(client, "ClientInventoryVersion"), SnapshotSequence = Number(client, "ClientInventorySequence") });
        Save(name + "-client", client).GetAwaiter().GetResult();
        var ds = ReadDs().GetAwaiter().GetResult(); Save(name + "-ds", ds).GetAwaiter().GetResult();
    }
    private async Task SaveLastEvidence()
    {
        try
        {
            // single-entry可能已创建实例但在返回RoomReply前启动失败。只读本轮新账号的
            // 已登记InstanceId补齐现场路径，不枚举或接管其他测试的工作目录。
            if (string.IsNullOrEmpty(_instance) && !string.IsNullOrEmpty(_connection) && !string.IsNullOrEmpty(_player))
            {
                await using var database = new MySqlConnection(_connection);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await database.OpenAsync(timeout.Token);
                _instance = await database.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
                    SELECT s.InstanceId FROM ServerInstances s JOIN RunMembers m ON m.RunId=s.RunId
                    JOIN Runs r ON r.RunId=s.RunId WHERE m.CharacterId=@Player ORDER BY r.CreatedAtUtc DESC LIMIT 1
                    """, new { Player = _player }, cancellationToken: timeout.Token)) ?? "";
            }
            var client = await ReadJson(_clientReport) ?? _lastClient; if (client != null) await Save("last-client", client);
            var ds = !string.IsNullOrEmpty(_instance) ? await ReadDs() : _lastDs; if (ds != null) await Save("last-ds", ds);
            if (!string.IsNullOrEmpty(_instance) && _nativeInstances.All(value => value.Id != _instance)) _nativeInstances.Add((_instance, _entryOrdinal));
            foreach (var owned in _nativeInstances)
            {
                string log = Path.Combine(_workRoot, owned.Id, "server.log");
                if (!File.Exists(log)) continue;
                Directory.CreateDirectory(Path.Combine(options.Output, "native"));
                using var source = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                string name = owned.Entry <= 1 ? "server.log" : $"server-{owned.Entry:00}.log";
                using var target = File.Create(Path.Combine(options.Output, "native", name)); await source.CopyToAsync(target);
            }
        }
        catch (Exception error) { _checks.Add(new { Stage = "CaptureFailureEvidence", Passed = false, Failure = error.GetType().Name }); }
    }
    private bool StopOwnedClient()
    {
        if (_client == null) return true;
        bool stopped = false;
        try
        {
            stopped = _client.HasExited;
            if (!stopped) { _client.Kill(); stopped = _client.WaitForExit(5000); }
            if (!stopped) { _checks.Add(new { Stage = "StopOwnedClient", Passed = false, Failure = "ExitTimeout" }); _passed = false; }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        { _checks.Add(new { Stage = "StopOwnedClient", Passed = false, Failure = error.GetType().Name }); _passed = false; }
        finally { _client.Dispose(); _client = null; }
        return stopped;
    }
    private async Task StopOwnedDs()
    {
        if (string.IsNullOrEmpty(_connection) || string.IsNullOrEmpty(_player)) return;
        try
        {
            await using var database = new MySqlConnection(_connection); await database.OpenAsync();
            var rows = await database.QueryAsync<OwnInstance>("""
                SELECT s.ProcessId,s.ProcessStartTicks,s.Executable FROM ServerInstances s
                JOIN RunMembers m ON m.RunId=s.RunId WHERE m.CharacterId=@Player
                """, new { Player = _player });
            foreach (var row in rows)
            {
                if (!string.Equals(Path.GetFullPath(row.Executable), options.ServerExe, StringComparison.OrdinalIgnoreCase)) continue;
                var identity = new InstanceProcessIdentity(row.ProcessId, row.ProcessStartTicks, row.Executable);
                InstanceProcesses.StopMatching(identity);
                var wait = Stopwatch.StartNew();
                while (InstanceProcesses.Matches(identity) && wait.Elapsed.TotalSeconds < 3) await Task.Delay(100);
                if (InstanceProcesses.Matches(identity)) throw new CaseFailure("OwnedDsDidNotStop");
            }
        }
        catch (Exception error) { _checks.Add(new { Stage = "StopOwnedDs", Passed = false, Failure = error.GetType().Name }); _passed = false; }
    }
    private sealed class OwnInstance { public int ProcessId { get; set; } public long ProcessStartTicks { get; set; } public string Executable { get; set; } = ""; }
    private sealed class ExitDatabaseState
    {
        public string MemberState { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public uint PersistentVersion { get; set; }
        public int RemainingLeases { get; set; }
    }
    private void Stage(string name) { _stage = name; Console.WriteLine(name); }
    private static void Require(bool value, string failure) { if (!value) throw new CaseFailure(failure); }
    private static string Text(JsonNode? node, string key) => node?[key]?.GetValue<string>() ?? "";
    private static ulong Number(JsonNode? node, string key) => node?[key] is JsonValue value && value.TryGetValue<ulong>(out var parsed) ? parsed : 0;
    private static long Signed(JsonNode? node, string key) => node?[key] is JsonValue value && value.TryGetValue<long>(out var parsed) ? parsed : long.MinValue;
    private static bool Flag(JsonNode? node, string key) => node?[key]?.GetValue<bool>() ?? false;
    private static IEnumerable<JsonObject> Objects(JsonNode? node, string key) => (node?[key] as JsonArray)?.OfType<JsonObject>() ?? [];
    private static int CardCount(JsonObject report, string id) => (int)Number(Objects(report["Inventory"], "Cards").FirstOrDefault(i => Text(i, "InstanceId") == id), "RemainingCharges");
    private static int DsCardCount(JsonObject report, string id)
        => Objects(report, "ActorInventories").Where(a => Number(a, "ActorId") == 1).SelectMany(a => Objects(a, "Cards"))
            .Where(c => Text(c, "InstanceId") == id).Select(c => (int)Number(c, "RemainingCharges")).DefaultIfEmpty(-1).Single();
    private static int Location(JsonObject report, string id)
        => Objects(report["Inventory"], "Items").Where(i => Text(i, "InstanceId") == id).Select(i => (int)Number(i, "LocationKind")).DefaultIfEmpty(-1).Single();
    private static string Owner(JsonObject report, string id) => Text(Objects(report["Inventory"], "Items").FirstOrDefault(i => Text(i, "InstanceId") == id), "OwnerId");
}
