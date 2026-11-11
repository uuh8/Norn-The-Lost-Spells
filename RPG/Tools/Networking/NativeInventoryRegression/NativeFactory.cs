using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Norn.NativeInventoryRegression;

/// <summary>
/// 一次显式验收拥有的真实Kestrel Host。只允许现有私有MySQL的test Schema；
/// 不复制OnlineFactory的可变假时钟，不让Supervisor扫描历史Starting Run或控制其他测试进程。
/// MVC.Testing 10.0.13的本地XML已核：UseKestrel(port)必须早于CreateClient/Services，返回void。
/// </summary>
internal sealed class NativeFactory : WebApplicationFactory<Norn.Business.Program>
{
    private readonly Dictionary<string, string?> _settings;
    private readonly string _contentRoot;
    public SafeHostLog Logs { get; } = new();
    public NativeFactory(Dictionary<string, string?> settings, string contentRoot)
    { _settings = settings; _contentRoot = contentRoot; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(_contentRoot);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(_settings));
        builder.ConfigureLogging(logging => { logging.ClearProviders(); logging.AddProvider(Logs); });
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(TimeProvider.System);
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType?.FullName == "Norn.Business.Features.Rooms.RoomInstanceSupervisor").ToArray())
                services.Remove(descriptor);
        });
    }

    public static void RequireTestConnection(string value)
    {
        try
        {
            var parsed = new MySqlConnectionStringBuilder(value);
            if (parsed.Server == "127.0.0.1" && parsed.Port == 13384 && parsed.Database == "norn_online_test") return;
        }
        catch (ArgumentException) { }
        // 不能拼原连接串或解析异常；错误证据只说明隔离门失败。
        throw new CaseFailure("TestDatabaseGuardRejected");
    }
}

/// <summary>公开证据只保存Host错误类别/事件编号，不复制可能带凭据的异常正文或请求Body。</summary>
internal sealed class SafeHostLog : ILoggerProvider
{
    public ConcurrentQueue<object> Errors { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);
    public void Dispose() { }
    private sealed class Sink(SafeHostLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            owner.Errors.Enqueue(new { Utc = DateTime.UtcNow, Level = logLevel.ToString(), Category = category,
                EventId = eventId.Id, ExceptionType = exception?.GetType().Name });
        }
    }
}

internal sealed class CaseFailure(string code) : Exception(code) { public string Code { get; } = code; }
