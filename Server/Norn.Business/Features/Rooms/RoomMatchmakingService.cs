using Dapper;
using Game.NetworkProtocol;
using MySqlConnector;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Rooms;

/// <summary>首版按Dungeon分队，短数据库Gate原子分配，不由Client先查询空位再竞争Join。</summary>
public sealed class RoomMatchmakingService(RoomTransactions transactions, OnlineOptions options, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    public Task<RoomReply> MatchAsync(string player, CreateRoomRequest request, CancellationToken ct)
    {
        if (!options.DungeonIds.Contains(request.DungeonId, StringComparer.Ordinal)) throw new OnlineFailure(400, "UnknownDungeon");
        return transactions.MutateAsync(async (db, tx) =>
        {
            await Expire(db, tx, ct);
            string? current = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT RoomId FROM PartyMembers WHERE CharacterId=@Player", new { Player = player }, tx, cancellationToken: ct));
            if (current != null)
            {
                var view = await RoomTransactions.Read(db, tx, current, ct);
                if (view.DungeonId != request.DungeonId) throw new OnlineFailure(409, "DifferentDungeonRoom");
                await Touch(db, tx, player, ct); return view; // 回包丢失或Client重启恢复同房，不再分配第二席。
            }
            string? room = await db.QueryFirstOrDefaultAsync<string>(new CommandDefinition("""
                SELECT p.RoomId FROM Parties p JOIN PartyMembers m ON m.RoomId=p.RoomId
                WHERE p.State='Open' AND p.IsMatchmaking=TRUE AND p.DungeonId=@Dungeon
                GROUP BY p.RoomId,p.CreatedAtUtc HAVING COUNT(*)<4 ORDER BY p.CreatedAtUtc,p.RoomId LIMIT 1
                """, new { Dungeon = request.DungeonId }, tx, cancellationToken: ct));
            if (room == null)
            {
                room = Guid.NewGuid().ToString("N");
                await db.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO Parties(RoomId,DungeonId,LeaderCharacterId,State,ReadyRevision,RoomEpoch,CreatedAtUtc,IsMatchmaking)
                    VALUES(@Room,@Dungeon,@Player,'Open',1,0,@Now,TRUE);
                    INSERT INTO PartyMembers(CharacterId,RoomId,Seat,LastSeenUtc) VALUES(@Player,@Room,0,@Now)
                    """, new { Room = room, Dungeon = request.DungeonId, Player = player, Now }, tx, cancellationToken: ct));
            }
            else
            {
                var party = await RoomTransactions.Party(db, tx, room, ct);
                int[] seats = (await db.QueryAsync<int>(new CommandDefinition("SELECT Seat FROM PartyMembers WHERE RoomId=@Room", new { Room = room }, tx, cancellationToken: ct))).ToArray();
                int seat = 0; while (seat < 4 && Array.IndexOf(seats, seat) >= 0) seat++;
                if (seat == 4) throw new OnlineFailure(409, "RoomFull");
                await db.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO PartyMembers(CharacterId,RoomId,Seat,LastSeenUtc) VALUES(@Player,@Room,@Seat,@Now);
                    UPDATE PartyMembers SET IsReady=FALSE,ReadyInventoryVersion=0 WHERE RoomId=@Room;
                    UPDATE Parties SET ReadyRevision=@Revision WHERE RoomId=@Room
                    """, new { Player = player, Room = room, Seat = seat, Now, Revision = RoomTransactions.Next(party.ReadyRevision) }, tx, cancellationToken: ct));
            }
            return await RoomTransactions.Read(db, tx, room, ct);
        }, ct);
    }

    public Task<RoomReply> PresenceAsync(string player, string room, CancellationToken ct)
    {
        RoomTransactions.RequireId(room);
        return transactions.MutateAsync(async (db, tx) =>
        {
            await RoomTransactions.RequireMember(db, tx, room, player, ct);
            await Touch(db, tx, player, ct); return await RoomTransactions.Read(db, tx, room, ct);
        }, ct);
    }
    public Task<int> ExpireAsync(CancellationToken ct) => transactions.MutateAsync((db, tx) => Expire(db, tx, ct), ct);

    private Task Touch(MySqlConnection db, MySqlTransaction tx, string player, CancellationToken ct) => db.ExecuteAsync(new CommandDefinition(
        "UPDATE PartyMembers SET LastSeenUtc=@Now WHERE CharacterId=@Player", new { Now, Player = player }, tx, cancellationToken: ct));

    private async Task<int> Expire(MySqlConnection db, MySqlTransaction tx, CancellationToken ct)
    {
        // 每次最多处理100个大厅；Starting/Running只受实例Epoch/Lease约束，不能由UI心跳回收。
        string[] rooms = (await db.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT p.RoomId FROM Parties p JOIN PartyMembers m ON m.RoomId=p.RoomId
            WHERE p.IsMatchmaking=TRUE AND p.State='Open' AND m.LastSeenUtc<=@Cutoff LIMIT 100
            """, new { Cutoff = Now.AddSeconds(-90) }, tx, cancellationToken: ct))).ToArray();
        foreach (string room in rooms)
        {
            var party = await RoomTransactions.Party(db, tx, room, ct);
            await db.ExecuteAsync(new CommandDefinition("DELETE FROM PartyMembers WHERE RoomId=@Room AND LastSeenUtc<=@Cutoff", new { Room = room, Cutoff = Now.AddSeconds(-90) }, tx, cancellationToken: ct));
            var members = (await db.QueryAsync<string>(new CommandDefinition("SELECT CharacterId FROM PartyMembers WHERE RoomId=@Room ORDER BY Seat", new { Room = room }, tx, cancellationToken: ct))).ToArray();
            string leader = Array.IndexOf(members, party.LeaderCharacterId) >= 0 ? party.LeaderCharacterId : members.FirstOrDefault() ?? party.LeaderCharacterId;
            await db.ExecuteAsync(new CommandDefinition("""
                UPDATE Parties SET State=@State,LeaderCharacterId=@Leader,ReadyRevision=@Revision WHERE RoomId=@Room;
                UPDATE PartyMembers SET IsReady=FALSE,ReadyInventoryVersion=0 WHERE RoomId=@Room
                """, new { Room = room, State = members.Length == 0 ? "Closed" : "Open", Leader = leader, Revision = RoomTransactions.Next(party.ReadyRevision) }, tx, cancellationToken: ct));
        }
        return rooms.Length;
    }
}
