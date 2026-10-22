using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using Norn.Business.Content;
using Norn.Business.Infrastructure;
using MySqlConnector;

namespace Norn.Business.Features.Inventory;

/// <summary>角色行锁串行化同一资产聚合的写入；事务不跨网络回合，更不会跨整场副本。</summary>
public sealed class InventoryService(OnlineDatabase database, ContentManifestValidator content)
{
    public async Task<OnlineProfileReply> GetProfileAsync(string player, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        // RR 的一致视图让 Version、Wallet、Items 属于同一时刻，避免移动并发时返回混合快照。
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var reply = await ReadSnapshotAsync(connection, tx, player, ct);
        await tx.CommitAsync(ct); return reply;
    }

    // 入局复用同一份只读映射，但连接/Transaction由RunEntry持有，不能另开RR视图而读到锁定前的数据。
    internal async Task<OnlineProfileReply> ReadSnapshotAsync(MySqlConnection connection, MySqlTransaction tx, string player, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ProfileRow>(new CommandDefinition("""
            SELECT c.CharacterId,a.Username,c.Version,c.SafeHub,w.Balance FROM Characters c
            JOIN Accounts a ON a.AccountId=c.AccountId JOIN Wallets w ON w.CharacterId=c.CharacterId
            WHERE c.CharacterId=@Player
            """, new { Player = player }, tx, cancellationToken: ct)) ?? throw new OnlineFailure(404, "ProfileNotFound");
        var items = await connection.QueryAsync<ItemRow>(new CommandDefinition("""
            SELECT i.InstanceId,i.ContentId,i.Revision,l.Area,l.Slot FROM Items i
            JOIN ItemLocations l ON l.InstanceId=i.InstanceId AND l.OwnerCharacterId=i.OwnerCharacterId
            WHERE i.OwnerCharacterId=@Player ORDER BY l.Area,l.Slot,i.InstanceId
            """, new { Player = player }, tx, cancellationToken: ct));
        OnlineItemReply[] view = items.Select(i => new OnlineItemReply { InstanceId = i.InstanceId, ContentId = i.ContentId,
            DisplayName = content.RequireItem(i.ContentId).DisplayName, Area = i.Area, Slot = i.Slot, Revision = i.Revision }).ToArray();
        return new OnlineProfileReply { PlayerId = row.CharacterId, Username = row.Username, Version = row.Version, Gold = row.Balance,
            SafeHub = row.SafeHub, BackpackSlots = OnlineInventoryAreas.BackpackCapacity, WarehouseSlots = OnlineInventoryAreas.WarehouseCapacity,
            ContentManifestHash = content.Manifest.Hash, Items = view };
    }

    public async Task<InventoryMoveReply> TryApplyInventoryAsync(string player, InventoryMoveRequest request, CancellationToken ct)
    {
        if (!IsId(request.CommandId) || !IsId(request.InstanceId) || request.ExpectedVersion == 0 ||
            !(request.Destination == OnlineInventoryAreas.Backpack || request.Destination == OnlineInventoryAreas.Warehouse) || request.Slot < 0 ||
            request.Slot >= (request.Destination == OnlineInventoryAreas.Backpack ? OnlineInventoryAreas.BackpackCapacity : OnlineInventoryAreas.WarehouseCapacity))
            throw new OnlineFailure(400, "InvalidMove");
        string canonical = FormattableString.Invariant($"move-v1|{request.ExpectedVersion}|{request.InstanceId}|{request.Destination}|{request.Slot}");
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        await using var connection = await database.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        uint? version = await connection.QuerySingleOrDefaultAsync<uint?>(new CommandDefinition("SELECT Version FROM Characters WHERE CharacterId=@Player FOR UPDATE", new { Player = player }, tx, cancellationToken: ct));
        if (version == null) throw new OnlineFailure(404, "ProfileNotFound");
        // 收据先于版本校验：成功回包丢失后，携带旧版本重试仍要得到第一次的结果。
        var receipt = await connection.QuerySingleOrDefaultAsync<ReceiptRow>(new CommandDefinition("SELECT RequestHash,ResultJson FROM InventoryCommands WHERE CharacterId=@Player AND CommandId=@Command", new { Player = player, Command = request.CommandId }, tx, cancellationToken: ct));
        if (receipt != null)
        {
            if (receipt.RequestHash != hash) throw new OnlineFailure(409, "CommandConflict");
            return JsonSerializer.Deserialize<InventoryMoveReply>(receipt.ResultJson, ContentManifestValidator.Json)!;
        }
        // 先返回已提交的收据，再阻止新写入；重放成功结果不会再次改资产。入局与移动共享角色行锁。
        if (await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM AssetLeases WHERE CharacterId=@Player", new { Player = player }, tx, cancellationToken: ct)) > 0)
            throw new OnlineFailure(409, "AssetsLeased");
        if (version != request.ExpectedVersion || version == uint.MaxValue) throw new OnlineFailure(409, "ProfileVersionConflict");
        string? id = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT ContentId FROM Items WHERE InstanceId=@Instance AND OwnerCharacterId=@Player", new { Instance = request.InstanceId, Player = player }, tx, cancellationToken: ct));
        if (id == null) throw new OnlineFailure(404, "ItemNotOwned");
        content.RequireItem(id);
        if (await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM ItemLocations WHERE OwnerCharacterId=@Player AND Area=@Area AND Slot=@Slot", new { Player = player, Area = request.Destination, request.Slot }, tx, cancellationToken: ct)) > 0)
            throw new OnlineFailure(409, "SlotOccupied");
        int moved = await connection.ExecuteAsync(new CommandDefinition("UPDATE ItemLocations SET Area=@Area,Slot=@Slot WHERE InstanceId=@Instance AND OwnerCharacterId=@Player", new { Player = player, Area = request.Destination, request.Slot, Instance = request.InstanceId }, tx, cancellationToken: ct));
        if (moved != 1) throw new OnlineFailure(409, "LocationMissing");
        uint committed = version.Value + 1;
        await connection.ExecuteAsync(new CommandDefinition("UPDATE Items SET Revision=Revision+1 WHERE InstanceId=@Instance; UPDATE Characters SET Version=@Version WHERE CharacterId=@Player", new { Instance = request.InstanceId, Player = player, Version = committed }, tx, cancellationToken: ct));
        var reply = new InventoryMoveReply { CommandId = request.CommandId, CommittedVersion = committed, InstanceId = request.InstanceId, Destination = request.Destination, Slot = request.Slot };
        await connection.ExecuteAsync(new CommandDefinition("INSERT INTO InventoryCommands VALUES(@Player,@Command,@Hash,@Json,UTC_TIMESTAMP(6))", new { Player = player, Command = request.CommandId, Hash = hash, Json = JsonSerializer.Serialize(reply, ContentManifestValidator.Json) }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return reply;
    }

    private static bool IsId(string? id) => id != null && Guid.TryParseExact(id, "N", out var value) && value.ToString("N") == id;
    private sealed class ProfileRow { public string CharacterId { get; set; } = ""; public string Username { get; set; } = ""; public uint Version { get; set; } public string SafeHub { get; set; } = ""; public long Balance { get; set; } }
    private sealed class ItemRow { public string InstanceId { get; set; } = ""; public string ContentId { get; set; } = ""; public uint Revision { get; set; } public string Area { get; set; } = ""; public int Slot { get; set; } }
    private sealed class ReceiptRow { public string RequestHash { get; set; } = ""; public string ResultJson { get; set; } = ""; }
}
