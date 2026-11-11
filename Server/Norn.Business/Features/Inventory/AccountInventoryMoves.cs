using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using MySqlConnector;
using Norn.Business.Content;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Inventory;

/// <summary>
/// Hub账号结构化移动：Client只给原实例和目标位置，所有状态/整栈成员/交换对象均来自受锁数据库。
/// 不运行Gameplay，不伪造Run/Actor，也不提交Client资产草稿。位置、选杖、版本及幂等收据同事务提交。
/// </summary>
internal static class AccountInventoryMoves
{
    internal static async Task<InventoryMoveReply> ApplyAsync(OnlineDatabase database,ContentManifestValidator content,
        InventoryService inventory,string player,InventoryMoveRequest request,CancellationToken ct)
    {
        var move=JsonSerializer.Deserialize<InventoryMoveRequest>(JsonSerializer.Serialize(request,ContentManifestValidator.Json),ContentManifestValidator.Json)!;
        RequireShape(move);
        // Move 的既有摘要保持字节不变；选杖是独立账号命令，不能与相同 ID 的位置事务混用收据。
        string canonical=move.OperationKind switch
        {
            1=>FormattableString.Invariant($"account-move-v1|{move.ExpectedVersion}|{move.InstanceId}|{move.TargetLocationKind}|{move.TargetParentInstanceId}|{move.Slot}|{move.GridX}|{move.GridY}|{move.QuarterTurns}|{move.WholeStack}"),
            2=>FormattableString.Invariant($"account-select-v1|{move.ExpectedVersion}|{move.InstanceId}|{move.Slot}"),
            _=>"account-hub-v1|"+JsonSerializer.Serialize(move,ContentManifestValidator.Json)
        };
        string hash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        await using var db=await database.OpenAsync(ct);
        await using var tx=await db.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        uint? version=await db.QuerySingleOrDefaultAsync<uint?>(new CommandDefinition(
            "SELECT Version FROM Characters WHERE CharacterId=@Player FOR UPDATE",new{Player=player},tx,cancellationToken:ct));
        if(version==null)throw new OnlineFailure(404,"ProfileNotFound");
        var prior=await db.QuerySingleOrDefaultAsync<Receipt>(new CommandDefinition(
            "SELECT RequestHash,ResultJson FROM InventoryCommands WHERE CharacterId=@Player AND CommandId=@Id",
            new{Player=player,Id=move.CommandId},tx,cancellationToken:ct));
        if(prior!=null)
        {
            if(prior.RequestHash!=hash)throw Failure("CommandConflict");
            // 已提交事实先于新版本/租约：丢回包后入局也不能把原成功误报成失败。
            return JsonSerializer.Deserialize<InventoryMoveReply>(prior.ResultJson,ContentManifestValidator.Json)!;
        }
        if(await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM AssetLeases WHERE CharacterId=@Player",new{Player=player},tx,cancellationToken:ct))!=0)
            throw Failure("AssetsLeased");
        if(version!=move.ExpectedVersion||version==uint.MaxValue)throw Failure("ProfileVersionConflict");
        var profile=await inventory.ReadSnapshotAsync(db,tx,player,ct);
        var candidate=AccountSnapshot(profile,content);
        // 先验证真实源布局；不能通过本次Move默默修复/转换未知旧平面资产或损坏父链。
        OwnedCombatAssetRules.RequireAccount(candidate,player,content.GameplayConfigHash,content);
        var items=candidate.Items.ToDictionary(i=>i.InstanceId,StringComparer.Ordinal);
        if(!items.TryGetValue(move.InstanceId,out var source))throw new OnlineFailure(404,"ItemNotOwned");
        var before=items.ToDictionary(p=>p.Key,p=>At(p.Value),StringComparer.Ordinal);
        var beforeStates=items.ToDictionary(p=>p.Key,p=>JsonSerializer.Serialize(p.Value,ContentManifestValidator.Json),StringComparer.Ordinal);
        var beforeAmounts=items.ToDictionary(p=>p.Key,p=>p.Value.MaterialState?.Quantity??0,StringComparer.Ordinal);
        var worlds=profile.WorldItems.ToDictionary(i=>i.InstanceId,StringComparer.Ordinal);
        var dropped=new List<OnlineHubWorldItemReply>();
        var removedWorlds=new List<string>();
        var retired=new List<OnlineOwnedItemSnapshot>();
        ulong transferred=0;bool mergedPickup=false;
        string previousSelected=candidate.SelectedWandInstanceId;
        if(move.OperationKind==2)
        {
            // 选中只接受本人真实装备槽里的法杖，不能借选中把仓库/背包法杖装备起来。
            if(source.LocationKind!=3||source.Slot!=move.Slot)throw Failure("WandNotEquipped");
            candidate.SelectedWandInstanceId=source.InstanceId;
        }
        else if(move.OperationKind==3)
        {
            if(source.LocationKind!=4)throw Failure("BackpackNotEquipped");
            Organize(items,source,content,retired);
        }
        else if(move.OperationKind==4)
        {
            if(source.LocationKind==5||source.EntryBoundBackpack)throw Failure("InvalidOwnership");
            var roots=move.WholeStack&&source.LocationKind==1&&source.MaterialState==null
                ?items.Values.Where(i=>At(i)==before[source.InstanceId]).ToArray():new[]{source};
            foreach(var root in roots)
            {
                Put(root,new(5,"",-1,0,0,0));
                dropped.Add(new(){InstanceId=root.InstanceId,WorldOwnerId=Guid.NewGuid().ToString("N"),Pose=move.WorldPose});
            }
        }
        else if(move.OperationKind==5)
        {
            if(source.LocationKind!=5||!worlds.TryGetValue(source.InstanceId,out var ground)||ground.WorldOwnerId!=move.WorldOwnerId)
                throw Failure("WorldItemChanged");
            if(move.TargetLocationKind is 2 or 3 or 4 or 7 or 8&&items.Values.Any(i=>i.InstanceId!=source.InstanceId&&i.LocationKind==move.TargetLocationKind
                &&i.ParentInstanceId==move.TargetParentInstanceId&&i.Slot==move.Slot))throw Failure("SlotOccupied");
            var destination=new Position(move.TargetLocationKind,move.TargetParentInstanceId,move.Slot,move.GridX,move.GridY,move.QuarterTurns);
            if(source.MaterialState!=null)
            {
                var target=destination.Kind==1?items.Values.FirstOrDefault(i=>i.InstanceId!=source.InstanceId&&At(i)==destination&&i.MaterialState!=null
                    &&i.ContentId==source.ContentId&&i.MaterialState.MaterialKey==source.MaterialState.MaterialKey):null;
                if(target!=null)
                {
                    ulong room=content.RequireItem(target.ContentId).MaterialItemCapacityGmu-target.MaterialState!.Quantity;
                    transferred=Math.Min(source.MaterialState.Quantity,room);
                    if(transferred==0)throw Failure("BackpackFull");
                    target.MaterialState.Quantity+=transferred;mergedPickup=true;
                    if(transferred==source.MaterialState.Quantity){items.Remove(source.InstanceId);retired.Add(source);removedWorlds.Add(source.InstanceId);}
                    else source.MaterialState.Quantity-=transferred;
                }
                else transferred=source.MaterialState.Quantity;
            }
            if(!mergedPickup)
            {
                if(move.TargetLocationKind==0)Apply(items,source,move,content);
                else Put(source,destination);
                removedWorlds.Add(source.InstanceId);
            }
        }
        else
        {
            // 地面进入库存必须携带匹配Spawn的Pickup，不能以普通Move绕开旧请求栅栏。
            if(source.LocationKind==5)throw Failure("WorldItemChanged");
            Apply(items,source,move,content);
        }
        candidate.Items=items.Values.ToArray();
        // 玩家始终保有实际装备的包；直接卸掉最后一包不是换包。原目标4分支会原子交换
        // 新旧包并迁移真实子项，继续允许升级大包；失败在任何SQL/Receipt写入前保持原资产。
        if (!candidate.Items.Any(i => i.LocationKind == 4)) throw Failure("MinimumBackpackRequired");
        // 原WandLoadoutService保持仍装备的选中ID；被卸下时选首个装备槽，完全空手则明确空串。
        string selected=candidate.SelectedWandInstanceId;
        if(!items.TryGetValue(selected,out var selectedItem)||selectedItem.LocationKind!=3)
            selected=items.Values.Where(i=>i.LocationKind==3).OrderBy(i=>i.Slot).Select(i=>i.InstanceId).FirstOrDefault()??"";
        candidate.SelectedWandInstanceId=selected;
        OwnedCombatAssetRules.RequireAccount(candidate,player,content.GameplayConfigHash,content);
        string[] changed=items.Values.Where(i=>At(i)!=before[i.InstanceId]||(i.MaterialState?.Quantity??0)!=beforeAmounts[i.InstanceId])
            .Select(i=>i.InstanceId).Concat(retired.Select(i=>i.InstanceId)).ToArray();
        if(changed.Any(id=>(items.TryGetValue(id,out var live)?live:retired.Single(i=>i.InstanceId==id)).Revision==uint.MaxValue))throw Failure("InventoryRevisionExhausted");
        bool selectedChanged=selected!=previousSelected;
        uint committed=changed.Length==0&&!selectedChanged?version.Value:version.Value+1;
        // 交换必须先释放所有受影响位置，避免SQL唯一槽索引依赖UPDATE先后；不删除Items或侧表。
        foreach(string[] batch in changed.Chunk(256))await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM ItemLocations WHERE OwnerCharacterId=@Player AND InstanceId IN @Ids",new{Player=player,Ids=batch},tx,cancellationToken:ct));
        var worldSlots=(await db.QueryAsync<int>(new CommandDefinition("SELECT Slot FROM ItemLocations WHERE OwnerCharacterId=@Player AND CombatLocationKind=5",new{Player=player},tx,cancellationToken:ct))).ToHashSet();
        foreach(string id in changed)
        {
            if(!items.TryGetValue(id,out var item))continue;
            bool warehouse=item.LocationKind==0;int slot=item.Slot;
            if(item.LocationKind==5){slot=0;while(worldSlots.Contains(slot))slot++;if(slot>=OnlineOwnedAssetSnapshot.MaximumItems+24)throw Failure("InventoryCapacityExceeded");worldSlots.Add(slot);}
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind,ParentInstanceId,GridX,GridY,QuarterTurns)
                VALUES(@Id,@Player,@Area,@Slot,@Kind,@Parent,@X,@Y,@Turns)
                """,new{Id=id,Player=player,Area=warehouse?"Warehouse":null,Slot=slot,Kind=warehouse?(int?)null:item.LocationKind,
                    Parent=item.ParentInstanceId.Length==0?null:item.ParentInstanceId,item.X,item.Y,Turns=item.QuarterTurns},tx,cancellationToken:ct));
            if(item.MaterialState!=null&&item.MaterialState.Quantity!=beforeAmounts[id])
                if(await db.ExecuteAsync(new CommandDefinition("UPDATE ItemMaterialStates SET Quantity=@Amount WHERE InstanceId=@Id",new{Id=id,Amount=item.MaterialState.Quantity},tx,cancellationToken:ct))!=1)
                    throw Failure("InvalidCombatMaterial");
        }
        foreach(string[] batch in changed.Chunk(256))
            if(await db.ExecuteAsync(new CommandDefinition("UPDATE Items SET Revision=Revision+1 WHERE OwnerCharacterId=@Player AND InstanceId IN @Ids",
                new{Player=player,Ids=batch},tx,cancellationToken:ct))!=batch.Length)throw Failure("ItemNotOwned");
        if(changed.Length!=0||selectedChanged)
        {
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO CharacterCombatLoadouts(CharacterId,SelectedWandInstanceId)VALUES(@Player,@Selected)
                ON DUPLICATE KEY UPDATE SelectedWandInstanceId=@Selected;
                UPDATE Characters SET Version=@Version WHERE CharacterId=@Player
                """,new{Player=player,Selected=selected.Length==0?null:selected,Version=committed},tx,cancellationToken:ct));
        }
        var actual=items.TryGetValue(move.InstanceId,out var surviving)?surviving:source;
        var replyAt=mergedPickup?new Position(move.TargetLocationKind,move.TargetParentInstanceId,move.Slot,move.GridX,move.GridY,move.QuarterTurns):At(actual);
        var reply=new InventoryMoveReply{CommandId=move.CommandId,CommittedVersion=committed,InstanceId=move.InstanceId,
            OperationKind=move.OperationKind,TargetLocationKind=replyAt.Kind,TargetParentInstanceId=replyAt.Parent,
            Slot=replyAt.Slot,GridX=replyAt.X,GridY=replyAt.Y,QuarterTurns=replyAt.Turns,WholeStack=move.WholeStack,Transferred=transferred,
            WorldOwnerId=move.OperationKind==4?dropped.Single(i=>i.InstanceId==move.InstanceId).WorldOwnerId:move.WorldOwnerId,
            WorldPose=move.OperationKind==4?move.WorldPose:null};
        await db.ExecuteAsync(new CommandDefinition("INSERT INTO InventoryCommands VALUES(@Player,@Id,@Hash,@Json,UTC_TIMESTAMP(6))",
            new{Player=player,Id=move.CommandId,Hash=hash,Json=JsonSerializer.Serialize(reply,ContentManifestValidator.Json)},tx,cancellationToken:ct));
        // 命令收据与永久退休/姿态记录属于同一事务；先插收据满足外键，未Commit均不可见。
        foreach(var old in retired)await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO AccountItemRetirements(InstanceId,OwnerCharacterId,CommandId,ContentId,Revision,Reason,SnapshotJson,RecordedAtUtc)
              VALUES(@Id,@Player,@Command,@Content,@Revision,'Merged',@Snapshot,UTC_TIMESTAMP(6))
            """,new{Id=old.InstanceId,Player=player,Command=move.CommandId,Content=old.ContentId,Revision=old.Revision+1,Snapshot=beforeStates[old.InstanceId]},tx,cancellationToken:ct));
        foreach(string id in removedWorlds)
            if(await db.ExecuteAsync(new CommandDefinition("DELETE FROM HubWorldItems WHERE InstanceId=@Id AND OwnerCharacterId=@Player AND WorldOwnerId=@Spawn",
                new{Id=id,Player=player,Spawn=move.WorldOwnerId},tx,cancellationToken:ct))!=1)throw Failure("WorldItemChanged");
        foreach(var ground in dropped)await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO HubWorldItems(InstanceId,OwnerCharacterId,WorldOwnerId,SceneKey,WorldSceneKey,PoseJson,DropCommandId,RecordedAtUtc)
              VALUES(@Id,@Player,@Spawn,@Scene,@World,@Pose,@Command,UTC_TIMESTAMP(6))
            """,new{Id=ground.InstanceId,Player=player,Spawn=ground.WorldOwnerId,Scene=ground.Pose.SceneKey,World=ground.Pose.WorldSceneKey,
                Pose=JsonSerializer.Serialize(ground.Pose,ContentManifestValidator.Json),Command=move.CommandId},tx,cancellationToken:ct));
        await tx.CommitAsync(ct);return reply;
    }

    private static void Apply(Dictionary<string,OnlineOwnedItemSnapshot> items,OnlineOwnedItemSnapshot source,
        InventoryMoveRequest move,ContentManifestValidator content)
    {
        var original=At(source);
        var destination=new Position(move.TargetLocationKind,move.TargetParentInstanceId,move.Slot,move.GridX,move.GridY,move.QuarterTurns);
        if(destination.Kind==4)
        {
            if(source.LocationKind==4)return;
            if(source.LocationKind!=0||content.RequireItem(source.ContentId).Kind!=2)throw Failure("InvalidCombatLocation");
            var old=items.Values.SingleOrDefault(i=>i.LocationKind==4);
            Put(source,destination);
            if(old!=null)
            {
                Put(old,original);
                foreach(var child in items.Values.Where(i=>i.LocationKind==1&&i.ParentInstanceId==old.InstanceId))
                    child.ParentInstanceId=source.InstanceId;
            }
            return;
        }
        var moving=new List<OnlineOwnedItemSnapshot>{source};
        // 同一原实包格已经过源校验，成员由服务器推导；材料实体是GMU状态，不以整栈意图复制/分裂。
        if(move.WholeStack&&source.LocationKind==1&&source.MaterialState==null)
            moving.AddRange(items.Values.Where(i=>i.InstanceId!=source.InstanceId&&At(i)==original));
        if(moving.Count>1&&destination.Kind is not (0 or 1))throw Failure("SlotOccupied");
        if(destination.Kind==0)
        {
            var movingIds=moving.Select(i=>i.InstanceId).ToHashSet(StringComparer.Ordinal);
            var occupied=items.Values.Where(i=>i.LocationKind==0&&!movingIds.Contains(i.InstanceId)).Select(i=>i.Slot).ToHashSet();
            foreach(var item in moving)
            {
                int slot=item==source?destination.Slot:-1;
                if(slot<0){slot=0;while(slot<OnlineInventoryAreas.WarehouseCapacity&&occupied.Contains(slot))slot++;}
                if(slot>=OnlineInventoryAreas.WarehouseCapacity||!occupied.Add(slot))throw Failure("SlotOccupied");
                Put(item,new(0,"",slot,0,0,0));
            }
            return;
        }
        if(destination.Kind==3)
        {
            if(source.LocationKind is not (0 or 1 or 3))throw Failure("InvalidCombatLocation");
            var target=items.Values.SingleOrDefault(i=>i.InstanceId!=source.InstanceId&&At(i)==destination);
            if(target!=null)Put(target,original); // 原EquipWand：目标旧杖回来源位置，整个父程序仍跟原杖。
        }
        else if(destination.Kind is 2 or 7)
        {
            var target=items.Values.SingleOrDefault(i=>i.InstanceId!=source.InstanceId&&At(i)==destination);
            if(target!=null)Put(target,original); // 原TryPlaceSpell：两个原卡原子换位，不重建次数或附件。
        }
        foreach(var item in moving)Put(item,destination);
    }

    // 结构移动与商店共用同一服务端完整账号映射；调用方只接已验证DB快照，不能接Client资产草稿。
    internal static OnlineOwnedAssetSnapshot AccountSnapshot(OnlineProfileReply profile,ContentManifestValidator content)=>new()
    {
        SchemaVersion=OnlineOwnedAssetSnapshot.CurrentSchemaVersion,PlayerId=profile.PlayerId,InventoryId=profile.PlayerId,
        InventoryVersion=profile.Version,GameplayConfigHash=content.GameplayConfigHash,Gold=profile.Gold,CurrencyVersion=1,
        SelectedWandInstanceId=profile.CombatAssets?.SelectedWandInstanceId??"",
        Items=profile.Items.Select(i=>new OnlineOwnedItemSnapshot{InstanceId=i.InstanceId,ContentId=i.ContentId,Revision=i.Revision,
            LocationKind=i.LocationKind,ParentInstanceId=i.ParentInstanceId,Slot=i.Slot,X=i.X,Y=i.Y,QuarterTurns=i.QuarterTurns,
            RemainingCharges=i.RemainingCharges,HasUnlimitedCharges=i.HasUnlimitedCharges,EntryBoundBackpack=i.EntryBoundBackpack,
            UseTemplateWandStats=i.UseTemplateWandStats,WandStats=i.WandStats,ContainerState=i.ContainerState,
            MaterialState=i.MaterialState,AttachmentState=i.AttachmentState}).ToArray()
    };
    private static void RequireShape(InventoryMoveRequest move)
    {
        if(move.OperationKind is <1 or >5||!Id(move.CommandId)||!Id(move.InstanceId)||move.ExpectedVersion==0||move.Destination!=""
            ||move.TargetParentInstanceId==null||move.TargetLocationKind is not (0 or 1 or 2 or 3 or 4 or 5 or 7 or 8)
            ||(move.TargetLocationKind is 1 or 2?!Id(move.TargetParentInstanceId):move.TargetParentInstanceId.Length!=0)
            ||move.QuarterTurns is <0 or >3||move.GridX<0||move.GridY<0
            ||move.TargetLocationKind!=1&&(move.GridX!=0||move.GridY!=0||move.QuarterTurns!=0)
            ||move.TargetLocationKind==0&&move.Slot is < -1 or >=OnlineInventoryAreas.WarehouseCapacity
            ||move.TargetLocationKind==1&&move.Slot!=-1
            ||move.TargetLocationKind==2&&move.Slot is <0 or >=32
            ||move.TargetLocationKind==3&&move.Slot is <0 or >=3
            ||move.TargetLocationKind==4&&move.Slot!=0
            ||move.TargetLocationKind==5&&move.Slot!=-1
            ||move.TargetLocationKind==7&&move.Slot is <0 or >=12
            ||move.TargetLocationKind==8&&move.Slot is <0 or >=7
            ||move.OperationKind==2&&(move.TargetLocationKind!=3||move.WholeStack))
            throw new OnlineFailure(400,"InvalidMove");
        if(move.OperationKind==3&&(move.TargetLocationKind!=4||move.WholeStack)
            ||move.OperationKind!=4&&move.TargetLocationKind==5
            ||move.OperationKind==4&&(move.TargetLocationKind!=5||!string.IsNullOrEmpty(move.WorldOwnerId))
            ||move.OperationKind==5&&(!Id(move.WorldOwnerId)||move.WholeStack)
            ||move.OperationKind!=5&&move.OperationKind!=4&&!string.IsNullOrEmpty(move.WorldOwnerId)
            ||move.OperationKind!=4&&move.WorldPose!=null)throw new OnlineFailure(400,"InvalidMove");
        if(move.OperationKind==4)RequireWorldPose(move.WorldPose);
    }
    /// <summary>
    /// Unity沿原五点支撑准备Grounded姿态；业务服验证私人场景和数值，不冒充Physics地形裁决。
    /// 显式三轴比例保持原模型表现，绝不把可视姿态用于金币、物品数量或碰撞/拾取半径。
    /// </summary>
    internal static void RequireWorldPose(OnlineHubWorldItemPose? pose)
    {
        if(pose==null||pose.SceneKey is not ("hub.map1" or "home.map1")||pose.WorldSceneKey!=pose.SceneKey||pose.PoseKind!=2
            ||!float.IsFinite(pose.PositionX)||!float.IsFinite(pose.PositionY)||!float.IsFinite(pose.PositionZ)
            ||!float.IsFinite(pose.RotationX)||!float.IsFinite(pose.RotationY)||!float.IsFinite(pose.RotationZ)||!float.IsFinite(pose.RotationW)
            ||Math.Abs((double)pose.RotationX*pose.RotationX+(double)pose.RotationY*pose.RotationY+(double)pose.RotationZ*pose.RotationZ+(double)pose.RotationW*pose.RotationW-1)>.01
            ||pose.HasVisualScaleOverride&&(!Scale(pose.VisualScaleX)||!Scale(pose.VisualScaleY)||!Scale(pose.VisualScaleZ)))
            throw new OnlineFailure(400,"InvalidHubWorldPose");
    }
    private static bool Scale(float value)=>float.IsFinite(value)&&Math.Abs(value)>.00001f;

    /// <summary>
    /// 逐项保持原BackpackOrganizeOps：按ID合并兼容GMU，再以类型/内容/面积/ID整组排格。
    /// 候选完全成功后才写SQL；找不到位置、容量不足或版本冲突不会产生半份材料或丢失源身份。
    /// </summary>
    private static void Organize(Dictionary<string,OnlineOwnedItemSnapshot> items,OnlineOwnedItemSnapshot bag,
        ContentManifestValidator content,List<OnlineOwnedItemSnapshot> retired)
    {
        var bagRules=content.RequireItem(bag.ContentId);
        var entries=items.Values.Where(i=>i.LocationKind==1&&i.ParentInstanceId==bag.InstanceId).OrderBy(i=>i.InstanceId,StringComparer.Ordinal).ToArray();
        for(int i=0;i<entries.Length;i++)
        {
            if(!items.TryGetValue(entries[i].InstanceId,out var target)||target.MaterialState==null)continue;
            ulong capacity=content.RequireItem(target.ContentId).MaterialItemCapacityGmu;
            for(int j=i+1;j<entries.Length;j++)
            {
                if(!items.TryGetValue(entries[j].InstanceId,out var source)||source.MaterialState==null||target.ContentId!=source.ContentId
                    ||target.MaterialState.MaterialKey!=source.MaterialState.MaterialKey)continue;
                ulong amount=Math.Min(source.MaterialState.Quantity,capacity-target.MaterialState.Quantity);
                if(amount==0)continue;
                target.MaterialState.Quantity+=amount;
                if(amount==source.MaterialState.Quantity){items.Remove(source.InstanceId);retired.Add(source);}
                else source.MaterialState.Quantity-=amount;
            }
        }
        var groups=new List<OrganizeGroup>();
        foreach(var entry in entries)
        {
            if(!items.ContainsKey(entry.InstanceId))continue;
            var group=groups.FirstOrDefault(g=>At(g.First)==At(entry));
            if(group==null){group=new(entry,content.RequireItem(entry.ContentId));groups.Add(group);}
            group.Items.Add(entry);
        }
        groups.Sort((a,b)=>
        {
            int type=Priority(a.Rules.Kind).CompareTo(Priority(b.Rules.Kind));if(type!=0)return type;
            int id=string.CompareOrdinal(a.First.ContentId,b.First.ContentId);if(id!=0)return id;
            int area=b.Rules.OccupiedCells.Length.CompareTo(a.Rules.OccupiedCells.Length);
            return area!=0?area:string.CompareOrdinal(a.First.InstanceId,b.First.InstanceId);
        });
        var occupied=new HashSet<(int X,int Y)>();
        foreach(var group in groups)
        {
            bool placed=false;int originalTurns=group.First.QuarterTurns;
            for(int y=0;y<bagRules.BackpackHeight&&!placed;y++)for(int x=0;x<bagRules.BackpackWidth&&!placed;x++)for(int turn=0;turn<4&&!placed;turn++)
            {
                int rotation=(originalTurns+turn)%4;
                int width=(rotation&1)==0?group.Rules.ShapeWidth:group.Rules.ShapeHeight,height=(rotation&1)==0?group.Rules.ShapeHeight:group.Rules.ShapeWidth;
                if(x+width>bagRules.BackpackWidth||y+height>bagRules.BackpackHeight)continue;
                var cells=group.Rules.OccupiedCells.Select(c=>Rotate(group.Rules,c,rotation)).ToArray();
                if(cells.Any(c=>occupied.Contains((x+c.X,y+c.Y))))continue;
                foreach(var c in cells)occupied.Add((x+c.X,y+c.Y));
                foreach(var item in group.Items)Put(item,new(1,bag.InstanceId,-1,x,y,rotation));
                placed=true;
            }
            if(!placed)throw Failure("BackpackFull");
        }
    }
    private static int Priority(int kind)=>kind switch{4=>0,5=>1,6=>2,3=>3,1=>4,_=>5};
    private static (int X,int Y) Rotate(ContentManifestEntry rules,ContentShapeCell cell,int turns)
    {
        int x=cell.X,y=cell.Y,width=rules.ShapeWidth,height=rules.ShapeHeight;
        for(int i=0;i<turns;i++){(x,y)=(height-1-y,x);(width,height)=(height,width);}
        return(x,y);
    }
    private sealed class OrganizeGroup(OnlineOwnedItemSnapshot first,ContentManifestEntry rules)
    {internal OnlineOwnedItemSnapshot First=first;internal ContentManifestEntry Rules=rules;internal List<OnlineOwnedItemSnapshot> Items=[];}
    private static Position At(OnlineOwnedItemSnapshot item)=>new(item.LocationKind,item.ParentInstanceId,item.Slot,item.X,item.Y,item.QuarterTurns);
    private static void Put(OnlineOwnedItemSnapshot item,Position target)
    {item.LocationKind=target.Kind;item.ParentInstanceId=target.Parent;item.Slot=target.Slot;item.X=target.X;item.Y=target.Y;item.QuarterTurns=target.Turns;}
    private static bool Id(string? text)=>text!=null&&Guid.TryParseExact(text,"N",out var value)&&value.ToString("N")==text;
    private static OnlineFailure Failure(string code)=>new(409,code);
    private readonly record struct Position(int Kind,string Parent,int Slot,int X,int Y,int Turns);
    private sealed class Receipt{public string RequestHash{get;set;}="";public string ResultJson{get;set;}="";}
}
