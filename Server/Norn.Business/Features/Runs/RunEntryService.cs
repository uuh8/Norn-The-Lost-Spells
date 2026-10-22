using System.Text.Json;
using Dapper;
using Norn.Business.Content;
using Norn.Business.Features.Inventory;
using Norn.Business.Features.Rooms;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Runs;

// 只表示已提交的开局预留，不是Ready、JoinTicket或已认证的网络连接。
public sealed record RunReservation(string RunId, string RoomId, uint RoomEpoch, uint StartRevision);

/// <summary>把线上资产写权限从整备期切换到指定Run/Epoch；不启动进程，不运行Gameplay，也不改动原始装备。</summary>
public sealed class RunEntryService(RoomTransactions transactions, InventoryService inventory)
{
    public Task<RunReservation> PrepareAsync(string leader, string room, uint readyRevision, CancellationToken ct)
    {
        RoomTransactions.RequireId(room);
        return transactions.MutateAsync(async (db, tx) =>
        {
            await RoomTransactions.RequireMember(db, tx, room, leader, ct);
            var party = await RoomTransactions.Party(db, tx, room, ct);
            if (party.LeaderCharacterId != leader) throw new OnlineFailure(403, "LeaderRequired");
            if (party.ReadyRevision != readyRevision) throw new OnlineFailure(409, "RoomRevisionConflict");
            if (party.State == "Starting" && party.ActiveRunId != null)
            {
                // 双击/回包丢失/新Host重试，都返回已提交的同一Run；不能再插入一份初始装备快照。
                var existing = await db.QuerySingleAsync<RunRow>(new CommandDefinition("SELECT * FROM Runs WHERE RunId=@Run", new { Run = party.ActiveRunId }, tx, cancellationToken: ct));
                if (existing.State != "Starting" || existing.RoomEpoch != party.RoomEpoch || existing.StartRevision != readyRevision) throw new OnlineFailure(409, "RunStateConflict");
                return new RunReservation(existing.RunId, room, existing.RoomEpoch, existing.StartRevision);
            }
            RoomTransactions.RequireOpen(party);
            var members = (await RoomTransactions.Read(db, tx, room, ct)).Members;
            if (members.Length < 2) throw new OnlineFailure(409, "NotEnoughPlayers");
            if (members.Length > 4 || members.Any(m => !m.Ready)) throw new OnlineFailure(409, "MembersNotReady");
            // 稳定锁顺序避免将来多聚合操作形成循环；Inventory只取一个角色锁，因此不会反向等待Gate。
            Array.Sort(members, (a, b) => StringComparer.Ordinal.Compare(a.PlayerId, b.PlayerId));
            foreach (var member in members)
            {
                uint version = await db.ExecuteScalarAsync<uint>(new CommandDefinition("SELECT Version FROM Characters WHERE CharacterId=@Player FOR UPDATE", new { Player = member.PlayerId }, tx, cancellationToken: ct));
                if (version != member.InventoryVersion) throw new OnlineFailure(409, "ProfileVersionConflict");
                if (await db.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM AssetLeases WHERE CharacterId=@Player", new { Player = member.PlayerId }, tx, cancellationToken: ct)) > 0) throw new OnlineFailure(409, "AssetsLeased");
            }
            string run = Guid.NewGuid().ToString("N"); uint epoch = RoomTransactions.Next(party.RoomEpoch);
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO Runs(RunId,RoomId,RoomEpoch,StartRevision,State,CreatedAtUtc) VALUES(@Run,@Room,@Epoch,@Revision,'Starting',UTC_TIMESTAMP(6))
                """, new { Run = run, Room = room, Epoch = epoch, Revision = readyRevision }, tx, cancellationToken: ct));
            foreach (var member in members)
            {
                // 当前N1最多24个位置，快照有界；包含仓库但全部原位保留，后续副本只获得携带区域的Gameplay权限。
                var snapshot = await inventory.ReadSnapshotAsync(db, tx, member.PlayerId, ct);
                await db.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO RunMembers(RunId,CharacterId,InventoryVersion,EntrySnapshot,State) VALUES(@Run,@Player,@Version,@Snapshot,'Reserved');
                    INSERT INTO AssetLeases(CharacterId,RunId,RoomEpoch) VALUES(@Player,@Run,@Epoch)
                    """, new { Run = run, Player = member.PlayerId, Version = snapshot.Version, Snapshot = JsonSerializer.Serialize(snapshot, ContentManifestValidator.Json), Epoch = epoch }, tx, cancellationToken: ct));
            }
            await db.ExecuteAsync(new CommandDefinition("""
                UPDATE Parties SET State='Starting',RoomEpoch=@Epoch,ActiveRunId=@Run WHERE RoomId=@Room;
                INSERT INTO RunLifecycleEvents(RunId,RoomEpoch,EventCode,RecordedAtUtc) VALUES(@Run,@Epoch,'EntryReserved',UTC_TIMESTAMP(6))
                """, new { Run = run, Room = room, Epoch = epoch }, tx, cancellationToken: ct));
            return new RunReservation(run, room, epoch, readyRevision);
        }, ct);
    }

    public Task<bool> FailStartAsync(string run, uint epoch, string code, CancellationToken ct)
    {
        RoomTransactions.RequireId(run);
        if (code is not ("BuildNotFound" or "PortUnavailable" or "ReadyTimeout" or "LaunchFailed" or "Cancelled")) throw new ArgumentException("失败记录只能使用已知Code，不能记录含凭据的任意Exception文本。", nameof(code));
        return transactions.MutateAsync(async (db, tx) =>
        {
            var record = await db.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition("SELECT * FROM Runs WHERE RunId=@Run", new { Run = run }, tx, cancellationToken: ct));
            if (record == null || record.State != "Starting" || record.RoomEpoch != epoch) return false;
            var party = await RoomTransactions.Party(db, tx, record.RoomId, ct);
            if (party.State != "Starting" || party.ActiveRunId != run || party.RoomEpoch != epoch) return false;
            string[] players = (await db.QueryAsync<string>(new CommandDefinition("SELECT CharacterId FROM RunMembers WHERE RunId=@Run ORDER BY CharacterId", new { Run = run }, tx, cancellationToken: ct))).ToArray();
            foreach (string player in players)
                _ = await db.ExecuteScalarAsync<uint>(new CommandDefinition("SELECT Version FROM Characters WHERE CharacterId=@Player FOR UPDATE", new { Player = player }, tx, cancellationToken: ct));
            // 启动前资产从未被移动/消耗，无需把快照再发一次；只解除匹配Run+Epoch的写锁，不制造新实例。
            // 同一事务先失效Epoch再清租约，对外只有提交后的整体结果可见。
            await db.ExecuteAsync(new CommandDefinition("""
                UPDATE Parties SET State='Open',RoomEpoch=@NextEpoch,ActiveRunId=NULL,ReadyRevision=@Revision WHERE RoomId=@Room;
                UPDATE PartyMembers SET IsReady=FALSE,ReadyInventoryVersion=0 WHERE RoomId=@Room;
                UPDATE Runs SET State='Failed',FailureCode=@Code WHERE RunId=@Run AND RoomEpoch=@Epoch;
                UPDATE RunMembers SET State='RolledBack' WHERE RunId=@Run;
                DELETE FROM AssetLeases WHERE RunId=@Run AND RoomEpoch=@Epoch;
                INSERT INTO RunLifecycleEvents(RunId,RoomEpoch,EventCode,RecordedAtUtc) VALUES(@Run,@Epoch,@Code,UTC_TIMESTAMP(6))
                """, new { Run = run, Room = record.RoomId, Epoch = epoch, NextEpoch = RoomTransactions.Next(epoch), Revision = RoomTransactions.Next(party.ReadyRevision), Code = code }, tx, cancellationToken: ct));
            return true;
        }, ct);
    }
    private sealed class RunRow
    { public string RunId { get; set; } = ""; public string RoomId { get; set; } = ""; public uint RoomEpoch { get; set; } public uint StartRevision { get; set; } public string State { get; set; } = ""; }
}
