using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using MySqlConnector;
using Norn.Business.Content;
using Norn.Business.Features.Accounts;
using Norn.Business.Features.Inventory;
using Norn.Business.Features.Runs;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Rooms;

public sealed record InstanceLaunchGrant(InstanceLaunchDescriptor Descriptor, string? Secret, bool IsNew);
public sealed record InstanceProcessIdentity(int ProcessId, long StartTicks, string Executable);

/// <summary>实例/Ticket只存摘要；所有权限与Epoch变更都在Room Gate保护的短事务中，不跨启动进程持锁。</summary>
public sealed class RoomInstanceService(OnlineDatabase database, RoomTransactions transactions, ContentManifestValidator content, OnlineOptions options, TimeProvider clock)
{
    private RoomInstanceOptions Config => options.Instances;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    /// <summary>
    /// 在短 SQL 事务里声明唯一实例，并从不可变 Party 取 Dungeon；进程启动由 RoomLauncher 在提交后执行。
    /// 这样 HTTP 重试只能拿回原 Instance/Port/地图，不会因尚未登记 PID 而新起第二个服务器。
    /// </summary>
    public Task<InstanceLaunchGrant> AllocateAsync(RunReservation run, CancellationToken ct)
    {
        if (!Config.Enabled) throw new OnlineFailure(503, "InstancesDisabled"); Config.Validate();
        return transactions.MutateAsync(async (db, tx) =>
        {
            var party = await RoomTransactions.Party(db, tx, run.RoomId, ct);
            if (party.ActiveRunId != run.RunId || party.RoomEpoch != run.RoomEpoch || party.State != "Starting") throw new OnlineFailure(409, "InstanceEpochExpired");
            // DungeonId 只在建房事务中写入，现有 API 不允许换图；在同一 Room Gate 事务读取，
            // 避免进程启动重试从调用者或旧 launch.json 猜地图。未来允许换图时必须改为读取冻结 Run 字段。
            string dungeon = RequireKnownDungeon(party.DungeonId);
            var previous = await db.QuerySingleOrDefaultAsync<InstanceRow>(new CommandDefinition("SELECT * FROM ServerInstances WHERE RunId=@Run", new { Run = run.RunId }, tx, cancellationToken: ct));
            if (previous != null) return new InstanceLaunchGrant(Descriptor(previous, dungeon), null, false);
            var reserved = (await db.QueryAsync<int>(new CommandDefinition("SELECT ActivePort FROM ServerInstances WHERE ActivePort IS NOT NULL", transaction: tx, cancellationToken: ct))).ToHashSet();
            if (reserved.Count >= Config.MaximumInstances) throw new OnlineFailure(503, "InstanceCapacity");
            foreach (var endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners()) reserved.Add(endpoint.Port);
            int port = Config.FirstPort; while (port <= Config.LastPort && reserved.Contains(port)) port++;
            if (port > Config.LastPort) throw new OnlineFailure(503, "PortUnavailable");
            string id = Guid.NewGuid().ToString("N"), secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO ServerInstances(InstanceId,RunId,RoomId,RoomEpoch,State,SecretHash,UdpPort,ActivePort,ReadyDeadlineUtc)
                VALUES(@Id,@Run,@Room,@Epoch,'Launching',@Hash,@Port,@Port,@Deadline)
                """, new { Id = id, Run = run.RunId, Room = run.RoomId, Epoch = run.RoomEpoch, Hash = AccountService.HashToken(secret), Port = port, Deadline = Now.AddSeconds(Config.ReadyTimeoutSeconds) }, tx, cancellationToken: ct));
            return new InstanceLaunchGrant(Descriptor(await Get(db, tx, id, ct), dungeon), secret, true);
        }, ct);
    }
    public Task RecordProcessAsync(string instance, InstanceProcessIdentity process, CancellationToken ct) => transactions.MutateAsync(async (db, tx) =>
    {
        if (!InstanceProcesses.Matches(process)) throw new OnlineFailure(409, "InstanceProcessMismatch");
        var row = await Get(db, tx, instance, ct);
        if (row.State != "Launching" || row.ProcessId != null) throw new OnlineFailure(409, "InstanceStateConflict");
        await db.ExecuteAsync(new CommandDefinition("UPDATE ServerInstances SET ProcessId=@ProcessId,ProcessStartTicks=@StartTicks,Executable=@Executable WHERE InstanceId=@Id", new { Id = instance, process.ProcessId, process.StartTicks, process.Executable }, tx, cancellationToken: ct)); return true;
    }, ct);
    /// <summary>
    /// DS 完成地图/规则装配后才提交 Ready。Secret、进程身份、Run/Epoch、配置版本与地图都成立，
    /// 才能在同一事务发布 Ready/Running；仅看到一个活着或已监听的进程不构成可入场状态。
    /// </summary>
    public Task<InstanceReadyReply> ReadyAsync(string instance, string secret, InstanceReadyRequest request, CancellationToken ct) => transactions.MutateAsync(async (db, tx) =>
    {
        var row = await Credential(db, tx, instance, secret, ct); content.RequireVersion(request.ProtocolVersion, request.ManifestHash);
        if (request.BuildVersion != content.Manifest.BuildVersion || string.IsNullOrEmpty(content.GameplayConfigHash) || request.GameplayConfigHash != content.GameplayConfigHash)
            throw new OnlineFailure(409, "ContentVersionMismatch");
        var party = await RequireCurrent(db, tx, row, request.RoomEpoch, false, ct);
        // Ready 不只是证明进程在监听：DS 必须确认它实际装配的副本与业务授权相同。
        // 放在所有 UPDATE 前，错误/旧描述既不能发布 Running，也不能污染正确 DS 随后的重试。
        if (!DungeonIdentityRules.Matches(party.DungeonId, request.DungeonId)) throw new OnlineFailure(409, "DungeonMismatch");
        if (row.State == "Launching" && Now >= row.ReadyDeadlineUtc) throw new OnlineFailure(409, "InstanceEpochExpired");
        if (row.ProcessId != request.ProcessId || row.ProcessStartTicks != request.ProcessStartTicks || !InstanceProcesses.Matches(new InstanceProcessIdentity(request.ProcessId, request.ProcessStartTicks, row.Executable))) throw new OnlineFailure(409, "InstanceProcessMismatch");
        DateTime until = Now.AddSeconds(Config.LeaseSeconds);
        await db.ExecuteAsync(new CommandDefinition("""
            UPDATE ServerInstances SET State='Ready',LastHeartbeatUtc=@Now,LeaseExpiresUtc=@Until WHERE InstanceId=@Id;
            UPDATE Runs SET State='Running' WHERE RunId=@Run; UPDATE Parties SET State='Running' WHERE RoomId=@Room
            """, new { Id = instance, Now, Until = until, Run = row.RunId, Room = row.RoomId }, tx, cancellationToken: ct));
        var entries = await db.QueryAsync<EntryRow>(new CommandDefinition("SELECT r.EntrySnapshot,m.Seat FROM RunMembers r JOIN PartyMembers m ON m.CharacterId=r.CharacterId AND m.RoomId=@Room WHERE r.RunId=@Run ORDER BY m.Seat", new { Run = row.RunId, Room = row.RoomId }, tx, cancellationToken: ct));
        var profiles = entries.Select(e => new InstanceEntryMember { ActorId = (ulong)e.Seat + 1, Profile = JsonSerializer.Deserialize<OnlineProfileReply>(e.EntrySnapshot, ContentManifestValidator.Json)! }).ToArray();
        if (profiles.Length is < 2 or > 4 || profiles.Any(p => p.Profile.ContentManifestHash != content.Manifest.Hash)) throw new OnlineFailure(409, "EntrySnapshotMismatch");
        foreach (var member in profiles)
        {
            var assets = member.Profile.CombatAssets;
            OwnedCombatAssetRules.Require(assets, member.Profile.PlayerId, content.GameplayConfigHash, content, true);
            if (assets.RunId != row.RunId || assets.ActorId != member.ActorId || assets.InventoryVersion != member.Profile.Version)
                throw new OnlineFailure(409, "EntrySnapshotMismatch");
        }
        return new InstanceReadyReply { DungeonId = party.DungeonId, LeaseExpiresUnixMilliseconds = Unix(until), Members = profiles };
    }, ct);
    public Task<InstanceLeaseReply> HeartbeatAsync(string instance, string secret, uint epoch, CancellationToken ct) => transactions.MutateAsync(async (db, tx) =>
    {
        var row = await Credential(db, tx, instance, secret, ct); await RequireCurrent(db, tx, row, epoch, true, ct); DateTime until = Now.AddSeconds(Config.LeaseSeconds);
        await db.ExecuteAsync(new CommandDefinition("UPDATE ServerInstances SET LastHeartbeatUtc=@Now,LeaseExpiresUtc=@Until WHERE InstanceId=@Id", new { Id = instance, Now, Until = until }, tx, cancellationToken: ct));
        return new InstanceLeaseReply { LeaseExpiresUnixMilliseconds = Unix(until) };
    }, ct);
    public Task<JoinTicketReply> MintTicketAsync(string player, string room, CancellationToken ct) => transactions.MutateAsync(async (db, tx) =>
    {
        await RoomTransactions.RequireMember(db, tx, room, player, ct); var party = await RoomTransactions.Party(db, tx, room, ct);
        var row = await db.QuerySingleOrDefaultAsync<InstanceRow>(new CommandDefinition("SELECT * FROM ServerInstances WHERE RunId=@Run", new { Run = party.ActiveRunId }, tx, cancellationToken: ct));
        if (row == null || row.State != "Ready") throw new OnlineFailure(409, "InstanceNotReady");
        await RequireCurrent(db, tx, row, party.RoomEpoch, true, ct);
        DateTime? last = await db.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition("SELECT LastTicketUtc FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player", new { Run = row.RunId, Player = player }, tx, cancellationToken: ct));
        if (last.HasValue && Now < last.Value.AddSeconds(5)) throw new OnlineFailure(429, "TicketRateLimited");
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); DateTime expires = Now.AddSeconds(60);
        // 每成员至多一票（含消费票），替换后旧摘要查不到仍拒绝；限频持久化，不随Host重启归零。
        await db.ExecuteAsync(new CommandDefinition("""
            DELETE FROM JoinTickets WHERE InstanceId=@Id AND CharacterId=@Player;
            INSERT INTO JoinTickets VALUES(@Hash,@Id,@Player,@Epoch,@Protocol,@Build,@Manifest,@Expires,NULL);
            UPDATE RunMembers SET LastTicketUtc=@Now WHERE RunId=@Run AND CharacterId=@Player
            """, new { Id = row.InstanceId, Player = player, Hash = AccountService.HashToken(token), Epoch = row.RoomEpoch, Protocol = content.Manifest.ProtocolVersion, Build = content.Manifest.BuildVersion, Manifest = content.Manifest.Hash, Expires = expires, Now, Run = row.RunId }, tx, cancellationToken: ct));
        return new JoinTicketReply { Ticket = token, PlayerId = player, InstanceId = row.InstanceId, RoomId = room, RunId = row.RunId, DungeonId = party.DungeonId, RoomEpoch = row.RoomEpoch, Port = row.UdpPort, ExpiresUnixMilliseconds = Unix(expires) };
    }, ct);
    /// <summary>
    /// DS 用独立实例凭据兑换 Client 的一次性 Ticket。Client 仅提供票与期望角色，不提供地图选择；
    /// 成功事务同时消费票并增加 ConnectionEpoch，回放失败，重连保留同一 Actor 与 Party 地图身份。
    /// </summary>
    public Task<ConsumeJoinTicketReply> ConsumeAsync(string instance, string secret, ConsumeJoinTicketRequest request, CancellationToken ct) => transactions.MutateAsync(async (db, tx) =>
    {
        var row = await Credential(db, tx, instance, secret, ct); var party = await RequireCurrent(db, tx, row, request.RoomEpoch, true, ct);
        if (!IsToken(request.Ticket)) throw new OnlineFailure(401, "InvalidJoinTicket");
        var ticket = await db.QuerySingleOrDefaultAsync<TicketRow>(new CommandDefinition("SELECT * FROM JoinTickets WHERE TicketHash=@Hash", new { Hash = AccountService.HashToken(request.Ticket) }, tx, cancellationToken: ct));
        if (ticket == null || ticket.InstanceId != instance || ticket.CharacterId != request.PlayerId || ticket.RoomEpoch != row.RoomEpoch || ticket.ConsumedUtc != null || Now >= ticket.ExpiresUtc || ticket.ProtocolVersion != content.Manifest.ProtocolVersion || ticket.BuildVersion != content.Manifest.BuildVersion || ticket.ManifestHash != content.Manifest.Hash) throw new OnlineFailure(401, "InvalidJoinTicket");
        var member = await db.QuerySingleOrDefaultAsync<AdmissionRow>(new CommandDefinition("""
            SELECT r.ConnectionEpoch,m.Seat FROM RunMembers r JOIN PartyMembers m ON m.CharacterId=r.CharacterId AND m.RoomId=@Room
            JOIN AssetLeases l ON l.CharacterId=r.CharacterId AND l.RunId=r.RunId AND l.RoomEpoch=@Epoch
            WHERE r.RunId=@Run AND r.CharacterId=@Player AND r.State IN ('Reserved','Admitted')
            """, new { Room = row.RoomId, Run = row.RunId, Player = request.PlayerId, Epoch = row.RoomEpoch }, tx, cancellationToken: ct));
        if (member == null || member.ConnectionEpoch == uint.MaxValue) throw new OnlineFailure(401, "InvalidJoinTicket");
        uint connectionEpoch = member.ConnectionEpoch + 1;
        await db.ExecuteAsync(new CommandDefinition("UPDATE JoinTickets SET ConsumedUtc=@Now WHERE TicketHash=@Hash; UPDATE RunMembers SET State='Admitted',ConnectionEpoch=@ConnectionEpoch WHERE RunId=@Run AND CharacterId=@Player", new { Now, Hash = AccountService.HashToken(request.Ticket), ConnectionEpoch = connectionEpoch, Run = row.RunId, Player = request.PlayerId }, tx, cancellationToken: ct));
        // Client 没有提交 Dungeon 选择字段；兑换结果只从受保护的 Party 取得，DS 还须与自己的启动身份再比对。
        return new ConsumeJoinTicketReply { PlayerId = request.PlayerId, RoomId = row.RoomId, RunId = row.RunId, DungeonId = party.DungeonId, RoomEpoch = row.RoomEpoch, ConnectionEpoch = connectionEpoch, ActorId = (ulong)member.Seat + 1 };
    }, ct);
    public Task<bool> FailAsync(string instance, string code, CancellationToken ct)
    {
        if (code is not ("ReadyTimeout" or "LaunchFailed" or "Cancelled" or "ProcessExited" or "LeaseExpired")) throw new ArgumentException("只允许固定Code，不记录含凭据的异常文本。", nameof(code));
        return transactions.MutateAsync(async (db, tx) =>
        {
            var row = await Get(db, tx, instance, ct);
            if (row.State == "Failed" || code == "ReadyTimeout" && (row.State != "Launching" || Now < row.ReadyDeadlineUtc) || code == "LeaseExpired" && (row.State != "Ready" || Now < row.LeaseExpiresUtc)) return false;
            var party = await RoomTransactions.Party(db, tx, row.RoomId, ct);
            if (party.ActiveRunId == row.RunId && party.RoomEpoch == row.RoomEpoch)
            {
                var players = await db.QueryAsync<string>(new CommandDefinition("SELECT CharacterId FROM RunMembers WHERE RunId=@Run ORDER BY CharacterId", new { Run = row.RunId }, tx, cancellationToken: ct));
                foreach (string player in players) _ = await db.ExecuteScalarAsync<uint>(new CommandDefinition("SELECT Version FROM Characters WHERE CharacterId=@Player FOR UPDATE", new { Player = player }, tx, cancellationToken: ct));
                // N2没有局中资产写入，只解除原位资产托管。N6必须按成员结算状态恢复，不能照搬此启动/只读阶段逻辑。
                await db.ExecuteAsync(new CommandDefinition("""
                    UPDATE Parties SET State='Open',RoomEpoch=@NextEpoch,ActiveRunId=NULL,ReadyRevision=@Revision WHERE RoomId=@Room;
                    UPDATE PartyMembers SET IsReady=FALSE,ReadyInventoryVersion=0 WHERE RoomId=@Room;
                    UPDATE Runs SET State='Failed',FailureCode=@Code WHERE RunId=@Run;
                    UPDATE RunMembers SET State='RolledBack' WHERE RunId=@Run;
                    DELETE FROM AssetLeases WHERE RunId=@Run AND RoomEpoch=@Epoch;
                    INSERT INTO RunLifecycleEvents(RunId,RoomEpoch,EventCode,RecordedAtUtc) VALUES(@Run,@Epoch,@Code,@Now)
                    """, new { Room = row.RoomId, Run = row.RunId, Epoch = row.RoomEpoch, NextEpoch = RoomTransactions.Next(row.RoomEpoch), Revision = RoomTransactions.Next(party.ReadyRevision), Code = code, Now }, tx, cancellationToken: ct));
            }
            await db.ExecuteAsync(new CommandDefinition("UPDATE ServerInstances SET State='Failed',ActivePort=NULL,FailureCode=@Code WHERE InstanceId=@Id; DELETE FROM JoinTickets WHERE InstanceId=@Id", new { Id = instance, Code = code }, tx, cancellationToken: ct)); return true;
        }, ct);
    }
    public async Task<int> ExpireDueAsync(CancellationToken ct)
    {
        var rows = await ActiveAsync(ct); int count = 0;
        foreach (var row in rows)
        {
            string? code = row.State == "Launching" && Now >= row.ReadyDeadlineUtc ? "ReadyTimeout" : row.State == "Ready" && Now >= row.LeaseExpiresUtc ? "LeaseExpired" : null;
            if (code != null && await FailAsync(row.InstanceId, code, ct)) count++;
        }
        return count;
    }
    public async Task<InstanceRow[]> ActiveAsync(CancellationToken ct)
    { await using var db = await database.OpenAsync(ct); return (await db.QueryAsync<InstanceRow>(new CommandDefinition("SELECT * FROM ServerInstances WHERE ActivePort IS NOT NULL", cancellationToken: ct))).ToArray(); }
    public Task<RoomInstanceReply> ViewAsync(string player, string room, CancellationToken ct) => transactions.MutateAsync(async (db, tx) =>
    {
        await RoomTransactions.RequireMember(db, tx, room, player, ct); var party = await RoomTransactions.Party(db, tx, room, ct);
        var row = await db.QuerySingleOrDefaultAsync<InstanceRow>(new CommandDefinition("SELECT * FROM ServerInstances WHERE RunId=@Run", new { Run = party.ActiveRunId }, tx, cancellationToken: ct)) ?? throw new OnlineFailure(404, "InstanceNotFound");
        return new RoomInstanceReply { InstanceId = row.InstanceId, RunId = row.RunId, DungeonId = RequireKnownDungeon(party.DungeonId), RoomEpoch = row.RoomEpoch, Port = row.UdpPort, State = row.State };
    }, ct);
    /// <summary>
    /// 只组合已提交的实例身份，不创建进程或读取 Client 选择。重复 Allocate 与新 Allocate 共用此入口，
    /// 避免业务重试丢掉地图字段；存活 DS 的 re-adopt 只监督既有 PID，不重写其描述文件。
    /// </summary>
    private InstanceLaunchDescriptor Descriptor(InstanceRow row, string dungeon) => new()
    {
        InstanceId = row.InstanceId, RoomId = row.RoomId, RunId = row.RunId, DungeonId = dungeon,
        RoomEpoch = row.RoomEpoch, Port = row.UdpPort, BusinessBaseUrl = Config.BusinessBaseUrl,
        ProtocolVersion = content.Manifest.ProtocolVersion, BuildVersion = content.Manifest.BuildVersion,
        ManifestHash = content.Manifest.Hash, GameplayConfigHash = content.GameplayConfigHash,
        HeartbeatSeconds = Config.HeartbeatSeconds, FreezeAfterSeconds = Config.FreezeAfterSeconds,
        IdleShutdownSeconds = Config.IdleShutdownSeconds
    };
    private static async Task<InstanceRow> Get(MySqlConnection db, MySqlTransaction tx, string id, CancellationToken ct) => await db.QuerySingleOrDefaultAsync<InstanceRow>(new CommandDefinition("SELECT * FROM ServerInstances WHERE InstanceId=@Id", new { Id = id }, tx, cancellationToken: ct)) ?? throw new OnlineFailure(404, "InstanceNotFound");
    private static async Task<InstanceRow> Credential(MySqlConnection db, MySqlTransaction tx, string id, string secret, CancellationToken ct)
    {
        if (!IsToken(secret)) throw new OnlineFailure(401, "InvalidInstanceCredential");
        return await db.QuerySingleOrDefaultAsync<InstanceRow>(new CommandDefinition("SELECT * FROM ServerInstances WHERE InstanceId=@Id AND SecretHash=@Hash", new { Id = id, Hash = AccountService.HashToken(secret) }, tx, cancellationToken: ct)) ?? throw new OnlineFailure(401, "InvalidInstanceCredential");
    }
    /// <summary>
    /// 验证数据库中当前授权并返回同一事务快照中的 Party，调用者不要再从 Client/文件重建地图身份。
    /// 所有调用方已取得 RoomMutationGate，期间 ActiveRun/Epoch/Dungeon 不会被另一业务写请求拆开修改。
    /// </summary>
    private async Task<RoomTransactions.PartyRow> RequireCurrent(MySqlConnection db, MySqlTransaction tx, InstanceRow row, uint epoch, bool ready, CancellationToken ct)
    {
        if (row.RoomEpoch != epoch || row.State == "Failed" || ready && row.State != "Ready" || row.State == "Ready" && (row.LeaseExpiresUtc == null || Now >= row.LeaseExpiresUtc)) throw new OnlineFailure(409, "InstanceEpochExpired");
        var party = await RoomTransactions.Party(db, tx, row.RoomId, ct);
        if (party.ActiveRunId != row.RunId || party.RoomEpoch != row.RoomEpoch || party.State is not ("Starting" or "Running")) throw new OnlineFailure(409, "InstanceEpochExpired");
        RequireKnownDungeon(party.DungeonId);
        // 规则升级后，旧 DS 不能只靠旧密钥续租。复用唯一冻结 JSON，不在重启时把旧规则重新标成新 Hash。
        int mismatched = await db.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM RunMembers WHERE RunId=@Run
            AND COALESCE(JSON_UNQUOTE(JSON_EXTRACT(EntrySnapshot,'$.CombatAssets.GameplayConfigHash')),'')<>@Hash
            """, new { Run = row.RunId, Hash = content.GameplayConfigHash }, tx, cancellationToken: ct));
        if (!IsToken(content.GameplayConfigHash) || mismatched != 0) throw new OnlineFailure(409, "ContentVersionMismatch");
        return party;
    }
    private string RequireKnownDungeon(string dungeon)
    {
        // 格式检查不能取代业务白名单：一个看起来合法的新字符串也不能创建尚未部署的地图。
        if (!DungeonIdentityRules.IsValidId(dungeon) || !options.DungeonIds.Contains(dungeon, StringComparer.Ordinal))
            throw new OnlineFailure(409, "UnknownDungeon");
        return dungeon;
    }
    private static bool IsToken(string? s) => s != null && s.Length == 64 && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static long Unix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    public sealed class InstanceRow
    {
        public string InstanceId { get; set; } = ""; public string RunId { get; set; } = ""; public string RoomId { get; set; } = ""; public uint RoomEpoch { get; set; }
        public string State { get; set; } = ""; public int UdpPort { get; set; } public int? ProcessId { get; set; } public long? ProcessStartTicks { get; set; } public string Executable { get; set; } = "";
        public DateTime ReadyDeadlineUtc { get; set; } public DateTime? LeaseExpiresUtc { get; set; }
        public InstanceProcessIdentity? Identity => ProcessId.HasValue && ProcessStartTicks.HasValue ? new(ProcessId.Value, ProcessStartTicks.Value, Executable) : null;
    }
    private sealed class EntryRow { public string EntrySnapshot { get; set; } = ""; public int Seat { get; set; } }
    private sealed class AdmissionRow { public uint ConnectionEpoch { get; set; } public int Seat { get; set; } }
    private sealed class TicketRow
    {
        public string InstanceId { get; set; } = ""; public string CharacterId { get; set; } = ""; public uint RoomEpoch { get; set; } public uint ProtocolVersion { get; set; }
        public string BuildVersion { get; set; } = ""; public string ManifestHash { get; set; } = ""; public DateTime ExpiresUtc { get; set; } public DateTime? ConsumedUtc { get; set; }
    }
}
