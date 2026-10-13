using System;
using Game.Core;

namespace Game.Run
{
    /// <summary>磁盘普通字段与权威库存之间的边界；完整验证成功才发布 Profile。</summary>
    public static class ProfileSaveMapper
    {
        public const int CurrentSchema = 12;
        public static SinglePlayerSaveDto ToDto(SinglePlayerProfile profile, long generation)
        {
            if (profile == null || generation < 0) throw new ArgumentException("不能保存非法 Profile。");
            if(!HubWorldItemRecord.IsValid(profile.HubWorldItems))throw new ArgumentException("地面显示/姿态记录非法，拒绝保存。");
            if (profile.PendingRaid == null && profile.Items.HasWorldTransit) throw new ArgumentException("据点快照不能保存无代理的在途材料。");
            if (profile.PendingRaid == null && profile.PendingMaterialOutputs > 0) throw new ArgumentException("附着材料尚在世界输出中，不能保存孤儿预算。");
            var dto = new SinglePlayerSaveDto
            {
                SchemaVersion = CurrentSchema, SaveGeneration = generation, ActorId = profile.ActorId,
                Currency = profile.Currency, InventoryVersion = profile.Items.Version,
                PendingRaid = profile.PendingRaid?.Copy(), LastRaidReceipt = profile.LastRaidReceipt,
                HasPendingRaid = profile.PendingRaid != null, HasLastRaidReceipt = profile.LastRaidReceipt != null,
                AdventureProgress = profile.AdventureProgress, Shop = profile.Shop?.Copy(),
                ToolBindings = (string[])profile.ToolBindings.Clone(),HubWorldItems=HubWorldItemRecord.CopyAll(profile.HubWorldItems)
            };
            var savedItems = new System.Collections.Generic.List<SinglePlayerItemDto>(profile.Items.Count);
            foreach (InventoryEntry entry in profile.Items)
            {
                // Raid日志只恢复资产裁决。已投出量属于即将结束的世界，不写成可恢复的满瓶。
                if (entry.Location.Kind == ItemLocationKind.ThrownContainer) continue;
                RuntimeItemState item = entry.Item; ItemLocation location = entry.Location;
                var saved = new SinglePlayerItemDto
                {
                    InstanceId = item.InstanceId, ContentId = item.ContentId, RemainingCharges = item.RemainingCharges,
                    HasUnlimitedCharges = item.HasUnlimitedCharges, StackStateKey = item.StackStateKey,
                    LocationKind = location.Kind, OwnerId = location.OwnerId, X = location.X, Y = location.Y,
                    QuarterTurns = location.QuarterTurns, SlotIndex = location.SlotIndex
                };
                if (item.Composition != null)
                {
                    var buffer = new MaterialAmount[8]; int count = item.Composition.CopyTo(buffer);
                    saved.Composition = new MaterialAmount[count]; Array.Copy(buffer, saved.Composition, count);
                    saved.ContainerCapacity = item.Composition.Capacity; saved.ContainerMaterials = item.Composition.AllowedMaterials;
                }
                if (item.Material != null) { saved.MaterialKey = item.Material.MaterialKey; saved.MaterialQuantity = item.Material.Quantity; }
                if (item.Attachment != null)
                { saved.AttachmentContainerId = item.Attachment.ContainerId; saved.AttachmentDose = item.Attachment.Dose; saved.AttachmentEnabled = item.Attachment.Enabled; }
                savedItems.Add(saved);
            }
            dto.Items = savedItems.ToArray();
            return dto;
        }

