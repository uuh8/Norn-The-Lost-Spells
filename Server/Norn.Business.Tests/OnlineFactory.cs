using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Game.NetworkProtocol;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>实际 ASP.NET Host + 独立 MySQL test Schema；缺少环境就明确失败，不退回内存假库。</summary>
public sealed class OnlineFactory : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    public readonly MutableOnlineClock Clock = new();
    public string Connection { get; }
    public ContentManifest Manifest { get; }
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
        _manifestPath = Environment.GetEnvironmentVariable("NORN_CONTENT_MANIFEST") ?? throw new InvalidOperationException("缺少正式导出的 Manifest 路径。");
        Manifest = JsonSerializer.Deserialize<ContentManifest>(File.ReadAllText(_manifestPath), Json)!;
        // 防误用：集成测试只能使用专用实例/专用 Schema，绝不修改已有个人数据库。
        var connection = new MySqlConnector.MySqlConnectionStringBuilder(Connection);
        if (connection.Server != "127.0.0.1" || connection.Port != 13384 || connection.Database != "norn_online_test") throw new InvalidOperationException("拒绝在非 Norn test Schema 运行集成测试。");
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Online:Enabled"] = "true", ["Online:AutoMigrate"] = "true", ["ConnectionStrings:Online"] = Connection,
            ["Online:MigrationConnectionString"] = _migration, ["Online:ManifestPath"] = _manifestPath,
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
        client.DefaultRequestHeaders.Add("X-Norn-Protocol", "1");
        return login.Token;
    }
    public LoginAccountRequest LoginRequest(string username) => new() { Username = username, Password = Password, ProtocolVersion = Manifest.ProtocolVersion, ContentManifestHash = Manifest.Hash };
    public static InventoryMoveRequest Move(OnlineProfileReply profile, int item = 0, int slot = 0) => new()
    { CommandId = Guid.NewGuid().ToString("N"), ExpectedVersion = profile.Version, InstanceId = profile.Items[item].InstanceId, Destination = OnlineInventoryAreas.Warehouse, Slot = slot };
    public const string Password = "Norn_test_password_2026!"; // 仅测试账号使用，不是开发库或任何真实用户密码。
}
public sealed class MutableOnlineClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan time) => _now += time;
}
