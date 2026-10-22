using MySqlConnector;

namespace Norn.Business.Infrastructure;

/// <summary>DataSource 拥有连接池，每次工作借用独立连接；不能跨请求共享同一个 MySqlConnection。</summary>
public sealed class OnlineDatabase : IDisposable
{
    private readonly MySqlDataSource? _source;
    public OnlineDatabase(IConfiguration configuration, OnlineOptions options)
    {
        // ASP.NET 可自动插入 Authentication Middleware；关闭线上模块时构造 Handler 也不能要求数据库配置。
        if (!options.Enabled) return;
        string connection = configuration.GetConnectionString("Online") ?? throw new InvalidOperationException("缺少 ConnectionStrings:Online。");
        _source = new MySqlDataSourceBuilder(connection).Build();
    }
    public ValueTask<MySqlConnection> OpenAsync(CancellationToken ct) => (_source ?? throw new InvalidOperationException("Online 模块未启用。")).OpenConnectionAsync(ct);
    public void Dispose() => _source?.Dispose();
}
