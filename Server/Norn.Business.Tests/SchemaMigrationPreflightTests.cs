using System.Reflection;
using Dapper;
using MySqlConnector;
using Norn.Business.Infrastructure;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>006 已应用后保持字节不变；后续执行器须在任何006 DDL前验证旧约束的完整逻辑分组。</summary>
public sealed class SchemaMigrationPreflightTests
{
    [Theory, Trait("Category", "DatabaseIntegration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactLegacyPredicateIsAccepted_ButSameTokensWithDifferentGroupingAreRejected(bool wrongGrouping)
    {
        var method = typeof(SchemaMigrator).GetMethod("RequireContainerLegacyCheckAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(method != null, "不可重写已应用006；执行器仍缺少保留AND/OR分组的前置门禁。");
        await using var f = new OnlineFactory();
        await using var db = new MySqlConnection(f.MigrationConnection); await db.OpenAsync();
        string table = "n5pf_" + Guid.NewGuid().ToString("N")[..8] + "_ItemLocations";
        string predicate = wrongGrouping
            ? "Area='Backpack' AND Slot>=0 AND (Slot<8 OR Area='Warehouse') AND Slot>=0 AND Slot<16"
            : "(Area='Backpack' AND Slot>=0 AND Slot<8) OR (Area='Warehouse' AND Slot>=0 AND Slot<16)";
        await db.ExecuteAsync($"CREATE TABLE {table}(Area VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NULL,Slot INT NOT NULL,CHECK(Area IN ('Backpack','Warehouse')),CHECK({predicate})) ENGINE=InnoDB");
        const string query = "SELECT cc.CHECK_CLAUSE FROM information_schema.TABLE_CONSTRAINTS tc JOIN information_schema.CHECK_CONSTRAINTS cc ON cc.CONSTRAINT_SCHEMA=tc.CONSTRAINT_SCHEMA AND BINARY cc.CONSTRAINT_NAME=BINARY tc.CONSTRAINT_NAME WHERE tc.CONSTRAINT_SCHEMA=DATABASE() AND LOWER(tc.TABLE_NAME)=LOWER(@Table) AND LOWER(tc.CONSTRAINT_NAME)=CONCAT(LOWER(tc.TABLE_NAME),'_chk_2')";
        string before = await db.QuerySingleAsync<string>(query, new { Table = table });
        Task Invoke() => (Task)method!.Invoke(null, new object[] { db, table, CancellationToken.None })!;
        if (wrongGrouping) await Assert.ThrowsAsync<InvalidOperationException>(Invoke);
        else await Invoke();
        Assert.Equal(before, await db.QuerySingleAsync<string>(query, new { Table = table }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {table}"));
        // 与现有迁移fixture一致，只留本轮唯一前缀空表；不授DROP、不关闭FK、不操作别人的表。
    }
}
