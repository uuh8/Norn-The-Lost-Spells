using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Norn.Business.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Norn.Business.Tests;

/// <summary>在唯一表前缀内执行真正的旧 Schema→005 SQL；不清空既有测试表，也不创建/停止数据库服务。</summary>
public sealed class OwnedCombatMigrationTests(ITestOutputHelper output)
{
    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task LegacyStarter_MovesOriginalIdsOnce_AndPreservesMovedLeasedAndUnrelatedInventory()
    {
        await using var f = new OnlineFactory(); // 构造仅读取专用测试配置，不启动 Web Host。
        string prefix = "n4m_" + Guid.NewGuid().ToString("N")[..8] + "_";
        string[] scripts = [Resource("001_online.sql"), Resource("002_rooms.sql"), Resource("005_owned_combat.sql")];
        var tables = scripts.SelectMany(sql => Regex.Matches(sql, @"CREATE TABLE(?: IF NOT EXISTS)? (\w+)").Select(m => m.Groups[1].Value)).Distinct().ToArray();
        var constraints = scripts.SelectMany(sql => Regex.Matches(sql, @"\bCONSTRAINT (\w+)").Select(m => m.Groups[1].Value)).Distinct();
        var identifiers = tables.Concat(constraints).ToHashSet(StringComparer.Ordinal);
        // 单引号常量（包括 JSON 的 'Items' 键）保持原文，只替换 SQL 标识符。
        string Rewrite(string sql) => Regex.Replace(sql, @"'(?:''|[^'])*'|\b[A-Za-z_][A-Za-z0-9_]*\b", m => identifiers.Contains(m.Value) ? prefix + m.Value : m.Value);
        await using var db = new MySqlConnection(f.MigrationConnection); await db.OpenAsync();
        try
        {
            await db.ExecuteAsync(Rewrite(scripts[0])); await db.ExecuteAsync(Rewrite(scripts[1]));
            var untouched = await Seed(db, prefix); var moved = await Seed(db, prefix); var leased = await Seed(db, prefix);
            string unrelated = Guid.NewGuid().ToString("N");
            await db.ExecuteAsync($"INSERT INTO {prefix}Items VALUES(@Id,@Player,'other.saved.item',7); INSERT INTO {prefix}ItemLocations VALUES(@Id,@Player,'Warehouse',3)", new { Id = unrelated, Player = untouched.Player });
            await db.ExecuteAsync($"UPDATE {prefix}ItemLocations SET Area='Warehouse',Slot=2 WHERE InstanceId=@Id; UPDATE {prefix}Items SET Revision=2 WHERE InstanceId=@Id; UPDATE {prefix}Characters SET Version=2 WHERE CharacterId=@Player", new { Id = moved.Wand, Player = moved.Player });
            string room = Guid.NewGuid().ToString("N"), run = Guid.NewGuid().ToString("N");
            await db.ExecuteAsync($"""
                INSERT INTO {prefix}Parties(RoomId,DungeonId,LeaderCharacterId,State,ReadyRevision,RoomEpoch,ActiveRunId,CreatedAtUtc) VALUES(@Room,'p7',@Player,'Starting',1,1,@Run,UTC_TIMESTAMP(6));
                INSERT INTO {prefix}Runs(RunId,RoomId,RoomEpoch,StartRevision,State,CreatedAtUtc) VALUES(@Run,@Room,1,1,'Starting',UTC_TIMESTAMP(6));
                INSERT INTO {prefix}AssetLeases VALUES(@Player,@Run,1)
                """, new { Room = room, Run = run, Player = leased.Player });

            await db.ExecuteAsync(Rewrite(scripts[2]));
            Assert.Equal(untouched.Wand, await db.QuerySingleAsync<string>($"SELECT SelectedWandInstanceId FROM {prefix}CharacterCombatLoadouts WHERE CharacterId=@Id", new { Id = untouched.Player }));
            Assert.Equal(3, await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {prefix}Items WHERE OwnerCharacterId=@Player", new { Player = untouched.Player }));
            Assert.Equal(2, await db.ExecuteScalarAsync<int>($"SELECT LocationKind FROM {prefix}ItemLocations WHERE InstanceId=@Id", new { Id = untouched.Card }));
            Assert.Equal(untouched.Wand, await db.QuerySingleAsync<string>($"SELECT ParentInstanceId FROM {prefix}ItemLocations WHERE InstanceId=@Id", new { Id = untouched.Card }));
            Assert.Equal(30, await db.ExecuteScalarAsync<int>($"SELECT RemainingCharges FROM {prefix}Items WHERE InstanceId=@Id", new { Id = untouched.Card }));
            Assert.False(await db.ExecuteScalarAsync<bool>($"SELECT HasUnlimitedCharges FROM {prefix}Items WHERE InstanceId=@Id", new { Id = untouched.Card }));
            Assert.Equal("Warehouse:3:7", await db.QuerySingleAsync<string>($"SELECT CONCAT(l.Area,':',l.Slot,':',i.Revision) FROM {prefix}Items i JOIN {prefix}ItemLocations l USING(InstanceId) WHERE i.InstanceId=@Id", new { Id = unrelated }));
            Assert.Equal(2, await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {prefix}OwnedCombatStarterAudits WHERE Status='NeedsReview'"));
            Assert.Equal("Warehouse:2:2", await db.QuerySingleAsync<string>($"SELECT CONCAT(l.Area,':',l.Slot,':',i.Revision) FROM {prefix}Items i JOIN {prefix}ItemLocations l USING(InstanceId) WHERE i.InstanceId=@Id", new { Id = moved.Wand }));
            Assert.Equal("Backpack:0:1", await db.QuerySingleAsync<string>($"SELECT CONCAT(l.Area,':',l.Slot,':',i.Revision) FROM {prefix}Items i JOIN {prefix}ItemLocations l USING(InstanceId) WHERE i.InstanceId=@Id", new { Id = leased.Wand }));
            string before = await db.QuerySingleAsync<string>($"SELECT BeforeJson FROM {prefix}OwnedCombatStarterAudits WHERE CharacterId=@Player", new { Player = untouched.Player });
            using var audit = JsonDocument.Parse(before); Assert.Equal(3, audit.RootElement.GetProperty("Items").GetArrayLength());
            // 重复授权由业务唯一审计键阻止；同一历史数据不会因进程重启获得第二份实例/30次重置。
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"INSERT INTO {prefix}OwnedCombatStarterAudits SELECT * FROM {prefix}OwnedCombatStarterAudits WHERE CharacterId=@Player", new { Player = untouched.Player }));
            Assert.Equal(3, await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {prefix}OwnedCombatStarterAudits"));
        }
        finally
        {
            // Migration 用户刻意没有 DROP；按反向依赖清理本测试行，空 Schema fixture 留给受控维护入口移除。
            // 不为测试扩大权限，也不关闭 FOREIGN_KEY_CHECKS；输出精确前缀方便审计，不包含凭据。
            foreach (string table in tables.Reverse())
                if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@Table", new { Table = prefix + table }) != 0)
                    await db.ExecuteAsync($"DELETE FROM {prefix}{table}");
            output.WriteLine("MigrationFixtureEmptyTablesPrefix=" + prefix);
        }
    }
    private static string Resource(string suffix)
    {
        var assembly = typeof(SchemaMigrator).Assembly;
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!); return reader.ReadToEnd();
    }
    private static async Task<(string Player, string Wand, string Card)> Seed(MySqlConnection db, string prefix)
    {
        string player = Guid.NewGuid().ToString("N"), wand = Guid.NewGuid().ToString("N"), card = Guid.NewGuid().ToString("N"), name = "legacy_" + player[..10];
        await db.ExecuteAsync($"""
            INSERT INTO {prefix}Accounts VALUES(@Player,@Name,@Name,'test_hash',UTC_TIMESTAMP(6));
            INSERT INTO {prefix}Characters VALUES(@Player,@Player,1,'online.hub.spawn'); INSERT INTO {prefix}Wallets VALUES(@Player,7);
            INSERT INTO {prefix}Items VALUES(@Wand,@Player,'wandtest.wand.a',1),(@Card,@Player,'wandtest.fire',1);
            INSERT INTO {prefix}ItemLocations VALUES(@Wand,@Player,'Backpack',0),(@Card,@Player,'Backpack',1)
            """, new { Player = player, Wand = wand, Card = card, Name = name });
        return (player, wand, card);
    }
}
