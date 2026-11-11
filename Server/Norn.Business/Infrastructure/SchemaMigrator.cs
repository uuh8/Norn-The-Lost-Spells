using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;

namespace Norn.Business.Infrastructure;

/// <summary>版本与脚本摘要一起校验；MySQL DDL 会隐式提交，失败不能伪装成整段 DDL 已回滚。</summary>
public sealed class SchemaMigrator
{
    public static async Task ApplyAsync(string connectionString, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("迁移需要独立的 MigrationConnectionString。");
        // 仅迁移专用连接允许 SQL session 变量：006 必须从元数据获取旧匿名 CHECK 的
        // 精确大小写名称后 PREPARE DROP。此为 Connector 解析选项，不授予数据库权限，
        // 不修改 runtime 连接、私有配置或已应用迁移，也不根据猜测关闭任何约束。
        var migrationConnection = new MySqlConnectionStringBuilder(connectionString) { AllowUserVariables = true };
        await using var connection = new MySqlConnection(migrationConnection.ConnectionString);
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
            // 007固体状态沿同一EmbeddedResource顺序追加；仅建表、不回填旧资产。
            // 001–006仍逐份核对原Hash，不能为新增状态重写历史或跳过旧版本校验。
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
                // 006 已应用的环境只走上方不可变Hash检查；尚未应用时，在其任何DDL前
                // 保留完整AND/OR分组核验旧CHECK，防止同token的人工改组被当作原约束。
                if (version == 6) await RequireContainerLegacyCheckAsync(connection, "ItemLocations", ct);
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

    private static async Task RequireContainerLegacyCheckAsync(MySqlConnection connection, string table, CancellationToken ct)
    {
        // table仅用于元数据参数，绝不拼入DDL；生产固定ItemLocations，隔离测试可用自己的前缀表。
        var clauses = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT cc.CHECK_CLAUSE FROM information_schema.TABLE_CONSTRAINTS tc
            JOIN information_schema.CHECK_CONSTRAINTS cc ON cc.CONSTRAINT_SCHEMA=tc.CONSTRAINT_SCHEMA
              AND BINARY cc.CONSTRAINT_NAME=BINARY tc.CONSTRAINT_NAME
            WHERE tc.CONSTRAINT_SCHEMA=DATABASE() AND LOWER(tc.TABLE_NAME)=LOWER(@Table)
              AND tc.CONSTRAINT_TYPE='CHECK'
              AND LOWER(tc.CONSTRAINT_NAME)=CONCAT(LOWER(tc.TABLE_NAME),'_chk_2') LIMIT 2
            """, new { Table = table }, cancellationToken: ct))).ToArray();
        if (clauses.Length != 1) throw new InvalidOperationException("006前置检查：旧位置CHECK缺失或不唯一，未执行DDL。");
        // MySQL8.4.13的元数据给字符串附charset及转义引号。只去这些展示差异和空白，
        // 括号必须逐字保留；删除所有括号会把不同逻辑分组误认成相同资产约束。
        string actual = clauses[0].Replace("\\", "").Replace("`", "").ToLowerInvariant();
        actual = Regex.Replace(Regex.Replace(actual, "_[a-z0-9]+(?=')", ""), @"\s+", "");
        const string expected = "(((area='backpack')and(slot>=0)and(slot<8))or((area='warehouse')and(slot>=0)and(slot<16)))";
        if (actual != expected) throw new InvalidOperationException("006前置检查：旧位置CHECK的完整分组不匹配，未执行DDL。请核查Schema。");
    }
}
