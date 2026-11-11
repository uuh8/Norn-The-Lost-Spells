using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Game.NetworkProtocol;
using MySqlConnector;
using Norn.Business.Content;
using Norn.Business.Features.Inventory;
using Norn.Business.Features.Rooms;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Runs;

// 只表示已提交的开局预留，不是Ready、JoinTicket或已认证的网络连接。
public sealed record RunReservation(string RunId, string RoomId, uint RoomEpoch, uint StartRevision);

/// <summary>原子发放获批入局物品并把资产写权限切换到指定Run/Epoch；不启动进程、不运行Gameplay，不替换既有装备。</summary>
public sealed class RunEntryService(RoomTransactions transactions, InventoryService inventory, ContentManifestValidator content)
{
    public Task<RunReservation> PrepareAsync(string leader, string room, uint readyRevision, CancellationToken ct)
    {
        RoomTransactions.RequireId(room);
        return transactions.MutateAsync(async (db, tx) =>
        {
            await RoomTransactions.RequireMember(db, tx, room, leader, ct);
            var party = await RoomTransactions.Party(db, tx, room, ct);
            if (party.DungeonId == "home") throw new OnlineFailure(400, "HomeSinglePlayerOnly");
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
            foreach (var member in members) await FreezeMember(db, tx, member.PlayerId, member.Seat, run, epoch, ct);
            await db.ExecuteAsync(new CommandDefinition("""
                UPDATE Parties SET State='Starting',RoomEpoch=@Epoch,ActiveRunId=@Run WHERE RoomId=@Room;
                INSERT INTO RunLifecycleEvents(RunId,RoomEpoch,EventCode,RecordedAtUtc) VALUES(@Run,@Epoch,'EntryReserved',UTC_TIMESTAMP(6))
                """, new { Run = run, Room = room, Epoch = epoch }, tx, cancellationToken: ct));
            return new RunReservation(run, room, epoch, readyRevision);
        }, ct);
    }

