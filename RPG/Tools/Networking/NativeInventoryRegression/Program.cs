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
        try
        {
            // 独立的测试租约维护入口：严格限定本轮已正常退出的隔离测试实例，先审计再显式到期。
            // 不经过NativeCase/端口分配，也不放宽正式实例容量或操作玩家dev Schema。
            if(args.Length==2 && args[0]=="--reservation-lease-audit")
                return await ReservationLeaseAudit.RunAsync(args[1]);
            return await new NativeCase(Options.Parse(args)).RunAsync();
        }
        catch (CaseFailure failure) { Console.Error.WriteLine(failure.Code); return 1; }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name); return 1; }
    }
}

internal sealed record Options(string ServerExe, string ClientExe, string Output, string Root, int TimeoutSeconds, bool CoreOnly, string Dungeon, string InputMode, string Scenario)
{
    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        bool core = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--core-only") { core = true; continue; }
            if (args[i] is not ("--server-exe" or "--client-exe" or "--output" or "--repo" or "--timeout-seconds" or "--dungeon" or "--input-mode" or "--scenario") || i + 1 == args.Length)
                throw new CaseFailure("ArgumentsInvalid");
            values.Add(args[i], args[++i]);
        }
        string Need(string key) => values.TryGetValue(key, out var value) ? Path.GetFullPath(value) : throw new CaseFailure("Missing" + key);
        string root = values.TryGetValue("--repo", out var explicitRoot) ? Path.GetFullPath(explicitRoot) : FindRoot();
        int timeout = values.TryGetValue("--timeout-seconds", out var seconds) && int.TryParse(seconds, out int parsed) ? parsed : 240;
        if (timeout is < 60 or > 280) throw new CaseFailure("TimeoutOutside60To280Seconds");
        string dungeon = values.GetValueOrDefault("--dungeon", "p7");
        if (dungeon is not ("p7" or "home")) throw new CaseFailure("DungeonMustBeP7OrHome");
        string inputMode = values.GetValueOrDefault("--input-mode", "keyboard");
        if (inputMode is not ("keyboard" or "direct")) throw new CaseFailure("InputModeMustBeKeyboardOrDirect");
        string scenario = values.GetValueOrDefault("--scenario", "existing");
        if (scenario is not ("existing" or "rapid-inventory" or "rapid-mouse-inventory" or "rapid-mouse-coop" or "ordinary-pickup-movement" or "rapid-long-busy" or "liquid-review-coop")) throw new CaseFailure("UnknownInventoryScenario");
        bool liquidReview = scenario == "liquid-review-coop";
        if (scenario is "rapid-mouse-coop" or "ordinary-pickup-movement" or "rapid-long-busy" or "liquid-review-coop")
        {
            if (!values.ContainsKey("--timeout-seconds")) timeout = 280;
            if (timeout != 280) throw new CaseFailure(scenario == "rapid-long-busy"
                ? "LongBusyRequires280SecondDeadline" : "CoopOrMovementRequires280SecondDeadline");
        }
        if (liquidReview && (core || dungeon != "p7" || inputMode != "direct"))
            throw new CaseFailure("LiquidReviewRequiresFullP7DirectMode");
        if (scenario != "existing" && !liquidReview && (core || dungeon != "p7" || inputMode != "keyboard"))
            throw new CaseFailure("RapidInventoryRequiresFullP7KeyboardMode");
        return new(Need("--server-exe"), Need("--client-exe"), Need("--output"), root, timeout, core, dungeon, inputMode, scenario);
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
    private string _rapidWand = "", _rapidFire = "", _rapidLooseCard = "", _rapidBag = "", _rapidBottle = "";
    private readonly List<string> _rapidModifiers = [], _rapidFailures = [];
    private readonly Dictionary<string, int> _rapidInitialCharges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _rapidEntryCharges = new(StringComparer.Ordinal);
    private readonly List<(string Id, int Entry)> _nativeInstances = [];
    private readonly HashSet<string> _heldSyntheticKeys = new(StringComparer.Ordinal);
    private bool _syntheticPointerHeld;
    private CancellationToken _deadline;
    private PeerContext? _peer;
    private ulong _coopWorldEpoch;
    private uint _coopRoomEpoch;
    private bool IsLiquidReview => options.Scenario == "liquid-review-coop";
    private bool IsCoop => options.Scenario == "rapid-mouse-coop" || IsLiquidReview;
    private bool IsOrdinaryMovement => options.Scenario == "ordinary-pickup-movement";
    private bool IsRapidLongBusy => options.Scenario == "rapid-long-busy";

    /// <summary>
    /// 第二个真实进程有自己的Bearer、命令文件和报告水位。不能临时交换Actor1的全局字段，
    /// 否则并行启动/失败清理可能把A的指令写给B或用A的登录态读B的退出收据。
    /// </summary>
    private sealed class PeerContext(HttpClient http, string directory)
    {
        public HttpClient Http { get; } = http;
        public string Directory { get; } = directory;
        public string Command => Path.Combine(Directory, "command.json");
        public string Report => Path.Combine(Directory, "inventory-report.json");
        public string PlayerId = "", WandId = "", FireId = "", ExitCommand = "";
        public int FireCharges;
        public uint Epoch;
        public Process? Process;
        public JsonObject? LastReport;
        public JsonNode? EntryItems;
        public string[] OriginalItemIds = [];
        public bool ExitVerified;
    }

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
            if (IsLiquidReview)
            {
                Require(Environment.GetEnvironmentVariable("NORN_NATIVE_GRAPHICAL_PROBE") == "1", "LiquidReviewRequiresGraphicalClients");
                string probeDirectory = Environment.GetEnvironmentVariable("NORN_LIQUID_MOTION_PROBE") ?? "";
                Require(!string.IsNullOrWhiteSpace(probeDirectory) && Path.IsPathFullyQualified(probeDirectory)
                    && string.Equals(Path.GetFullPath(probeDirectory), Path.GetFullPath(options.Output), StringComparison.OrdinalIgnoreCase),
                    "LiquidReviewRequiresItsOwnOutputProbeDirectory");
            }
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
            if (IsLiquidReview) await RunLiquidReviewCoop();
            else if (options.Scenario is "rapid-mouse-inventory" or "rapid-mouse-coop")
            {
                await RunRapidMouseInventory();
                if (IsCoop) await ExitCoopPeerAndVerify();
            }
            else if (IsRapidLongBusy) await RunRapidLongBusy();
            else if (options.Scenario == "rapid-inventory") await RunRapidInventory();
            else if (IsOrdinaryMovement) await RunOrdinaryPickupMovement();
            else await RunGameplay();
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
            if (!_passed && !IsCoop && _client != null && !_client.HasExited && _lastClient != null && Flag(_lastClient, "ClientInventoryReady")
                && !(IsRapidLongBusy && Text(_lastClient["LastRunExitStatus"], "Phase") == "Committed"))
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    if (!IsOrdinaryMovement && !IsRapidLongBusy) _deadline = cleanup.Token;
                    if (IsOrdinaryMovement) await WaitClient(r => !Flag(r, "SyntheticMovePulseActive")
                        && Number(r, "SyntheticMoveCompletedSequence") == Number(r, "SyntheticMovePulseSequence"), "CleanupMoveReleased", 4);
                    if (_syntheticPointerHeld) await SyntheticPointer(false);
                    foreach (string key in _heldSyntheticKeys.ToArray()) await SyntheticKey(key, false);
                    // 新长按场景的失败退出仍用同一280秒预算；结果未知时等待原Command，不能另造退出ID。
                    if (!IsRapidLongBusy || string.IsNullOrEmpty(Text(_lastClient["LastRunExitStatus"], "CommandId")))
                        await Issue(new JsonObject { ["Action"] = "exit" });
                    await WaitClient(r => Text(r["LastRunExitStatus"], "Phase") == "Committed", "CleanupExit", 18);
                }
                catch (Exception error) { _checks.Add(new { Stage = "CleanupExit", Passed = false, Failure = error.GetType().Name }); }
            }
            // 合作场景也严格使用原280秒Token；失败清理不追加一轮玩法预算。
            // 超时后可能无法再完成退出，保留失败与现场，随后停止进程绝不记成结算成功。
            if (!_passed && IsCoop) await CleanupCoopExits();
            await SaveLastEvidence();
            await SaveCoopEvidence();
            StopOwnedClient();
            StopOwnedPeer();
            await StopOwnedDs();
            if (_factory != null)
            {
                try { await _factory.DisposeAsync(); }
                catch (Exception error) { _checks.Add(new { Stage = "DisposeOwnedHost", Passed = false, Failure = error.GetType().Name }); _passed = false; }
            }
            _http?.Dispose();
            _peer?.Http.Dispose();
            Environment.SetEnvironmentVariable("NORN_N5_PLAYER_RESOURCE_PROBE", previousProbe);
            // 一次性Ticket/控制文件不作为公开证据；账号密码/Token从未写入文件。
            if (!string.IsNullOrEmpty(_private))
                foreach (string file in new[] { "ticket.json", "probe-config.json", "command.json", "command.tmp", "ui-config.json", "ui-command.ui" })
                {
                    try { string path = Path.Combine(_private, file); if (File.Exists(path)) File.Delete(path); }
                    catch (IOException) { _checks.Add(new { Stage = "RemoveOwnPrivateControl", Passed = false }); }
                }
            if (_peer != null)
                foreach (string file in new[] { "ticket.json", "probe-config.json", "command.json", "command.tmp" })
                {
                    try { string path = Path.Combine(_peer.Directory, file); if (File.Exists(path)) File.Delete(path); }
                    catch (IOException) { _checks.Add(new { Stage = "RemovePeerPrivateControl", Passed = false }); }
                }
            if (!_passed && string.IsNullOrEmpty(_failure)) _failure = "CleanupFailed";
            await File.WriteAllTextAsync(Path.Combine(options.Output, "evidence.json"), JsonSerializer.Serialize(new
            {
                CaseId = _caseId, Passed = _passed, Failure = _failure, Stage = _stage, CoreOnly = options.CoreOnly,
                DungeonId = options.Dungeon, EntryCount = _entryOrdinal, InputMode = options.InputMode, Scenario = options.Scenario,
                PlayerId = _player, PeerPlayerId = _peer?.PlayerId, PeerExitVerified = _peer?.ExitVerified,
                RunId = _run, InstanceId = _instance, Checks = _checks,
                HostErrors = _factory?.Logs.Errors.ToArray(), RecordedAtUtc = DateTime.UtcNow,
                Scope = "Real loopback Kestrel/MySQL/Native DS/Native Client and original UI callbacks; "
                    + (IsCoop ? "two independently authenticated Native Clients in one private cooperative room; Actor2 has no synthetic-input permission; actual idle and exit results are recorded in Checks; " : "")
                    + (IsLiquidReview ? "graphical liquid review only; no F, Mouse, Tab or container-input coverage; physical input is not masked and this is not a controlled performance A/B; " : "")
                    + (IsRapidLongBusy ? "real Mouse held through low-mana EmptyOutput, live Revision2 reprogram, two Tab release orders and strict MoveOwned recovery; " : "")
                    + (options.InputMode == "keyboard" ? "keyboard mode routes held selection, container G and ground F through virtual Keyboard and real InputActions when exercised; "
                        : "explicit direct fallback uses original request methods and does not cover keyboard; ")
                    + (IsOrdinaryMovement ? "bounded real InputSystem WASD movement and actual EventSystem Raycast/Mouse drag are checked; " : "pointer Raycast is not covered; ")
                    + "hardware/OS input and subjective combat feel are not covered; individual executed paths are recorded in Checks"
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
        if (options.Scenario is "rapid-inventory" or "rapid-mouse-inventory" or "rapid-mouse-coop" or "rapid-long-busy" or "liquid-review-coop") profile = await PrepareRapidInventory(manifest, profile);
        else if (options.Scenario == "existing" && !options.CoreOnly && options.Dungeon == "p7") profile = await PrepareTwoWandsThreeAccountContainers(manifest, profile);
        if (IsCoop) await RegisterPeerAndEnterCoop(manifest, profile);
        else await EnterAndStart(profile);
    }

    /// <summary>
    /// 独立复现场景仅复用正式商店与账号移动HTTP。入场即有现场同形程序，
    /// 不复制玩家checkpoint/实例ID、不写SQL，不把新购Fire的12次伪造成现场13次。
    /// 原注册Fire留作附近普通掉落正控制；它与参与施法的购买Fire始终是两个真实实例。
    /// </summary>
    private async Task<OnlineProfileReply> PrepareRapidInventory(ContentManifest manifest, OnlineProfileReply profile)
    {
        Stage("RapidAccountPrepare");
        string starter = profile.CombatAssets.SelectedWandInstanceId;
        _rapidBag = profile.Items.Single(item => item.LocationKind == 4).InstanceId;
        _rapidLooseCard = profile.Items.Single(item => item.ContentId == "wandtest.fire"
            && item.LocationKind == 2 && item.ParentInstanceId == starter).InstanceId;
        long initialGold = profile.Gold;
        long expectedCost = 0;
        string[] purchases = ["norn.v2.wand.0", "norn.v2.damage", "norn.v2.damage", "norn.v2.damage", "norn.v2.damage", "wandtest.fire"];
        for (int index = 0; index < purchases.Length; index++)
        {
            var offer = manifest.Shop.Offers.Single(value => value.ContentId == purchases[index]);
            var shop = await Get<AccountShopReply>("/api/online/shop");
            var priorIds = shop.Profile.Items.Select(item => item.InstanceId).ToHashSet(StringComparer.Ordinal);
            var bought = await Post<AccountShopTradeRequest, AccountShopTradeReply>("/api/online/shop/trade", new()
            { CommandId = Guid.NewGuid().ToString("N"), ShopId = shop.Shop.ShopId, ConfigHash = shop.Shop.ConfigHash,
                ExpectedVersion = shop.Profile.Version, ExpectedShopVersion = shop.Shop.Version, Kind = 0, Quantity = 1,
                OfferId = offer.OfferId, ToBackpack = true, AllowWarehouseFallback = true, BackpackId = _rapidBag });
            Require(bought.Success, "RapidPurchaseRejected");
            profile = await Get<OnlineProfileReply>("/api/online/profile");
            Require(profile.Version >= bought.InventoryVersion, "RapidPurchaseProfileNotConfirmed");
            var added = profile.Items.Single(item => !priorIds.Contains(item.InstanceId) && item.ContentId == offer.ContentId);
            expectedCost = checked(expectedCost + offer.BuyPrice);
            if (index == 0) _rapidWand = added.InstanceId;
            else
            {
                _rapidInitialCharges.Add(added.InstanceId, added.RemainingCharges);
                if (index == 5 && IsRapidLongBusy)
                {
                    // 长按要覆盖低蓝而非卡片耗尽。沿真实账号API把原starter的30次Fire迁入Rapid，
                    // 再把商店12次卡放回starter；既不造次数，也不把耗尽产生的EmptyOutput算低蓝。
                    _rapidFire = _rapidLooseCard; _rapidLooseCard = added.InstanceId;
                    _rapidInitialCharges.Add(_rapidFire, profile.Items.Single(item => item.InstanceId == _rapidFire).RemainingCharges);
                    await Post<InventoryMoveRequest, InventoryMoveReply>("/api/online/inventory/move", new()
                    { CommandId = Guid.NewGuid().ToString("N"), ExpectedVersion = profile.Version, InstanceId = _rapidFire,
                        OperationKind = 1, TargetLocationKind = 2, TargetParentInstanceId = _rapidWand, Slot = 4 });
                    profile = await Get<OnlineProfileReply>("/api/online/profile");
                    Require(profile.Items.Any(item => item.InstanceId == _rapidFire && item.LocationKind == 2
                        && item.ParentInstanceId == _rapidWand && item.Slot == 4
                        && item.RemainingCharges == _rapidInitialCharges[_rapidFire]), "LongBusyOriginalStarterFireNotMoved");
                }
                else if (index == 5) _rapidFire = added.InstanceId;
                else _rapidModifiers.Add(added.InstanceId);
            }
            int target = index == 0 ? 3 : 2, slot = index == 0 ? 0 : index == 5 ? (IsRapidLongBusy ? 0 : 5) : index - 1;
            string parent = index == 0 ? "" : index == 5 && IsRapidLongBusy ? starter : _rapidWand;
            await Post<InventoryMoveRequest, InventoryMoveReply>("/api/online/inventory/move", new()
            { CommandId = Guid.NewGuid().ToString("N"), ExpectedVersion = profile.Version, InstanceId = added.InstanceId,
                OperationKind = 1, TargetLocationKind = target, TargetParentInstanceId = parent, Slot = slot });
            profile = await Get<OnlineProfileReply>("/api/online/profile");
            Require(profile.Items.Any(item => item.InstanceId == added.InstanceId && item.LocationKind == target
                && item.Slot == slot && (target != 2 || item.ParentInstanceId == parent)), "RapidAccountPlacementNotConfirmed");
        }
        await Post<InventoryMoveRequest, InventoryMoveReply>("/api/online/inventory/move", new()
        { CommandId = Guid.NewGuid().ToString("N"), ExpectedVersion = profile.Version, InstanceId = _rapidWand,
            OperationKind = 2, TargetLocationKind = 3, TargetParentInstanceId = "", Slot = 0 });
        profile = await Get<OnlineProfileReply>("/api/online/profile");
        Require(profile.CombatAssets.SelectedWandInstanceId == _rapidWand && profile.Gold == initialGold - expectedCost,
            "RapidSelectedWandOrPurchaseCostMismatch");
        _checks.Add(new { Stage = "RapidFormalAccountLoadout", Passed = true, InitialGold = initialGold, profile.Gold,
            ExpectedCost = expectedCost, WandId = _rapidWand, FireId = _rapidFire, ModifierIds = _rapidModifiers.ToArray(),
            PurchasedInitialCharges = _rapidInitialCharges, LooseCardId = _rapidLooseCard,
            Covered = IsRapidLongBusy
                ? "Original shop and inventory HTTP; four modifiers in slots 0-3, original starter Fire30 in slot4, purchased Fire12 moved into starter; no checkpoint import"
                : "Original shop and inventory HTTP; four modifiers in slots 0-3, slot 4 empty, purchased Fire in slot 5; no checkpoint import" });
        return profile;
    }

    /// <summary>
    /// 先移开Modifier做同一根杖的普通Fire正控制，再装回现场程序。每次真实DS施法后等8秒，
    /// 分别观察普通Move、原Keyboard F和原拖放；拒绝也是证据，不能套只接受成功的CommandCommitted。
    /// 所有命令仍由当前Source生成版本。三轮保留8秒门并给正式退出留下预算，避免首个Busy遮住后续行为。
    /// </summary>
    private async Task RunRapidInventory()
    {
        Stage("RapidOwnerLoadout");
        var initial = _lastClient ?? throw new CaseFailure("RapidOwnerMissing");
        Require(Text(initial["Inventory"], "SelectedWandInstanceId") == _rapidWand, "RapidOwnerSelectedDifferentWand");
        for (int i = 0; i < _rapidModifiers.Count; i++) Require(RapidProgramAt(initial, _rapidModifiers[i], i), "RapidOwnerModifierSlotMismatch");
        Require(RapidProgramAt(initial, _rapidFire, 5) && !Objects(initial["Inventory"], "Cards")
            .Any(card => Text(card, "ParentWandInstanceId") == _rapidWand && Signed(card, "Slot") == 4), "RapidOwnerFireOrGapMismatch");
        // P7入局通过RaidSpellEligibility一次性扣Modifier资格；它不是每次施法耗用卡。
        // 因此必须区分商店8次与入局7次，不能把正式资格扣费误判为补发/丢失。
        foreach (var pair in _rapidInitialCharges)
        {
            int expected = pair.Value - (_rapidModifiers.Contains(pair.Key) ? 1 : 0);
            Require(expected >= 0 && CardCount(initial, pair.Key) == expected, "RapidEntryQualificationChargesMismatch");
            _rapidEntryCharges.Add(pair.Key, CardCount(initial, pair.Key));
        }
        _checks.Add(new { Stage = "RapidRaidEntryQualification", Passed = true,
            PurchasedCharges = _rapidInitialCharges, EntryCharges = _rapidEntryCharges });
        _rapidBottle = Text(Objects(initial["Inventory"], "Items").Single(item => Flag(item, "HasContainerState")
            && Signed(item, "LocationKind") == 8 && Signed(item, "Slot") == 0), "InstanceId");
        Record("RapidInitialInstalledOwner", initial);
        for (int i = 0; i < _rapidModifiers.Count; i++)
        {
            string id = _rapidModifiers[i];
            await UiMove(id, "SpellStorage", null, i, null, r => Location(r, id) == 7 && Owner(r, id) == _player);
        }
        await UiMove(_rapidLooseCard, "SpellStorage", null, 4, null, r => Location(r, _rapidLooseCard) == 7);
        int looseCharges = CardCount(_lastClient!, _rapidLooseCard);
        await RapidDropLooseCard(); // 必须在施法之前准备地面实例，不能在泄漏后靠补发/强制Drop布置测试。
        await RapidCheckGates("BeforeAnyCast", looseCharges);
        if (_rapidFailures.Count != 0) throw new CaseFailure(_rapidFailures[0]);
        int fireBefore = CardCount(_lastClient!, _rapidFire);
        await RapidCast("PlainFireControl");
        await RapidCheckGates("AfterPlainFire", looseCharges);
        if (_rapidFailures.Count != 0) throw new CaseFailure(_rapidFailures[0]);
        Require(CardCount(_lastClient!, _rapidFire) == fireBefore - 1, "RapidPlainFireDidNotConsumeOneCharge");
        foreach (string id in _rapidModifiers) Require(CardCount(_lastClient!, id) == _rapidEntryCharges[id], "RapidControlConsumedDetachedModifier");
        for (int i = 0; i < _rapidModifiers.Count; i++)
        {
            string id = _rapidModifiers[i]; int slot = i;
            await UiMove(id, "ProgramSlot", _rapidWand, slot, null, r => RapidProgramAt(r, id, slot));
        }
        Record("RapidModifierProgramInstalled", _lastClient!);
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            await RapidCast("RapidCast" + attempt);
            await RapidCheckGates("AfterRapidCast" + attempt, looseCharges);
            foreach (string id in _rapidModifiers)
                Require(CardCount(_lastClient!, id) == _rapidEntryCharges[id], "RapidCastConsumedQualificationAgain");
        }
        _checks.Add(new { Stage = "RapidInventorySummary", Passed = _rapidFailures.Count == 0, Failures = _rapidFailures.ToArray(),
            RapidCastAttempts = 3, SettleSecondsPerGate = 8,
            Covered = "Formal DS Combat SendIntent and original UI open/close lifecycle; virtual Keyboard F, original UI move/drop/pickup callbacks",
            NotCovered = "Mouse/Driver attack, physical Tab key, movement, authored Ice at (-16,0,25), pointer Raycast and hardware input" });
        if (_rapidFailures.Count != 0) throw new CaseFailure(_rapidFailures[0]);
        await ExitAndVerify(_rapidFire, CardCount(_lastClient!, _rapidFire));
    }

    private bool RapidProgramAt(JsonObject report, string id, int slot)
        => Objects(report["Inventory"], "Cards").Any(card => Text(card, "InstanceId") == id
            && Text(card, "ParentWandInstanceId") == _rapidWand && Signed(card, "Slot") == slot && Signed(card, "LocationKind") == 2);

    /// <summary>
    /// 图形液体观察与输入验收分开：两人仍经原账户/房间/Ticket入场，但不排任何虚拟键鼠事件。
    /// 外部Start-NativeSample继续同步开启既有10秒Probe；这里只等自有进程的完整输出，再走原退出。
    /// direct不是物理输入隔离：不改玩家focus门，也不屏蔽OS输入。发现真实Driver/资产污染仍失败，
    /// 即使末态看起来空闲，也不能把这一轮称作受控性能A/B或F拾取已通过。
    /// </summary>
    private async Task RunLiquidReviewCoop()
    {
        Stage("GraphicalLiquidReview");
        var initial = _lastClient ?? throw new CaseFailure("LiquidReviewOwnerMissing");
        var peer = _peer ?? throw new CaseFailure("CoopPeerMissing");
        Require(_client != null && peer.Process != null && _lastDs != null, "LiquidReviewOwnedProcessesMissing");
        int primaryPid = _client!.Id, peerPid = peer.Process!.Id;
        int authorityPid = checked((int)Number(_lastDs, "ProcessId"));
        Require(authorityPid > 0 && authorityPid != primaryPid && authorityPid != peerPid, "LiquidReviewAuthorityProcessMissing");
        var motionPids = new HashSet<int> { primaryPid, peerPid, authorityPid };
        var renderPids = new HashSet<int> { primaryPid, peerPid };
        var completedMotion = new Dictionary<int, JsonObject>();
        var completedRender = new Dictionary<int, JsonObject>();
        var completedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int initialCharges = CardCount(initial, _rapidFire);
        var wait = Stopwatch.StartNew();
        while (completedMotion.Count != 3 || completedRender.Count != 2)
        {
            _deadline.ThrowIfCancellationRequested();
            Require(!_client.HasExited && !peer.Process.HasExited, "LiquidReviewClientExitedDuringSampling");
            foreach (string path in Directory.EnumerateFiles(options.Output, "liquid-*.json"))
            {
                if (completedPaths.Contains(path)) continue;
                bool motion = Path.GetFileName(path).StartsWith("liquid-motion-", StringComparison.Ordinal);
                bool render = Path.GetFileName(path).StartsWith("liquid-render-", StringComparison.Ordinal);
                if (!motion && !render) continue;
                var report = await ReadJson(path);
                if (report == null) continue;
                int pid = checked((int)Number(report, "ProcessId"));
                var expected = motion ? motionPids : renderPids;
                var completed = motion ? completedMotion : completedRender;
                if (!expected.Contains(pid) || completed.ContainsKey(pid)) continue;
                Require(Text(report, "State") == "Completed", "LiquidReviewProbeDidNotComplete");
                completed.Add(pid, report);completedPaths.Add(path);
            }
            if (completedMotion.Count == 3 && completedRender.Count == 2) break;
            Require(wait.Elapsed.TotalSeconds < 90, "LiquidReviewOutputsTimedOut");
            await Task.Delay(250, _deadline);
        }
        Require(Text(completedMotion[authorityPid], "Mode") == "Authority"
            && Text(completedMotion[primaryPid], "Mode") == "Replica" && Text(completedMotion[peerPid], "Mode") == "Replica",
            "LiquidReviewProcessRolesMismatch");
        var final = await Issue(new JsonObject { ["Action"] = "report" });
        var second = await IssuePeer("report");
        bool primaryIdle = !Flag(final, "SyntheticKeyboardAllowed") && !Flag(final, "SyntheticPointerAllowed")
            && Number(final, "DriverCastCount") == 0 && Number(final, "ProbeCastSequence") == 0
            && !Flag(final, "PlayerCastObserved") && SameItems(initial["Inventory"]?["Items"], final["Inventory"]?["Items"]);
        bool peerIdle = !Flag(second, "SyntheticKeyboardAllowed") && !Flag(second, "SyntheticPointerAllowed")
            && Number(second, "DriverCastCount") == 0 && Number(second, "ProbeCastSequence") == 0
            && !Flag(second, "PlayerCastObserved") && SameItems(peer.EntryItems, second["Inventory"]?["Items"]);
        _checks.Add(new { Stage = "GraphicalLiquidReviewOutputs", Passed = true, MotionProcesses = motionPids.ToArray(),
            RenderProcesses = renderPids.ToArray(), KeyboardCovered = false, MouseCovered = false, PhysicalInputIsolated = false,
            ControlledPerformanceAB = false, Scope = "Completed native probe output only; visible liquid, timing and GPU validation remain separate evidence" });
        _checks.Add(new { Stage = "GraphicalLiquidReviewInputObservation", Passed = primaryIdle && peerIdle,
            PrimaryIdle = primaryIdle, PeerIdle = peerIdle, PrimaryDriverCasts = Number(final, "DriverCastCount"),
            PeerDriverCasts = Number(second, "DriverCastCount"), PhysicalInputIsolated = false, ControlledPerformanceAB = false });
        await Save("liquid-review-final-client", final);
        await Save("liquid-review-final-peer2-client", second);
        Require(primaryIdle && peerIdle, "LiquidReviewPhysicalInputOrAssetsChanged");
        await ExitAndVerify(_rapidFire, initialCharges);
        await ExitCoopPeerAndVerify();
    }

    /// <summary>
    /// Mouse模式始终沿Wizard原InputAction→CaptureAttackPress→StepPrediction→IntentProduced，
    /// 不调用Probe cast，不为测试手写Driver命令序号。两种真实Tab顺序覆盖用户记忆不确定的分支；
    /// 仍从出生点普通掉落开始，不能把未移动到作者Ice的结果说成Ice拾取通过。
    /// </summary>
    private async Task RunRapidMouseInventory()
    {
        Stage("RapidMouseInitialOwner");
        var initial = _lastClient ?? throw new CaseFailure("RapidMouseOwnerMissing");
        Require(Flag(initial, "SyntheticPointerAllowed") && Number(initial, "ProbeCastSequence") == 0
            && Number(initial, "DriverCastCount") == 0, "RapidMouseNotExclusiveInputMode");
        Require(Text(initial["Inventory"], "SelectedWandInstanceId") == _rapidWand && RapidProgramAt(initial, _rapidFire, 5),
            "RapidMouseSelectedProgramMismatch");
        for (int i = 0; i < _rapidModifiers.Count; i++)
        {
            string id = _rapidModifiers[i];
            Require(RapidProgramAt(initial, id, i) && CardCount(initial, id) == _rapidInitialCharges[id] - 1,
                "RapidMouseEntryQualificationMismatch");
            _rapidEntryCharges.Add(id, CardCount(initial, id));
        }
        Require(CardCount(initial, _rapidFire) == _rapidInitialCharges[_rapidFire]
            && !Objects(initial["Inventory"], "Cards").Any(card => Text(card, "ParentWandInstanceId") == _rapidWand
                && Signed(card, "Slot") == 4), "RapidMouseEntryFireOrGapMismatch");
        _rapidBottle = Text(Objects(initial["Inventory"], "Items").Single(item => Flag(item, "HasContainerState")
            && Signed(item, "LocationKind") == 8 && Signed(item, "Slot") == 0), "InstanceId");
        Record("RapidMouseInitialInstalledOwner", initial);
        await UiMove(_rapidLooseCard, "SpellStorage", null, 4, null, r => Location(r, _rapidLooseCard) == 7);
        int looseCharges = CardCount(_lastClient!, _rapidLooseCard);
        await RapidDropLooseCard();
        await RapidCheckGates("MouseBeforeCast", looseCharges);
        if (_rapidFailures.Count != 0) throw new CaseFailure(_rapidFailures[0]);

        await RapidMouseTabOrder("ReleaseBeforeTab", true, looseCharges);
        await RapidMouseTabOrder("ReleaseInsideTab", false, looseCharges);
        _checks.Add(new { Stage = "RapidMouseInventorySummary", Passed = _rapidFailures.Count == 0,
            Failures = _rapidFailures.ToArray(), Orders = new[] { "Mouse down -> Driver cast -> Mouse up -> Tab open", "Mouse down -> Driver cast -> Tab open -> Mouse up" },
            Covered = "Original Wizard Mouse InputAction and Driver sequence, real virtual Keyboard Tab, eight-second inventory gates, original F and UI callbacks, fresh Authority Busy state",
            NotCovered = "Hardware/OS input, arbitrary pointer Raycast, movement and authored Ice at (-16,0,25)" });
        if (_rapidFailures.Count != 0) throw new CaseFailure(_rapidFailures[0]);
        if (IsCoop) await CaptureCoopObservation("BeforeActor1Exit", _lastClient!, _lastDs!);
        await ExitAndVerify(_rapidFire, CardCount(_lastClient!, _rapidFire));
    }

    /// <summary>
    /// 复现长按后的持有者问题，不以已豁免的地面拾取代替Busy门。账号阶段保留Fire在slot4，
    /// 入局后由原UI正式改到slot5，DS和Client都必须安装Revision2；不导入旧checkpoint或重建Draw。
    /// 两轮只改变Mouse/Tab顺序，Fire始终须有次数，真正耗尽另由独立场景验证。
    /// </summary>
    private async Task RunRapidLongBusy()
    {
        Stage("LongBusyInitialOwner");
        var initial = _lastClient ?? throw new CaseFailure("LongBusyOwnerMissing");
        Require(Flag(initial, "SyntheticPointerAllowed") && Number(initial, "ProbeCastSequence") == 0
            && Number(initial, "DriverCastCount") == 0 && Number(initial["Inventory"], "ProgramRevision") == 1
            && Text(initial["Inventory"], "SelectedWandInstanceId") == _rapidWand
            && RapidProgramAt(initial, _rapidFire, 4) && CardCount(initial, _rapidFire) == 30
            && CardCount(initial, _rapidFire) == _rapidInitialCharges[_rapidFire], "LongBusyEntryProgramMismatch");
        for (int i = 0; i < _rapidModifiers.Count; i++)
        {
            string id = _rapidModifiers[i];
            Require(RapidProgramAt(initial, id, i) && CardCount(initial, id) == _rapidInitialCharges[id] - 1,
                "LongBusyEntryQualificationMismatch");
            _rapidEntryCharges.Add(id, CardCount(initial, id));
        }
        _rapidBottle = Text(Objects(initial["Inventory"], "Items").Single(item => Flag(item, "HasContainerState")
            && Signed(item, "LocationKind") == 8 && Signed(item, "Slot") == 0), "InstanceId");
        Record("LongBusyInitialSlot4Owner", initial);
        await UiMove(_rapidFire, "ProgramSlot", _rapidWand, 5, null, r => RapidProgramAt(r, _rapidFire, 5)
            && Number(r["Inventory"], "ProgramRevision") == 2);
        Require(Number(_lastClient!["LastInventoryIntent"], "Kind") == 12
            && Number(_lastClient["LastInventoryIntent"], "TargetLocationKind") == 2
            && Number(_lastClient["LastInventoryIntent"], "SourceProgramRevision") == 1
            && Number(_lastClient["LastInventoryIntent"], "TargetProgramRevision") == 1
            && CardCount(_lastClient, _rapidFire) == 30, "LongBusyReprogramDidNotUseOriginalUiMove");
        await Issue(new JsonObject { ["Action"] = "ui-close" });
        await CaptureRapidAuthorityBusy("LongBusyRevision2", Number(_lastClient, "ServerTick"));
        var owner = Objects(_lastDs, "ActorInventories").Single(value => Number(value, "ActorId") == 1);
        Require(Number(owner, "ProgramRevision") == 2 && DsCardCount(_lastDs!, _rapidFire) == 30,
            "LongBusyAuthorityRevision2Missing");
        Record("LongBusyRevision2Installed", _lastClient!);

        try
        {
            if (_rapidFailures.Count == 0) await RapidLongBusyOrder("LongReleaseBeforeTab", true, 4);
            // 第一轮已捕获真实Busy时不再施法污染持有者现场；仍走下面的正式有限退出。
            if (_rapidFailures.Count == 0) await RapidLongBusyOrder("LongReleaseInsideTab", false, 6);
        }
        catch (CaseFailure failure)
        {
            _rapidFailures.Add(failure.Code);
            _checks.Add(new { Stage = "LongBusyFailureBeforeExit", Passed = false, Failure = failure.Code,
                Client = _lastClient?.DeepClone() });
            // 未到低蓝/意图持续增长等同样保存持有者；正常失败沿原预算再取一次新DS，绝不取消Ticket自救。
            if (!_deadline.IsCancellationRequested)
            {
                try { await CaptureRapidAuthorityBusy("LongBusyFailure", Number(_lastClient, "ServerTick")); }
                catch (CaseFailure capture) { _checks.Add(new { Stage = "LongBusyFailureOwnerCapture", Passed = false, Failure = capture.Code }); }
            }
        }
        _checks.Add(new { Stage = "RapidLongBusySummary", Passed = _rapidFailures.Count == 0,
            Failures = _rapidFailures.ToArray(), RequiredEmptyOutputsPerRound = 3, HoldMinimumSeconds = 50, HoldLimitSeconds = 65,
            QuietSecondsAfterProcessedUp = 8, ExpectedProgramRevision = 2,
            Covered = "Original Mouse/Tab InputActions, live UI reprogram, low-mana non-exhausted EmptyOutput, MoveOwned Kind12 and fresh Authority Busy gates",
            NotCovered = "Fire exhaustion, region2 travel, hardware input, old checkpoint Draw/ticket reconstruction or proof of the historical unique root cause" });
        // 即使已证明Busy，也先保存诊断并验证原退出；退出成功不能把前面的失败改成Passed。
        await ExitAndVerify(_rapidFire, CardCount(_lastClient!, _rapidFire));
        if (_rapidFailures.Count != 0) throw new CaseFailure(_rapidFailures[0]);
    }

    /// <summary>
    /// Probe仅保留最近64个Driver意图，因此工具每次读报告就按CommandId累计本轮事实。
    /// 终结后仍核对总计数，任何缺失/跨Epoch/非Driver请求都明确失败，不能因长按绕环而少算扣费。
    /// </summary>
    private void CaptureLongBusyCasts(JsonObject report, ulong previousCommand, uint epoch, long caller,
        Dictionary<ulong, JsonObject> history)
    {
        Require(Number(report, "ConnectionEpoch") == epoch && Number(report, "ProbeCastSequence") == 0,
            "LongBusyInputEpochChanged");
        foreach (var cast in Objects(report, "DriverCasts"))
        {
            ulong command = Number(cast, "CommandId"); if (command <= previousCommand) continue;
            Require(Number(cast, "ConnectionEpoch") == epoch && Signed(cast, "CallerId") == caller && caller != 0
                && Text(cast, "WandInstanceId") == _rapidWand && Number(cast, "ProgramRevision") == 2
                && Text(cast, "Source") == "Driver.IntentProduced", "LongBusyCastIdentityMismatch");
            bool terminal = Text(cast, "Result") is "Released" or "Rejected";
            if (terminal) Require(Text(cast, "ReceiptSource") == "CombatNetworkChannel.FactReceived", "LongBusyFactSourceMismatch");
            if (history.TryGetValue(command, out var old) && Text(old, "Result") is "Released" or "Rejected")
            {
                Require(terminal && Text(old, "Result") == Text(cast, "Result") && Text(old, "Failure") == Text(cast, "Failure"),
                    "LongBusyTerminalFactChanged");
                continue;
            }
            Require(history.ContainsKey(command) || history.Count < 1024, "LongBusyToolHistoryBudgetExceeded");
            history[command] = (JsonObject)cast.DeepClone();
        }
    }

    /// <summary>
    /// EmptyOutput必须来自本轮真实DS终结Fact，且Fire还有次数；同时保存低于Fire原20 Mana成本的
    /// 实际Owner样本。这里只证明进入低蓝零输出分支，不用“长按了多少秒”推定发生了什么。
    /// </summary>
    private async Task RapidLongBusyOrder(string label, bool releaseBeforeTab, int targetCell)
    {
        Stage(label);
        await WaitClient(r => !Flag(r["Ui"], "IsOpen") && !Flag(r, "Suppressed") && !Flag(r, "Frozen")
            && Flag(r, "CanAdvance") && Flag(r, "Focused") && !Flag(r, "SyntheticLeftPressed")
            && Flag(r["Inventory"], "SelectedWandResourcesAvailable") && Real(r["Inventory"], "Mana") >= 119.9,
            label + "RecoveredInputPrecondition", 12);
        var before = await Issue(new JsonObject { ["Action"] = "report" });
        Require(RapidProgramAt(before, _rapidFire, 5) && Number(before["Inventory"], "ProgramRevision") == 2,
            label + "ProgramChanged");
        ulong priorCount = Number(before, "DriverCastCount"), priorReleased = Number(before, "DriverReleasedCount");
        ulong previousCommand = Number(before["LastDriverCast"], "CommandId");
        uint epoch = checked((uint)Number(before, "ConnectionEpoch")); long caller = Signed(before, "LocalInventoryDriverId");
        int fireBefore = CardCount(before, _rapidFire); Require(fireBefore > 0, label + "FireAlreadyExhausted");
        var history = new Dictionary<ulong, JsonObject>();
        var lowManaSamples = new List<object>(); var lowManaEmptyCommands = new HashSet<ulong>();
        var observedEmptyCommands = new HashSet<ulong>(); int sampledEmptyCount = 0;
        var hold = Stopwatch.StartNew(); JsonObject opened; ulong? processedUpCount = null;
        try
        {
            await SyntheticPointer(true);
            var empty = await WaitClient(r =>
            {
                CaptureLongBusyCasts(r, previousCommand, epoch, caller, history);
                Require(Flag(r, "SyntheticLeftPressed") && Flag(r, "Focused") && Flag(r, "CanAdvance")
                    && !Flag(r, "Frozen") && !Flag(r, "Suppressed") && !Flag(r["Ui"], "IsOpen"), label + "HeldInputGateChanged");
                Require(CardCount(r, _rapidFire) > 0, label + "FireExhaustedBeforeLowManaCoverage");
                var emptyCommands = history.Values.Where(cast => Text(cast, "Result") == "Rejected"
                    && Text(cast, "Failure") == "EmptyOutput").Select(cast => Number(cast, "CommandId")).ToArray();
                int count = emptyCommands.Length;
                Require(Flag(r["Inventory"], "SelectedWandResourcesAvailable"), label + "ResourceSampleUnavailable");
                double mana = Real(r["Inventory"], "Mana");
                if (count > sampledEmptyCount)
                {
                    sampledEmptyCount = count;
                    var newlyObserved = emptyCommands.Where(command => observedEmptyCommands.Add(command)).ToArray();
                    if (mana < 20)
                    {
                        foreach (ulong command in newlyObserved) lowManaEmptyCommands.Add(command);
                        lowManaSamples.Add(new { EmptyOutputCount = count, NewlyObservedEmptyCommands = newlyObserved, Mana = mana,
                            FireRemaining = CardCount(r, _rapidFire), ServerTick = Number(r, "ServerTick"),
                            InventoryVersion = Number(r, "ClientInventoryVersion"), ResourceVersion = Number(r["Inventory"], "ResourceVersion"),
                            RecordedAtUtc = Text(r, "RecordedAtUtc") });
                    }
                }
                return hold.Elapsed.TotalSeconds >= 50 && lowManaEmptyCommands.Count >= 3 && mana < 20
                    && history.Values.Any(cast => Text(cast, "Result") == "Released");
            }, label + "ThreeLowManaEmptyOutputs", 65);
            _checks.Add(new { Stage = label + "HeldLowManaReached", Passed = true, HoldSeconds = hold.Elapsed.TotalSeconds,
                EmptyOutputCount = sampledEmptyCount, LowManaEmptyOutputCount = lowManaEmptyCommands.Count,
                LowManaThreshold = 20, Samples = lowManaSamples,
                FireStillPositive = CardCount(empty, _rapidFire), Casts = history.Values.Select(cast => cast.DeepClone()).ToArray() });
            await Save(label + "-held-low-mana-client", empty);
            if (releaseBeforeTab)
            {
                await SyntheticPointer(false);
                processedUpCount = Number(_lastClient, "DriverCastCount");
            }
            await KeyboardStroke("Tab");
            opened = await WaitClient(r => Flag(r["Ui"], "IsOpen") && Flag(r, "Suppressed"), label + "OriginalTabOpened", 8);
            CaptureLongBusyCasts(opened, previousCommand, epoch, caller, history);
            Require(Flag(opened, "SyntheticLeftPressed") != releaseBeforeTab, label + "MouseTabOrderMismatch");
            if (processedUpCount.HasValue)
                Require(Number(opened, "DriverCastCount") == processedUpCount.Value, label + "NewIntentBetweenMouseUpAndTab");
            _checks.Add(new { Stage = label + "OriginalInputOrder", Passed = true, ReleasedBeforeTab = releaseBeforeTab,
                LeftPressedAtOpen = Flag(opened, "SyntheticLeftPressed"), Source = opened["Ui"]?["SourceType"]?.DeepClone() });
            await Save(label + "-tab-open-client", opened);
        }
        finally
        {
            if (_syntheticPointerHeld)
            {
                await SyntheticPointer(false);
                processedUpCount = Number(_lastClient, "DriverCastCount");
            }
        }

        // 以真正processed key-up之后的计数为水位，而非工具写命令时间。每份报告都核查不再新增意图，
        // 保留八秒静默正证据；迟到的旧Fact可以终结原Command，但不能被当作新的输入。
        ulong stoppedCount = processedUpCount ?? throw new CaseFailure(label + "ProcessedMouseUpMissing");
        var quiet = Stopwatch.StartNew();
        var settled = await WaitClient(r =>
        {
            CaptureLongBusyCasts(r, previousCommand, epoch, caller, history);
            Require(!Flag(r, "SyntheticLeftPressed") && Number(r, "DriverCastCount") == stoppedCount,
                label + "CastIntentContinuedAfterMouseUp");
            return quiet.Elapsed.TotalSeconds >= 8 && history.Values.All(cast => Text(cast, "Result") is "Released" or "Rejected");
        }, label + "EightSecondsWithoutNewIntent", 13);
        _checks.Add(new { Stage = label + "MouseUpQuiet", Passed = true, ElapsedSeconds = quiet.Elapsed.TotalSeconds,
            StoppedDriverCount = stoppedCount, FinalDriverCount = Number(settled, "DriverCastCount"), Focused = Flag(settled, "Focused"),
            Held = Flag(settled, "SyntheticLeftPressed"), Source = settled["Ui"]?["SourceType"]?.DeepClone() });
        await Save(label + "-eight-seconds-settled-client", settled);

        // 第一轮Tool0→Bag，第二轮Bag内换到另一空格，二者都必须是Kind12 MoveOwned。
        // 不能用BindTool/已豁免的Pickup成功代替严格库存门恢复，也不能把Busy伪装成等待Snapshot超时。
        await RapidObserveReceipt(label + "MoveOwned", _rapidBottle, 12, false,
            RapidUiCommand(_rapidBottle, "Backpack", _rapidBag, 0, targetCell, targetCell),
            r => Location(r, _rapidBottle) == 1 && Owner(r, _rapidBottle) == _rapidBag
                && Objects(r["Inventory"], "Items").Any(item => Text(item, "InstanceId") == _rapidBottle
                    && Signed(item, "X") == targetCell && Signed(item, "Y") == targetCell));
        await CaptureRapidAuthorityBusy(label, Number(_lastClient, "ServerTick"));
        var authority = Objects(_lastDs, "ActorInventories").Single(owner => Number(owner, "ActorId") == 1);
        int authorityFire = DsCardCount(_lastDs!, _rapidFire);
        Require(authorityFire > 0 && Number(authority, "ProgramRevision") == 2, label + "AuthorityFireExhaustedOrProgramChanged");
        var installed = await WaitClient(r =>
        {
            CaptureLongBusyCasts(r, previousCommand, epoch, caller, history);
            Require(Number(r, "DriverCastCount") == stoppedCount && !Flag(r, "SyntheticLeftPressed"), label + "LateCastAfterMouseUp");
            return Number(r, "ClientInventoryVersion") >= Number(authority, "InventoryVersion")
                && CardCount(r, _rapidFire) == authorityFire && RapidProgramAt(r, _rapidFire, 5)
                && history.Values.All(cast => Text(cast, "Result") is "Released" or "Rejected");
        }, label + "FinalOwnerAndFactsInstalled", 12);
        ulong released = Number(installed, "DriverReleasedCount") - priorReleased;
        Require(Number(installed, "DriverReleasedCount") >= priorReleased
            && Number(installed, "DriverCastCount") >= priorCount && (ulong)history.Count == Number(installed, "DriverCastCount") - priorCount
            && (ulong)history.Values.Count(cast => Text(cast, "Result") == "Released") == released,
            label + "FireAccountingHistoryIncomplete");
        int consumed = fireBefore - authorityFire;
        bool conserved = consumed >= 0 && (ulong)consumed == released;
        if (!conserved) _rapidFailures.Add(label + "FireChargeDeltaMismatch");
        foreach (string id in _rapidModifiers)
            Require(CardCount(installed, id) == _rapidEntryCharges[id], label + "ConsumedQualificationAgain");
        _checks.Add(new { Stage = label + "FireChargeConservation", Passed = conserved, FireBefore = fireBefore,
            FireAfter = authorityFire, Consumed = consumed, DriverReleased = released, DriverAttempts = history.Count,
            EmptyOutputs = history.Values.Count(cast => Text(cast, "Result") == "Rejected" && Text(cast, "Failure") == "EmptyOutput"),
            Casts = history.Values.OrderBy(cast => Number(cast, "CommandId")).Select(cast => cast.DeepClone()).ToArray() });
        await Save(label + "-final-client", installed);
        await KeyboardStroke("Tab");
        await WaitClient(r => !Flag(r["Ui"], "IsOpen") && !Flag(r, "Suppressed"), label + "OriginalTabClosed", 8);
    }

    private async Task RapidMouseTabOrder(string label, bool releaseBeforeTab, int looseCharges)
    {
        Stage(label);
        var before = await Issue(new JsonObject { ["Action"] = "report" });
        Require(!Flag(before["Ui"], "IsOpen") && !Flag(before, "Suppressed") && !Flag(before, "Frozen")
            && Flag(before, "CanAdvance") && Flag(before, "Focused") && !Flag(before, "SyntheticLeftPressed") && Number(before, "ProbeCastSequence") == 0,
            label + "InputPreconditionFailed");
        ulong priorCount = Number(before, "DriverCastCount"), priorReleased = Number(before, "DriverReleasedCount");
        ulong previousCommand = Number(before["LastDriverCast"], "CommandId");
        int fireBefore = CardCount(before, _rapidFire);
        JsonObject opened;
        try
        {
            await SyntheticPointer(true);
            var fired = await WaitClient(r => Number(r, "DriverCastCount") >= priorCount + 2
                && Number(r, "DriverReleasedCount") >= priorReleased + 2, label + "OriginalDriverRepeatedRelease", 12);
            var commands = Objects(fired, "DriverCasts").Where(value => Number(value, "CommandId") > previousCommand).ToArray();
            Require(commands.Length != 0 && commands.All(value => Text(value, "Source") == "Driver.IntentProduced"
                && Signed(value, "CallerId") == Signed(fired, "LocalInventoryDriverId")
                && Number(value, "ConnectionEpoch") == Number(fired, "ConnectionEpoch") && Text(value, "WandInstanceId") == _rapidWand)
                && commands.Any(value => Text(value, "Result") == "Released" && Text(value, "ReceiptSource") == "CombatNetworkChannel.FactReceived")
                && Flag(fired, "PlayerCastObserved") && Number(fired, "ProbeCastSequence") == 0
                && Number(fired, "ConnectionEpoch") == Number(before, "ConnectionEpoch"),
                label + "DidNotUseOriginalWizardDriverAndDsReceipt");
            await Save(label + "-mouse-held-driver", fired);
            if (releaseBeforeTab) await SyntheticPointer(false);
            await KeyboardStroke("Tab");
            opened = await WaitClient(r => Flag(r["Ui"], "IsOpen") && Flag(r, "Suppressed"), label + "OriginalTabOpened", 8);
            Require(Flag(opened, "SyntheticLeftPressed") != releaseBeforeTab
                && Number(opened, "ConnectionEpoch") == Number(before, "ConnectionEpoch"), label + "MouseTabOrderMismatch");
            _checks.Add(new { Stage = label + "OriginalInputOrder", Passed = true, ReleasedBeforeTab = releaseBeforeTab,
                LeftPressedAtOpen = Flag(opened, "SyntheticLeftPressed"), OriginalTabOpened = Flag(opened["Ui"], "IsOpen"),
                InputSuppressed = Flag(opened, "Suppressed"), ProbeCastSequence = Number(opened, "ProbeCastSequence"),
                NewDriverIntents = Number(opened, "DriverCastCount") - priorCount,
                NewDriverReleased = Number(opened, "DriverReleasedCount") - priorReleased });
            await Save(label + "-tab-open", opened);
        }
        finally
        {
            // 无论Tab/收据断言是否失败，都仅松开本工具的虚拟左键；不清用户设备或绕过输入门。
            if (_syntheticPointerHeld) await SyntheticPointer(false);
        }
        ulong capturedCount = Number(opened, "DriverCastCount");
        await Task.Delay(TimeSpan.FromSeconds(1), _deadline);
        var releasedInUi = await Issue(new JsonObject { ["Action"] = "report" });
        Require(!Flag(releasedInUi, "SyntheticLeftPressed") && Flag(releasedInUi["Ui"], "IsOpen")
            && Flag(releasedInUi, "Suppressed") && Number(releasedInUi, "DriverCastCount") == capturedCount,
            label + "UiCaptureLeakedNewCast");
        await Save(label + "-released-while-ui-open", releasedInUi);
        await KeyboardStroke("Tab");
        await WaitClient(r => !Flag(r["Ui"], "IsOpen") && !Flag(r, "Suppressed"), label + "OriginalTabClosed", 8);
        await RapidCheckGates(label + "Settled", looseCharges);
        foreach (string id in _rapidModifiers)
            Require(CardCount(_lastClient!, id) == _rapidEntryCharges[id], label + "ConsumedQualificationAgain");
        await CaptureRapidAuthorityBusy(label, Number(_lastClient, "ServerTick"));
        // 同一程序只有这张Fire产出：每个真实Released必须恰好支付一次，而Rejected不得扣次数。
        // 先等同一新Authority样本的Owner安装及本轮全部Driver回执终结，再比较，
        // 不把旧Owner/尚在途的fact错判为丢扣，也不以最终实际余额自证正确。
        var authority = Objects(_lastDs, "ActorInventories").Single(owner => Number(owner, "ActorId") == 1);
        int authorityFire = DsCardCount(_lastDs!, _rapidFire);
        Require(authorityFire >= 0, label + "AuthorityFireMissing");
        var settled = await WaitClient(r => Number(r, "ClientInventoryVersion") >= Number(authority, "InventoryVersion")
            && CardCount(r, _rapidFire) == authorityFire && RapidProgramAt(r, _rapidFire, 5)
            && Objects(r, "DriverCasts").Where(cast => Number(cast, "CommandId") > previousCommand)
                .All(cast => Text(cast, "Result") is "Released" or "Rejected"), label + "FireOwnerAndFactsSettled", 12);
        ulong finalCount = Number(settled, "DriverCastCount"), finalReleased = Number(settled, "DriverReleasedCount");
        var roundCasts = Objects(settled, "DriverCasts").Where(cast => Number(cast, "CommandId") > previousCommand).ToArray();
        Require(Number(settled, "ConnectionEpoch") == Number(before, "ConnectionEpoch") && finalCount >= priorCount
            && finalReleased >= priorReleased && (ulong)roundCasts.Length == finalCount - priorCount,
            label + "FireAccountingHistoryIncomplete");
        int consumed = fireBefore - CardCount(settled, _rapidFire);
        ulong released = finalReleased - priorReleased;
        Require((ulong)roundCasts.Count(cast => Text(cast, "Result") == "Released") == released,
            label + "FireReleasedHistoryMismatch");
        bool conserved = consumed >= 0 && (ulong)consumed == released;
        if (!conserved) _rapidFailures.Add(label + "FireChargeDeltaMismatch");
        _checks.Add(new { Stage = label + "FireChargeConservation", Passed = conserved, FireId = _rapidFire,
            FireBefore = fireBefore, FireAfter = authorityFire, Consumed = consumed, DriverReleased = released,
            DriverAttempts = finalCount - priorCount, AuthorityInventoryVersion = Number(authority, "InventoryVersion"),
            InstalledInventoryVersion = Number(settled, "ClientInventoryVersion"), Casts = roundCasts.Select(cast => cast.DeepClone()).ToArray() });
        await Save(label + "-fire-conservation-client", settled);
        if (IsCoop) await CaptureCoopObservation(label, settled, _lastDs!);
    }

    private async Task SyntheticPointer(bool pressed)
    {
        if (pressed) _syntheticPointerHeld = true;
        var queued = await Issue(new JsonObject { ["Action"] = "synthetic-pointer", ["Key"] = "leftButton", ["Pressed"] = pressed });
        Require(Flag(queued, "SyntheticPointerAllowed") && Signed(queued, "SyntheticPointerDeviceId") > 0,
            "SyntheticPointerNotExplicitlyInstalled");
        var processed = await WaitClient(r => Signed(r, "SyntheticPointerDeviceId") > 0 && Flag(r, "SyntheticLeftPressed") == pressed,
            "SyntheticPointerProcessed" + (pressed ? "Down" : "Up"), 8);
        _checks.Add(new { Stage = "SyntheticPointerProcessed", Passed = true, Pressed = pressed,
            DeviceId = Signed(processed, "SyntheticPointerDeviceId"), Position = processed["SyntheticPointerPosition"]?.DeepClone() });
        if (!pressed) _syntheticPointerHeld = false;
    }

    private async Task CaptureRapidAuthorityBusy(string label, ulong minimumTick)
    {
        // DS state.json沿原Heartbeat约15秒落盘；明确等新Tick，不能拿施法前旧零值证明释放。
        var ds = await WaitDs(r => Objects(r, "ActorInventories").Any(owner => Number(owner, "ActorId") == 1
            && Number(owner, "ProbeSampleTick") >= minimumTick && Flag(owner["OwnerBusy"], "Captured")
            && Flag(owner, "DriverBusyCaptured")), label + "FreshAuthorityBusy", 25);
        var actor = Objects(ds, "ActorInventories").Single(owner => Number(owner, "ActorId") == 1);
        bool busy = Flag(actor["OwnerBusy"], "RaidActionsFrozen") || Flag(actor["OwnerBusy"], "ItemsResourceBusy")
            || Flag(actor["OwnerBusy"], "CastPending") || Number(actor["OwnerBusy"], "ActiveReservations") != 0;
        bool driverBusy = Flag(actor, "TimelineActive") || Flag(actor, "Queued");
        if (busy) _rapidFailures.Add(label + "AuthorityStillBusy");
        if (driverBusy) _rapidFailures.Add(label + "DriverStillBusy");
        _checks.Add(new { Stage = label + "FreshAuthorityBusy", Passed = !busy && !driverBusy, DriverStillBusy = driverBusy, MinimumTick = minimumTick,
            SampleTick = Number(actor, "ProbeSampleTick"), OwnerBusy = actor["OwnerBusy"]?.DeepClone(),
            DriverBusyCaptured = Flag(actor, "DriverBusyCaptured"), TimelineActive = Flag(actor, "TimelineActive"),
            Queued = Flag(actor, "Queued"), QueueRemaining = actor["QueueRemaining"]?.DeepClone() });
        await Save(label + "-fresh-authority", ds);
    }

    private async Task RapidCast(string label)
    {
        Stage(label);
        var before = await Issue(new JsonObject { ["Action"] = "ui-close" });
        Require(Flag(before, "CanAdvance") && !Flag(before, "Frozen") && !Flag(before, "Suppressed"), label + "WorldNotRunnable");
        ulong previous = Number(before["LastProbeCast"], "CommandId"), sequence = Number(before, "ClientInventorySequence");
        await Issue(new JsonObject { ["Action"] = "cast" });
        var after = await WaitClient(r => Number(r["LastProbeCast"], "CommandId") != 0
            && Number(r["LastProbeCast"], "CommandId") != previous && Text(r["LastProbeCast"], "Result") is "Released" or "Rejected",
            label + "TerminalFact", 12);
        Require(Number(after["LastProbeCast"], "ConnectionEpoch") == Number(after, "ConnectionEpoch")
            && Text(after["LastProbeCast"], "WandInstanceId") == _rapidWand && !Flag(after, "PlayerCastObserved"), label + "CastIdentityMismatch");
        bool released = Text(after["LastProbeCast"], "Result") == "Released";
        if (released) after = await WaitClient(r => Number(r, "ClientInventorySequence") > sequence, label + "OwnerInstalled", 12);
        else _rapidFailures.Add(label + "Rejected" + Text(after["LastProbeCast"], "Failure"));
        _checks.Add(new { Stage = label, Passed = released, Cast = after["LastProbeCast"]?.DeepClone(),
            BeforeResources = before["Inventory"]?.DeepClone(), AfterResources = after["Inventory"]?.DeepClone() });
        await Save(label + "-client", after);
    }

    private async Task RapidCheckGates(string label, int looseCharges)
    {
        Stage(label + "SettleEightSeconds");
        var settled = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(8), _deadline);
        var snapshot = await Issue(new JsonObject { ["Action"] = "report" });
        _checks.Add(new { Stage = label + "Settled", Passed = true, ElapsedSeconds = settled.Elapsed.TotalSeconds,
            Frozen = Flag(snapshot, "Frozen"), CanAdvance = Flag(snapshot, "CanAdvance"), Suppressed = Flag(snapshot, "Suppressed"),
            TimeScale = snapshot["TimeScale"]?.DeepClone(), Resources = snapshot["Inventory"]?.DeepClone() });
        Require(Flag(snapshot, "CanAdvance") && !Flag(snapshot, "Frozen"), label + "WorldNotRunnable");
        bool fromTool = Location(snapshot, _rapidBottle) == 8;
        var move = RapidUiCommand(_rapidBottle, fromTool ? "Backpack" : "Shortcut", fromTool ? _rapidBag : "", 0, 4, 4);
        await RapidObserveReceipt(label + "Move", _rapidBottle, fromTool ? 12 : 6, false, move,
            r => Location(r, _rapidBottle) == (fromTool ? 1 : 8) && (fromTool ? Owner(r, _rapidBottle) == _rapidBag
                : Objects(r["Inventory"], "Items").Any(item => Text(item, "InstanceId") == _rapidBottle && Signed(item, "Slot") == 0)));
        bool picked = await RapidObserveReceipt(label + "KeyboardPickup", _rapidLooseCard, 15, true, null,
            r => (Location(r, _rapidLooseCard) == 7 && Owner(r, _rapidLooseCard) == _player
                || Location(r, _rapidLooseCard) == 1 && Owner(r, _rapidLooseCard) == _rapidBag) && CardCount(r, _rapidLooseCard) == looseCharges);
        if (picked) await RapidDropLooseCard();
        bool dragged = await RapidObserveReceipt(label + "DragPickup", _rapidLooseCard, 15, false,
            RapidUiCommand(_rapidLooseCard, "Backpack", _rapidBag, 0, 6, 6),
            r => Location(r, _rapidLooseCard) == 1 && Owner(r, _rapidLooseCard) == _rapidBag && CardCount(r, _rapidLooseCard) == looseCharges);
        if (dragged) await RapidDropLooseCard();
        await Issue(new JsonObject { ["Action"] = "ui-close" });
        await Save(label + "-ds", await ReadDs());
    }

    private Task RapidDropLooseCard() => UiMove(_rapidLooseCard, "Ground", null, 0, null, r => Location(r, _rapidLooseCard) == -1);
    private static JsonObject RapidUiCommand(string item, string target, string owner, int slot, int x, int y)
        => new() { ["Action"] = "ui-drag", ["ItemId"] = item, ["TargetKind"] = target, ["TargetId"] = owner,
            ["TargetSlot"] = slot, ["Intent"] = new JsonObject { ["GridX"] = x, ["GridY"] = y } };

    /// <summary>
    /// 只关联本次真实Caller/新CommandId/Epoch；失败不要求Owner序列推进，否则Busy会被伪装成超时。
    /// 成功仍必须有更新Owner与精确实例落点；失败则保留拒绝、UI文字、原资源与实际附近焦点。
    /// </summary>
    private async Task<bool> RapidObserveReceipt(string label, string item, int kind, bool keyboard, JsonObject? command,
        Func<JsonObject, bool> installed)
    {
        Stage(label);
        var before = await Issue(new JsonObject { ["Action"] = keyboard ? "ui-close" : "ui-open" });
        if (keyboard) before = await WaitClient(r => Flag(r, "NearbyInputEnabled") && Text(r, "NearbyFocusedItemId") == item
            && Signed(r, "NearbyFocusedPickupId") != 0, label + "OriginalFocus", 8);
        long caller = keyboard ? Signed(before, "NearbyFocusedPickupId") : Signed(before["Ui"], "ControllerId");
        ulong previous = Number(before["LastInventoryIntent"], "CommandId"), sequence = Number(before, "ClientInventorySequence");
        if (keyboard) await KeyboardStroke("F");
        else await Issue(command ?? throw new CaseFailure(label + "CommandMissing"));
        var after = await WaitClient(r => Number(r["LastInventoryIntent"], "CommandId") != 0
            && Number(r["LastInventoryIntent"], "CommandId") != previous
            && Number(r["LastMatchingInventoryReceipt"], "CommandId") == Number(r["LastInventoryIntent"], "CommandId"), label + "Receipt", 12);
        Require(Signed(after, "LastInventoryCallerId") == caller && caller != 0
            && Number(after["LastInventoryIntent"], "Kind") == (ulong)kind && Text(after["LastInventoryIntent"], "ItemId") == item
            && Number(after["LastMatchingInventoryReceipt"], "ConnectionEpoch") == Number(before, "ConnectionEpoch")
            && Number(after["LastInventoryIntent"], "ExpectedInventoryVersion") == Number(before, "ClientInventoryVersion")
            && Flag(after["LastInventoryIntent"], "AutoPlace") == keyboard, label + "RequestIdentityMismatch");
        bool success = Flag(after["LastMatchingInventoryReceipt"], "Success");
        if (success) after = await WaitClient(r => Number(r, "ClientInventorySequence") > sequence
            && Number(r, "ClientInventoryVersion") >= Number(r["LastMatchingInventoryReceipt"], "InventoryVersion")
            && installed(r), label + "OwnerInstalled", 12);
        string failure = Text(after["LastMatchingInventoryReceipt"], "Failure");
        bool unchanged = JsonNode.DeepEquals(before["Inventory"]?["Items"], after["Inventory"]?["Items"]);
        if (!success) _rapidFailures.Add(label + (failure == "Busy" ? "StillBusy" : "Rejected" + failure));
        if (!success && !unchanged) _rapidFailures.Add(label + "RejectedButOwnedItemsChanged");
        _checks.Add(new { Stage = label, Passed = success, StillBusy = !success && failure == "Busy",
            CallerId = caller, Intent = after["LastInventoryIntent"]?.DeepClone(), Receipt = after["LastMatchingInventoryReceipt"]?.DeepClone(),
            OwnedItemsUnchangedOnRejection = success ? (bool?)null : unchanged,
            BeforeVersion = Number(before, "ClientInventoryVersion"), AfterVersion = Number(after, "ClientInventoryVersion"),
            FocusedGroundItem = Text(after, "NearbyFocusedItemId"), UiStatus = Text(after["Ui"], "LastInteractionStatus"),
            Frozen = Flag(after, "Frozen"), Suppressed = Flag(after, "Suppressed"), Resources = after["Inventory"]?.DeepClone() });
        await Save(label + "-client", after);
        return success;
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

    /// <summary>
    /// 私房仍走正式HTTP的Create/Join/Ready/Start，唯一差别是避免Match加入历史test大厅。
    /// 两个账号都由业务服冻结入局资产、分配Seat并签一次性Ticket；没有直接调用领域服务或写DB。
    /// 第二Client先启动，主Client后启动；ActorId来自Seat，绝不能按进程启动次序伪造。
    /// </summary>
    private async Task RegisterPeerAndEnterCoop(ContentManifest manifest, OnlineProfileReply primaryProfile)
    {
        Stage("CoopRegisterPeer");
        _peer = new PeerContext(_factory!.CreateClient(), Path.Combine(_private, "peer2"));
        Directory.CreateDirectory(_peer.Directory);
        Require(_peer.Http.BaseAddress == _http!.BaseAddress, "CoopPeerHostMismatch");
        string username = "reg_" + Guid.NewGuid().ToString("N")[..16];
        string password = Guid.NewGuid().ToString("N") + "!Native";
        await PostFor<RegisterAccountRequest, JsonObject>(_peer.Http, "/api/online/accounts", new() { Username = username, Password = password });
        var login = await PostFor<LoginAccountRequest, LoginAccountReply>(_peer.Http, "/api/online/sessions", new()
        { Username = username, Password = password, ProtocolVersion = manifest.ProtocolVersion, ContentManifestHash = manifest.Hash });
        _peer.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        _peer.Http.DefaultRequestHeaders.Add("X-Norn-Manifest", manifest.Hash);
        _peer.Http.DefaultRequestHeaders.Add("X-Norn-Protocol", manifest.ProtocolVersion.ToString());
        var peerProfile = await GetFor<OnlineProfileReply>(_peer.Http, "/api/online/profile");
        _peer.PlayerId = peerProfile.PlayerId;
        _peer.WandId = peerProfile.CombatAssets.SelectedWandInstanceId;
        var peerFire = peerProfile.Items.Single(item => item.ContentId == "wandtest.fire"
            && item.LocationKind == 2 && item.ParentInstanceId == _peer.WandId);
        _peer.FireId = peerFire.InstanceId; _peer.FireCharges = peerFire.RemainingCharges;
        _peer.OriginalItemIds = peerProfile.Items.Select(item => item.InstanceId).ToArray();
        Require(_peer.PlayerId != _player && !string.IsNullOrEmpty(_peer.WandId)
            && peerProfile.Items.Any(item => item.InstanceId == _peer.WandId && item.ContentId == "wandtest.wand.a" && item.LocationKind == 3)
            && !primaryProfile.Items.Any(item => _peer.OriginalItemIds.Contains(item.InstanceId, StringComparer.Ordinal)),
            "CoopPeerStarterOrOwnershipMismatch");

        Stage("CoopCreateJoinReadyStart");
        var room = await Post<CreateRoomRequest, RoomReply>("/api/online/rooms", new() { DungeonId = "p7" });
        Require(!room.SinglePlayer && room.State == "Open" && room.LeaderPlayerId == _player
            && room.Members.Length == 1 && room.Members[0].PlayerId == _player && room.Members[0].Seat == 0,
            "CoopCreateSeatMismatch");
        room = await PostFor<object, RoomReply>(_peer.Http, $"/api/online/rooms/{room.RoomId}/members", null);
        RequireCoopMembers(room);
        // Join会清空全员Ready；每次Ready会推进Revision，不能并行提交两个旧Revision。
        room = await Post<RoomReadyRequest, RoomReply>($"/api/online/rooms/{room.RoomId}/ready", new()
        { ExpectedRevision = room.ReadyRevision, ExpectedInventoryVersion = primaryProfile.Version, Ready = true });
        room = await PostFor<RoomReadyRequest, RoomReply>(_peer.Http, $"/api/online/rooms/{room.RoomId}/ready", new()
        { ExpectedRevision = room.ReadyRevision, ExpectedInventoryVersion = peerProfile.Version, Ready = true });
        RequireCoopMembers(room);
        Require(room.Members.All(member => member.Ready)
            && room.Members.Single(member => member.PlayerId == _player).InventoryVersion == primaryProfile.Version
            && room.Members.Single(member => member.PlayerId == _peer.PlayerId).InventoryVersion == peerProfile.Version,
            "CoopReadyVersionMismatch");
        await Save("coop-room-ready", JsonSerializer.SerializeToNode(room, Program.Json)!);
        _entryOrdinal++;
        var instance = await Post<StartRoomRequest, RoomInstanceReply>($"/api/online/rooms/{room.RoomId}/start",
            new() { ExpectedRevision = room.ReadyRevision });
        _run = instance.RunId; _instance = instance.InstanceId; _coopRoomEpoch = instance.RoomEpoch;
        Require(!string.IsNullOrEmpty(_run) && !string.IsNullOrEmpty(_instance) && _coopRoomEpoch != 0, "CoopStartIdentityMissing");
        _nativeInstances.Add((_instance, _entryOrdinal));
        var launched = Stopwatch.StartNew();
        while (instance.State != "Ready")
        {
            Require(instance.State is not ("Failed" or "Completed") && launched.Elapsed.TotalSeconds < 55, "CoopInstanceReadyTimedOut");
            await Task.Delay(250, _deadline);
            instance = await Get<RoomInstanceReply>($"/api/online/rooms/{room.RoomId}/instance");
            Require(instance.InstanceId == _instance && instance.RunId == _run && instance.RoomEpoch == _coopRoomEpoch,
                "CoopInstanceChangedDuringStart");
        }
        Require(instance.DungeonId == "p7", "CoopInstanceDungeonMismatch");
        room = await Get<RoomReply>($"/api/online/rooms/{room.RoomId}");
        RequireCoopMembers(room);
        Require(room.RunId == _run && room.RoomEpoch == _coopRoomEpoch && room.State == "Running", "CoopRunningRoomMismatch");
        await Save("coop-room-running", JsonSerializer.SerializeToNode(room, Program.Json)!);

        Stage("CoopTwoNativeClients");
        // Ticket有效期60秒：签一张马上交给其Client，不先签两张再串行等待启动。
        var peerTicket = await PostFor<object, JoinTicketReply>(_peer.Http, $"/api/online/rooms/{room.RoomId}/ticket", null);
        RequireCoopTicket(peerTicket, room.RoomId, _peer.PlayerId);
        _peer.Process = await StartCoopClient(_peer.Directory, _peer.Report, _peer.Command, peerTicket, "client-peer2.log", false);
        var primaryTicket = await Post<object, JoinTicketReply>($"/api/online/rooms/{room.RoomId}/ticket", null);
        RequireCoopTicket(primaryTicket, room.RoomId, _player);
        _client = await StartCoopClient(_private, _clientReport, _command, primaryTicket, "client.log", !IsLiquidReview);
        var primaryReady = WaitClient(IsCoopClientReady, "CoopActor1Ready", 55);
        var peerReady = WaitPeer(IsCoopClientReady, "CoopActor2Ready", 55);
        await Task.WhenAll(primaryReady, peerReady);
        var main = await primaryReady; var second = await peerReady;
        Require(Number(main, "ActorId") == 1 && Number(second, "ActorId") == 2, "CoopNativeActorMismatch");
        _coopWorldEpoch = Number(main, "WorldEpoch");
        _peer.Epoch = checked((uint)Number(second, "ConnectionEpoch"));
        Require(_coopWorldEpoch != 0 && _peer.Epoch != 0 && Number(second, "WorldEpoch") == _coopWorldEpoch,
            "CoopNativeWorldOrEpochMismatch");
        _peer.EntryItems = second["Inventory"]?["Items"]?.DeepClone();
        Require(_peer.EntryItems is JsonArray, "CoopPeerEntryItemsMissing");
        // 开局仅等一次DS冷采样。后续直接复用原Mouse检查已等到的新Authority样本，
        // 不为每个库存操作追加一个15秒心跳周期。
        var ds = await WaitDs(value => Number(value, "Members") == 2 && Number(value, "Clients") == 2
            && AuthorityOwners(value).Count() == 2
            && AuthorityOwners(value).All(owner => Number(owner, "ProbeSampleTick") >= Number(main, "ServerTick")),
            "CoopTwoAdmittedOwnerSamples", 25);
        await CaptureCoopObservation("CoopInitial", main, ds);
        Record("OwnerReady", main);
    }

    private void RequireCoopMembers(RoomReply room)
    {
        Require(!room.SinglePlayer && room.DungeonId == "p7" && room.LeaderPlayerId == _player
            && room.Members.Length == 2 && room.Members.Any(member => member.PlayerId == _player && member.Seat == 0)
            && room.Members.Any(member => member.PlayerId == _peer!.PlayerId && member.Seat == 1), "CoopRoomMembersMismatch");
    }

    private void RequireCoopTicket(JoinTicketReply ticket, string room, string player)
        => Require(ticket.PlayerId == player && ticket.RoomId == room && ticket.RunId == _run && ticket.InstanceId == _instance
            && ticket.RoomEpoch == _coopRoomEpoch && ticket.DungeonId == "p7", "CoopTicketIdentityMismatch");

    private static bool IsCoopClientReady(JsonObject report)
        => Flag(report, "Connected") && Flag(report, "Authenticated") && Flag(report, "ClientInventoryReady")
            && Flag(report, "InventoryCaptured") && Flag(report, "WorldReady") && Flag(report, "CanAdvance")
            && !Flag(report, "Frozen") && !Flag(report, "Authority") && Text(report, "DungeonId") == "p7";

    private async Task<Process> StartCoopClient(string directory, string report, string command, JoinTicketReply ticket, string log, bool input)
    {
        string ticketPath = Path.Combine(directory, "ticket.json"), configPath = Path.Combine(directory, "probe-config.json");
        await File.WriteAllTextAsync(ticketPath, JsonSerializer.Serialize(new { Ticket = ticket,
            Output = Path.Combine(directory, "admission-report.json"), DurationSeconds = 300, ExpectRejection = false }, Program.Json), _deadline);
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new { Output = report, Command = command,
            AllowSyntheticKeyboard = input, AllowSyntheticPointer = input }, Program.Json), _deadline);
        Directory.CreateDirectory(Path.Combine(options.Output, "native"));
        var start = new ProcessStartInfo(options.ClientExe)
        { WorkingDirectory = Path.GetDirectoryName(options.ClientExe), UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        AddClientRenderingArguments(start);
        ConfigureMaterialTimingProbe(start);
        foreach (string argument in new[] { "-force-d3d11", "-logFile", Path.Combine(options.Output, "native", log),
            "--norn-room-client", ticketPath, "--norn-inventory-regression", configPath }) start.ArgumentList.Add(argument);
        foreach (string key in start.Environment.Keys.Where(key => key.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("Online__", StringComparison.OrdinalIgnoreCase) || key.StartsWith("NORN_N1_", StringComparison.OrdinalIgnoreCase)
            || key == "NORN_CONTENT_MANIFEST").ToArray()) start.Environment.Remove(key);
        start.Environment.Remove("NORN_INSTANCE_SECRET");
        var process = Process.Start(start) ?? throw new CaseFailure("CoopClientStartFailed");
        _ = process.Handle;
        return process;
    }

    /// <summary>
    /// 默认仍使用batchmode回归；显式图形采样必须走日常Player相机/Present路径，
    /// 否则显式RenderRequest的一张图不能代表正常GPU帧成本或逐帧运动。
    /// 只影响本工具拥有的Client，使用虚拟输入，不注入系统键鼠。
    /// </summary>
    private static void AddClientRenderingArguments(ProcessStartInfo start)
    {
        if (Environment.GetEnvironmentVariable("NORN_NATIVE_GRAPHICAL_PROBE") != "1")
        { start.ArgumentList.Add("-batchmode"); return; }
        foreach (string argument in new[] { "-screen-fullscreen", "0", "-screen-width", "960", "-screen-height", "540" })
            start.ArgumentList.Add(argument);
    }

    /// <summary>
    /// 显式Native诊断为每个Client创建独立计时配置。DS继承父进程的配置，双Client若也继承
    /// 同一路径会争用命令/输出；此处只在进程启动前写一次私有实验配置，不改变游戏协议或输入。
    /// 原TimingProbe有固定事件容量、目标Key和时限，正常ENV为空时完全不创建观察者。
    /// </summary>
    private void ConfigureMaterialTimingProbe(ProcessStartInfo start)
    {
        if (Environment.GetEnvironmentVariable("NORN_NATIVE_MATERIAL_TIMING") != "1") return;
        string stem = Path.Combine(options.Output, "timing-client-" + Guid.NewGuid().ToString("N"));
        string config = stem + ".config.json";
        File.WriteAllText(config, JsonSerializer.Serialize(new { Output = stem + ".json", Command = stem + ".request",
            Capacity = 32768, DurationMilliseconds = 10000, Domain = 1, X = 0, Y = 0, Z = 0, WorldEpoch = 0UL }, Program.Json));
        start.Environment["NORN_N5_TIMING_PROBE"] = config;
    }

    private async Task CaptureCoopObservation(string label, JsonObject primary, JsonObject ds)
    {
        var peer = _peer ?? throw new CaseFailure("CoopPeerMissing");
        var report = await WaitPeer(value => Number(value, "ServerTick") >= Number(primary, "ServerTick"), label + "PeerCurrent", 8);
        Require(IsCoopClientReady(primary) && IsCoopClientReady(report) && Number(primary, "ActorId") == 1 && Number(report, "ActorId") == 2
            && Number(report, "ConnectionEpoch") == peer.Epoch && Number(primary, "WorldEpoch") == _coopWorldEpoch
            && Number(report, "WorldEpoch") == _coopWorldEpoch && Number(ds, "WorldEpoch") == _coopWorldEpoch,
            label + "CoopClientIdentityChanged");
        Require(!Flag(report, "SyntheticKeyboardAllowed") && !Flag(report, "SyntheticPointerAllowed")
            && Signed(report, "SyntheticKeyboardDeviceId") == 0 && Signed(report, "SyntheticPointerDeviceId") == 0
            && Number(report, "ProbeCastSequence") == 0 && Number(report, "DriverCastCount") == 0 && !Flag(report, "PlayerCastObserved"),
            label + "CoopPeerWasNotIdle");
        Require(Text(report["Inventory"], "SelectedWandInstanceId") == peer.WandId
            && CardCount(report, peer.FireId) == peer.FireCharges && SameItems(peer.EntryItems, report["Inventory"]?["Items"]),
            label + "CoopPeerAssetsChanged");
        var owners = AuthorityOwners(ds).ToArray();
        Require(Number(ds, "Members") == 2 && Number(ds, "Clients") == 2 && Flag(ds, "CanSimulate") && !Flag(ds, "Frozen")
            && owners.Length == 2 && owners.Any(owner => Number(owner, "ActorId") == 1) && owners.Any(owner => Number(owner, "ActorId") == 2),
            label + "CoopTwoAuthorityOwnersMissing");
        var authority = owners.Single(owner => Number(owner, "ActorId") == 2);
        var primaryAuthority = owners.Single(owner => Number(owner, "ActorId") == 1);
        Require(Number(primaryAuthority, "ConnectionEpoch") == Number(primary, "ConnectionEpoch")
            && Text(primaryAuthority, "SelectedWandInstanceId") == _rapidWand
            && Text(authority, "SelectedWandInstanceId") == peer.WandId, label + "CoopAuthorityIdentityMismatch");
        Require(Number(authority, "ConnectionEpoch") == peer.Epoch && Flag(authority, "DriverBusyCaptured") && Flag(authority["OwnerBusy"], "Captured")
            && !Flag(authority, "TimelineActive") && !Flag(authority, "Queued") && !Flag(authority["OwnerBusy"], "RaidActionsFrozen")
            && !Flag(authority["OwnerBusy"], "ItemsResourceBusy") && !Flag(authority["OwnerBusy"], "CastPending")
            && Number(authority["OwnerBusy"], "ActiveReservations") == 0 && CardCount(new JsonObject { ["Inventory"] = authority.DeepClone() }, peer.FireId) == peer.FireCharges,
            label + "CoopPeerAuthorityChanged");
        var primaryIds = Objects(primary["Inventory"], "Items").Select(item => Text(item, "InstanceId")).ToHashSet(StringComparer.Ordinal);
        Require(Objects(report["Inventory"], "Items").All(item => !primaryIds.Contains(Text(item, "InstanceId"))), label + "CoopOwnerIdsOverlap");
        _checks.Add(new { Stage = label + "TwoActualNativeOwners", Passed = true, WorldEpoch = _coopWorldEpoch,
            Members = Number(ds, "Members"), Clients = Number(ds, "Clients"), PeerActorId = 2, PeerPlayerId = peer.PlayerId,
            PeerEpoch = peer.Epoch, PeerSampleTick = Number(authority, "ProbeSampleTick"), PeerServerTick = Number(report, "ServerTick"),
            PeerFireId = peer.FireId, peer.FireCharges, PeerIdle = true, PeerOwnerBusy = authority["OwnerBusy"]?.DeepClone() });
        await Save(label + "-peer2-client", report);
        await Save(label + "-coop-ds", ds);
    }

    // DS固定输出四席；JsonUtility会把未绑定席序列化成ActorId=0的值壳。
    // 只去掉明确的空席，不能只挑Actor1/2而把意外的第三个真实Owner藏起来。
    private static IEnumerable<JsonObject> AuthorityOwners(JsonObject report)
        => Objects(report, "ActorInventories").Where(owner => Number(owner, "ActorId") != 0);

    private static bool SameItems(JsonNode? first, JsonNode? second)
    {
        if (first is not JsonArray left || second is not JsonArray right || left.Count != right.Count) return false;
        // 比较实例内容而非Snapshot数组顺序；没有输入的Peer不应发生资产移动或数量变化。
        return left.OfType<JsonObject>().All(item => right.OfType<JsonObject>()
            .Any(candidate => Text(candidate, "InstanceId") == Text(item, "InstanceId") && JsonNode.DeepEquals(candidate, item)));
    }

    private async Task ExitCoopPeerAndVerify(bool cleanup = false)
    {
        var peer = _peer ?? throw new CaseFailure("CoopPeerMissing");
        if (peer.ExitVerified) return;
        Stage("CoopActor2NormalExit");
        var before = await WaitPeer(value => Flag(value, "ClientInventoryReady")
            || Text(value["LastRunExitStatus"], "Phase") == "Committed", "CoopPeerExitReady", 8);
        Require(Number(before, "ActorId") == 2 && Number(before, "ConnectionEpoch") == peer.Epoch, "CoopPeerExitIdentityMismatch");
        bool idle = Number(before, "ProbeCastSequence") == 0 && Number(before, "DriverCastCount") == 0;
        if (!cleanup) Require(idle, "CoopPeerExitIdentityMismatch");
        // 正常验收仍要求Peer从未施法。失败清理则先保存它实际拥有的资产：一次真实鼠标输入
        // 已使本轮失败，但不能因此在发送exit前抛错，再由Kill替代原退出/可靠持久化。
        // 身份、Epoch、原Command与收据仍逐项验证；只把idle/Fire守恒的错误延后，不放宽产品门。
        // 首次退出可发一个新命令；响应未知时只等原Command，绝不再造退出ID。
        string observed = Text(before["LastRunExitStatus"], "CommandId");
        if (!string.IsNullOrEmpty(observed)) peer.ExitCommand = observed;
        if (string.IsNullOrEmpty(peer.ExitCommand))
        {
            Require(Flag(before, "CanAdvance") && !Flag(before, "Frozen"), "CoopPeerStoppedWithActor1Exit");
            var queued = await IssuePeer("exit");
            peer.ExitCommand = Text(queued["LastRunExitStatus"], "CommandId");
            Require(!string.IsNullOrEmpty(peer.ExitCommand), "CoopPeerExitCommandMissing");
        }
        var final = await WaitPeer(value => Text(value["LastRunExitStatus"], "Phase") == "Committed"
            && Text(value["LastRunExitStatus"], "CommandId") == peer.ExitCommand, "CoopPeerExitCommitted", 35);
        Require(Text(final["LastRunExitStatus"], "Reason") == "VoluntaryExit", "CoopPeerExitWasNotVoluntary");
        var receipt = await GetFor<OnlineRunMemberExitReceipt>(peer.Http, $"/api/online/runs/{_run}/member-exit");
        Require(receipt.Exited && receipt.Reason == OnlineRunMemberExitReason.VoluntaryExit && !receipt.SuccessfulExtraction
            && receipt.PlayerId == peer.PlayerId && receipt.ActorId == 2 && receipt.InstanceId == _instance && receipt.RunId == _run
            && receipt.RoomEpoch == _coopRoomEpoch && receipt.ConnectionEpoch == peer.Epoch && receipt.SettlementId == peer.ExitCommand
            && receipt.PayloadHash == Text(final["LastRunExitStatus"], "PayloadHash"), "CoopPeerFinalReceiptMismatch");
        var profile = await GetFor<OnlineProfileReply>(peer.Http, "/api/online/profile");
        Require(profile.PlayerId == peer.PlayerId && profile.Version == receipt.CommittedVersion && !profile.AssetsLocked
            && string.IsNullOrEmpty(profile.ActiveRunId), "CoopPeerFinalProfileIdentityOrLeaseMismatch");
        bool assetsUnchanged = profile.CombatAssets.SelectedWandInstanceId == peer.WandId
            && profile.Items.SingleOrDefault(item => item.InstanceId == peer.FireId)?.RemainingCharges == peer.FireCharges
            && peer.OriginalItemIds.All(id => profile.Items.Any(item => item.InstanceId == id));
        if (!cleanup) Require(assetsUnchanged, "CoopPeerFinalProfileMismatch");
        // 与Actor1已有只读持久化核验同口径；这里不写DB、不调用Service绕过退出协议。
        await using (var database = new MySqlConnection(_connection))
        {
            await database.OpenAsync(_deadline);
            var persisted = await database.QuerySingleAsync<ExitDatabaseState>(new CommandDefinition("""
                SELECT m.State MemberState,c.Version PersistentVersion,x.RequestHash,
                  (SELECT COUNT(*) FROM AssetLeases a WHERE a.RunId=m.RunId AND a.CharacterId=m.CharacterId) RemainingLeases
                FROM RunMembers m JOIN Characters c ON c.CharacterId=m.CharacterId
                JOIN RunMemberExitReceipts x ON x.RunId=m.RunId AND x.CharacterId=m.CharacterId
                WHERE m.RunId=@Run AND m.CharacterId=@Player
                """, new { Run = _run, Player = peer.PlayerId }, cancellationToken: _deadline));
            Require(persisted.MemberState == "Exited" && persisted.RemainingLeases == 0 && persisted.PersistentVersion == receipt.CommittedVersion
                && persisted.RequestHash == receipt.PayloadHash, "CoopPeerPersistedExitMismatch");
            await Save("peer2-exit-database-state", JsonSerializer.SerializeToNode(persisted, Program.Json)!);
        }
        peer.ExitVerified = true;
        _checks.Add(new { Stage = "CoopPeerReliableExit", Passed = true, peer.PlayerId, ActorId = 2, receipt.SettlementId,
            receipt.CommittedVersion, receipt.PayloadHash, peer.FireId, peer.FireCharges });
        await Save("peer2-final-receipt", JsonSerializer.SerializeToNode(receipt, Program.Json)!);
        await Save("peer2-final-client", final);
        if (cleanup && (!idle || !assetsUnchanged))
        {
            // Committed只证明已保存实际状态，不会把受污染的idle/次数验收改绿。原始主失败仍由
            // RunAsync保留；此处另存可解释的检查，再抛原类别交给cleanup记录，不掩盖任何计费变化。
            _checks.Add(new { Stage = "CoopPeerCleanupInputAndAssetInvariant", Passed = false, Idle = idle,
                AssetsUnchanged = assetsUnchanged, DriverCastCount = Number(before, "DriverCastCount"),
                ProbeCastSequence = Number(before, "ProbeCastSequence"), ExpectedFireCharges = peer.FireCharges,
                PersistedFireCharges = profile.Items.SingleOrDefault(item => item.InstanceId == peer.FireId)?.RemainingCharges,
                ReliableExitVerified = peer.ExitVerified });
            throw new CaseFailure(!idle ? "CoopPeerExitIdentityMismatch" : "CoopPeerFinalProfileMismatch");
        }
    }

    private async Task<JsonObject> IssuePeer(string action)
    {
        var peer = _peer ?? throw new CaseFailure("CoopPeerMissing");
        Require(action is "report" or "exit", "CoopPeerActionOutsideReadonlyAndExit");
        Require(!File.Exists(peer.Command), "CoopPeerPreviousCommandPending");
        string before = Text(peer.LastReport, "LastActionAtUtc"), temporary = Path.Combine(peer.Directory, "command.tmp");
        await File.WriteAllTextAsync(temporary, new JsonObject { ["Action"] = action }.ToJsonString(Program.Json), _deadline);
        File.Move(temporary, peer.Command);
        var report = await WaitPeer(value => Text(value, "LastAction") == action && Text(value, "LastActionAtUtc") != before
            && !File.Exists(peer.Command), "CoopPeerCommand" + action, 8);
        if (Text(report, "LastActionStatus").StartsWith("Rejected:", StringComparison.Ordinal)) throw new CaseFailure("CoopPeerProbeRejected" + action);
        return report;
    }

    private async Task<JsonObject> WaitPeer(Func<JsonObject, bool> predicate, string label, int seconds)
    {
        var peer = _peer ?? throw new CaseFailure("CoopPeerMissing");
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            _deadline.ThrowIfCancellationRequested();
            if (peer.Process != null && peer.Process.HasExited) throw new CaseFailure("CoopPeerClientExited" + peer.Process.ExitCode);
            var report = await ReadJson(peer.Report);
            if (report != null)
            {
                peer.LastReport = report;
                // 主Client启动失败时，Peer也可能已经真实Admitted。立即记下首次认证Epoch，
                // 失败清理仍能用本人身份退出；不能等两端全部Ready才知道第二人的退出资格。
                if (peer.Epoch == 0 && Flag(report, "Authenticated") && Number(report, "ActorId") == 2
                    && Number(report, "ConnectionEpoch") != 0)
                    peer.Epoch = checked((uint)Number(report, "ConnectionEpoch"));
                if (Text(report["LastRunExitStatus"], "Reason") == "FinalDeath") throw new CaseFailure("CoopPeerActualFinalDeath");
                if (predicate(report)) return report;
            }
            await Task.Delay(200, _deadline);
        }
        throw new CaseFailure(label + "TimedOut");
    }

    private async Task<T> PostFor<TRequest, T>(HttpClient client, string route, TRequest? request)
    {
        using var response = request is null ? await client.PostAsync(route, null, _deadline)
            : await client.PostAsJsonAsync(route, request, Program.Json, _deadline);
        await RequireHttp(response);
        return (await response.Content.ReadFromJsonAsync<T>(Program.Json, _deadline))!;
    }

    private async Task<T> GetFor<T>(HttpClient client, string route)
    {
        using var response = await client.GetAsync(route, _deadline); await RequireHttp(response);
        return (await response.Content.ReadFromJsonAsync<T>(Program.Json, _deadline))!;
    }

    private async Task CleanupCoopExits()
    {
        string failedStage = _stage;
        // 两次尝试各自捕获失败；A拒绝/超时不能跳过B。没有确证收据便保留整例失败。
        if (_client != null && !_client.HasExited && _lastClient != null && Flag(_lastClient, "ClientInventoryReady")
            && Text(_lastClient["LastRunExitStatus"], "Phase") != "Committed")
        {
            try
            {
                _deadline.ThrowIfCancellationRequested();
                // 设备松开失败不能跳过原exit；每次失败独立记账，继续让Owner保存当前真实资产。
                if (_syntheticPointerHeld)
                {
                    try { await SyntheticPointer(false); }
                    catch (Exception error) { _checks.Add(new { Stage = "CoopCleanupPointerRelease", Passed = false, Failure = error.GetType().Name }); }
                }
                foreach (string key in _heldSyntheticKeys.ToArray())
                {
                    try { await SyntheticKey(key, false); }
                    catch (Exception error) { _checks.Add(new { Stage = "CoopCleanupKeyRelease", Passed = false, Key = key, Failure = error.GetType().Name }); }
                }
                var latest = await ReadJson(_clientReport) ?? _lastClient;
                if (string.IsNullOrEmpty(Text(latest["LastRunExitStatus"], "CommandId"))) await Issue(new JsonObject { ["Action"] = "exit" });
                await WaitClient(value => Text(value["LastRunExitStatus"], "Phase") == "Committed", "CoopActor1CleanupExit", 18);
            }
            catch (Exception error) { _checks.Add(new { Stage = "CoopActor1CleanupExit", Passed = false, Failure = error.GetType().Name }); }
        }
        if (_peer?.Process != null && !_peer.Process.HasExited && !_peer.ExitVerified)
        {
            try { await ExitCoopPeerAndVerify(cleanup: true); }
            catch (Exception error) { _checks.Add(new { Stage = "CoopPeerCleanupExit", Passed = false, Failure = error.GetType().Name }); }
        }
        _stage = failedStage;
    }

    private async Task SaveCoopEvidence()
    {
        if (_peer == null) return;
        try
        {
            var report = await ReadJson(_peer.Report) ?? _peer.LastReport;
            if (report != null) await Save("last-peer2-client", report);
            var admission = await ReadJson(Path.Combine(_peer.Directory, "admission-report.json"));
            if (admission != null) await Save("peer2-admission", admission);
        }
        catch (Exception error) { _checks.Add(new { Stage = "CaptureCoopFailureEvidence", Passed = false, Failure = error.GetType().Name }); }
    }

    private void StopOwnedPeer()
    {
        if (_peer?.Process == null) return;
        var process = _peer.Process;
        try
        {
            if (!process.HasExited) { process.Kill(); Require(process.WaitForExit(5000), "OwnedPeerDidNotStop"); }
        }
        catch (Exception error) { _checks.Add(new { Stage = "StopOwnedPeer", Passed = false, Failure = error.GetType().Name }); _passed = false; }
        finally { process.Dispose(); _peer.Process = null; }
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
        await WritePrivate("probe-config.json", new { Output = _clientReport, Command = _command, AllowSyntheticKeyboard = options.InputMode == "keyboard",
            AllowSyntheticPointer = options.Scenario == "rapid-mouse-inventory" || IsOrdinaryMovement || IsRapidLongBusy,
            AllowSyntheticMovement = IsOrdinaryMovement });
        if (IsOrdinaryMovement) await WritePrivate("ui-config.json", new { ObserveMovementOnly = true, MovementSamples = 0,
            Output = Path.Combine(_private, "ui-report"), Command = Path.Combine(_private, "ui-command") });
        Directory.CreateDirectory(Path.Combine(options.Output, "native"));
        var start = new ProcessStartInfo(options.ClientExe)
        { WorkingDirectory = Path.GetDirectoryName(options.ClientExe), UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        string clientLog = _entryOrdinal == 1 ? "client.log" : $"client-{_entryOrdinal:00}.log";
        AddClientRenderingArguments(start);
        ConfigureMaterialTimingProbe(start);
        foreach (string argument in new[] { "-force-d3d11", "-logFile", Path.Combine(options.Output, "native", clientLog),
            "--norn-room-client", Path.Combine(_private, "ticket.json"), "--norn-inventory-regression", Path.Combine(_private, "probe-config.json") }) start.ArgumentList.Add(argument);
        if (IsOrdinaryMovement)
        {
            start.ArgumentList.Add("--norn-portal-acceptance"); start.ArgumentList.Add(Path.Combine(_private, "ui-config.json"));
        }
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

    /// <summary>
    /// 从正式注册 starter 入局，只移动并拾回自己的原 Fire。移动经过虚拟 Keyboard、Player Action、
    /// Prediction 与 DS；最后拖包经过真实 Mouse/EventSystem。这里不写 Transform、不造资产或库存意图。
    /// 初次丢下和重丢沿既有 UI 回调 helper；不能把这两步称为真实指针拖放覆盖。
    /// </summary>
    private async Task RunOrdinaryPickupMovement()
    {
        Stage("OrdinaryMovementStarter");
        var initial = await WaitClient(r => Flag(r, "MovementCaptured") && Flag(r, "SyntheticMovementAllowed"), "MovementObservationReady", 8);
        string wand = Text(initial["Inventory"], "SelectedWandInstanceId");
        var fire = Objects(initial["Inventory"], "Cards").SingleOrDefault(c => Text(c, "ParentWandInstanceId") == wand
            && Text(c, "TemplateId") == "wandtest.fire") ?? throw new CaseFailure("OriginalStarterFireMissing");
        string item = Text(fire, "InstanceId"); int charges = CardCount(initial, item);
        string bag = Text(Objects(initial["Inventory"], "Items").Single(i => Number(i, "LocationKind") == 4), "InstanceId");
        Require(charges == 30 && Number(initial, "DriverCastCount") == 0 && Number(initial, "ProbeCastSequence") == 0
            && !Flag(initial, "PlayerCastObserved"), "OrdinaryStarterWasAlreadyCast");
        string[] ids = Objects(initial["Inventory"], "Items").Select(i => Text(i, "InstanceId")).Order(StringComparer.Ordinal).ToArray();
        var spawn = MovementPosition(initial);
        Require(PlanarDistance(spawn, new(-4.95, 0, -8.02)) < 1, "OrdinaryMovementUnexpectedP7Spawn");
        CheckMovementBounds(initial);
        await UiMove(item, "Ground", null, 0, null, r => Location(r, item) == -1);
        Require(Number(_lastClient!["LastInventoryIntent"], "Kind") == 14, "OrdinaryInitialDropWasNotDropItem");
        await Issue(new JsonObject { ["Action"] = "ui-close" });
        var ground = await WaitFocusedGround(item, "OrdinaryInitialGround");
        var groundPosition = ReadPoint(ground["NearbyFocusedPosition"]);
        string worldOwner = Text(ground, "NearbyFocusedWorldOwner");
        Require(!string.IsNullOrEmpty(worldOwner) && Distance(MovementPosition(ground), groundPosition) <= 3,
            "OrdinaryInitialGroundPoseMissingOrTooFar");
        await Save("ordinary-initial-ground", ground);

        // P7 首席的静态地板在 z=5 结束，北侧还有墙；只走中央 x≈-4.95 的 z=0 折返点。
        // 每个 pulse 都等真实 KeyUp 被处理，下一次根据新 Client 位置计算，不能靠预估按键秒数假定到达。
        await MoveToPoint(new(spawn.X, spawn.Y, 0), "OrdinaryOutbound");
        Require(PlanarDistance(MovementPosition(_lastClient!), spawn) > 6, "OrdinaryMovementDidNotLeaveSpawnBySixMeters");
        await VerifyMovementEndpoint("OrdinaryFarEndpoint", spawn, groundPosition, returning: false);
        await MoveToPoint(groundPosition, "OrdinaryReturn");
        await VerifyMovementEndpoint("OrdinaryReturnEndpoint", spawn, groundPosition, returning: true);
        await PickupSpellByKeyboard(item, bag, charges);
        Require(Text(_lastClient!["LastInventoryIntent"], "WorldOwnerId") == worldOwner,
            "OrdinaryFPickupUsedDifferentGroundOwner");
        await WaitClient(r => Text(r, "NearbyFocusedItemId") != item && Location(r, item) != -1, "OrdinaryFGroundProxyRemoved", 5);
        AssertOrdinaryInventory(_lastClient!, ids, item, charges, "OrdinaryF");

        await UiMove(item, "Ground", null, 0, null, r => Location(r, item) == -1);
        Require(Number(_lastClient!["LastInventoryIntent"], "Kind") == 14, "OrdinarySecondDropWasNotDropItem");
        await Issue(new JsonObject { ["Action"] = "ui-close" });
        var second = await WaitFocusedGround(item, "OrdinarySecondGround");
        string secondOwner = Text(second, "NearbyFocusedWorldOwner");
        Require(!string.IsNullOrEmpty(secondOwner) && secondOwner != worldOwner
            && Distance(MovementPosition(second), ReadPoint(second["NearbyFocusedPosition"])) <= 3, "OrdinarySecondGroundIdentityInvalid");
        await DragOrdinaryGroundToBackpack(item, bag, secondOwner, charges);
        AssertOrdinaryInventory(_lastClient!, ids, item, charges, "OrdinaryMouse");
        var profile = await ExitAndVerify(item, charges);
        Require(profile.Items.Select(i => i.InstanceId).Order(StringComparer.Ordinal).SequenceEqual(ids), "OrdinaryExitChangedInstanceSet");
        var persisted = profile.Items.Single(i => i.InstanceId == item);
        Require(persisted.LocationKind == 1 && persisted.ParentInstanceId == bag && persisted.RemainingCharges == charges
            && persisted.X == 2 && persisted.Y == 2 && persisted.QuarterTurns == 0,
            "OrdinaryExitDidNotPersistBackpackPickup");
        _checks.Add(new { Stage = "OrdinaryPickupMovementComplete", Passed = true, ItemId = item, BackpackId = bag,
            OriginalItemCount = ids.Length, RemainingCharges = charges, InitialGroundOwner = worldOwner, SecondGroundOwner = secondOwner,
            Covered = "Starter Fire self-drop, real WASD outbound >6m/return, fresh DS endpoints, original F Kind15, real Mouse nearby-to-backpack, voluntary exit" });
    }

    private Task<JsonObject> WaitFocusedGround(string item, string label) => WaitClient(r => Flag(r, "MovementCaptured")
        && Text(r, "NearbyFocusedItemId") == item && Signed(r, "NearbyFocusedPickupId") != 0
        && !string.IsNullOrEmpty(Text(r, "NearbyFocusedWorldOwner")), label, 8);

    private static void AssertOrdinaryInventory(JsonObject report, string[] originalIds, string item, int charges, string label)
    {
        Require(Objects(report["Inventory"], "Items").Select(i => Text(i, "InstanceId")).Order(StringComparer.Ordinal).SequenceEqual(originalIds),
            label + "ChangedOriginalInstanceSet");
        Require(CardCount(report, item) == charges && Number(report, "DriverCastCount") == 0
            && Number(report, "ProbeCastSequence") == 0 && !Flag(report, "PlayerCastObserved"), label + "UnexpectedCastOrChargeChange");
    }

    private readonly record struct WorldPoint(double X, double Y, double Z);
    private static double Real(JsonNode? node, string key)
    {
        Require(node?[key] is JsonValue value && value.TryGetValue<double>(out double number) && double.IsFinite(number), "MissingFiniteDiagnostic" + key);
        return node![key]!.GetValue<double>();
    }
    private static WorldPoint ReadPoint(JsonNode? node) => new(Real(node, "x"), Real(node, "y"), Real(node, "z"));
    private static WorldPoint MovementPosition(JsonObject report)
    {
        Require(Flag(report, "MovementCaptured"), "MovementObservationNotCaptured");
        return ReadPoint(report["MovementObservation"]?["Position"]);
    }
    private static double PlanarDistance(WorldPoint a, WorldPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));
    private static double Distance(WorldPoint a, WorldPoint b) => Math.Sqrt(Math.Pow(PlanarDistance(a, b), 2) + Math.Pow(a.Y - b.Y, 2));
    private static void CheckMovementBounds(JsonObject report)
    {
        var p = MovementPosition(report); double yaw = Real(report, "ViewYaw");
        Require(p.X is >= -6.4 and <= -3.5 && p.Y is >= -.5 and <= 1.5 && p.Z is >= -10 and <= 1,
            "OrdinaryMovementLeftReviewedFloorCorridor");
        Require(Math.Abs(Math.IEEERemainder(yaw, 360)) <= 3, "OrdinaryMovementViewYawChanged");
        Require(Flag(report, "CanAdvance") && !Flag(report, "Frozen") && Flag(report["MovementObservation"], "HasFocus") && !Flag(report["MovementObservation"], "InputSuppressed")
            && !Flag(report["MovementObservation"], "OnlineInputBlocked"), "OrdinaryMovementInputGateClosed");
    }

    private async Task MoveToPoint(WorldPoint target, string label)
    {
        Stage(label); int stalled = 0;
        for (int pulse = 0; pulse < 12; pulse++)
        {
            _deadline.ThrowIfCancellationRequested();
            CheckMovementBounds(_lastClient!); var before = MovementPosition(_lastClient!);
            double distance = PlanarDistance(before, target);
            if (distance <= .45) return;
            double dx = target.X - before.X, dz = target.Z - before.Z;
            bool alongZ = Math.Abs(dz) >= Math.Abs(dx);
            string key = alongZ ? dz > 0 ? "W" : "S" : dx > 0 ? "D" : "A";
            double remaining = alongZ ? Math.Abs(dz) : Math.Abs(dx);
            // 5m/s 仅作保守 pulse 初值，0.8 系数留刹停余量；实际终点与无进展都由新位置裁决。
            double duration = Math.Clamp(remaining / 5 * .8, .05, 1);
            ulong previousPulse = Number(_lastClient, "SyntheticMovePulseSequence"), previousInput = Number(_lastClient, "InputSequence");
            Require(!Flag(_lastClient, "SyntheticMovePulseActive")
                && Number(_lastClient, "SyntheticMoveCompletedSequence") == previousPulse, "PreviousMovementPulseNotReleased");
            var queued = await Issue(new JsonObject { ["Action"] = "synthetic-move", ["Key"] = key, ["DurationSeconds"] = duration });
            ulong sequence = Number(queued, "SyntheticMovePulseSequence");
            Require(Flag(queued, "SyntheticMovementAllowed") && sequence == previousPulse + 1, "MovementPulseSequenceDidNotAdvance");
            var completed = await WaitClient(r => Number(r, "SyntheticMovePulseSequence") == sequence
                && Number(r, "SyntheticMoveCompletedSequence") == sequence && !Flag(r, "SyntheticMovePulseActive"), label + "PulseCompleted", 8);
            CheckMovementBounds(completed);
            var observation = completed["SyntheticMoveObservation"];
            double actionX = Real(observation?["MoveActionValue"], "x"), actionY = Real(observation?["MoveActionValue"], "y");
            double processedX = Real(observation?["ProcessedMove"], "x"), processedY = Real(observation?["ProcessedMove"], "y");
            double expectedX = key == "D" ? 1 : key == "A" ? -1 : 0, expectedY = key == "W" ? 1 : key == "S" ? -1 : 0;
            Require(Flag(completed, "SyntheticMoveActionObserved") && Text(completed, "SyntheticMovePulseKey") == key
                && Number(completed, "InputSequence") > previousInput
                && actionX * expectedX + actionY * expectedY > .5 && processedX * expectedX + processedY * expectedY > .5
                && Flag(observation, "HasFocus") && !Flag(observation, "InputSuppressed") && !Flag(observation, "OnlineInputBlocked"), "MovementPulseDidNotReachOriginalAction");
            Require(new[] { "W", "A", "S", "D" }.All(k => !("," + Text(completed, "SyntheticHeldKeys") + ",").Contains("," + k + ",", StringComparison.Ordinal)),
                "MovementPulseKeyStillHeld");
            var after = MovementPosition(completed);
            stalled = distance - PlanarDistance(after, target) < .03 ? stalled + 1 : 0;
            _checks.Add(new { Stage = label + "Pulse", Passed = true, Key = key, DurationSeconds = duration, Sequence = sequence,
                InputBefore = previousInput, InputAfter = Number(completed, "InputSequence"), Before = before, After = after,
                Observation = observation?.DeepClone() });
            await Save(label + "-pulse-" + pulse, completed);
            Require(stalled < 3, "OrdinaryMovementMadeNoProgress");
        }
        throw new CaseFailure(label + "PulseBudgetExceeded");
    }

    private async Task VerifyMovementEndpoint(string label, WorldPoint spawn, WorldPoint ground, bool returning)
    {
        Stage(label);
        Require(!Flag(_lastClient, "SyntheticMovePulseActive"), "EndpointMovementStillActive");
        DateTimeOffset since = DateTimeOffset.UtcNow; ulong tick = Number(_lastClient, "ServerTick");
        // DS state 由原 Heartbeat 稀疏落盘。只在两个停步终点等新样本，不能把此前出生点报告当移动证据。
        var ds = await WaitDs(r => DateTimeOffset.TryParse(Text(r, "RecordedAtUtc"), out var recorded) && recorded >= since
            && Number(r, "MovementTick") >= tick && r["ActorPositions"] is JsonArray positions && positions.Count >= 1,
            label + "FreshDs", 25);
        var client = await WaitClient(r => DateTimeOffset.TryParse(Text(r, "RecordedAtUtc"), out var recorded) && recorded >= since
            && Number(r, "ServerTick") >= Number(ds, "MovementTick"), label + "CurrentClient", 5);
        CheckMovementBounds(client);
        var authority = ReadPoint(ds["ActorPositions"]![0]); var local = MovementPosition(client);
        Require(!Flag(ds, "Frozen") && Flag(ds, "CanSimulate") && Number(ds, "WorldEpoch") == Number(client, "WorldEpoch")
            && Distance(authority, local) <= .5, label + "AuthorityClientPositionMismatch");
        Require(returning ? Distance(authority, ground) <= 3 && Distance(local, ground) <= 3
            : PlanarDistance(authority, spawn) > 6 && PlanarDistance(local, spawn) > 6, label + "DistanceGateFailed");
        _checks.Add(new { Stage = label, Passed = true, Authority = authority, Client = local, Ground = ground,
            MovementTick = Number(ds, "MovementTick"), RecordedAtUtc = Text(ds, "RecordedAtUtc"), ClientAuthorityDistance = Distance(authority, local) });
        await Save(label + "-fresh-ds", ds); await Save(label + "-client", client);
    }

    private async Task DragOrdinaryGroundToBackpack(string item, string bag, string worldOwner, int charges)
    {
        Stage("OrdinaryRealMouseNearbyToBackpack");
        await Issue(new JsonObject { ["Action"] = "ui-open" });
        var before = await WaitClient(r => Flag(r["Ui"], "IsOpen") && Flag(r["Ui"]?["BackpackGridTarget"], "Available"), "BackpackGridTargetReady", 8);
        var target = before["Ui"]!["BackpackGridTarget"]!;
        Require(Text(target, "OwnerId") == bag && Signed(target, "GridX") == 2 && Signed(target, "GridY") == 2, "BackpackGridTargetOwnerMismatch");
        var geometry = await CaptureUiInput("ordinary-drag-geometry");
        var source = Objects(geometry, "Nodes").SingleOrDefault(n => Text(n, "Kind") == "InventoryItem"
            && Text(n, "ItemId") == item && Text(n, "LocationKind") == "WorldItem") ?? throw new CaseFailure("NearbyOriginalItemScreenNodeMissing");
        Require(Text(source, "OwnerId") == worldOwner, "NearbyScreenNodeGroundOwnerMismatch");
        double sx = Real(source["Center"], "x"), sy = Real(source["Center"], "y");
        double tx = Real(target["Point"], "x"), ty = Real(target["Point"], "y");
        string sourcePath = Text(source, "Path"), targetPath = Text(target, "Path");
        Require(!string.IsNullOrEmpty(sourcePath) && !string.IsNullOrEmpty(targetPath)
            && Math.Abs(tx - sx) + Math.Abs(ty - sy) > 32, "MouseDragGeometryInvalid");
        await SyntheticPointerAt(sx, sy, false);
        VerifyUiPointer(await CaptureUiInput("ordinary-source-hover"), sx, sy, sourcePath, false, false);
        ulong sequence = Number(_lastClient, "ClientInventorySequence"), previous = Number(_lastClient?["LastInventoryIntent"], "CommandId");
        Exception? failed = null;
        try
        {
            await SyntheticPointerAt(sx, sy, true);
            VerifyUiPointer(await CaptureUiInput("ordinary-source-pressed"), sx, sy, sourcePath, true, false);
            // 先跨过 EventSystem Drag Threshold，再送最终位置；没有调用 BeginDrag/OnDrop 或工具 ui-drag。
            double length = Math.Sqrt((tx - sx) * (tx - sx) + (ty - sy) * (ty - sy));
            await SyntheticPointerAt(sx + (tx - sx) / length * 24, sy + (ty - sy) / length * 24, true);
            var dragging = await CaptureUiInput("ordinary-drag-threshold");
            Require(Flag(dragging, "InventoryIsDragging") && PointerState(dragging, true, true), "RealMouseDidNotBeginDrag");
            await SyntheticPointerAt(tx, ty, true);
            VerifyUiPointer(await CaptureUiInput("ordinary-grid-drag-over"), tx, ty, targetPath, true, true);
            await SyntheticPointerAt(tx, ty, false);
            var after = await WaitClient(r => CommandCommitted(r, previous, sequence, value => Location(value, item) == 1
                && Owner(value, item) == bag && CardCount(value, item) == charges), "OrdinaryMousePickupOwnerInstalled", 15);
            var uiReceipt = after["Ui"]?["LastOnlineCommandReceipt"];
            var installed = Objects(after["Inventory"], "Items").Single(i => Text(i, "InstanceId") == item);
            Require(Number(after["LastInventoryIntent"], "Kind") == 15 && Text(after["LastInventoryIntent"], "ItemId") == item
                && Text(after["LastInventoryIntent"], "WorldOwnerId") == worldOwner && !Flag(after["LastInventoryIntent"], "AutoPlace")
                && Text(after["LastInventoryIntent"], "TargetBackpackId") == bag
                && Signed(after["LastInventoryIntent"], "TargetLocationKind") == 1
                && Signed(after["LastInventoryIntent"], "GridX") == 2 && Signed(after["LastInventoryIntent"], "GridY") == 2
                && Signed(after["LastInventoryIntent"], "QuarterTurns") == 0
                && Signed(after, "LastInventoryCallerId") == Signed(after["Ui"], "ControllerId")
                && Signed(after["Ui"], "ControllerId") == Signed(before["Ui"], "ControllerId")
                && uiReceipt != null && Flag(uiReceipt, "Success")
                && Number(uiReceipt, "CommandId") == Number(after["LastInventoryIntent"], "CommandId")
                && Number(uiReceipt, "ConnectionEpoch") == Number(after, "ConnectionEpoch")
                && Number(uiReceipt, "InventoryVersion") == Number(after["LastMatchingInventoryReceipt"], "InventoryVersion")
                && Signed(installed, "X") == 2 && Signed(installed, "Y") == 2 && Signed(installed, "QuarterTurns") == 0,
                "RealMouseDidNotSubmitOriginalGridPickup");
            var released = await CaptureUiInput("ordinary-grid-released");
            Require(!Flag(released, "InventoryIsDragging") && PointerState(released, false, false)
                && !Objects(released, "Nodes").Any(n => Text(n, "Kind") == "InventoryItem" && Text(n, "ItemId") == item
                    && Text(n, "LocationKind") == "WorldItem"), "RealMouseDragDidNotReleaseOrGroundViewRemained");
            Record("OrdinaryRealMousePickup", after);
        }
        catch (Exception error) { failed = error; throw; }
        finally
        {
            if (_syntheticPointerHeld)
                try { await SyntheticPointerAt(tx, ty, false); }
                catch (Exception error) when (failed != null)
                { _checks.Add(new { Stage = "OrdinaryMouseRelease", Passed = false, Failure = error.GetType().Name }); }
        }
    }

    private async Task SyntheticPointerAt(double x, double y, bool pressed)
    {
        if (pressed) _syntheticPointerHeld = true;
        var queued = await Issue(new JsonObject { ["Action"] = "synthetic-pointer-at", ["Key"] = "leftButton", ["Pressed"] = pressed,
            ["PointerPosition"] = new JsonObject { ["x"] = x, ["y"] = y } });
        Require(Flag(queued, "SyntheticPointerAllowed") && Signed(queued, "SyntheticPointerDeviceId") > 0, "SyntheticPointerAtNotInstalled");
        await WaitClient(r => Flag(r, "SyntheticLeftPressed") == pressed && Math.Abs(Real(r["SyntheticPointerPosition"], "x") - x) <= 1
            && Math.Abs(Real(r["SyntheticPointerPosition"], "y") - y) <= 1, "SyntheticPointerAtProcessed", 8);
        if (!pressed) _syntheticPointerHeld = false;
    }

    private async Task<JsonObject> CaptureUiInput(string label)
    {
        string command = Path.Combine(_private, "ui-command.ui"), output = Path.Combine(_private, "ui-report.ui.json");
        Require(!File.Exists(command), "PreviousUiReadOnlyRequestPending");
        DateTimeOffset since = DateTimeOffset.UtcNow;
        // 只读 UI Probe 以文件存在触发，不解析命令体；独立路径不会被旧 Portal 的命令入口消费。
        await File.WriteAllTextAsync(command, "read", _deadline);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < 6)
        {
            _deadline.ThrowIfCancellationRequested();
            var report = await ReadJson(output);
            if (!File.Exists(command) && report != null && DateTimeOffset.TryParse(Text(report, "RecordedAtUtc"), out var recorded) && recorded >= since)
            {
                await Save(label + "-ui", report);
                Require(Text(report, "DiagnosticError") == "" && Flag(report, "InventoryIsOpen") && Flag(report, "ModuleEnabled")
                    && Flag(report, "Focused") && Flag(report, "EventSystemFocused") && Text(report, "ModuleName") == "InputSystemUIInputModule"
                    && Flag(report, "PointEnabled") && Flag(report, "ClickEnabled"), "UiInputDiagnosticNotUsable");
                return report;
            }
            await Task.Delay(100, _deadline);
        }
        throw new CaseFailure("UiReadOnlyRequestTimedOut");
    }
    private static bool PointerState(JsonObject ui, bool pressed, bool dragging) => (ui["PointerStates"] as JsonArray)?.Any(row => row != null
        && row.GetValue<string>().Contains("pressed=" + pressed, StringComparison.Ordinal)
        && row.GetValue<string>().Contains("dragging=" + dragging, StringComparison.Ordinal)) == true;
    private static void VerifyUiPointer(JsonObject ui, double x, double y, string path, bool pressed, bool dragging)
    {
        string hit = (ui["RaycastTargets"] as JsonArray)?.FirstOrDefault()?.GetValue<string>() ?? "";
        Require(hit == path || hit.StartsWith(path + "/", StringComparison.Ordinal), "UiFirstRaycastMissedExpectedTarget");
        Require(Math.Abs(Real(ui["UiPoint"], "x") - x) <= 1 && Math.Abs(Real(ui["UiPoint"], "y") - y) <= 1
            && Math.Abs(Real(ui["MousePosition"], "x") - x) <= 1 && Math.Abs(Real(ui["MousePosition"], "y") - y) <= 1
            && PointerState(ui, pressed, dragging), "UiPointerStateDidNotMatchProcessedMouse");
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
        if (options.Dungeon == "home")
        {
            // 研究台仍受原空间资格约束；关闭研究窗后，紧凑Tab编辑自己的卡必须和P7一样可用。
            // 旧验收在出生点不满足研究资格时跳过全部移卡，恰好漏掉本次用户反馈，不能再把跳过算覆盖。
            bool researchOpened = await HomeResearchCommand(16, true);
            if (researchOpened) await HomeResearchCommand(17, false);
        }
        await UiMove(cardId, "SpellStorage", null, 0, null, r => Location(r, cardId) == 7);
        await UiMove(cardId, "ProgramSlot", wand, 0, null, r => Location(r, cardId) == 2 && Owner(r, cardId) == wand);
        _checks.Add(new { Stage = "CardMovementCoverage", Passed = true, Covered = true,
            Reason = "Original compact UI callbacks and real receipts; Home does not borrow an open research session" });
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
            if (options.InputMode == "keyboard")
            {
                // 容器F走PickupContainer，不能证明用户现场的Spell/PickupItem分支。
                // 丢下的必须是刚消耗到28次的同一张卡；仍走原UI拖放，拾回后装回原杖再续既有流程。
                int remainingCharges = CardCount(_lastClient!, cardId);
                Require(remainingCharges == 28 && Location(_lastClient!, cardId) == 2 && Owner(_lastClient!, cardId) == wand,
                    "KeyboardSpellPickupSourceWasNotTheConsumedOwnedCard");
                await UiMove(cardId, "Ground", null, 0, null, r => Location(r, cardId) == -1);
                Require(Number(_lastClient!["LastInventoryIntent"], "Kind") == 14,
                    "KeyboardSpellPickupSetupDidNotUseOriginalUiDropItem");
                await PickupSpellByKeyboard(cardId, bag, remainingCharges);
                await UiMove(cardId, "ProgramSlot", wand, 0, null, r => Location(r, cardId) == 2
                    && Owner(r, cardId) == wand && CardCount(r, cardId) == remainingCharges);
            }
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
            ulong droppedBottleQuantity = Number(Objects(_lastClient!["Inventory"], "Items").Single(item => Text(item, "InstanceId") == bottle), "ContainerTotal");
            await UiMove(bottle, "Ground", null, 0, null, r => Location(r, bottle) == -1);
            if (options.InputMode == "keyboard") await PickupByKeyboard(bottle, bag, droppedBottleQuantity);
            else await UiMove(bottle, "Backpack", bag, 0, new JsonObject { ["GridX"] = 4, ["GridY"] = 4 }, r => Location(r, bottle) == 1);
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
        await OrganizeThroughOriginalButton();
        var exitedProfile = await ExitAndVerify(cardId, 28);
        if (thrownContainers.Count != 0)
        {
            Require(exitedProfile.Items.All(item => !thrownContainers.Contains(item.InstanceId)), "ThrownContainerRevivedAfterExit");
            _checks.Add(new { Stage = "ThrownContainerFinalProfile", Passed = true, ItemIds = thrownContainers.ToArray(),
                InputMode = options.InputMode,
                Covered = (options.InputMode == "keyboard" ? "Virtual Keyboard G down/up through original InputAction and Driver" : "Explicit direct Driver throw fallback")
                    + ", confirmed Owner removal and reliable exit Profile; no hardware G input" });
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
        if (options.InputMode == "keyboard") await KeyboardStroke("G");
        else await Issue(new JsonObject { ["Action"] = "container-throw" });
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
        if (IsCoop)
            Require(receipt.ActorId == 1 && receipt.RoomEpoch == _coopRoomEpoch
                && receipt.ConnectionEpoch == Number(finalClient, "ConnectionEpoch")
                && receipt.ConnectionEpoch == Number(finalClient["LastRunExitStatus"], "ConnectionEpoch"), "CoopActor1FinalReceiptEpochMismatch");
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
    private async Task OrganizeThroughOriginalButton()
    {
        Stage("OriginalOrganizeButton");
        await Issue(new JsonObject { ["Action"] = "ui-open" });
        string bag = Text(Objects(_lastClient!["Inventory"], "Items").Single(item => Number(item, "LocationKind") == 4), "InstanceId");
        ulong previous = Number(_lastClient["LastInventoryIntent"], "CommandId");
        await Issue(new JsonObject { ["Action"] = "ui-organize" });
        var report = await WaitClient(value =>
        {
            var intent = value["LastInventoryIntent"]; var receipt = value["LastMatchingInventoryReceipt"];
            ulong command = Number(intent, "CommandId");
            if (command == 0 || command == previous || Number(intent, "Kind") != 18
                || Text(intent, "ItemId") != bag || Number(receipt, "CommandId") != command) return false;
            if (!Flag(receipt, "Success")) throw new CaseFailure("OrganizeReceipt" + Text(receipt, "Failure"));
            // 空包/已整理包可以是合法no-op；版本至少到达真实收据，不强迫伪造一次布局变化。
            return Number(value, "ClientInventoryVersion") >= Number(receipt, "InventoryVersion");
        }, "OriginalOrganizeOwnerInstalled", 15);
        _checks.Add(new { Stage = "OriginalOrganizeButton", Passed = true, BagId = bag,
            Version = Number(report, "ClientInventoryVersion"),
            Covered = "Original shared Controller button, Kind18 matching success receipt and installed Owner; no local layout writes or mouse Raycast claim" });
        Record("OriginalOrganizeOwnerInstalled", report);
    }

    private async Task Inventory(JsonObject intent, Func<JsonObject, bool> installed)
    {
        Stage("InventorySelection"); ulong sequence = Number(_lastClient, "ClientInventorySequence");
        ulong previousCommand = Number(_lastClient?["LastInventoryIntent"], "CommandId");
        // 这里是工具刚构造的CLR int JsonValue，不是从Wire解析的JsonElement。
        // TryGetValue<ulong>不会自动转换int；静默退0会错误绕过Keyboard与HUD断言。
        // 此方法只接受选择命令，未知种类/缺字段必须失败，不能静默降级为direct。
        int kind = intent["Kind"]?.GetValue<int>() ?? throw new CaseFailure("SelectionKindMissing");
        int targetSlot = intent["TargetSlot"]?.GetValue<int>() ?? throw new CaseFailure("SelectionSlotMissing");
        Require(kind is 1 or 3, "InventorySelectionKindInvalid");
        bool selection = true;
        int heldSlot = checked(targetSlot + (kind == 3 ? 3 : 0));
        if (selection && options.InputMode == "keyboard")
        {
            Require(heldSlot is >= 0 and < 10, "KeyboardSelectionSlotOutsideHotbar");
            await KeyboardStroke("1234567890"[heldSlot].ToString());
        }
        else await Issue(new JsonObject { ["Action"] = "inventory", ["Intent"] = intent });
        var report = await WaitClient(r => CommandCommitted(r, previousCommand, sequence,
            value => installed(value) && (!selection || HudSelectionInstalled(value, heldSlot, Text(intent, "ItemId")))), "InventorySelectionAndHud", 15);
        if (selection)
            Require(Signed(report, "LocalInventoryDriverId") != 0 && Signed(report, "LastInventoryCallerId") == Signed(report, "LocalInventoryDriverId"),
                "HeldSelectionDidNotUseTheOriginalDriverCaller");
        if (options.InputMode == "keyboard")
            _checks.Add(new { Stage = "OriginalKeyboardSelection", Passed = true,
                Key = "1234567890"[heldSlot].ToString(), HeldSlot = heldSlot, Kind = kind,
                DriverCallerId = Signed(report, "LastInventoryCallerId"), ItemId = Text(intent, "ItemId"),
                Covered = "Processed Keyboard down/up, original Driver caller, success receipt, newer Owner and actual single selected HUD Frame" });
        Record("InventorySelection", report);
    }

    private async Task PickupByKeyboard(string item, string bag, ulong expectedQuantity)
    {
        Stage("OriginalKeyboardGroundPickup");
        var after = await PickupOriginalProxyByKeyboard(item, 8, value => Location(value, item) == 1
            && Owner(value, item) == bag, "KeyboardPickup");
        Require(Number(Objects(after["Inventory"], "Items").Single(value => Text(value, "InstanceId") == item), "ContainerTotal") == expectedQuantity,
            "KeyboardPickupChangedContainerQuantity");
        _checks.Add(new { Stage = "OriginalKeyboardGroundPickup", Passed = true, ItemId = item,
            ContainerQuantity = expectedQuantity, InputMode = options.InputMode,
            Covered = "UI drop then real virtual Keyboard F through NearbyPickupInput, original WorldItemPickup caller, matching receipt and Owner version" });
        Record("KeyboardPickupOwnerInstalled", after);
    }

    private async Task PickupSpellByKeyboard(string item, string bag, int expectedCharges)
    {
        Stage("OriginalKeyboardSpellGroundPickup");
        // 自动拾取允许正式规则先放SpellStorage，满时回退当前包；两种结果都必须归属本玩家。
        // 不能用“附近消失”代替Owner安装，也不能只见Kind15就忽略原proxy Caller和实际成功收据。
        var after = await PickupOriginalProxyByKeyboard(item, 15, value => Location(value, item) == 7 && Owner(value, item) == _player
            || Location(value, item) == 1 && Owner(value, item) == bag, "KeyboardSpellPickup");
        var installed = Objects(after["Inventory"], "Items").Single(value => Text(value, "InstanceId") == item);
        Require(Flag(installed, "IsSpell") && !Flag(installed, "HasContainerState")
            && CardCount(after, item) == expectedCharges && Number(installed, "RemainingCharges") == (ulong)expectedCharges,
            "KeyboardPickupChangedSpellOrRemainingCharges");
        _checks.Add(new { Stage = "OriginalKeyboardSpellGroundPickup", Passed = true, ItemId = item,
            RemainingCharges = expectedCharges, LocationKind = Location(after, item), OwnerId = Owner(after, item), InputMode = options.InputMode,
            Covered = "Original UI DropItem then virtual Keyboard F PickupItem (15), same Spell instance, original WorldItemPickup caller, matching receipt and Owner version; charges unchanged" });
        Record("KeyboardSpellPickupOwnerInstalled", after);
    }

    private async Task<JsonObject> PickupOriginalProxyByKeyboard(string item, int kind, Func<JsonObject, bool> installed, string evidencePrefix)
    {
        await Issue(new JsonObject { ["Action"] = "ui-close" });
        var before = await WaitClient(r => Flag(r, "NearbyInputEnabled") && Number(r, "NearbyPickupCount") > 0
            && Text(r, "NearbyFocusedItemId") == item && Signed(r, "NearbyFocusedPickupId") != 0, evidencePrefix + "FocusedOriginalInstance", 8);
        ulong sequence = Number(before, "ClientInventorySequence"), previous = Number(before["LastInventoryIntent"], "CommandId");
        long caller = Signed(before, "NearbyFocusedPickupId");
        await KeyboardStroke("F");
        var after = await WaitClient(r => CommandCommitted(r, previous, sequence, installed), evidencePrefix + "OwnerInstalled", 15);
        Require(Number(after["LastInventoryIntent"], "Kind") == (ulong)kind && Text(after["LastInventoryIntent"], "ItemId") == item
            && Flag(after["LastInventoryIntent"], "AutoPlace") && Signed(after, "LastInventoryCallerId") == caller,
            "KeyboardPickupDidNotUseOriginalWorldItemProxy");
        return after;
    }

    private async Task KeyboardStroke(string key)
    {
        Exception? failed = null;
        try { await SyntheticKey(key, true); }
        catch (Exception error) { failed = error; throw; }
        finally
        {
            // G需要真实松开才投掷；数字/F也必须释放，下一次不能继承上一次按住。
            // 失败时仍尽力释放，不以释放错误覆盖原始失败；总超时后的剩余键由原退出清理窗口处理。
            try { await SyntheticKey(key, false); }
            catch (Exception error) when (failed != null)
            { _checks.Add(new { Stage = "SyntheticKeyRelease", Key = key, Passed = false, Failure = error.GetType().Name }); }
        }
    }
    private async Task SyntheticKey(string key, bool pressed)
    {
        if (pressed)
        {
            // 默认1Hz报告已含真实Application.isFocused。读取新鲜报告再排down，不额外发一个
            // report命令延长Rapid持鼠标时间；失焦是本次Native输入前置不足，不能误判成拾取倒退。
            // direct/review也不绕过此门：review根本不调用SyntheticKey，正式玩家focus规则不变。
            RequireNativeInputFocus(await ReadJson(_clientReport) ?? _lastClient, key);
            _heldSyntheticKeys.Add(key);
        }
        var queued = await Issue(new JsonObject { ["Action"] = "synthetic-key", ["Key"] = key, ["Pressed"] = pressed });
        Require(Flag(queued, "SyntheticKeyboardAllowed") && Signed(queued, "SyntheticKeyboardDeviceId") > 0,
            "SyntheticKeyboardNotExplicitlyInstalled");
        var processed = await WaitClient(r =>
        {
            // 排队之后也可能被另一个图形窗口夺走焦点。release仍无条件尽力执行，
            // down则明确报告失焦，不等待8秒后把设备未处理包装成Gameplay无响应。
            if (pressed) RequireNativeInputFocus(r, key);
            return Signed(r, "SyntheticKeyboardDeviceId") > 0
                && ("," + Text(r, "SyntheticHeldKeys") + ",").Contains("," + key + ",", StringComparison.Ordinal) == pressed;
        },
            "SyntheticKeyboardProcessed" + key + (pressed ? "Down" : "Up"), 8);
        _checks.Add(new { Stage = "SyntheticKeyboardProcessed", Passed = true, Key = key, Pressed = pressed,
            DeviceId = Signed(processed, "SyntheticKeyboardDeviceId"), HeldKeys = Text(processed, "SyntheticHeldKeys") });
        if (!pressed) _heldSyntheticKeys.Remove(key);
    }
    private void RequireNativeInputFocus(JsonObject? report, string key)
    {
        bool hasTime = DateTimeOffset.TryParse(Text(report, "RecordedAtUtc"), out var captured);
        double? age = hasTime ? (DateTimeOffset.UtcNow - captured).TotalSeconds : null;
        if (hasTime && age >= 0 && age <= 2 && Flag(report, "Focused")) return;
        _checks.Add(new { Stage = "NativeInputFocusPrecondition", Passed = false, Key = key,
            ReportAtUtc = Text(report, "RecordedAtUtc"), AgeSeconds = age, Focused = Flag(report, "Focused"),
            SyntheticKeyboardDeviceId = Signed(report, "SyntheticKeyboardDeviceId"),
            SyntheticKeyboardEnabled = Flag(report, "SyntheticKeyboardEnabled"),
            SyntheticKeyboardCanRunInBackground = Flag(report, "SyntheticKeyboardCanRunInBackground"),
            BackgroundBehavior = Text(report, "InputBackgroundBehavior"), RunInBackground = Flag(report, "RunInBackground") });
        throw new CaseFailure("NativeInputFocusUnavailable");
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
            if (IsCoop && _peer?.Process != null && !_peer.ExitVerified && _peer.Process.HasExited)
                throw new CaseFailure("CoopPeerClientExited" + _peer.Process.ExitCode);
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
        const long maximumDiagnosticReportBytes = 32L * 1024 * 1024;
        try
        {
            if (!File.Exists(path)) return null;
            // Native Probe用覆盖写；默认ReadAllText的FileShare.Read会禁止对端新开写句柄，
            // 已在单Mouse日志出现Sharing violation。读者允许写入/替换，完整性仍由JSON解析
            // 与上层命令/新鲜Tick确认，而不是用文件锁阻止真实PlayerLoop报告。
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > maximumDiagnosticReportBytes) throw new CaseFailure("DiagnosticReportTooLarge");
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true, 4096, leaveOpen: true);
            string text = await reader.ReadToEndAsync();
            // 共享读期间文件可能增长；读前大小不能替代读完后的字节边界。
            if (stream.Length > maximumDiagnosticReportBytes || System.Text.Encoding.UTF8.GetByteCount(text) > maximumDiagnosticReportBytes)
                throw new CaseFailure("DiagnosticReportTooLarge");
            return JsonNode.Parse(text) as JsonObject;
        }
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
