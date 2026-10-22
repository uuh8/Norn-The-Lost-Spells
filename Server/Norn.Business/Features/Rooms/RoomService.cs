using System.Data;
using Dapper;
using Game.NetworkProtocol;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Rooms;

/// <summary>业务服拥有成员/Ready权威；Client只提供自己的意图，不能替另一角色占位或准备。</summary>
public sealed class RoomService(OnlineDatabase database, RoomTransactions transactions, OnlineOptions options)
{
    public Task<RoomReply> CreateAsync(string player, CreateRoomRequest request, CancellationToken ct)
    {
        if (!options.DungeonIds.Contains(request.DungeonId, StringComparer.Ordinal)) throw new OnlineFailure(400, "UnknownDungeon");
        return transactions.MutateAsync(async (db, tx) =>
        {
            if (await db.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM PartyMembers WHERE CharacterId=@Player", new { Player = player }, tx, cancellationToken: ct)) != 0) throw new OnlineFailure(409, "AlreadyInRoom");
            string room = Guid.NewGuid().ToString("N");
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO Parties(RoomId,DungeonId,LeaderCharacterId,State,ReadyRevision,RoomEpoch,CreatedAtUtc) VALUES(@Room,@Dungeon,@Player,'Open',1,0,UTC_TIMESTAMP(6));
                INSERT INTO PartyMembers(CharacterId,RoomId,Seat) VALUES(@Player,@Room,0)
                """, new { Room = room, Dungeon = request.DungeonId, Player = player }, tx, cancellationToken: ct));
            return await RoomTransactions.Read(db, tx, room, ct);
        }, ct);
    }

    public Task<RoomReply> JoinAsync(string player, string room, CancellationToken ct)
    {
        RoomTransactions.RequireId(room);
        return transactions.MutateAsync(async (db, tx) =>
        {
            var party = await RoomTransactions.Party(db, tx, room, ct);
            string? existing = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT RoomId FROM PartyMembers WHERE CharacterId=@Player", new { Player = player }, tx, cancellationToken: ct));
            if (existing == room) return await RoomTransactions.Read(db, tx, room, ct); // 响应丢失后的重复加入不推进Revision。
            if (existing != null) throw new OnlineFailure(409, "AlreadyInRoom");
            RoomTransactions.RequireOpen(party);
            int[] seats = (await db.QueryAsync<int>(new CommandDefinition("SELECT Seat FROM PartyMembers WHERE RoomId=@Room", new { Room = room }, tx, cancellationToken: ct))).ToArray();
            int seat = 0; while (seat < 4 && Array.IndexOf(seats, seat) >= 0) seat++;
            if (seat == 4) throw new OnlineFailure(409, "RoomFull");
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO PartyMembers(CharacterId,RoomId,Seat) VALUES(@Player,@Room,@Seat);
                UPDATE PartyMembers SET IsReady=FALSE,ReadyInventoryVersion=0 WHERE RoomId=@Room;
                UPDATE Parties SET ReadyRevision=@Revision WHERE RoomId=@Room
                """, new { Player = player, Room = room, Seat = seat, Revision = RoomTransactions.Next(party.ReadyRevision) }, tx, cancellationToken: ct));
            return await RoomTransactions.Read(db, tx, room, ct);
        }, ct);
    }

    public async Task<RoomReply> GetAsync(string player, string room, CancellationToken ct)
    {
        RoomTransactions.RequireId(room);
        await using var db = await database.OpenAsync(ct);
        // 读不抢全局写Gate；一致视图防止把旧Revision与新成员列表拼成一个应答。
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await RoomTransactions.RequireMember(db, tx, room, player, ct);
        var reply = await RoomTransactions.Read(db, tx, room, ct); await tx.CommitAsync(ct); return reply;
    }

    public async Task<RoomReply> GetCurrentAsync(string player, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        string? room = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT RoomId FROM PartyMembers WHERE CharacterId=@Player", new { Player = player }, tx, cancellationToken: ct));
        if (room == null) throw new OnlineFailure(404, "RoomNotFound");
        // 与成员列表同一快照；创建/加入回包丢失后无需让Client保存RoomId才能找回自己的房间。
        var reply = await RoomTransactions.Read(db, tx, room, ct); await tx.CommitAsync(ct); return reply;
    }

    public Task<RoomReply> SetReadyAsync(string player, string room, RoomReadyRequest request, CancellationToken ct)
    {
        RoomTransactions.RequireId(room);
        return transactions.MutateAsync(async (db, tx) =>
        {
            await RoomTransactions.RequireMember(db, tx, room, player, ct);
            var party = await RoomTransactions.Party(db, tx, room, ct); RoomTransactions.RequireOpen(party);
            if (party.ReadyRevision != request.ExpectedRevision) throw new OnlineFailure(409, "RoomRevisionConflict");
            uint version = await db.ExecuteScalarAsync<uint>(new CommandDefinition("SELECT Version FROM Characters WHERE CharacterId=@Player FOR UPDATE", new { Player = player }, tx, cancellationToken: ct));
            if (request.Ready && version != request.ExpectedInventoryVersion) throw new OnlineFailure(409, "ProfileVersionConflict");
            if (await db.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM AssetLeases WHERE CharacterId=@Player", new { Player = player }, tx, cancellationToken: ct)) > 0) throw new OnlineFailure(409, "AssetsLeased");
            uint readyVersion = request.Ready ? version : 0;
            var current = await RoomTransactions.Read(db, tx, room, ct);
            var member = current.Members.Single(m => m.PlayerId == player);
            if (member.Ready == request.Ready && member.InventoryVersion == readyVersion) return current;
            await db.ExecuteAsync(new CommandDefinition("""
                UPDATE PartyMembers SET IsReady=@Ready,ReadyInventoryVersion=@Version WHERE RoomId=@Room AND CharacterId=@Player;
                UPDATE Parties SET ReadyRevision=@Revision WHERE RoomId=@Room
                """, new { Player = player, Room = room, request.Ready, Version = readyVersion, Revision = RoomTransactions.Next(party.ReadyRevision) }, tx, cancellationToken: ct));
            return await RoomTransactions.Read(db, tx, room, ct);
        }, ct);
    }

    public Task<bool> LeaveAsync(string player, string room, CancellationToken ct)
    {
        RoomTransactions.RequireId(room);
        return transactions.MutateAsync(async (db, tx) =>
        {
            await RoomTransactions.RequireMember(db, tx, room, player, ct);
            var party = await RoomTransactions.Party(db, tx, room, ct); RoomTransactions.RequireOpen(party);
            await db.ExecuteAsync(new CommandDefinition("DELETE FROM PartyMembers WHERE RoomId=@Room AND CharacterId=@Player", new { Room = room, Player = player }, tx, cancellationToken: ct));
            string? next = await db.QueryFirstOrDefaultAsync<string>(new CommandDefinition("SELECT CharacterId FROM PartyMembers WHERE RoomId=@Room ORDER BY Seat LIMIT 1", new { Room = room }, tx, cancellationToken: ct));
            await db.ExecuteAsync(new CommandDefinition("""
                UPDATE Parties SET State=@State,LeaderCharacterId=@Leader,ReadyRevision=@Revision WHERE RoomId=@Room;
                UPDATE PartyMembers SET IsReady=FALSE,ReadyInventoryVersion=0 WHERE RoomId=@Room
                """, new { Room = room, State = next == null ? "Closed" : "Open", Leader = party.LeaderCharacterId == player ? next ?? player : party.LeaderCharacterId,
                    Revision = RoomTransactions.Next(party.ReadyRevision) }, tx, cancellationToken: ct));
            return true;
        }, ct);
    }
}
