using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Game.NetworkProtocol;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MySqlConnector;
using Norn.Business.Bootstrap;
using Norn.Business.Content;
using Norn.Business.Features.Accounts;
using Norn.Business.Features.Inventory;
using Norn.Business.Features.Rooms;
using Norn.Business.Features.Runs;
using Norn.Business.Infrastructure;

namespace Norn.Business;

/// <summary>独立业务进程的组合入口；不引用 UnityEngine，不运行副本 Tick。</summary>
public sealed class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // 测试宿主/部署 Provider 可能在 Build 才完成配置覆盖；注册工厂，在容器建好后绑定，不能提前缓存 Enabled=false。
        builder.Services.AddSingleton(services => services.GetRequiredService<IConfiguration>().GetSection("Online").Get<OnlineOptions>() ?? new OnlineOptions());
        // Foundation 端点继续按自己的小预算检查；线上 JSON 有独立的 4 KiB 宿主上限。
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = builder.Configuration.GetValue<bool>("Online:Enabled") ? 4096 : ProtocolCompatibility.MaxPingPayloadBytes);
            builder.Services.TryAddSingleton<TimeProvider>(TimeProvider.System);
            builder.Services.AddSingleton<OnlineDatabase>();
            builder.Services.AddSingleton<ContentManifestValidator>();
            builder.Services.AddSingleton<AccountService>();
            builder.Services.AddSingleton<InventoryService>();
            builder.Services.AddSingleton<RoomTransactions>();
            builder.Services.AddSingleton<RoomService>();
            builder.Services.AddSingleton<RoomMatchmakingService>();
            builder.Services.AddSingleton<RoomInstanceService>();
            builder.Services.AddSingleton<RoomLauncher>();
            builder.Services.AddHostedService<RoomInstanceSupervisor>();
            builder.Services.AddSingleton<RunEntryService>();
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.IncludeFields = true;
                options.SerializerOptions.PropertyNamingPolicy = null;
                options.SerializerOptions.PropertyNameCaseInsensitive = false;
                // Client 不能悄悄夹带 LocalSave/Gold/PlayerId 字段；默认忽略未知字段会掩盖这类错误。
                options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            });
            builder.Services.AddAuthentication(SessionAuthenticationHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = 429;
                // 本机首版使用单个全局有界 limiter，避免按任意用户名创建无限状态；多实例限流留给远端阶段。
                options.AddFixedWindowLimiter("online-auth", policy =>
                { policy.PermitLimit = builder.Configuration.GetValue("Online:AuthRequestsPerMinute", 30); policy.Window = TimeSpan.FromMinutes(1); policy.QueueLimit = 0; policy.AutoReplenishment = true; });
            });
        var app = builder.Build();
        var online = app.Services.GetRequiredService<OnlineOptions>();
        app.MapFoundationEndpoints();
        if (online.Enabled)
        {
            if (online.SessionMinutes < 1 || online.SessionMinutes > 60 || online.AuthRequestsPerMinute < 1 || online.AuthRequestsPerMinute > 120)
                throw new InvalidOperationException("线上 Session/Rate Limit 配置超出范围。");
            _ = app.Services.GetRequiredService<ContentManifestValidator>();
            _ = app.Services.GetRequiredService<OnlineDatabase>(); // 启用模块却缺少 Runtime 连接串时，在监听前明确失败。
            if (online.AutoMigrate) await SchemaMigrator.ApplyAsync(online.MigrationConnectionString, app.Lifetime.ApplicationStopping);
            app.Use(async (context, next) =>
            {
                try { await next(context); }
                catch (OnlineFailure failure) { await failure.Result().ExecuteAsync(context); }
                catch (MySqlException exception)
                {
                    app.Logger.LogError("Online storage request failed. MySQL code {Code}", exception.Number);
                    context.Response.StatusCode = 503;
                    await context.Response.WriteAsJsonAsync(new OnlineErrorReply { Code = "StorageUnavailable" }, context.RequestAborted);
                }
            });
            app.UseRateLimiter();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapOnlineEndpoints();
            if (online.RoomsEnabled && online.Instances.Enabled) app.MapInstanceEndpoints();
            if (online.RoomsEnabled) app.MapRoomEndpoints(); // N2-2真实实例与入场尚未接好，部署默认不开放房间。
        }
        await app.RunAsync();
    }
}