        public static ProfilePersistenceResult TryRestore(SinglePlayerSaveDto dto, ItemCatalogIndex catalog, out SinglePlayerProfile profile)
        {
            profile = null;
            if (dto == null || dto.SchemaVersion <= 0) return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "缺少存档 schema。");
            if (dto.SchemaVersion < 1 || dto.SchemaVersion > CurrentSchema) return new ProfilePersistenceResult(ProfilePersistenceStatus.UnsupportedSchema, "存档版本暂不支持；原档保留。");
            if (catalog == null || string.IsNullOrWhiteSpace(dto.ActorId) || dto.SaveGeneration < 0 || dto.Currency < 0 || dto.Items == null)
                return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "存档基础字段非法。");
            if(!HubWorldItemRecord.IsValid(dto.HubWorldItems) || dto.SchemaVersion<8 && dto.HubWorldItems!=null && dto.HubWorldItems.Length>0)
                return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData,"地面资产/生成记录非法，原档保留。");
            if (dto.AdventureProgress < 0 || dto.SchemaVersion < 5 && (dto.Shop != null && !string.IsNullOrEmpty(dto.Shop.ShopId) || dto.AdventureProgress != 0)
                || dto.Shop != null && !string.IsNullOrEmpty(dto.Shop.ShopId) && !dto.Shop.IsValid(dto.AdventureProgress, dto.SaveGeneration))
                return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "商店/经济进展记录非法，原档保留。");
            bool hasPending = dto.HasPendingRaid || dto.PendingRaid != null && !string.IsNullOrEmpty(dto.PendingRaid.RaidId);
            bool hasReceipt = dto.HasLastRaidReceipt || dto.LastRaidReceipt != null && !string.IsNullOrEmpty(dto.LastRaidReceipt.RaidId);
            if (dto.SchemaVersion < 4 && (hasPending || hasReceipt))
                return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "旧Schema不能携带Raid日志。");
            var entries = new InventoryEntry[dto.Items.Length];
            for (int i = 0; i < dto.Items.Length; i++)
            {
                SinglePlayerItemDto item = dto.Items[i];
                if (item == null) return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "存档包含空物品。");
                if (item.LocationKind == ItemLocationKind.ThrownContainer)
                    return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "据点存档含未结束投掷；原文件保留，不能恢复孤儿材料。");
                if (dto.SchemaVersion < 7 && item.LocationKind == ItemLocationKind.SpellStorage)
                    return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "旧版本不能携带法术专区位置，原档保留。");
                if ((item.LocationKind == ItemLocationKind.EquippedBackpack || item.LocationKind == ItemLocationKind.EquippedWand || item.LocationKind == ItemLocationKind.SpellStorage || item.LocationKind == ItemLocationKind.EquippedTool)
                    && !string.Equals(item.OwnerId, dto.ActorId, StringComparison.Ordinal))
                    return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "装备归属不属于本 Profile。");
                ContainerComposition composition = null; MaterialItemState material = null;
                if (!catalog.TryGetRules(item.ContentId, out var rules)) return new ProfilePersistenceResult(ProfilePersistenceStatus.UnknownContent, "材料物品配置未知。");
                if (dto.SchemaVersion >= 2 && (rules.Kind == ItemKind.Container
                    ? (item.ContainerCapacity != rules.ContainerCapacity
                        && !(dto.SchemaVersion <= 10 && (item.ContainerCapacity == 150 || item.ContainerCapacity == 300 || item.ContainerCapacity == 600)
                            && rules.ContainerCapacity == item.ContainerCapacity * 100)) || item.ContainerMaterials != rules.ContainerMaterials
                    : item.ContainerCapacity != 0 || item.ContainerMaterials != 0))
                    return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "容器固定容量/兼容配置与保存不匹配。");
                // JsonUtility 会把 null 数组写成 []；非容器的空数组只是“没有组分”。
                if (item.Composition != null && (item.Composition.Length > 0 || rules.Kind == ItemKind.Container && dto.SchemaVersion >= 2))
                {
                    if (dto.SchemaVersion == 1 || rules.Kind != ItemKind.Container || item.Composition.Length > 8)
                        return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "组分种类/schema 不匹配。");
                    composition = new ContainerComposition(rules.ContainerCapacity, rules.ContainerMaterials);
                    if (item.Composition.Length > 0)
                    {
                        for (int j = 0; j < item.Composition.Length; j++) if (item.Composition[j].Quantity == 0)
                            return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "零量不能作为已保存组分。");
                        if (!composition.CanAdd(item.Composition, item.Composition.Length))
                            return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "组分重复、未知或超容。");
                        composition.AddReserved(item.Composition, item.Composition.Length);
                    }
                }
                if (item.MaterialKey != 0 || item.MaterialQuantity != 0)
                {
                    if (dto.SchemaVersion == 1 || rules.Kind != ItemKind.Material || item.MaterialKey != rules.MaterialKey
                        || item.MaterialQuantity == 0 || item.MaterialQuantity > rules.MaterialItemCapacity)
                        return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "材料键/GMU 非法。");
                    material = new MaterialItemState(item.MaterialKey, item.MaterialQuantity);
                }
                SpellAttachmentState attachment = null;
                if (!string.IsNullOrEmpty(item.AttachmentContainerId) || item.AttachmentDose != 0 || item.AttachmentEnabled)
                {
                    if (dto.SchemaVersion < 3 || rules.Kind != ItemKind.Spell || string.IsNullOrWhiteSpace(item.AttachmentContainerId) || item.AttachmentDose == 0)
                        return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "附着配置/schema非法，原档保留。");
                    attachment = new SpellAttachmentState(item.AttachmentContainerId, item.AttachmentDose, item.AttachmentEnabled);
                }
                entries[i] = new InventoryEntry(new RuntimeItemState(item.InstanceId, item.ContentId,
                    item.RemainingCharges, item.HasUnlimitedCharges, item.StackStateKey, composition, material, attachment),
                    new ItemLocation(item.LocationKind, item.OwnerId, item.X, item.Y, item.QuarterTurns, item.SlotIndex));
            }
            if (!ItemOwnershipStore.TryRestore(catalog, entries, dto.InventoryVersion, out ItemOwnershipStore items, out InventoryFailureReason reason)
                && !(dto.SchemaVersion<=9 && LegacyContainerLayoutMigration.TryRestore(catalog,entries,dto.InventoryVersion,out items))
                && !(dto.SchemaVersion<=11 && (reason==InventoryFailureReason.Occupied || reason==InventoryFailureReason.OutOfBounds)
                    && LegacyWandLayoutMigration.TryRestore(catalog,entries,dto.InventoryVersion,dto.SchemaVersion,out items)))
                return new ProfilePersistenceResult(reason == InventoryFailureReason.UnknownContent ? ProfilePersistenceStatus.UnknownContent
                    : ProfilePersistenceStatus.InvalidData, "库存恢复被拒绝：" + reason);
            profile = new SinglePlayerProfile(dto.ActorId, items, dto.SaveGeneration, dto.Currency);
            profile.AdventureProgress = dto.AdventureProgress;
            profile.HubWorldItems=new System.Collections.Generic.List<HubWorldItemRecord>(HubWorldItemRecord.CopyAll(dto.HubWorldItems??Array.Empty<HubWorldItemRecord>()));
            profile.Shop = dto.Shop != null && !string.IsNullOrEmpty(dto.Shop.ShopId) ? dto.Shop.Copy() : null;
            profile.ToolBindings = dto.SchemaVersion >= 6 ? dto.ToolBindings : null;
            if (dto.SchemaVersion == 6 && dto.ToolBindings != null && dto.ToolBindings.Length > 6)
            {
                // 旧版本最多六位；不能把损坏旧档的第七个字段变成新增资产使用权限。
                profile.ToolBindings = new string[6]; Array.Copy(dto.ToolBindings, profile.ToolBindings, 6);
            }
            ToolbeltService.NormalizeProfile(profile);
            if (hasPending)
            {
                var p = dto.PendingRaid;
                if (p == null || string.IsNullOrWhiteSpace(p.RaidId) || string.IsNullOrWhiteSpace(p.HubActorId) || p.RaidActorId != dto.ActorId
                    || (p.Phase != Game.Core.RaidPhase.Departing && p.Phase != Game.Core.RaidPhase.Exploring)
                    || !Unique(p.ProtectedIds, 2) || !Unique(p.HubAssetIds, int.MaxValue) || !Unique(p.QualifiedIds, int.MaxValue)
                    || p.Rollback == null || p.Rollback.ActorId != p.HubActorId
                    || !TryRestoreRollback(p.Rollback,dto.SchemaVersion,catalog,out var original).Success)
                { profile = null; return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "PendingRaid非法或缺少可靠回滚快照。"); }
                for (int i = 0; i < p.ProtectedIds.Length; i++)
                    if (!original.Items.TryGet(p.ProtectedIds[i], out var protectedItem)
                        || !catalog.TryGetRules(protectedItem.ContentId, out var protectedRules) || protectedRules.Kind != ItemKind.Spell
                        || !RaidProfileOps.IsCarried(original, p.ProtectedIds[i]))
                    { profile = null; return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "保护对象不属于出发前携带副本。"); }
                for (int i = 0; i < p.HubAssetIds.Length; i++)
                    if (!original.Items.TryGet(p.HubAssetIds[i], out _) || !items.TryGet(p.HubAssetIds[i], out _)
                        || RaidProfileOps.IsCarried(original, p.HubAssetIds[i]))
                    { profile = null; return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "据点未带出记录非法。"); }
                profile.PendingRaid = p.Copy();
                if(dto.SchemaVersion<=11 && !KeepMigratedRaidAssets(entries,p,profile,original))
                {profile=null;return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData,"旧远征空间迁移缺少可靠的出发快照，拒绝提交候选。");}
                // 回滚快照也使用已拆开的候选，否则下一次保存会把旧同格瓶标成新版合法布局。
                profile.PendingRaid.Rollback=ProfileAssetSnapshot.From(ToDto(original,original.SaveGeneration));
            }
            if (hasReceipt)
            {
                var r = dto.LastRaidReceipt;
                if (r == null || string.IsNullOrWhiteSpace(r.RaidId) || r.Outcome > Game.Core.RaidOutcome.Crash || r.Generation <= 0 || r.Generation > dto.SaveGeneration
                    || r.Returned < 0 || r.Lost < 0 || hasPending && dto.PendingRaid.RaidId == r.RaidId)
                { profile = null; return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "结算收据非法。"); }
                profile.LastRaidReceipt = r;
            }
            return new ProfilePersistenceResult(ProfilePersistenceStatus.Success);
        }
        private static bool KeepMigratedRaidAssets(InventoryEntry[] before,PendingRaidRecord source,SinglePlayerProfile current,SinglePlayerProfile original)
        {
            var overflow=new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            for(int i=0;i<before.Length;i++)
                if(before[i].Location.Kind==ItemLocationKind.Backpack && current.Items.TryGetLocation(before[i].Item.InstanceId,out var location)
                    && location.Kind==ItemLocationKind.Warehouse)overflow.Add(before[i].Item.InstanceId);
            foreach(var item in source.Rollback.Items)
                if(item.LocationKind==ItemLocationKind.Backpack && original.Items.TryGetLocation(item.InstanceId,out var location)
                    && location.Kind==ItemLocationKind.Warehouse)overflow.Add(item.InstanceId);
            if(overflow.Count==0)return true;
            var kept=new System.Collections.Generic.List<string>(current.PendingRaid.HubAssetIds);
            foreach(string id in overflow)
            {
                // 自动转仓不是玩家在副本丢弃。仅用可靠的出发实例建立据点保留名单，
                // 同时统一回滚/当前归属，避免成功结算把迁移资产当作未携带掉落删除。
                // 没有出发来源的旧副本新物品不能伪造为据点原资产，宁可拒绝候选。
                if(!original.Items.TryMove(id,ItemLocation.Warehouse).Success || !current.Items.TryMove(id,ItemLocation.Warehouse).Success)return false;
                if(!kept.Contains(id))kept.Add(id);
            }
            current.PendingRaid.HubAssetIds=kept.ToArray();
            ToolbeltService.NormalizeProfile(original);ToolbeltService.NormalizeProfile(current);return true;
        }
        private static ProfilePersistenceResult TryRestoreRollback(ProfileAssetSnapshot snapshot,int schema,ItemCatalogIndex catalog,out SinglePlayerProfile profile)
        {
            var dto=snapshot.ToDto();dto.SchemaVersion=schema;
            return TryRestore(dto,catalog,out profile);
        }
        private static bool Unique(string[] ids, int maximum)
        {
            if (ids == null || ids.Length > maximum) return false;
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < ids.Length; i++) if (string.IsNullOrWhiteSpace(ids[i]) || !seen.Add(ids[i])) return false;
            return true;
        }
    }
}
