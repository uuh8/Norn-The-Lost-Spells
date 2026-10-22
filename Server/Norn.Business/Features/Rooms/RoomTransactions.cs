using System.Data;
using Dapper;
using Game.NetworkProtocol;
using MySqlConnector;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Rooms;

/// <summary>所有Room写按Gate→Party→角色排序取得锁；只覆盖SQL，不跨HTTP/进程启动持锁。</summary>
public sealed class RoomTransactions(OnlineDatabase database)
{
    public async Task<T> MutateAsync<T>(Func<MySqlConnection, MySqlTransaction, Task<T>> operation, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // 低并发首版优先让不变量可推导；持久化锁也覆盖两个业务Host，不是仅进程内Semaphore。
        int gate = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT GateId FROM RoomMutationGate WHERE GateId=1 FOR UPDATE", transaction: tx, cancellationToken: ct));
        if (gate != 1) throw new InvalidOperationException("RoomMutationGate缺失，拒绝在无串行化保证时修改房间。");
        T value = await operation(connection, tx);
        await tx.CommitAsync(ct); return value;
    }

    internal static void RequireId(string? id)
    { if (id == null || !Guid.TryParseExact(id, "N", out var guid) || guid.ToString("N") != id) throw new OnlineFailure(400, "InvalidRoomId"); }
    internal static uint Next(uint value)
    { if (value == uint.MaxValue) throw new OnlineFailure(409, "RoomRevisionExhausted"); return value + 1; }
    internal static async Task<PartyRow> Party(MySqlConnection db, MySqlTransaction tx, string room, CancellationToken ct) =>
        await db.QuerySingleOrDefaultAsync<PartyRow>(new CommandDefinition("SELECT * FROM Parties WHERE RoomId=@Room", new { Room = room }, tx, cancellationToken: ct)) ?? throw new OnlineFailure(404, "RoomNotFound");
    internal static async Task RequireMember(MySqlConnection db, MySqlTransaction tx, string room, string player, CancellationToken ct)
    {
        if (await db.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM PartyMembers WHERE RoomId=@Room AND CharacterId=@Player", new { Room = room, Player = player }, tx, cancellationToken: ct)) != 1)
            throw new OnlineFailure(404, "RoomNotFound");
    }
    internal static void RequireOpen(PartyRow party)
    { if (party.State != "Open") throw new OnlineFailure(409, "RoomNotOpen"); }
    internal static async Task<RoomReply> Read(MySqlConnection db, MySqlTransaction tx, string room, CancellationToken ct)
    {
        var party = await Party(db, tx, room, ct);
        var members = await db.QueryAsync<MemberRow>(new CommandDefinition("""
            SELECT m.CharacterId,a.Username,m.Seat,m.IsReady,m.ReadyInventoryVersion FROM PartyMembers m
            JOIN Characters c ON c.CharacterId=m.CharacterId JOIN Accounts a ON a.AccountId=c.AccountId
            WHERE m.RoomId=@Room ORDER BY m.Seat
            """, new { Room = room }, tx, cancellationToken: ct));
        return new RoomReply { RoomId = room, DungeonId = party.DungeonId, LeaderPlayerId = party.LeaderCharacterId, State = party.State,
            ReadyRevision = party.ReadyRevision, RoomEpoch = party.RoomEpoch, RunId = party.ActiveRunId ?? "",
            Members = members.Select(m => new RoomMemberReply { PlayerId = m.CharacterId, Username = m.Username, Seat = m.Seat, Ready = m.IsReady, InventoryVersion = m.ReadyInventoryVersion }).ToArray() };
    }
    internal sealed class PartyRow
    {
        public string RoomId { get; set; } = ""; public string DungeonId { get; set; } = ""; public string LeaderCharacterId { get; set; } = "";
        public string State { get; set; } = ""; public uint ReadyRevision { get; set; } public uint RoomEpoch { get; set; } public string? ActiveRunId { get; set; }
    }
    private sealed class MemberRow
    { public string CharacterId { get; set; } = ""; public string Username { get; set; } = ""; public int Seat { get; set; } public bool IsReady { get; set; } public uint ReadyInventoryVersion { get; set; } }
}