    /// <summary>仅Launcher白名单验证后的单人入口调用；同账户、同命令固定返回原Run，不补发或另起实例。</summary>
    internal Task<RunReservation> PrepareSingleAsync(string player, SinglePlayerEntryRequest request, CancellationToken ct, string? practiceSessionScope = null)
    {
        if (request == null || !Guid.TryParseExact(request.CommandId, "N", out var id) || id.ToString("N") != request.CommandId
            || request.ExpectedInventoryVersion == 0) throw new OnlineFailure(400, "InvalidSinglePlayerEntry");
        if (request.DungeonId == "home" && !AccountPracticeContinuityStore.IsScope(practiceSessionScope))
            throw new OnlineFailure(409, "PracticeSessionScopeRequired");
        string canonical = JsonSerializer.Serialize(new { Kind = "SinglePlayerEntry", PlayerId = player,
            request.CommandId, request.DungeonId, request.ExpectedInventoryVersion }, ContentManifestValidator.Json);
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return transactions.MutateAsync(async (db, tx) =>
        {
            // 与现有库存命令同样先锁角色，再读命令收据；RoomMutationGate同时覆盖两个业务Host。
            uint version = await db.ExecuteScalarAsync<uint>(new CommandDefinition(
                "SELECT Version FROM Characters WHERE CharacterId=@Player FOR UPDATE", new { Player = player }, tx, cancellationToken: ct));
            var previous = await db.QuerySingleOrDefaultAsync<SingleReceiptRow>(new CommandDefinition(
                "SELECT RequestHash,ResultJson FROM InventoryCommands WHERE CharacterId=@Player AND CommandId=@Command",
                new { Player = player, Command = request.CommandId }, tx, cancellationToken: ct));
            if (previous != null)
            {
                if (previous.RequestHash != hash) throw new OnlineFailure(409, "CommandConflict");
                var saved = JsonSerializer.Deserialize<SingleReceipt>(previous.ResultJson, ContentManifestValidator.Json);
                if (saved?.Kind != "SinglePlayerEntry" || saved.Reservation == null) throw new OnlineFailure(409, "SinglePlayerReceiptInvalid");
                return saved.Reservation;
            }
            if (version != request.ExpectedInventoryVersion) throw new OnlineFailure(409, "ProfileVersionConflict");
            string? previousRoom = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                "SELECT RoomId FROM PartyMembers WHERE CharacterId=@Player", new { Player = player }, tx, cancellationToken: ct));
            RoomTransactions.PartyRow? reusable = null;
            if (previousRoom != null)
            {
                reusable = await RoomTransactions.Party(db, tx, previousRoom, ct);
                int count = await db.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM PartyMembers WHERE RoomId=@Room",
                    new { Room = previousRoom }, tx, cancellationToken: ct));
                int ownSeat = await db.ExecuteScalarAsync<int>(new CommandDefinition("SELECT Seat FROM PartyMembers WHERE CharacterId=@Player AND RoomId=@Room",
                    new { Player = player, Room = previousRoom }, tx, cancellationToken: ct));
                if (count != 1 || ownSeat != 0 || reusable.LeaderCharacterId != player || reusable.State != "Open"
                    || !string.IsNullOrEmpty(reusable.ActiveRunId) || !await RoomTransactions.IsSinglePlayerRoom(db, tx, previousRoom, ct))
                    throw new OnlineFailure(409, "AlreadyInRoom");
            }
            if (await db.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM AssetLeases WHERE CharacterId=@Player",
                new { Player = player }, tx, cancellationToken: ct)) != 0) throw new OnlineFailure(409, "AssetsLeased");
            string room = previousRoom ?? Guid.NewGuid().ToString("N"), run = Guid.NewGuid().ToString("N");
            uint epoch = reusable == null ? 1 : RoomTransactions.Next(reusable.RoomEpoch);
            uint revision = reusable == null ? 1 : RoomTransactions.Next(reusable.ReadyRevision);
            // 单人意图直接预留本人，不写IsReady=true。Party直到Starting才提交，对外没有可被加入的Open窗口。
            if (reusable == null)
                await db.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO Parties(RoomId,DungeonId,LeaderCharacterId,State,ReadyRevision,RoomEpoch,ActiveRunId,CreatedAtUtc,IsMatchmaking)
                    VALUES(@Room,@Dungeon,@Player,'Starting',@Revision,@Epoch,NULL,UTC_TIMESTAMP(6),FALSE);
                    INSERT INTO PartyMembers(CharacterId,RoomId,Seat,IsReady,ReadyInventoryVersion,LastSeenUtc)
                    VALUES(@Player,@Room,0,FALSE,0,UTC_TIMESTAMP(6))
                    """, new { Room = room, Dungeon = request.DungeonId, Player = player, Epoch = epoch, Revision = revision }, tx, cancellationToken: ct));
            else
                // 仅复用已结束且无租约的本人单人空局；原装备/命令收据不动，新Run使用新的fencing水位。
                await db.ExecuteAsync(new CommandDefinition("""
                    UPDATE Parties SET DungeonId=@Dungeon,State='Starting',ReadyRevision=@Revision,RoomEpoch=@Epoch WHERE RoomId=@Room;
                    UPDATE PartyMembers SET IsReady=FALSE,ReadyInventoryVersion=0,LastSeenUtc=UTC_TIMESTAMP(6) WHERE RoomId=@Room AND CharacterId=@Player
                    """, new { Room = room, Dungeon = request.DungeonId, Player = player, Epoch = epoch, Revision = revision }, tx, cancellationToken: ct));
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO Runs(RunId,RoomId,RoomEpoch,StartRevision,State,CreatedAtUtc)
                VALUES(@Run,@Room,@Epoch,@Revision,'Starting',UTC_TIMESTAMP(6));
                UPDATE Parties SET ActiveRunId=@Run WHERE RoomId=@Room
                """, new { Room = room, Dungeon = request.DungeonId, Player = player, Run = run, Epoch = epoch, Revision = revision }, tx, cancellationToken: ct));
            // scope来自Endpoint当前Bearer，只在首次预留冻结。原幂等正文/未知重放语义保持，
            // 不让同账号新Session通过重放旧命令改写原Entry的作用域或连续性。
            await FreezeMember(db, tx, player, 0, run, epoch, ct, practiceSessionScope, request.DungeonId == "home");
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO RunLifecycleEvents(RunId,RoomEpoch,EventCode,RecordedAtUtc) VALUES
                (@Run,@Epoch,'EntryReserved',UTC_TIMESTAMP(6)),(@Run,@Epoch,'SinglePlayerEntry',UTC_TIMESTAMP(6))
                """, new { Run = run, Epoch = epoch }, tx, cancellationToken: ct));
            var reservation = new RunReservation(run, room, epoch, revision);
            await db.ExecuteAsync(new CommandDefinition("INSERT INTO InventoryCommands VALUES(@Player,@Command,@Hash,@Json,UTC_TIMESTAMP(6))",
                new { Player = player, Command = request.CommandId, Hash = hash,
                    Json = JsonSerializer.Serialize(new SingleReceipt { Reservation = reservation }, ContentManifestValidator.Json) }, tx, cancellationToken: ct));
            return reservation;
        }, ct);
    }

    private async Task FreezeMember(MySqlConnection db, MySqlTransaction tx, string player, int seat, string run, uint epoch, CancellationToken ct,
        string? practiceSessionScope = null, bool home = false)
    {
        // 与合作入场共用原有事务：真实GMU/固定杖/附件和钱包来自账号，不读取模板补量或旧本地档。
        var snapshot = await inventory.ReadSnapshotAsync(db, tx, player, ct, practiceSessionScope);
        inventory.FreezeCombatAssets(snapshot, run, (ulong)seat + 1);
        snapshot = await inventory.EnsureEntryBackpackAsync(db, tx, snapshot, run, ct, practiceSessionScope);
        // Home是原个人和平实验，不能借反复进门变成获赠副本开局瓶的经济入口。
        if (!home) snapshot = await inventory.GrantEntryContainerAsync(db, tx, snapshot, ct, practiceSessionScope);
        inventory.FreezeCombatAssets(snapshot, run, (ulong)seat + 1);
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO RunMembers(RunId,CharacterId,InventoryVersion,EntrySnapshot,State) VALUES(@Run,@Player,@Version,@Snapshot,'Reserved');
            INSERT INTO AssetLeases(CharacterId,RunId,RoomEpoch) VALUES(@Player,@Run,@Epoch)
            """, new { Run = run, Player = player, Version = snapshot.Version,
                Snapshot = JsonSerializer.Serialize(snapshot, ContentManifestValidator.Json), Epoch = epoch }, tx, cancellationToken: ct));
        if (home) await HomePracticeAssets.SeedEntryAsync(db, tx, snapshot, run, content, ct);
    }
    private sealed class SingleReceiptRow { public string RequestHash { get; set; } = ""; public string ResultJson { get; set; } = ""; }
    private sealed class SingleReceipt { public SingleReceipt() { } public string Kind { get; set; } = "SinglePlayerEntry"; public RunReservation? Reservation { get; set; } }

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
            if (party.DungeonId == "home") await HomePracticeAssets.ReleaseUnstartedSeedAsync(db, tx, run, ct);
            // 启动前原装备未被移动/消耗；已提交包/瓶仍是真实资产，失败不删除或按快照再发一次。
            // 以后创建的是新Run时按开局政策重新检查空道具位，不把旧Run重试误当新局。
            // 只解除匹配Run+Epoch的写锁，不把冻结快照当作退款/结算再发一次。
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
