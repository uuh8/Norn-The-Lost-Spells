using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using MySqlConnector;
using Norn.Business.Content;
using Norn.Business.Features.Inventory;
using Norn.Business.Features.Rooms;
using Norn.Business.Features.Trading;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Runs;

/// <summary>
/// 已认证DS的个人ESC最终提交。Room Gate串行化资格/托管变更，角色按ID排序锁定；
/// 原ID、侧表、Wallet绝对值、收据、本人终态/租约同事务。绝不调用全队Fail或推断死亡结算。
/// </summary>
public sealed class RunMemberExitService(RoomTransactions transactions,RoomInstanceService instances,ContentManifestValidator content, AccountShopService shop, InventoryService inventory)
{
    public Task AuthorizeAsync(string instance,string secret,CancellationToken ct)
        =>transactions.MutateAsync(async(db,tx)=>{_=await instances.RequireExitCredentialAsync(db,tx,instance,secret,ct);return true;},ct);

    public Task<OnlineRunMemberExitReceipt> CommitAsync(string instance,string secret,OnlineRunMemberExitRequest request,CancellationToken ct)
        =>transactions.MutateAsync(async(db,tx)=>
        {
            // 先认证，再读已提交收据；本人lease已经解除也必须能解析未知HTTP结果。
            var authority=await instances.RequireExitCredentialAsync(db,tx,instance,secret,ct);
            var value=RunMemberExitRules.Freeze(request,out string json,out string hash);
            if(value.InstanceId!=instance||value.RunId!=authority.RunId||value.RoomEpoch!=authority.RoomEpoch)throw Failure("RunMemberExitStateConflict");
            var previous=await db.QuerySingleOrDefaultAsync<ReceiptRow>(new CommandDefinition(
                "SELECT * FROM RunMemberExitReceipts WHERE SettlementId=@Id",new{Id=value.SettlementId},tx,cancellationToken:ct));
            if(previous!=null)
            {
                if(previous.RunId!=value.RunId||previous.CharacterId!=value.PlayerId||previous.InstanceId!=instance||previous.RequestHash!=hash)
                    throw Failure("RunMemberExitIdempotencyConflict");
                return Decode(previous);
            }
            if(await db.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM RunMemberExitReceipts WHERE RunId=@Run AND CharacterId=@Player",
                new{Run=value.RunId,Player=value.PlayerId},tx,cancellationToken:ct))!=0)throw Failure("RunMemberExitAlreadyCommitted");

            _=await instances.RequireCheckpointAuthorityAsync(db,tx,instance,secret,value.RoomEpoch,true,ct);
            var party=await RoomTransactions.Party(db,tx,authority.RoomId,ct);
            var members=(await db.QueryAsync<MemberRow>(new CommandDefinition("""
                SELECT m.CharacterId,m.InventoryVersion,m.EntrySnapshot,m.State,m.ConnectionEpoch,p.Seat
                FROM RunMembers m LEFT JOIN PartyMembers p ON p.CharacterId=m.CharacterId AND p.RoomId=@Room
                WHERE m.RunId=@Run ORDER BY m.CharacterId
                """,new{Run=value.RunId,Room=authority.RoomId},tx,cancellationToken:ct))).ToArray();
            if(members.Length is <1 or >4)throw Failure("RunMemberExitStateConflict");
            bool home = party.DungeonId == "home";
            if (home)
            {
                // Home沿原和平规则不死亡清包、不产经济成功进度；即使DS误发，业务仍先保原资产。
                if (value.Reason == OnlineRunMemberExitReason.FinalDeath) throw Failure("HomeFinalDeathForbidden");
                if (value.SuccessfulExtraction || value.CollectedGmu != 0 || value.DefeatedEnemies != 0) throw Failure("HomeSuccessfulExtractionForbidden");
                if (members.Length != 1 || !await RoomTransactions.IsSinglePlayerRun(db, tx, value.RunId, value.RoomEpoch, ct))
                    throw Failure("HomeSinglePlayerOnly");
                if (value.SchemaVersion != OnlineRunMemberExitRequest.CurrentSchemaVersion || !AccountPracticeContinuityStore.IsScope(value.PracticeSessionScope))
                    throw Failure("PracticeSessionScopeConflict");
            }
            else if (value.PracticeSessionScope != "" || value.PracticeContinuityJson != "" || value.PracticeContinuityHash != ""
                || value.PracticeGroundItems is { Length: > 0 }) throw Failure("PracticeSceneMismatch");
            var target=members.SingleOrDefault(m=>m.CharacterId==value.PlayerId);
            if(target==null||target.State!="Admitted"||target.Seat==null||(ulong)target.Seat.Value+1!=value.ActorId)
                throw Failure("RunMemberExitStateConflict");
            var lease=await db.QuerySingleOrDefaultAsync<LeaseRow>(new CommandDefinition(
                "SELECT RunId,RoomEpoch FROM AssetLeases WHERE CharacterId=@Player",new{Player=value.PlayerId},tx,cancellationToken:ct));
            if(lease==null||lease.RunId!=value.RunId||lease.RoomEpoch!=value.RoomEpoch)throw Failure("RunMemberExitStateConflict");
            // Consume与本提交共用Room Gate，连接代次只递增且耗尽时拒绝，不回绕。
            // 收据已优先查过；严格落后的旧候选今后也不可能提交，未来代次不提供该证明。
            if(target.ConnectionEpoch>value.ConnectionEpoch)throw Failure("RunMemberExitConnectionSuperseded");
            if(target.ConnectionEpoch!=value.ConnectionEpoch)throw Failure("RunMemberExitStateConflict");
            uint currentVersion=0;
            foreach(var member in members)
            {
                uint version=await db.ExecuteScalarAsync<uint>(new CommandDefinition(
                    "SELECT Version FROM Characters WHERE CharacterId=@Player FOR UPDATE",new{Player=member.CharacterId},tx,cancellationToken:ct));
                if(member.CharacterId==value.PlayerId)currentVersion=version;
            }
            if(currentVersion!=value.ExpectedPersistentVersion||target.InventoryVersion!=value.ExpectedPersistentVersion||currentVersion==uint.MaxValue)
                throw Failure("RunMemberExitVersionConflict");
            RunMemberExitRules.RequireOwned(value,content);

