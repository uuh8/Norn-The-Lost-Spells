using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Norn.Business.Features.Rooms;

namespace Norn.NativeInventoryRegression;

/// <summary>
/// 本轮Native独占实例的到期审计，不是通用数据库清理器。只接受私有请求中的精确CaseId，
/// 再用公开成功证据、私有launch身份和数据库三方交叉核验；不会枚举/处理全库实例。
/// expire仅走原FailAsync到期事务。所有成员必须已可靠Exited且没有AssetLease，避免把
/// 尚未结算的失败用例误当垃圾回滚。这里没有SQL UPDATE/DELETE、进程控制或容量提档。
/// </summary>
internal static class ReservationLeaseAudit
{
    private const int MaximumRequestBytes=65536,MaximumCases=32;
    private static readonly Regex CasePattern=new(@"^20261117-\d{6}-[0-9a-f]{8}$",RegexOptions.CultureInvariant);
    private static readonly Regex IdPattern=new(@"^[0-9a-f]{32}$",RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions StrictJson=new(Program.Json){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};

    private sealed class Request
    {public string Action{get;set;}="";public string[] CaseIds{get;set;}=[];}
    private sealed record BoundCase(string CaseId,string EvidenceDirectory,string InstanceId,string RunId,string RoomId,
        uint RoomEpoch,bool Passed,bool Cooperative,bool PeerExitVerified,string[] Players);
    private sealed class InstanceAudit
    {
        public string InstanceId{get;set;}="";
        public string RunId{get;set;}="";
        public string RoomId{get;set;}="";
        public uint RoomEpoch{get;set;}
        public string State{get;set;}="";
        public string RunState{get;set;}="";
        public int? ActivePort{get;set;}
        public DateTime ReadyDeadlineUtc{get;set;}
        public DateTime? LeaseExpiresUtc{get;set;}
        public DateTime? LastHeartbeatUtc{get;set;}
        public DateTime DatabaseUtc{get;set;}
        public int MemberCount{get;set;}
        public int NonExitedMembers{get;set;}
        public int UnexpectedMembers{get;set;}
        public int AssetLeaseCount{get;set;}
    }
    private sealed record Observation(string CaseId,string EvidenceDirectory,bool EvidencePassed,bool Cooperative,
        bool PeerExitVerified,InstanceAudit Database,bool CanExpire,string? DueCode,string Eligibility);
    private sealed record Outcome(string CaseId,string InstanceId,string OutcomeCode,bool Applied,string? FailureType=null);

    /// <summary>
    /// Root只需接入一个独立Main分支；此入口不构造NativeCase、不找UDP空闲端口，也不启动DS/Client。
    /// audit只使用SELECT。expire先把完整只读preflight以CreateNew落盘，再调用原服务并另存结果；
    /// 任一预检不满足便保留审计并返回失败，不通过扩大名单、改期限或SQL解锁继续尝试。
    /// </summary>
    internal static async Task<int> RunAsync(string requestPath,CancellationToken ct=default)
    {
        Require(Path.IsPathFullyQualified(requestPath),"ReservationAuditRequiresAbsoluteRequest");
        string repository=FindRepository(requestPath),unity=Path.Combine(repository,"RPG"),privateRoot=Path.Combine(unity,".tools");
        requestPath=Confined(privateRoot,requestPath);
        byte[] requestBytes=await ReadBytes(requestPath,MaximumRequestBytes,ct);
        var request=JsonSerializer.Deserialize<Request>(requestBytes,StrictJson)??throw new CaseFailure("ReservationAuditRequestMissing");
        Require(request.Action is "audit" or "expire","ReservationAuditActionRejected");
        Require(request.CaseIds is {Length:>0 and <=MaximumCases} && request.CaseIds.All(id=>id!=null && CasePattern.IsMatch(id))
            && request.CaseIds.Distinct(StringComparer.Ordinal).Count()==request.CaseIds.Length,"ReservationAuditCaseIdsRejected");
        string evidenceRoot=Path.Combine(unity,"docs","superpowers","evidence","natural-fluid-20261117");
        Require(Directory.Exists(evidenceRoot),"ReservationAuditEvidenceRootMissing");
        var cases=new List<BoundCase>(request.CaseIds.Length);
        foreach(string id in request.CaseIds)cases.Add(await BindCase(unity,evidenceRoot,id,ct));
        Require(cases.Select(item=>item.InstanceId).Distinct(StringComparer.Ordinal).Count()==cases.Count,"ReservationAuditDuplicateInstance");

        string config=Environment.GetEnvironmentVariable("NORN_N1_CONFIG")??throw new CaseFailure("PrivateN1ConfigMissing");
        config=Confined(privateRoot,config);
        using var settings=JsonDocument.Parse(await ReadBytes(config,MaximumRequestBytes,ct));
        string connection=settings.RootElement.GetProperty("Test").GetString()??"";
        NativeFactory.RequireTestConnection(connection); // 只允许127.0.0.1:13384/norn_online_test，绝不输出原串。
        var before=new List<Observation>(cases.Count);
        await using(var db=new MySqlConnection(connection))
        {
            await db.OpenAsync(ct);
            foreach(var item in cases)before.Add(await Observe(db,item,ct));
        }
        string requestHash=Convert.ToHexStringLower(SHA256.HashData(requestBytes));
        await WriteSummary(evidenceRoot,request.Action,"Preflight",requestHash,before,[],ct);
        if(request.Action=="audit")return 0;
        // 已释放的精确实例是幂等no-op；其余实例必须全部通过才进入服务层，不能先清一部分
        // 再发现另一case其实未Exited。读后续租的竞态还会在下面重新SELECT和原FailAsync内核验。
        if(before.Any(item=>item.Database.ActivePort.HasValue && !item.CanExpire))return 1;

        var outcomes=new List<Outcome>(cases.Count);
        string manifest=Path.Combine(unity,"Assets","_Project","Resources","Online","ContentManifest.json");
        using var gameplay=JsonDocument.Parse(await ReadBytes(Path.Combine(unity,"Assets","_Project","Resources","Online","GameplayManifest.json"),MaximumRequestBytes,ct));
        string gameplayHash=gameplay.RootElement.GetProperty("GameplayConfigHash").GetString()??"";
        // 使用已有DI与真实时钟，但关闭房间/实例分配和迁移。默认TestServer不监听网络端口；
        // NativeFactory已移除Supervisor，故不会扫描其它旧Run、续启动进程或执行ExpireDueAsync。
        await using var factory=new NativeFactory(new Dictionary<string,string?>
        {
            ["Online:Enabled"]="true",["Online:AutoMigrate"]="false",["Online:RoomsEnabled"]="false",
            ["Online:Instances:Enabled"]="false",["ConnectionStrings:Online"]=connection,
            ["Online:ManifestPath"]=manifest,["Online:GameplayConfigHash"]=gameplayHash
        },Path.Combine(repository,"Server","Norn.Business"));
        using var scope=factory.Services.CreateScope();
        var instances=scope.ServiceProvider.GetRequiredService<RoomInstanceService>();
        await using(var db=new MySqlConnection(connection))
        {
            await db.OpenAsync(ct);
            foreach(var item in cases)
            {
                var fresh=await Observe(db,item,ct);
                if(!fresh.Database.ActivePort.HasValue)
                {outcomes.Add(new Outcome(item.CaseId,item.InstanceId,"AlreadyReleased",false));continue;}
                if(!fresh.CanExpire)
                {outcomes.Add(new Outcome(item.CaseId,item.InstanceId,"FreshPreconditionRejected",false));break;}
                try
                {
                    // code仅来自这条已核验行的真实到期状态；FailAsync在原Room Gate事务内重新
                    // 读取期限。若恰好续租，只返回false，不伪造过去时间或改资产绕过租约。
                    bool applied=await instances.FailAsync(item.InstanceId,fresh.DueCode!,ct);
                    outcomes.Add(new Outcome(item.CaseId,item.InstanceId,applied?"ExpiredByOriginalService":"ServiceDeclined",applied));
                }
                catch(Exception error) when(error is not OperationCanceledException)
                {outcomes.Add(new Outcome(item.CaseId,item.InstanceId,"ServiceFailure",false,error.GetType().Name));break;}
            }
            var after=new List<Observation>(cases.Count);
            foreach(var item in cases)after.Add(await Observe(db,item,ct));
            await WriteSummary(evidenceRoot,request.Action,"Result",requestHash,after,outcomes,ct);
            return after.All(item=>!item.Database.ActivePort.HasValue) && outcomes.Count==cases.Count?0:1;
        }
    }

    private static async Task<BoundCase> BindCase(string unity,string evidenceRoot,string caseId,CancellationToken ct)
    {
        BoundCase? found=null;
        foreach(string directory in Directory.EnumerateDirectories(evidenceRoot, "native-*",SearchOption.TopDirectoryOnly))
        {
            string name=Path.GetFileName(directory);
            if(!name.StartsWith("native-motion-",StringComparison.Ordinal) && !name.StartsWith("native-keyboard-",StringComparison.Ordinal))continue;
            string path=Confined(evidenceRoot,Path.Combine(directory,"evidence.json"));
            if(!File.Exists(path))continue;
            using var evidence=JsonDocument.Parse(await ReadBytes(path,4*1024*1024,ct));
            var value=evidence.RootElement;
            if(Text(value,"CaseId")!=caseId)continue;
            Require(found==null,"ReservationAuditAmbiguousEvidence");
            string instance=Text(value,"InstanceId"),run=Text(value,"RunId"),player=Text(value,"PlayerId"),peer=Text(value,"PeerPlayerId");
            Require(IdPattern.IsMatch(instance) && IdPattern.IsMatch(run) && IdPattern.IsMatch(player)
                && (peer.Length==0 || IdPattern.IsMatch(peer)),"ReservationAuditEvidenceIdentityRejected");
            string casesRoot=Path.Combine(unity,".tools","native-inventory-regression");
            string caseDirectory=Confined(casesRoot,Path.Combine(casesRoot,caseId));
            Require(Directory.Exists(caseDirectory),"ReservationAuditPrivateCaseMissing");
            string launch=Confined(caseDirectory,Path.Combine(caseDirectory,"instances",instance,"launch.json"));
            using var descriptor=JsonDocument.Parse(await ReadBytes(launch,MaximumRequestBytes,ct));
            var identity=descriptor.RootElement;
            string room=Text(identity,"RoomId");
            Require(Text(identity,"InstanceId")==instance && Text(identity,"RunId")==run && IdPattern.IsMatch(room),
                "ReservationAuditPrivateLaunchMismatch");
            bool hasEpoch=identity.TryGetProperty("RoomEpoch",out var epochValue);
            Require(hasEpoch && epochValue.ValueKind==JsonValueKind.Number
                && epochValue.TryGetUInt32(out uint epoch) && epoch!=0,"ReservationAuditPrivateLaunchMismatch");
            found=new BoundCase(caseId,Path.GetRelativePath(unity,directory),instance,run,room,epochValue.GetUInt32(),
                Flag(value,"Passed"),peer.Length!=0,Flag(value,"PeerExitVerified"),peer.Length==0?[player]:[player,peer]);
        }
        return found??throw new CaseFailure("ReservationAuditAllowedEvidenceMissing");
    }

    private static async Task<Observation> Observe(MySqlConnection db,BoundCase item,CancellationToken ct)
    {
        // 显式列白名单；不SELECT *，避免SecretHash/EntrySnapshot/票据进入内存报告。
        var row=await db.QuerySingleOrDefaultAsync<InstanceAudit>(new CommandDefinition("""
            SELECT s.InstanceId,s.RunId,s.RoomId,s.RoomEpoch,s.State,r.State RunState,s.ActivePort,
              s.ReadyDeadlineUtc,s.LeaseExpiresUtc,s.LastHeartbeatUtc,UTC_TIMESTAMP(6) DatabaseUtc,
              (SELECT COUNT(*) FROM RunMembers m WHERE m.RunId=s.RunId) MemberCount,
              (SELECT COUNT(*) FROM RunMembers m WHERE m.RunId=s.RunId AND m.State<>'Exited') NonExitedMembers,
              (SELECT COUNT(*) FROM RunMembers m WHERE m.RunId=s.RunId AND m.CharacterId NOT IN @Players) UnexpectedMembers,
              (SELECT COUNT(*) FROM AssetLeases l WHERE l.RunId=s.RunId) AssetLeaseCount
            FROM ServerInstances s JOIN Runs r ON r.RunId=s.RunId AND r.RoomId=s.RoomId AND r.RoomEpoch=s.RoomEpoch
            WHERE s.InstanceId=@Instance
            """,new{Instance=item.InstanceId,item.Players},cancellationToken:ct));
        Require(row!=null && row.InstanceId==item.InstanceId && row.RunId==item.RunId && row.RoomId==item.RoomId
            && row.RoomEpoch==item.RoomEpoch,"ReservationAuditDatabaseIdentityMismatch");
        // DATETIME(6)存的是原服务UTC；公开JSON明确Kind，不能让观察者按本机+08误读租约期限。
        row!.ReadyDeadlineUtc=DateTime.SpecifyKind(row.ReadyDeadlineUtc,DateTimeKind.Utc);
        row.DatabaseUtc=DateTime.SpecifyKind(row.DatabaseUtc,DateTimeKind.Utc);
        if(row.LeaseExpiresUtc.HasValue)row.LeaseExpiresUtc=DateTime.SpecifyKind(row.LeaseExpiresUtc.Value,DateTimeKind.Utc);
        if(row.LastHeartbeatUtc.HasValue)row.LastHeartbeatUtc=DateTime.SpecifyKind(row.LastHeartbeatUtc.Value,DateTimeKind.Utc);
        string? code=row.State=="Ready" && row.LeaseExpiresUtc.HasValue && row.DatabaseUtc>=row.LeaseExpiresUtc.Value?"LeaseExpired":
            row.State=="Launching" && row.DatabaseUtc>=row.ReadyDeadlineUtc?"ReadyTimeout":null;
        string eligibility=!item.Passed?"EvidenceNotPassed":item.Cooperative && !item.PeerExitVerified?"PeerExitNotVerified":
            row.MemberCount!=item.Players.Length || row.NonExitedMembers!=0 || row.UnexpectedMembers!=0?"MembersNotAllOwnedAndExited":
            row.AssetLeaseCount!=0?"AssetLeasesRemain":!row.ActivePort.HasValue?"AlreadyReleased":code==null?"NotDueOrUnsupportedState":"Eligible";
        return new Observation(item.CaseId,item.EvidenceDirectory,item.Passed,item.Cooperative,item.PeerExitVerified,row,eligibility=="Eligible",code,eligibility);
    }

    private static async Task WriteSummary(string root,string action,string phase,string requestHash,List<Observation> observations,List<Outcome> outcomes,CancellationToken ct)
    {
        string path=Path.Combine(root,"reservation-lease-"+action+"-"+phase.ToLowerInvariant()+"-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ")+"-"+Guid.NewGuid().ToString("N")+".json");
        await using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read,4096,FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(output,new{Action=action,Phase=phase,RecordedUtc=DateTime.UtcNow,RequestSha256=requestHash,
            Scope="Exact evidence-owned test instances only; no process control, SQL asset edits, global expiry scan or capacity change",Observations=observations,Outcomes=outcomes},Program.Json,ct);
        Console.WriteLine("ReservationLeaseAuditSummary: "+path);
    }
    private static async Task<byte[]> ReadBytes(string path,int maximum,CancellationToken ct)
    {
        await using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,4096,FileOptions.Asynchronous);
        Require(file.Length>0 && file.Length<=maximum,"ReservationAuditJsonSizeRejected");
        byte[] data=new byte[checked((int)file.Length)];await file.ReadExactlyAsync(data,ct);return data;
    }
    private static string FindRepository(string requestPath)
    {
        for(var directory=new FileInfo(Path.GetFullPath(requestPath)).Directory;directory!=null;directory=directory.Parent)
            if(File.Exists(Path.Combine(directory.FullName,"Server","Norn.Business","Norn.Business.csproj"))
                && Directory.Exists(Path.Combine(directory.FullName,"RPG","Tools","Networking","NativeInventoryRegression")))return directory.FullName;
        throw new CaseFailure("MonorepoRootNotFound");
    }
    private static string Confined(string root,string candidate)
    {
        root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));string target=Path.GetFullPath(candidate);
        Require(target.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"ReservationAuditPathEscapeRejected");
        // 不跟随本地junction/symlink读取另一工作区的旧case；检查文件与每层目录，不只比较路径字符串。
        for(string? path=target;path!=null && path.Length>=root.Length;path=Path.GetDirectoryName(path))
            if((File.Exists(path)||Directory.Exists(path)) && (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
                throw new CaseFailure("ReservationAuditReparsePointRejected");
        return target;
    }
    private static string Text(JsonElement value,string name)=>value.TryGetProperty(name,out var field) && field.ValueKind==JsonValueKind.String?field.GetString()??"":"";
    private static bool Flag(JsonElement value,string name)=>value.TryGetProperty(name,out var field) && field.ValueKind==JsonValueKind.True;
    private static void Require(bool value,string code){if(!value)throw new CaseFailure(code);}
}
