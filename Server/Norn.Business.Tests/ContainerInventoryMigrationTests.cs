using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Norn.Business.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Norn.Business.Tests;

/// <summary>真实001/002/005→006，仅使用唯一前缀表；主Agent统跑，不触及dev或真实玩家数据。</summary>
public sealed class ContainerInventoryMigrationTests(ITestOutputHelper output)
{
    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task AddedSchemaPreservesLegacyAssets_AllowsSeveralGridItems_AndEnforcesContainerAndParentConstraints()
    {
        // RED 在缺006资源时应明确断言失败，而不是继续执行SQL产生“不存在的表”噪声。
        ContainerInventoryTestSeed.RequireMigrationResource();
        string[] scripts = [Resource("001_online.sql"), Resource("002_rooms.sql"), Resource("005_owned_combat.sql"), Resource("006_container_inventory.sql")];
        string prefix = "n5ci_" + Guid.NewGuid().ToString("N")[..8] + "_";
        string[] tables = scripts.SelectMany(s => Regex.Matches(s, @"CREATE TABLE(?: IF NOT EXISTS)? (\w+)").Select(m => m.Groups[1].Value)).Distinct().ToArray();
        var identifiers = tables.Concat(scripts.SelectMany(s => Regex.Matches(s, @"(?:\bCONSTRAINT|\bDROP\s+CHECK)\s+(\w+)").Select(m => m.Groups[1].Value))).ToHashSet(StringComparer.Ordinal);
        string Rewrite(string sql)
        {
            string rewritten = Regex.Replace(sql, @"'(?:''|[^'])*'|\b[A-Za-z_][A-Za-z0-9_]*\b", m => identifiers.Contains(m.Value) ? prefix + m.Value : m.Value);
            // 普通SQL字符串（例如ContentId）不改；006只用这两个受控literal查/引用本fixture表。
            // 匿名CHECK精确名称由information_schema返回，不按操作系统猜表名/约束名大小写。
            return rewritten.Replace("LOWER('ItemLocations')", $"LOWER('{prefix}ItemLocations')", StringComparison.Ordinal)
                .Replace("'ALTER TABLE `ItemLocations` DROP CHECK `'", $"'ALTER TABLE `{prefix}ItemLocations` DROP CHECK `'", StringComparison.Ordinal);
        }
        await using var f = new OnlineFactory();
        // 仅迁移SQL使用会话变量构造精确DROP CHECK；不改私有连接文件或普通runtime连接/数据库权限。
        var migrationConnection = new MySqlConnectionStringBuilder(f.MigrationConnection) { AllowUserVariables = true };
        await using var db = new MySqlConnection(migrationConnection.ConnectionString); await db.OpenAsync();
        try
        {
            await db.ExecuteAsync(Rewrite(scripts[0])); await db.ExecuteAsync(Rewrite(scripts[1]));
            string player = Guid.NewGuid().ToString("N"), saved = Guid.NewGuid().ToString("N");
            await SeedPlayer(db, prefix, player);
            await db.ExecuteAsync($"INSERT INTO {prefix}Items(InstanceId,OwnerCharacterId,ContentId,Revision) VALUES(@Id,@Player,'legacy.saved.item',7); INSERT INTO {prefix}ItemLocations(InstanceId,OwnerCharacterId,Area,Slot) VALUES(@Id,@Player,'Backpack',5)",
                new { Id = saved, Player = player });
            await db.ExecuteAsync(Rewrite(scripts[2]));
            await db.ExecuteAsync(Rewrite(scripts[3]));
            Assert.Equal("Backpack:5:7", await db.QuerySingleAsync<string>($"SELECT CONCAT(l.Area,':',l.Slot,':',i.Revision) FROM {prefix}Items i JOIN {prefix}ItemLocations l USING(InstanceId) WHERE i.InstanceId=@Id", new { Id = saved }));
            Assert.Equal(1, await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {prefix}Items"));
            Assert.Equal(0, await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {prefix}ItemContainerStates"));
            // 允许真实网格Slot=-1不能反向放宽旧平面8/16槽边界。
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"UPDATE {prefix}ItemLocations SET Slot=8 WHERE InstanceId=@Id", new { Id = saved }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"UPDATE {prefix}ItemLocations SET Area='Warehouse',Slot=16 WHERE InstanceId=@Id", new { Id = saved }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"UPDATE {prefix}ItemLocations SET Slot=-1 WHERE InstanceId=@Id", new { Id = saved }));

            string bag = Guid.NewGuid().ToString("N"), first = Guid.NewGuid().ToString("N"), second = Guid.NewGuid().ToString("N");
            await db.ExecuteAsync($"""
                INSERT INTO {prefix}Items(InstanceId,OwnerCharacterId,ContentId,Revision)
                VALUES(@Bag,@Player,'wandtest.bag',1),(@A,@Player,'containertest.bottle',1),(@B,@Player,'containertest.bottle',1);
                INSERT INTO {prefix}ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind,ParentInstanceId,GridX,GridY,QuarterTurns)
                VALUES(@Bag,@Player,NULL,0,4,NULL,0,0,0),(@A,@Player,NULL,-1,1,@Bag,0,0,0),(@B,@Player,NULL,-1,1,@Bag,2,0,1);
                INSERT INTO {prefix}ItemContainerStates(InstanceId,SchemaVersion) VALUES(@A,1),(@B,1);
                INSERT INTO {prefix}ItemContainerComponents(InstanceId,MaterialKey,Quantity) VALUES(@A,1,20)
                """, new { Bag = bag, Player = player, A = first, B = second });
            Assert.Equal(2, await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {prefix}ItemLocations WHERE ParentInstanceId=@Bag AND Slot=-1", new { Bag = bag }));
            Assert.Equal(0, await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {prefix}ItemContainerComponents WHERE InstanceId=@Id", new { Id = second }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"INSERT INTO {prefix}ItemContainerComponents VALUES(@Id,1,1)", new { Id = first }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"INSERT INTO {prefix}ItemContainerComponents VALUES(@Id,9,1)", new { Id = second }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"INSERT INTO {prefix}ItemContainerComponents VALUES(@Id,1,0)", new { Id = second }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"INSERT INTO {prefix}ItemContainerStates VALUES(@Id,0)", new { Id = saved }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"INSERT INTO {prefix}ItemContainerStates VALUES(@Id,1)", new { Id = Guid.NewGuid().ToString("N") }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"UPDATE {prefix}ItemLocations SET Slot=0 WHERE InstanceId=@Id", new { Id = first }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"UPDATE {prefix}ItemLocations SET GridX=1 WHERE InstanceId=@Id", new { Id = bag }));

            string other = Guid.NewGuid().ToString("N"), foreign = Guid.NewGuid().ToString("N");
            await SeedPlayer(db, prefix, other);
            await db.ExecuteAsync($"INSERT INTO {prefix}Items(InstanceId,OwnerCharacterId,ContentId,Revision) VALUES(@Id,@Player,'wandtest.bag',1)", new { Id = foreign, Player = other });
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"UPDATE {prefix}ItemLocations SET ParentInstanceId=@Parent WHERE InstanceId=@Id", new { Parent = foreign, Id = first }));
            Assert.Equal(4, await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {prefix}Items WHERE OwnerCharacterId=@Player", new { Player = player }));
        }
        finally
        {
            // Migration用户无DROP；按反向外键依赖只清本fixture数据，不关闭FOREIGN_KEY_CHECKS或扩大权限。
            foreach (string table in tables.Reverse())
                if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@Table", new { Table = prefix + table }) != 0)
                    await db.ExecuteAsync($"DELETE FROM {prefix}{table}");
            output.WriteLine("ContainerMigrationFixtureEmptyTablesPrefix=" + prefix);
        }
    }
    [Fact, Trait("Category", "DatabaseIntegration"), Trait("Feature", "OwnedMaterial")]
    public async Task MaterialMigrationPreservesExistingItemsAndUsesPositiveUnsignedQuantityWithoutBackfill()
    {
        MaterialInventoryTestSeed.RequireMigrationResource();
        string[] scripts = [Resource("001_online.sql"), Resource("002_rooms.sql"), Resource("005_owned_combat.sql"),
            Resource("006_container_inventory.sql"), Resource("007_material_inventory.sql")];
        string prefix = "n5mi_" + Guid.NewGuid().ToString("N")[..8] + "_";
        string[] tables = scripts.SelectMany(s => Regex.Matches(s, @"CREATE TABLE(?: IF NOT EXISTS)? (\w+)").Select(m => m.Groups[1].Value)).Distinct().ToArray();
        var identifiers = tables.Concat(scripts.SelectMany(s => Regex.Matches(s, @"(?:\bCONSTRAINT|\bDROP\s+CHECK)\s+(\w+)").Select(m => m.Groups[1].Value))).ToHashSet(StringComparer.Ordinal);
        string Rewrite(string sql)
        {
            string rewritten = Regex.Replace(sql, @"'(?:''|[^'])*'|\b[A-Za-z_][A-Za-z0-9_]*\b", m => identifiers.Contains(m.Value) ? prefix + m.Value : m.Value);
            return rewritten.Replace("LOWER('ItemLocations')", $"LOWER('{prefix}ItemLocations')", StringComparison.Ordinal)
                .Replace("'ALTER TABLE `ItemLocations` DROP CHECK `'", $"'ALTER TABLE `{prefix}ItemLocations` DROP CHECK `'", StringComparison.Ordinal);
        }
        await using var f = new OnlineFactory();
        var migrationConnection = new MySqlConnectionStringBuilder(f.MigrationConnection) { AllowUserVariables = true };
        await using var db = new MySqlConnection(migrationConnection.ConnectionString); await db.OpenAsync();
        try
        {
            // 只在唯一前缀表上走真实旧DDL→007，不调用生产注册，也不为旧材料壳生成默认数量。
            for (int i = 0; i < scripts.Length - 1; i++) await db.ExecuteAsync(Rewrite(scripts[i]));
            string player = Guid.NewGuid().ToString("N"), saved = Guid.NewGuid().ToString("N"), empty = Guid.NewGuid().ToString("N");
            await SeedPlayer(db, prefix, player);
            await db.ExecuteAsync($"""
                INSERT INTO {prefix}Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges)
                VALUES(@Saved,@Player,'containertest.ice',7,0,FALSE),(@Empty,@Player,'legacy.saved.item',9,0,FALSE);
                INSERT INTO {prefix}ItemLocations(InstanceId,OwnerCharacterId,Area,Slot)
                VALUES(@Saved,@Player,'Warehouse',0),(@Empty,@Player,'Warehouse',1)
                """, new { Saved = saved, Empty = empty, Player = player });
            await db.ExecuteAsync(Rewrite(scripts[^1]));
            Assert.Equal(2, await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {prefix}Items"));
            Assert.Equal("containertest.ice:7", await db.QuerySingleAsync<string>($"SELECT CONCAT(ContentId,':',Revision) FROM {prefix}Items WHERE InstanceId=@Id", new { Id = saved }));
            Assert.Equal(0, await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {prefix}ItemMaterialStates"));
            Assert.Equal(1u, await db.ExecuteScalarAsync<uint>($"SELECT Version FROM {prefix}Characters WHERE CharacterId=@Id", new { Id = player }));

            // DDL证明精确unsigned存储；正式物品容量仍由Profile/Owned规则另验，不能因此授予超容量资产。
            await db.ExecuteAsync($"INSERT INTO {prefix}ItemMaterialStates(InstanceId,SchemaVersion,MaterialKey,Quantity) VALUES(@Id,1,5,@Quantity)", new { Id = saved, Quantity = ulong.MaxValue });
            Assert.Equal(ulong.MaxValue, await db.QuerySingleAsync<ulong>($"SELECT Quantity FROM {prefix}ItemMaterialStates WHERE InstanceId=@Id", new { Id = saved }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"INSERT INTO {prefix}ItemMaterialStates VALUES(@Id,1,5,1)", new { Id = saved }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"INSERT INTO {prefix}ItemMaterialStates VALUES(@Id,0,5,1)", new { Id = empty }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"INSERT INTO {prefix}ItemMaterialStates VALUES(@Id,2,5,1)", new { Id = empty }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"INSERT INTO {prefix}ItemMaterialStates VALUES(@Id,1,0,1)", new { Id = empty }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"INSERT INTO {prefix}ItemMaterialStates VALUES(@Id,1,9,1)", new { Id = empty }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"INSERT INTO {prefix}ItemMaterialStates VALUES(@Id,1,5,0)", new { Id = empty }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"INSERT INTO {prefix}ItemMaterialStates VALUES(@Id,1,5,1)", new { Id = Guid.NewGuid().ToString("N") }));
            Assert.Equal(0, await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {prefix}ItemMaterialStates WHERE InstanceId=@Id", new { Id = empty }));
        }
        finally
        {
            // 与006夹具相同，只按反向FK清本fixture数据，保留空前缀表，不DROP或修改历史/真实用户表。
            foreach (string table in tables.Reverse())
                if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@Table", new { Table = prefix + table }) != 0)
                    await db.ExecuteAsync($"DELETE FROM {prefix}{table}");
            output.WriteLine("MaterialMigrationFixtureEmptyTablesPrefix=" + prefix);
        }
    }

    [Fact, Trait("Category", "DatabaseIntegration"), Trait("Feature", "OwnedAttachment")]
    public async Task AttachmentMigrationKeepsUnsignedDoseAndDisabledSettingWithoutRequiringALiveSourceOrBackfilling()
    {
        SpellAttachmentInventoryTestSeed.RequireMigrationResource();
        string[] scripts = [Resource("001_online.sql"), Resource("002_rooms.sql"), Resource("005_owned_combat.sql"),
            Resource("006_container_inventory.sql"), Resource("007_material_inventory.sql"), Resource("008_spell_attachment_inventory.sql")];
        string prefix = "n5ai_" + Guid.NewGuid().ToString("N")[..8] + "_";
        string[] tables = scripts.SelectMany(s => Regex.Matches(s, @"CREATE TABLE(?: IF NOT EXISTS)? (\w+)").Select(m => m.Groups[1].Value)).Distinct().ToArray();
        var identifiers = tables.Concat(scripts.SelectMany(s => Regex.Matches(s, @"(?:\bCONSTRAINT|\bDROP\s+CHECK)\s+(\w+)").Select(m => m.Groups[1].Value))).ToHashSet(StringComparer.Ordinal);
        string Rewrite(string sql)
        {
            string rewritten = Regex.Replace(sql, @"'(?:''|[^'])*'|\b[A-Za-z_][A-Za-z0-9_]*\b", m => identifiers.Contains(m.Value) ? prefix + m.Value : m.Value);
            return rewritten.Replace("LOWER('ItemLocations')", $"LOWER('{prefix}ItemLocations')", StringComparison.Ordinal)
                .Replace("'ALTER TABLE `ItemLocations` DROP CHECK `'", $"'ALTER TABLE `{prefix}ItemLocations` DROP CHECK `'", StringComparison.Ordinal);
        }
        await using var f = new OnlineFactory();
        var migrationConnection = new MySqlConnectionStringBuilder(f.MigrationConnection) { AllowUserVariables = true };
        await using var db = new MySqlConnection(migrationConnection.ConnectionString); await db.OpenAsync();
        try
        {
            for (int i = 0; i < scripts.Length - 1; i++) await db.ExecuteAsync(Rewrite(scripts[i]));
            string player = Guid.NewGuid().ToString("N"), card = Guid.NewGuid().ToString("N"), empty = Guid.NewGuid().ToString("N"), missing = Guid.NewGuid().ToString("N");
            await SeedPlayer(db, prefix, player);
            await db.ExecuteAsync($"""
                INSERT INTO {prefix}Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges)
                VALUES(@Card,@Player,'wandtest.fire',7,30,FALSE),(@Empty,@Player,'legacy.saved.item',9,0,FALSE);
                INSERT INTO {prefix}ItemLocations(InstanceId,OwnerCharacterId,Area,Slot)
                VALUES(@Card,@Player,'Warehouse',0),(@Empty,@Player,'Warehouse',1)
                """, new { Card = card, Empty = empty, Player = player });
            await db.ExecuteAsync(Rewrite(scripts[^1]));
            Assert.Equal(0, await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {prefix}ItemSpellAttachmentStates"));
            Assert.Equal(2, await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {prefix}Items"));
            Assert.Equal("wandtest.fire:7:30", await db.QuerySingleAsync<string>($"SELECT CONCAT(ContentId,':',Revision,':',RemainingCharges) FROM {prefix}Items WHERE InstanceId=@Id", new { Id = card }));
            // 来源不存在仍可保存设置；没有Source FK，更不能为满足FK伪造一只瓶。Dose是请求量，不授予GMU。
            await db.ExecuteAsync($"INSERT INTO {prefix}ItemSpellAttachmentStates(InstanceId,SchemaVersion,ContainerId,Dose,Enabled) VALUES(@Id,1,@Source,@Dose,FALSE)",
                new { Id = card, Source = missing, Dose = ulong.MaxValue });
            Assert.Equal(0, await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {prefix}Items WHERE InstanceId=@Id", new { Id = missing }));
            Assert.Equal(ulong.MaxValue, await db.QuerySingleAsync<ulong>($"SELECT Dose FROM {prefix}ItemSpellAttachmentStates WHERE InstanceId=@Id", new { Id = card }));
            Assert.False(await db.QuerySingleAsync<bool>($"SELECT Enabled FROM {prefix}ItemSpellAttachmentStates WHERE InstanceId=@Id", new { Id = card }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"INSERT INTO {prefix}ItemSpellAttachmentStates VALUES(@Id,1,@Source,1,TRUE)", new { Id = card, Source = missing }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"INSERT INTO {prefix}ItemSpellAttachmentStates VALUES(@Id,0,@Source,1,TRUE)", new { Id = empty, Source = missing }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"INSERT INTO {prefix}ItemSpellAttachmentStates VALUES(@Id,2,@Source,1,TRUE)", new { Id = empty, Source = missing }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"INSERT INTO {prefix}ItemSpellAttachmentStates VALUES(@Id,1,'bad',1,TRUE)", new { Id = empty }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"INSERT INTO {prefix}ItemSpellAttachmentStates VALUES(@Id,1,@Source,1,TRUE)", new { Id = empty, Source = new string('F', 32) }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"INSERT INTO {prefix}ItemSpellAttachmentStates VALUES(@Id,1,@Source,0,TRUE)", new { Id = empty, Source = missing }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"INSERT INTO {prefix}ItemSpellAttachmentStates VALUES(@Id,1,@Source,1,2)", new { Id = empty, Source = missing }));
            await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync($"INSERT INTO {prefix}ItemSpellAttachmentStates VALUES(@Id,1,@Source,1,TRUE)", new { Id = Guid.NewGuid().ToString("N"), Source = missing }));
            Assert.Equal(0, await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {prefix}ItemSpellAttachmentStates WHERE InstanceId=@Id", new { Id = empty }));
        }
        finally
        {
            foreach (string table in tables.Reverse())
                if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@Table", new { Table = prefix + table }) != 0)
                    await db.ExecuteAsync($"DELETE FROM {prefix}{table}");
            output.WriteLine("AttachmentMigrationFixtureEmptyTablesPrefix=" + prefix);
        }
    }

    private static string Resource(string suffix)
    {
        var assembly = typeof(SchemaMigrator).Assembly;
        string? name = assembly.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal));
        Assert.NotNull(name);
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!); return reader.ReadToEnd();
    }
    private static Task SeedPlayer(MySqlConnection db, string prefix, string player)
        => db.ExecuteAsync($"""
            INSERT INTO {prefix}Accounts(AccountId,Username,NormalizedUsername,PasswordHash,CreatedAtUtc) VALUES(@Player,@Name,@Name,'isolated_fixture_hash',UTC_TIMESTAMP(6));
            INSERT INTO {prefix}Characters(CharacterId,AccountId,Version,SafeHub) VALUES(@Player,@Player,1,'online.hub.spawn');
            INSERT INTO {prefix}Wallets(CharacterId,Balance) VALUES(@Player,0)
            """, new { Player = player, Name = "ci_" + player[..16] });
}

/// <summary>只给独立测试账户准备真正的Owner/位置/组分；没有生产注册发放入口。</summary>
internal static class ContainerInventoryTestSeed
{
    internal readonly record struct Seed(string Bag, string Bottle, string Tool, string WarehouseWand, string WarehouseCard);
    internal static void RequireMigrationResource()
        => Assert.Contains(typeof(SchemaMigrator).Assembly.GetManifestResourceNames(), n => n.EndsWith("006_container_inventory.sql", StringComparison.Ordinal));

    internal static async Task<Seed> InsertAsync(OnlineFactory f, string player)
    {
        RequireMigrationResource();
        Assert.Contains(f.Manifest.Items, i => i.ContentId == "wandtest.bag" && i.Kind == 2);
        Assert.Contains(f.Manifest.Items, i => i.ContentId == "containertest.bottle" && i.Kind == 3);
        await using var db = new MySqlConnection(f.Connection);
        await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteAsync("SELECT CharacterId FROM Characters WHERE CharacterId=@Player FOR UPDATE", new { Player = player }, tx);
        // 注册已在唯一装备槽放入基础包；保留这个真实实例，只构造本测试需要的容器和仓库父子物品。
        string bag = await db.QuerySingleAsync<string>("SELECT InstanceId FROM ItemLocations WHERE OwnerCharacterId=@Player AND CombatLocationKind=4", new { Player = player }, tx);
        var seed = new Seed(bag, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        await db.ExecuteAsync("""
            INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges)
            VALUES(@Bottle,@Player,'containertest.bottle',3,0,FALSE),(@Tool,@Player,'containertest.bottle',1,0,FALSE),
              (@WarehouseWand,@Player,'wandtest.wand.a',1,0,FALSE),(@WarehouseCard,@Player,'wandtest.fire',1,5,FALSE);
            INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind,ParentInstanceId,GridX,GridY,QuarterTurns)
            VALUES(@Bottle,@Player,NULL,-1,1,@Bag,1,1,1),(@Tool,@Player,NULL,0,8,NULL,0,0,0),
              (@WarehouseWand,@Player,'Warehouse',0,NULL,NULL,0,0,0),(@WarehouseCard,@Player,NULL,0,2,@WarehouseWand,0,0,0);
            INSERT INTO ItemContainerStates(InstanceId,SchemaVersion) VALUES(@Bottle,1),(@Tool,1);
            INSERT INTO ItemContainerComponents(InstanceId,MaterialKey,Quantity) VALUES(@Bottle,1,20),(@Bottle,3,7);
            UPDATE Characters SET Version=Version+1 WHERE CharacterId=@Player
            """, new { seed.Bag, seed.Bottle, seed.Tool, seed.WarehouseWand, seed.WarehouseCard, Player = player }, tx);
        await tx.CommitAsync(); return seed;
    }
}
