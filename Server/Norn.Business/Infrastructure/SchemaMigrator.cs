using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using MySqlConnector;

namespace Norn.Business.Infrastructure;

/// <summary>版本与脚本摘要一起校验；MySQL DDL 会隐式提交，失败不能伪装成整段 DDL 已回滚。</summary>
public sealed class SchemaMigrator
{
    public static async Task ApplyAsync(string connectionString, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("迁移需要独立的 MigrationConnectionString。");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        string lockName = "norn-migrate-" + connection.Database;
        if (await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT GET_LOCK(@Name, 10)", new { Name = lockName }, cancellationToken: ct)) != 1)
            throw new InvalidOperationException("另一个迁移仍占有数据库锁。");
        try
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                CREATE TABLE IF NOT EXISTS SchemaMigrations (
                  Version INT PRIMARY KEY, ScriptHash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                  AppliedAtUtc DATETIME(6) NOT NULL
                ) ENGINE=InnoDB
                """, cancellationToken: ct));
            Assembly assembly = typeof(SchemaMigrator).Assembly;
            string[] names = assembly.GetManifestResourceNames().Where(n => n.Contains(".Migrations.") && n.EndsWith(".sql", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            int knownVersion = 0;
            foreach (string name in names)
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
                string sql = await reader.ReadToEndAsync(ct);
                int version = int.Parse(name.Split('.')[^2].Split('_')[0], System.Globalization.CultureInfo.InvariantCulture);
                knownVersion = Math.Max(knownVersion, version);
                string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
                string? applied = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT ScriptHash FROM SchemaMigrations WHERE Version=@Version", new { Version = version }, cancellationToken: ct));
                if (applied != null)
                {
                    if (applied != hash) throw new InvalidOperationException("已应用迁移的源码摘要被修改，请新增版本而非重写历史。");
                    continue;
                }
                await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
                await connection.ExecuteAsync(new CommandDefinition("INSERT INTO SchemaMigrations VALUES(@Version,@Hash,UTC_TIMESTAMP(6))", new { Version = version, Hash = hash }, cancellationToken: ct));
            }
            if (await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COALESCE(MAX(Version),0) FROM SchemaMigrations", cancellationToken: ct)) > knownVersion)
                throw new InvalidOperationException("数据库版本高于当前程序，拒绝降级启动。");
        }
        finally
        {
            // 命名锁属于连接，不属于 Transaction；取消请求也要释放，关闭连接仍是最后防线。
            await connection.ExecuteScalarAsync<int>("SELECT RELEASE_LOCK(@Name)", new { Name = lockName });
        }
    }
}
