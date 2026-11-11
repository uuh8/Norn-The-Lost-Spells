using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>实际 ASP.NET Host + 独立 MySQL test Schema；缺少环境就明确失败，不退回内存假库。</summary>
public class OnlineFactory : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    public readonly MutableOnlineClock Clock = new();
    public string Connection { get; }
    internal string MigrationConnection => _migration; // 仅受 guard 保护的测试 Schema；迁移隔离测试不公开/输出凭据。
    public ContentManifest Manifest { get; }
    public const string GameplayHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _migration, _manifestPath;
    private readonly int _authLimit;
    private readonly bool _rooms, _instances;
    public OnlineFactory(int authLimit = 120, bool rooms = true, bool instances = false)
    {
        _authLimit = authLimit;
        _rooms = rooms;
        _instances = instances;
        string file = Environment.GetEnvironmentVariable("NORN_N1_CONFIG") ?? throw new InvalidOperationException("N1 集成测试需要显式设置本机私有配置路径。");
        using var config = JsonDocument.Parse(File.ReadAllText(file));
        Connection = config.RootElement.GetProperty("Test").GetString()!;
        _migration = config.RootElement.GetProperty("TestMigration").GetString()!;
        // 两条连接都必须先过门再加载Host；Runtime正确不能替Migration连接授权DDL。
        RequireIsolatedConnections(Connection, _migration);
        _manifestPath = Environment.GetEnvironmentVariable("NORN_CONTENT_MANIFEST") ?? throw new InvalidOperationException("缺少正式导出的 Manifest 路径。");
        Manifest = JsonSerializer.Deserialize<ContentManifest>(File.ReadAllText(_manifestPath), Json)!;
    }
    /// <summary>纯配置校验，不开连接、不输出原串；可在没有MySQL的单元测试中验证两种权限的隔离。</summary>
    internal static void RequireIsolatedConnections(string runtime, string migration)
    {
        Require(runtime, "Runtime"); Require(migration, "Migration");
        static void Require(string value, string role)
        {
            try
            {
                var connection = new MySqlConnector.MySqlConnectionStringBuilder(value);
                if (connection.Server == "127.0.0.1" && connection.Port == 13384 && connection.Database == "norn_online_test") return;
            }
            catch (ArgumentException) { /* 格式错误同样拒绝；不能把含密码的原异常拼进测试输出。 */ }
            throw new InvalidOperationException($"拒绝在非 Norn test Schema 使用{role}连接运行集成测试。");
        }
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Online:Enabled"] = "true", ["Online:AutoMigrate"] = "true", ["ConnectionStrings:Online"] = Connection,
            ["Online:MigrationConnectionString"] = _migration, ["Online:ManifestPath"] = _manifestPath,
            ["Online:GameplayConfigHash"] = GameplayHash,
            ["Online:SessionMinutes"] = "30", ["Online:AuthRequestsPerMinute"] = _authLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Online:RoomsEnabled"] = _rooms.ToString(), ["Online:Instances:Enabled"] = _instances.ToString()
        }));
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            // 数据库契约测试手动推进Clock/回收，不让后台Supervisor杀测试Host或抢测量时序。
            var supervisor = services.FirstOrDefault(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && d.ImplementationType?.FullName == "Norn.Business.Features.Rooms.RoomInstanceSupervisor");
            if (supervisor != null) services.Remove(supervisor);
        });
    }
    public async Task<(HttpClient Client, OnlineProfileReply Profile, string Username, string Token)> NewPlayerAsync(string? username = null)
    {
        var client = CreateClient(); username ??= "n1_" + Guid.NewGuid().ToString("N")[..18];
        using var registered = await client.PostAsJsonAsync("/api/online/accounts", new RegisterAccountRequest { Username = username, Password = Password }, Json);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        string token = await LoginAsync(client, username);
        var profile = await client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", Json);
        Assert.NotNull(profile);
        return (client, profile, username, token);
    }
    public async Task<string> LoginAsync(HttpClient client, string username)
    {
        using var response = await client.PostAsJsonAsync("/api/online/sessions", LoginRequest(username), Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = (await response.Content.ReadFromJsonAsync<LoginAccountReply>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        client.DefaultRequestHeaders.Add("X-Norn-Manifest", Manifest.Hash);
        client.DefaultRequestHeaders.Add("X-Norn-Protocol", ProtocolCompatibility.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return login.Token;
    }
    public LoginAccountRequest LoginRequest(string username) => new() { Username = username, Password = Password, ProtocolVersion = Manifest.ProtocolVersion, ContentManifestHash = Manifest.Hash };
    public async Task<(HttpClient Client, OnlineProfileReply Profile, string Username, string Token)> NewInventoryPlayerAsync()
    {
        var player = await NewPlayerAsync();
        // Move 回归显式构建普通背包测试物品；产品注册仍只有真实装备的一杖一卡，不能反向迁就旧测试赠送副本。
        await using var db = new MySqlConnector.MySqlConnection(Connection);
        for (int slot = 0; slot < 2; slot++)
            await db.ExecuteAsync("""
                INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges)
                VALUES(@Instance,@Player,'wandtest.fire',1,5,FALSE);
                INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot) VALUES(@Instance,@Player,'Backpack',@Slot)
                """, new { Instance = Guid.NewGuid().ToString("N"), Player = player.Profile.PlayerId, Slot = slot });
        return (player.Client, (await player.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", Json))!, player.Username, player.Token);
    }
    public static InventoryMoveRequest Move(OnlineProfileReply profile, int item = 0, int slot = 0) => new()
    { CommandId = Guid.NewGuid().ToString("N"), ExpectedVersion = profile.Version, InstanceId = profile.Items.Where(i => i.Area is "Backpack" or "Warehouse").ElementAt(item).InstanceId, Destination = OnlineInventoryAreas.Warehouse, Slot = slot };
    public const string Password = "Norn_test_password_2026!"; // 仅测试账号使用，不是开发库或任何真实用户密码。
}
public sealed class MutableOnlineClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan time) => _now += time;
}