            // 所有immutable Entry共同界定原Run携带ID；仓库没有进入此集合，不会被全Owner替换误删。
            var originals=new Dictionary<string,Original>(StringComparer.Ordinal);
            OnlineOwnedAssetSnapshot? entryCurrency=null;
            foreach(var member in members)
            {
                var profile=JsonSerializer.Deserialize<OnlineProfileReply>(member.EntrySnapshot,ContentManifestValidator.Json);
                var assets=profile?.CombatAssets;
                if(profile?.PlayerId!=member.CharacterId||assets==null||assets.RunId!=value.RunId)throw Failure("RunMemberExitStateConflict");
                if (home && profile.PracticeSessionScope != value.PracticeSessionScope) throw Failure("PracticeSessionScopeConflict");
                OwnedCombatAssetRules.RequirePersisted(assets,member.CharacterId,content.GameplayConfigHash,content);
                if(member.CharacterId==value.PlayerId)entryCurrency=assets;
                foreach(var item in assets.Items)if(!originals.TryAdd(item.InstanceId,new(member.CharacterId,item)))throw Failure("RunMemberExitOwnershipConflict");
                if (home) foreach (var item in HomePracticeAssets.EntryGround(profile!, content))
                    if (!originals.TryAdd(item.InstanceId, new(member.CharacterId, item))) throw Failure("RunMemberExitOwnershipConflict");
            }
            RequireCurrency(value.Assets,entryCurrency??throw Failure("RunMemberExitStateConflict"));
            // Head是可选的已确认水位，不是退出提交的序号前置；单人稳定候选可以领先Journal。
            // 只比较显式Owned3事实，历史缺币字段不能按默认0当作余额。
            string? priorJson=await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                "SELECT PayloadJson FROM RunAssetCheckpointHeads WHERE RunId=@Run",new{Run=value.RunId},tx,cancellationToken:ct));
            if(priorJson!=null)
            {
                OnlineRunAssetCheckpoint? prior;
                try{prior=JsonSerializer.Deserialize<OnlineRunAssetCheckpoint>(priorJson,ContentManifestValidator.Json);}
                catch(JsonException){throw Failure("RunMemberExitStateConflict");}
                if(prior==null||prior.InstanceId!=instance||prior.RunId!=value.RunId||prior.RoomEpoch!=value.RoomEpoch)
                    throw Failure("RunMemberExitStateConflict");
                var recorded=prior.Members?.SingleOrDefault(m=>m.PlayerId==value.PlayerId)?.Assets;
                if(recorded!=null)RequireCurrency(value.Assets,recorded);
            }
            var custody=(await db.QueryAsync<CustodyRow>(new CommandDefinition(
                "SELECT * FROM RunItemCustody WHERE RunId=@Run",new{Run=value.RunId},tx,cancellationToken:ct)))
                .ToDictionary(c=>c.InstanceId,StringComparer.Ordinal);
            await SeedOriginals(db,tx,value.RunId,originals,custody,ct);
            var kept = home ? HomePracticeAssets.KeepCandidate(value, content) : value;
            await ValidateCandidate(db,tx,kept,originals,custody,ct);
            if (home) await ValidateHomeRetirements(db, tx, value, kept, originals, custody, ct);
            foreach(var original in originals.Values)
                if(original.Owner==value.PlayerId&&original.Item.EntryBoundBackpack
                    &&!value.Assets.Items.Any(i=>i.InstanceId==original.Item.InstanceId))throw Failure("RunMemberExitOwnershipConflict");

            ulong packet=await db.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT @@max_allowed_packet",transaction:tx,cancellationToken:ct));
            RunAssetCheckpointService.RequirePayloadBudget(System.Text.Encoding.UTF8.GetByteCount(json),packet);
            uint committed=currentVersion+1;
            var receipt=new OnlineRunMemberExitReceipt{SchemaVersion=value.SchemaVersion,SettlementId=value.SettlementId,InstanceId=instance,
                RunId=value.RunId,PlayerId=value.PlayerId,RoomEpoch=value.RoomEpoch,ConnectionEpoch=value.ConnectionEpoch,
                ActorId=value.ActorId,PayloadHash=hash,CommittedVersion=committed,Gold=value.Assets.Gold,
                CurrencyVersion=value.Assets.CurrencyVersion,Exited=true,Reason=value.Reason,
                FinalRetiredItems=value.FinalRetiredItems,FinalDeathLostMaterials=value.FinalDeathLostMaterials,
                SuccessfulExtraction=value.SuccessfulExtraction,CollectedGmu=value.CollectedGmu,DefeatedEnemies=value.DefeatedEnemies,
                PracticeSessionScope=value.PracticeSessionScope,PracticeContinuityHash=value.PracticeContinuityHash};
            // 收据先插入供custody外键引用，但仍在同一未提交事务；任一失败会把所有写入一起回滚。
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO RunMemberExitReceipts(SettlementId,RunId,CharacterId,InstanceId,RoomEpoch,RequestHash,RequestJson,ResultJson,CommittedAtUtc)
                VALUES(@Id,@Run,@Player,@Instance,@Epoch,@Hash,@Request,@Result,UTC_TIMESTAMP(6))
                """,new{Id=value.SettlementId,Run=value.RunId,Player=value.PlayerId,Instance=instance,Epoch=value.RoomEpoch,
                    Hash=hash,Request=json,Result=JsonSerializer.Serialize(receipt,ContentManifestValidator.Json)},tx,cancellationToken:ct));
            var persisted=kept;
            if(value.Reason==OnlineRunMemberExitReason.FinalDeath)
            {
                // Hash/收据始终对应清理前固定事实；另建持久候选保包外壳，不能改写原请求再重试。
                persisted=JsonSerializer.Deserialize<OnlineRunMemberExitRequest>(JsonSerializer.Serialize(value,ContentManifestValidator.Json),ContentManifestValidator.Json)!;
                persisted.Assets.Items=persisted.Assets.Items.Where(i=>i.LocationKind==4).ToArray();
                persisted.Assets.SelectedWandInstanceId="";
            }
            if (home) await HomePracticeAssets.PrepareWorldReplacementAsync(db, tx, value, hash, committed, ct);
            await PersistCarried(db,tx,persisted,originals,custody,ct);
            if (home)
            {
                await RetireHomeItems(db, tx, value, ct);
                await HomePracticeAssets.InstallWorldAsync(db, tx, value, ct);
                await AccountPracticeContinuityStore.WriteAsync(db, tx, value.PlayerId, value.RunId,
                    value.PracticeSessionScope, value.PracticeContinuityJson, value.PracticeContinuityHash, content.GameplayConfigHash, ct);
            }
            if(value.Reason==OnlineRunMemberExitReason.FinalDeath)await RetireFinalItems(db,tx,value,ct);
            await db.ExecuteAsync(new CommandDefinition("""
                UPDATE Wallets SET Balance=@Gold WHERE CharacterId=@Player;
                UPDATE Characters SET Version=@Version WHERE CharacterId=@Player;
                UPDATE RunMembers SET State='Exited' WHERE RunId=@Run AND CharacterId=@Player;
                DELETE FROM AssetLeases WHERE CharacterId=@Player AND RunId=@Run AND RoomEpoch=@Epoch;
                DELETE FROM JoinTickets WHERE InstanceId=@Instance AND CharacterId=@Player;
                DELETE FROM PartyMembers WHERE RoomId=@Room AND CharacterId=@Player;
                INSERT INTO RunLifecycleEvents(RunId,RoomEpoch,EventCode,RecordedAtUtc) VALUES(@Run,@Epoch,'MemberExited',UTC_TIMESTAMP(6))
                """,new{Gold=value.Assets.Gold,Version=committed,Player=value.PlayerId,Run=value.RunId,Epoch=value.RoomEpoch,
                    Instance=instance,Room=authority.RoomId},tx,cancellationToken:ct));
            if (home)
            {
                // keep只覆盖本次Home托管；仓库/Hub地面仍在库中。提交前重新读整个Owner，
                // 保留原4120与父链/roll上限，不能靠把资产拆到两个scene绕过完整Profile门。
                var profile = await inventory.ReadSnapshotAsync(db, tx, value.PlayerId, ct, value.PracticeSessionScope);
                OwnedCombatAssetRules.RequireAccount(AccountInventoryMoves.AccountSnapshot(profile, content), value.PlayerId, content.GameplayConfigHash, content);
            }
            if(party.LeaderCharacterId==value.PlayerId)
            {
                string? next=await db.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
                    "SELECT CharacterId FROM PartyMembers WHERE RoomId=@Room ORDER BY Seat LIMIT 1",new{Room=authority.RoomId},tx,cancellationToken:ct));
                if(next!=null)await db.ExecuteAsync(new CommandDefinition(
                    "UPDATE Parties SET LeaderCharacterId=@Leader WHERE RoomId=@Room",new{Leader=next,Room=authority.RoomId},tx,cancellationToken:ct));
            }
            // 只消费已认证DS明确成功事实；旧schema/普通ESC/死亡以及Home均不能借退出补货。
            if (value.SuccessfulExtraction) await shop.RecordTrustedSuccessProgressAsync(db, tx, value.PlayerId, value.RunId, ct);
            // 不修改RoomEpoch/Run状态，不调用Fail，不中止其他成员、世界推进或其租约。
            return receipt;
        },ct);

    public Task<OnlineRunMemberExitReceipt> FindAsync(string instance,string secret,uint epoch,string settlement,CancellationToken ct)
        =>transactions.MutateAsync(async(db,tx)=>
        {
            var authority=await instances.RequireExitCredentialAsync(db,tx,instance,secret,ct);
            if(authority.RoomEpoch!=epoch)throw Failure("RunMemberExitStateConflict");
            var row=await db.QuerySingleOrDefaultAsync<ReceiptRow>(new CommandDefinition(
                "SELECT * FROM RunMemberExitReceipts WHERE SettlementId=@Id AND InstanceId=@Instance AND RunId=@Run",
                new{Id=settlement,Instance=instance,Run=authority.RunId},tx,cancellationToken:ct));
            return row==null?throw new OnlineFailure(404,"RunMemberExitNotFound"):Decode(row);
        },ct);
    public Task<OnlineRunMemberExitReceipt> FindForPlayerAsync(string player,string run,CancellationToken ct)
        =>transactions.MutateAsync(async(db,tx)=>
        {
            RoomTransactions.RequireId(run);
            var row=await db.QuerySingleOrDefaultAsync<ReceiptRow>(new CommandDefinition(
                "SELECT * FROM RunMemberExitReceipts WHERE RunId=@Run AND CharacterId=@Player",
                new{Run=run,Player=player},tx,cancellationToken:ct));
            return row==null?throw new OnlineFailure(404,"RunMemberExitNotFound"):Decode(row);
        },ct);

    private static async Task SeedOriginals(MySqlConnection db,MySqlTransaction tx,string run,Dictionary<string,Original> originals,
        Dictionary<string,CustodyRow> custody,CancellationToken ct)
    {
        foreach(var batch in originals.Keys.Chunk(256))
        {
            var stored=(await db.QueryAsync<ItemRow>(new CommandDefinition(
                "SELECT InstanceId,OwnerCharacterId,ContentId,Revision FROM Items WHERE InstanceId IN @Ids",new{Ids=batch},tx,cancellationToken:ct)))
                .ToDictionary(i=>i.InstanceId,StringComparer.Ordinal);
            var active=(await db.QueryAsync<CustodyRow>(new CommandDefinition(
                "SELECT * FROM RunItemCustody WHERE InstanceId IN @Ids AND State='Active' AND RunId<>@Run",
                new{Ids=batch,Run=run},tx,cancellationToken:ct))).ToArray();
            if(active.Length!=0)throw Failure("RunMemberExitOwnershipConflict");
            foreach(string id in batch)
            {
                if(custody.ContainsKey(id))continue;
                var original=originals[id];
                if(!stored.TryGetValue(id,out var item)||item.OwnerCharacterId!=original.Owner||item.ContentId!=original.Item.ContentId)
                    throw Failure("RunMemberExitOwnershipConflict");
                await AddCustody(db,tx,run,id,item.ContentId,original.Owner,original.Item.Revision,ct);
                custody.Add(id,new(){RunId=run,InstanceId=id,ContentId=item.ContentId,OriginalOwnerCharacterId=original.Owner,
                    State="Active",Revision=original.Item.Revision});
            }
        }
    }
    private static async Task ValidateCandidate(MySqlConnection db,MySqlTransaction tx,OnlineRunMemberExitRequest value,
        Dictionary<string,Original> originals,Dictionary<string,CustodyRow> custody,CancellationToken ct)
    {
        foreach(var batch in value.Assets.Items.Chunk(256))
        {
            string[] ids=batch.Select(i=>i.InstanceId).ToArray();
            // 账号整理和Run退休共用唯一身份约束；旧快照不能把已耗尽材料重新带回Hub。
            if ((await Norn.Business.Features.Inventory.InventoryService.ReadRetiredIdsAsync(db,tx,ids,ct)).Count != 0)
                throw Failure("RunMemberExitOwnershipConflict");
            var stored=(await db.QueryAsync<ItemRow>(new CommandDefinition(
                "SELECT InstanceId,OwnerCharacterId,ContentId,Revision FROM Items WHERE InstanceId IN @Ids",new{Ids=ids},tx,cancellationToken:ct)))
                .ToDictionary(i=>i.InstanceId,StringComparer.Ordinal);
            var records=(await db.QueryAsync<IdentityRow>(new CommandDefinition(
                "SELECT InstanceId,RunId,ContentId,Revision,IsRetired FROM RunAssetCheckpointIdentities WHERE InstanceId IN @Ids",
                new{Ids=ids},tx,cancellationToken:ct))).GroupBy(i=>i.InstanceId).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.Ordinal);
            var foreign=(await db.QueryAsync<string>(new CommandDefinition(
                "SELECT InstanceId FROM RunItemCustody WHERE InstanceId IN @Ids AND (State='Retired' OR (RunId<>@Run AND State='Active'))",
                new{Ids=ids,Run=value.RunId},tx,cancellationToken:ct))).ToHashSet(StringComparer.Ordinal);
            foreach(var item in batch)
            {
                if(foreign.Contains(item.InstanceId))throw Failure("RunMemberExitOwnershipConflict");
                if(custody.TryGetValue(item.InstanceId,out var held)&&(held.State!="Active"||held.ContentId!=item.ContentId||item.Revision<held.Revision))
                    throw Failure("RunMemberExitOwnershipConflict");
                if(originals.TryGetValue(item.InstanceId,out var original))
                {
                    RunMemberExitRules.RequireOriginal(item,original.Item);
                    if (value.PracticeSessionScope.Length != 0 && (!stored.TryGetValue(item.InstanceId, out var persistent)
                        || persistent.OwnerCharacterId != original.Owner || persistent.ContentId != item.ContentId))
                        throw Failure("RunMemberExitOwnershipConflict");
                    if(item.EntryBoundBackpack&&original.Owner!=value.PlayerId)throw Failure("RunMemberExitOwnershipConflict");
                }
                else if(item.EntryBoundBackpack||stored.ContainsKey(item.InstanceId))throw Failure("RunMemberExitOwnershipConflict");
                if(records.TryGetValue(item.InstanceId,out var history))
                    foreach(var record in history)
                        if(record.RunId==value.RunId?(record.IsRetired||record.ContentId!=item.ContentId||item.Revision<record.Revision)
                            :!originals.ContainsKey(item.InstanceId))throw Failure("RunMemberExitOwnershipConflict");
                if(!custody.ContainsKey(item.InstanceId))
                {
                    await AddCustody(db,tx,value.RunId,item.InstanceId,item.ContentId,null,item.Revision,ct);
                    custody.Add(item.InstanceId,new(){RunId=value.RunId,InstanceId=item.InstanceId,ContentId=item.ContentId,State="Active",Revision=item.Revision});
                }
            }
        }
    }

    private async Task ValidateHomeRetirements(MySqlConnection db, MySqlTransaction tx, OnlineRunMemberExitRequest value,
        OnlineRunMemberExitRequest kept, Dictionary<string, Original> originals, Dictionary<string, CustodyRow> custody, CancellationToken ct)
    {
        var live = kept.Assets.Items.Select(i => i.InstanceId).ToHashSet(StringComparer.Ordinal);
        var retired = value.FinalRetiredItems.ToDictionary(i => i.InstanceId, StringComparer.Ordinal);
        // 缺项不是玩法证据。Entry、当前Active custody和已确认Journal中仍存活的每个身份，
        // 最终必须在carried/ground或真实退休事实之一；未知缺项保持Run/租约而非静默吞掉。
        var required = originals.Where(p => p.Value.Owner == value.PlayerId).Select(p => p.Key)
            .Concat(custody.Values.Where(c => c.State == "Active").Select(c => c.InstanceId)).ToHashSet(StringComparer.Ordinal);
        required.UnionWith(await db.QueryAsync<string>(new CommandDefinition(
            "SELECT InstanceId FROM RunAssetCheckpointIdentities WHERE RunId=@Run AND IsRetired=FALSE", new { Run = value.RunId }, tx, cancellationToken: ct)));
        if (required.Any(id => !live.Contains(id) && !retired.ContainsKey(id))) throw Failure("PracticeRetirementRequired");
        foreach (var batch in value.FinalRetiredItems.Chunk(256))
        {
            string[] ids = batch.Select(i => i.InstanceId).ToArray();
            var stored = (await db.QueryAsync<ItemRow>(new CommandDefinition(
                "SELECT InstanceId,OwnerCharacterId,ContentId,Revision FROM Items WHERE InstanceId IN @Ids", new { Ids = ids }, tx, cancellationToken: ct)))
                .ToDictionary(i => i.InstanceId, StringComparer.Ordinal);
            var identities = (await db.QueryAsync<IdentityRow>(new CommandDefinition(
                "SELECT InstanceId,RunId,ContentId,Revision,IsRetired FROM RunAssetCheckpointIdentities WHERE InstanceId IN @Ids",
                new { Ids = ids }, tx, cancellationToken: ct))).ToLookup(i => i.InstanceId, StringComparer.Ordinal);
            var foreign = (await db.QueryAsync<string>(new CommandDefinition(
                "SELECT InstanceId FROM RunItemCustody WHERE InstanceId IN @Ids AND RunId<>@Run AND State IN ('Active','Retired')",
                new { Ids = ids, Run = value.RunId }, tx, cancellationToken: ct))).ToHashSet(StringComparer.Ordinal);
            var accountRetired = await InventoryService.ReadRetiredIdsAsync(db, tx, ids, ct);
            foreach (var item in batch)
            {
                _ = content.RequireItem(item.ContentId);
                bool original = originals.TryGetValue(item.InstanceId, out var entry);
                if (live.Contains(item.InstanceId) || foreign.Contains(item.InstanceId) || accountRetired.Contains(item.InstanceId))
                    throw Failure("RunMemberExitOwnershipConflict");
                if (original && (entry!.Owner != value.PlayerId || entry.Item.EntryBoundBackpack || entry.Item.ContentId != item.ContentId || item.Revision < entry.Item.Revision))
                    throw Failure("RunMemberExitOwnershipConflict");
                if (stored.TryGetValue(item.InstanceId, out var sql)
                    && (!original || sql.OwnerCharacterId != value.PlayerId || sql.ContentId != item.ContentId || item.Revision < sql.Revision))
                    throw Failure("RunMemberExitOwnershipConflict");
                if (custody.TryGetValue(item.InstanceId, out var held)
                    && (held.State is not ("Active" or "Retired") || held.ContentId != item.ContentId || item.Revision < held.Revision))
                    throw Failure("RunMemberExitOwnershipConflict");
                foreach (var known in identities[item.InstanceId])
                    if (known.RunId == value.RunId ? known.ContentId != item.ContentId || item.Revision < known.Revision : !original)
                        throw Failure("RunMemberExitOwnershipConflict");
                // 可信DS可以领先周期Head提交真实“新生后退休”journal；数据库无该ID/外Run历史
                // 才接纳此出生证明。只保留终态身份，不伪造Items资产，也不强制先写Head再退出。
                if (held == null)
                {
                    await AddCustody(db, tx, value.RunId, item.InstanceId, item.ContentId, original ? value.PlayerId : null, item.Revision, ct);
                    custody.Add(item.InstanceId, new() { RunId = value.RunId, InstanceId = item.InstanceId, ContentId = item.ContentId,
                        OriginalOwnerCharacterId = original ? value.PlayerId : null, State = "Active", Revision = item.Revision });
                }
            }
        }
    }

    private static async Task RetireHomeItems(MySqlConnection db, MySqlTransaction tx, OnlineRunMemberExitRequest value, CancellationToken ct)
    {
        foreach (var item in value.FinalRetiredItems)
        {
            // 原Items及侧表留作审计，Retired custody永久拒绝复活。新生后已销毁而未入库的身份
            // 只占据真实终态journal/custody，不为满足审计而额外发一件物品。
            await db.ExecuteAsync(new CommandDefinition("""
                UPDATE CharacterCombatLoadouts SET SelectedWandInstanceId=NULL WHERE SelectedWandInstanceId=@Id;
                DELETE FROM ItemLocations WHERE InstanceId=@Id;
                UPDATE Items SET Revision=@Revision WHERE InstanceId=@Id AND OwnerCharacterId=@Player
                """, new { Id = item.InstanceId, item.Revision, Player = value.PlayerId }, tx, cancellationToken: ct));
            if (await db.ExecuteAsync(new CommandDefinition("""
                UPDATE RunItemCustody SET State='Retired',Revision=@Revision
                WHERE RunId=@Run AND InstanceId=@Id AND State IN ('Active','Retired')
                """, new { Run = value.RunId, Id = item.InstanceId, item.Revision }, tx, cancellationToken: ct)) != 1)
                throw Failure("RunMemberExitOwnershipConflict");
        }
    }

    private static Task AddCustody(MySqlConnection db,MySqlTransaction tx,string run,string id,string content,string? owner,uint revision,CancellationToken ct)
        =>db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO RunItemCustody(RunId,InstanceId,ContentId,OriginalOwnerCharacterId,State,Revision)
            VALUES(@Run,@Id,@Content,@Owner,'Active',@Revision)
            """,new{Run=run,Id=id,Content=content,Owner=owner,Revision=revision},tx,cancellationToken:ct));

    private static async Task RetireFinalItems(MySqlConnection db,MySqlTransaction tx,OnlineRunMemberExitRequest value,CancellationToken ct)
    {
        // 只终止清前快照明确随身的原ID；先前已交给队友/留在世界的缺项不在此集合。
        // 留Items/侧表及原RequestJson作审计，Retired托管禁止任何账号或后续Run重新授予。
        foreach(var batch in value.FinalRetiredItems.Chunk(256))
        {
            string[] ids=batch.Select(i=>i.InstanceId).ToArray();
            await db.ExecuteAsync(new CommandDefinition("""
                UPDATE CharacterCombatLoadouts SET SelectedWandInstanceId=NULL WHERE SelectedWandInstanceId IN @Ids;
                DELETE FROM ItemLocations WHERE InstanceId IN @Ids
                """,new{Ids=ids},tx,cancellationToken:ct));
            foreach(var item in batch)
                if(await db.ExecuteAsync(new CommandDefinition("""
                    UPDATE RunItemCustody SET State='Retired',Revision=@Revision
                    WHERE RunId=@Run AND InstanceId=@Id AND State='Active'
                    """,new{Run=value.RunId,Id=item.InstanceId,item.Revision},tx,cancellationToken:ct))!=1)
                    throw Failure("RunMemberExitOwnershipConflict");
        }
    }

    private async Task PersistCarried(MySqlConnection db,MySqlTransaction tx,OnlineRunMemberExitRequest value,
        Dictionary<string,Original> originals,Dictionary<string,CustodyRow> custody,CancellationToken ct)
    {
        string[] ids=value.Assets.Items.Select(i=>i.InstanceId).ToArray();
        var locations=new HashSet<string>(ids,StringComparer.Ordinal);
        // 周期Journal可能已经把耗尽卡/破瓶标成Retired，但原SQL父子位置直到最终事务才清理。
        // Home在前面已逐条验证真实退休证据；仅这些明确终止的旧子项允许随父物清位。
        // 普通副本、外Run墓碑或没有本次证据的Retired子项仍不能借此绕过归属检查。
        var homeRetired = value.SchemaVersion == OnlineRunMemberExitRequest.CurrentSchemaVersion
            && AccountPracticeContinuityStore.IsScope(value.PracticeSessionScope)
            ? value.FinalRetiredItems.Select(item => item.InstanceId).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        // 本人未携出的原物由本Run继续托管，其旧SQL位置不再是Hub槽位，避免合法替换撞唯一索引。
        // 只解除位置；原Items、组分/roll等侧表和托管ID仍保留，供队友后续承接同一实例。
        var heldLocations=await db.QueryAsync<string>(new CommandDefinition("""
            SELECT l.InstanceId FROM ItemLocations l JOIN Items i ON i.InstanceId=l.InstanceId
            JOIN RunItemCustody c ON c.InstanceId=i.InstanceId AND c.RunId=@Run AND c.State='Active'
            WHERE i.OwnerCharacterId=@Player AND l.OwnerCharacterId=@Player
            """,new{Run=value.RunId,Player=value.PlayerId},tx,cancellationToken:ct));
        foreach(string id in heldLocations)locations.Add(id);
        // 父物改Owner前先解除本Run原子链的旧位置FK；不在候选的子项仍留Run custody，不能回Hub重授。
        foreach(var batch in ids.Chunk(256))
        {
            var children=await db.QueryAsync<string>(new CommandDefinition(
                "SELECT InstanceId FROM ItemLocations WHERE ParentInstanceId IN @Ids",new{Ids=batch},tx,cancellationToken:ct));
            foreach(string child in children)
            {
                if(!custody.TryGetValue(child,out var held)
                    || held.State!="Active" && !(held.State=="Retired" && homeRetired.Contains(child)))
                    throw Failure("RunMemberExitOwnershipConflict");
                locations.Add(child);
            }
            await db.ExecuteAsync(new CommandDefinition(
                "UPDATE CharacterCombatLoadouts SET SelectedWandInstanceId=NULL WHERE SelectedWandInstanceId IN @Ids",new{Ids=batch},tx,cancellationToken:ct));
        }
        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE CharacterCombatLoadouts SET SelectedWandInstanceId=NULL WHERE CharacterId=@Player",new{Player=value.PlayerId},tx,cancellationToken:ct));
        foreach(var batch in locations.Chunk(256))await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM ItemLocations WHERE InstanceId IN @Ids",new{Ids=batch},tx,cancellationToken:ct));
        foreach(var batch in ids.Chunk(256))await db.ExecuteAsync(new CommandDefinition("""
            DELETE FROM ItemContainerComponents WHERE InstanceId IN @Ids;
            DELETE FROM ItemContainerStates WHERE InstanceId IN @Ids;
            DELETE FROM ItemMaterialStates WHERE InstanceId IN @Ids;
            DELETE FROM ItemSpellAttachmentStates WHERE InstanceId IN @Ids;
            DELETE FROM ItemWandStates WHERE InstanceId IN @Ids
            """,new{Ids=batch},tx,cancellationToken:ct));
        foreach(var item in value.Assets.Items)
        {
            int mode=content.RequireItem(item.ContentId).Kind==1&&!item.UseTemplateWandStats?1:0;
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges,WandStatsMode)
                VALUES(@Id,@Player,@Content,@Revision,@Charges,@Unlimited,@Mode)
                ON DUPLICATE KEY UPDATE OwnerCharacterId=@Player,Revision=@Revision,RemainingCharges=@Charges,HasUnlimitedCharges=@Unlimited,WandStatsMode=@Mode
                """,new{Id=item.InstanceId,Player=value.PlayerId,Content=item.ContentId,item.Revision,Charges=item.RemainingCharges,
                    Unlimited=item.HasUnlimitedCharges,Mode=mode},tx,cancellationToken:ct));
        }
        var worldSlots = (await db.QueryAsync<int>(new CommandDefinition(
            "SELECT Slot FROM ItemLocations WHERE OwnerCharacterId=@Player AND CombatLocationKind=5",
            new { Player = value.PlayerId }, tx, cancellationToken: ct))).ToHashSet();
        // 全部父物已存在后再安装位置，避免数组顺序依赖；NULL Parent只用于玩家根槽。
        foreach(var item in value.Assets.Items)
        {
            // Wire地面Slot=-1保持原协议；SQL内部非负Slot只满足既有唯一键，绝非新增UI容量。
            int slot = item.Slot;
            if (item.LocationKind == 5)
            {
                slot = 0; while (worldSlots.Contains(slot)) slot++;
                if (slot >= HomePracticeAssets.MaximumItems) throw Failure("InventoryCapacityExceeded"); worldSlots.Add(slot);
            }
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind,ParentInstanceId,GridX,GridY,QuarterTurns)
                VALUES(@Id,@Player,NULL,@Slot,@Kind,@Parent,@X,@Y,@Rotation)
                """,new{Id=item.InstanceId,Player=value.PlayerId,Slot=slot,Kind=item.LocationKind,
                    Parent=item.ParentInstanceId.Length==0?null:item.ParentInstanceId,item.X,item.Y,Rotation=item.QuarterTurns},tx,cancellationToken:ct));
            if(item.ContainerState!=null)
            {
                await db.ExecuteAsync(new CommandDefinition("INSERT INTO ItemContainerStates VALUES(@Id,@Schema)",
                    new{Id=item.InstanceId,Schema=item.ContainerState.SchemaVersion},tx,cancellationToken:ct));
                foreach(var part in item.ContainerState.Components)await db.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO ItemContainerComponents VALUES(@Id,@Key,@Quantity)",new{Id=item.InstanceId,Key=part.MaterialKey,part.Quantity},tx,cancellationToken:ct));
            }
            if(item.MaterialState is{}material)await db.ExecuteAsync(new CommandDefinition(
                "INSERT INTO ItemMaterialStates VALUES(@Id,@Schema,@Key,@Quantity)",
                new{Id=item.InstanceId,Schema=material.SchemaVersion,Key=material.MaterialKey,material.Quantity},tx,cancellationToken:ct));
            if(item.AttachmentState is{}attachment)await db.ExecuteAsync(new CommandDefinition(
                "INSERT INTO ItemSpellAttachmentStates VALUES(@Id,@Schema,@Container,@Dose,@Enabled)",
                new{Id=item.InstanceId,Schema=attachment.SchemaVersion,Container=attachment.ContainerId,attachment.Dose,attachment.Enabled},tx,cancellationToken:ct));
            if(item.WandStats!=null)await PersistentWandStateStore.WriteNewAsync(db,tx,value.PlayerId,item.InstanceId,item.WandStats,content,ct);
            await db.ExecuteAsync(new CommandDefinition("""
                UPDATE RunItemCustody SET State='Settled',SettlementId=@Settlement,FinalOwnerCharacterId=@Player,Revision=@Revision
                WHERE RunId=@Run AND InstanceId=@Id AND State='Active'
                """,new{Settlement=value.SettlementId,Player=value.PlayerId,item.Revision,Run=value.RunId,Id=item.InstanceId},tx,cancellationToken:ct));
        }
        string? selected=value.Assets.SelectedWandInstanceId.Length==0?null:value.Assets.SelectedWandInstanceId;
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO CharacterCombatLoadouts(CharacterId,SelectedWandInstanceId) VALUES(@Player,@Selected)
            ON DUPLICATE KEY UPDATE SelectedWandInstanceId=@Selected
            """,new{Player=value.PlayerId,Selected=selected},tx,cancellationToken:ct));
    }
    private static OnlineRunMemberExitReceipt Decode(ReceiptRow row)
        =>JsonSerializer.Deserialize<OnlineRunMemberExitReceipt>(row.ResultJson,ContentManifestValidator.Json)
            ??throw Failure("RunMemberExitReceiptInvalid");
    private static void RequireCurrency(OnlineOwnedAssetSnapshot candidate,OnlineOwnedAssetSnapshot recorded)
    {
        if(recorded.SchemaVersion!=OnlineOwnedAssetSnapshot.CurrentSchemaVersion)return;
        if(recorded.CurrencyVersion==0||recorded.Gold<0||candidate.CurrencyVersion<recorded.CurrencyVersion
            ||candidate.CurrencyVersion==recorded.CurrencyVersion&&candidate.Gold!=recorded.Gold)
            throw Failure("RunMemberExitCurrencyConflict");
    }
    private static OnlineFailure Failure(string code)=>new(409,code);
    private sealed record Original(string Owner,OnlineOwnedItemSnapshot Item);
    private sealed class MemberRow {public string CharacterId{get;set;}="";public uint InventoryVersion{get;set;}public string EntrySnapshot{get;set;}="";public string State{get;set;}="";public uint ConnectionEpoch{get;set;}public int? Seat{get;set;}}
    private sealed class LeaseRow {public string RunId{get;set;}="";public uint RoomEpoch{get;set;}}
    private sealed class ItemRow {public string InstanceId{get;set;}="";public string OwnerCharacterId{get;set;}="";public string ContentId{get;set;}="";public uint Revision{get;set;}}
    private sealed class IdentityRow {public string InstanceId{get;set;}="";public string RunId{get;set;}="";public string ContentId{get;set;}="";public uint Revision{get;set;}public bool IsRetired{get;set;}}
    private sealed class CustodyRow {public string RunId{get;set;}="";public string InstanceId{get;set;}="";public string ContentId{get;set;}="";public string? OriginalOwnerCharacterId{get;set;}public string State{get;set;}="";public uint Revision{get;set;}}
    private sealed class ReceiptRow {public string RunId{get;set;}="";public string CharacterId{get;set;}="";public string InstanceId{get;set;}="";public string RequestHash{get;set;}="";public string ResultJson{get;set;}="";}
}
