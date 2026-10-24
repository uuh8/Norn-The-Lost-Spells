-- N4 只追加 Schema。Area 保留 N1 的平面整备语义；NULL 才交给新位置编号，历史 CHECK/唯一键不重写。
ALTER TABLE Items
  ADD COLUMN RemainingCharges INT NOT NULL DEFAULT 0,
  ADD COLUMN HasUnlimitedCharges BOOLEAN NOT NULL DEFAULT FALSE,
  ADD CONSTRAINT CK_Items_Charges CHECK (RemainingCharges >= 0);

ALTER TABLE ItemLocations
  MODIFY COLUMN Area VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NULL,
  ADD COLUMN CombatLocationKind TINYINT UNSIGNED NULL,
  ADD COLUMN ParentInstanceId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NULL,
  ADD COLUMN LocationKind TINYINT UNSIGNED GENERATED ALWAYS AS
    (CASE WHEN Area='Warehouse' THEN 0 WHEN Area='Backpack' THEN 1 ELSE CombatLocationKind END) STORED,
  ADD COLUMN ParentScope CHAR(32) CHARACTER SET ascii COLLATE ascii_bin GENERATED ALWAYS AS (COALESCE(ParentInstanceId,'')) STORED,
  ADD UNIQUE KEY UQ_ItemLocations_OwnedScope (OwnerCharacterId,LocationKind,ParentScope,Slot),
  ADD CONSTRAINT FK_ItemLocations_OwnedParent FOREIGN KEY (ParentInstanceId,OwnerCharacterId) REFERENCES Items(InstanceId,OwnerCharacterId),
  ADD CONSTRAINT CK_ItemLocations_OwnedKind CHECK (
    (Area IS NOT NULL AND CombatLocationKind IS NULL AND ParentInstanceId IS NULL)
    OR (Area IS NULL AND CombatLocationKind IS NOT NULL AND
      ((CombatLocationKind=3 AND ParentInstanceId IS NULL AND Slot>=0 AND Slot<3)
       OR (CombatLocationKind=2 AND ParentInstanceId IS NOT NULL AND ParentInstanceId<>InstanceId AND Slot>=0 AND Slot<64)))
  );

CREATE TABLE CharacterCombatLoadouts (
  CharacterId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  SelectedWandInstanceId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  FOREIGN KEY (CharacterId) REFERENCES Characters(CharacterId),
  FOREIGN KEY (SelectedWandInstanceId,CharacterId) REFERENCES Items(InstanceId,OwnerCharacterId)
) ENGINE=InnoDB;

-- 独立一次性业务授权审计。NeedsReview 不会在下一次登录或 DS 重连时自动变成装备。
CREATE TABLE OwnedCombatStarterAudits (
  CharacterId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  GrantVersion INT NOT NULL,
  Status VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  WandInstanceId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NULL,
  SpellInstanceId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NULL,
  BeforeJson JSON NOT NULL,
  RecordedAtUtc DATETIME(6) NOT NULL,
  PRIMARY KEY (CharacterId,GrantVersion),
  FOREIGN KEY (CharacterId) REFERENCES Characters(CharacterId),
  CHECK (GrantVersion=1 AND Status IN ('Applied','NeedsReview'))
) ENGINE=InnoDB;

-- 只迁移 N1 原始 Starter 的原 ID/原位置/原 Revision；玩家搬动过、已有装备或活动 Lease 都必须人工处理。
-- DDL 在 MySQL 隐式提交；以下 DML 单独事务，不能宣称整份脚本可回滚。失败后按 SchemaMigrations/实际列审计恢复。
START TRANSACTION;
SELECT GateId FROM RoomMutationGate WHERE GateId=1 FOR UPDATE;
SELECT CharacterId FROM Characters ORDER BY CharacterId FOR UPDATE;
INSERT INTO OwnedCombatStarterAudits(CharacterId,GrantVersion,Status,WandInstanceId,SpellInstanceId,BeforeJson,RecordedAtUtc)
SELECT c.CharacterId,1,
  IF(w.InstanceId IS NOT NULL AND s.InstanceId IS NOT NULL AND lease.CharacterId IS NULL AND c.Version<4294967295,'Applied','NeedsReview'),
  w.InstanceId,s.InstanceId,
  JSON_OBJECT('Reason','N4LegacyStarterV1','InventoryVersion',c.Version,'HadActiveLease',lease.CharacterId IS NOT NULL,
    'Items',(SELECT JSON_ARRAYAGG(JSON_OBJECT('InstanceId',olditem.InstanceId,'ContentId',olditem.ContentId,'Revision',olditem.Revision,'Area',oldlocation.Area,'Slot',oldlocation.Slot))
      FROM Items olditem LEFT JOIN ItemLocations oldlocation ON oldlocation.InstanceId=olditem.InstanceId WHERE olditem.OwnerCharacterId=c.CharacterId)),UTC_TIMESTAMP(6)
FROM Characters c
LEFT JOIN Items w ON w.OwnerCharacterId=c.CharacterId AND w.ContentId='wandtest.wand.a' AND w.Revision=1
  AND EXISTS(SELECT 1 FROM ItemLocations x WHERE x.InstanceId=w.InstanceId AND x.Area='Backpack' AND x.Slot=0)
LEFT JOIN ItemLocations wl ON wl.InstanceId=w.InstanceId
LEFT JOIN Items s ON s.OwnerCharacterId=c.CharacterId AND s.ContentId='wandtest.fire' AND s.Revision=1
  AND EXISTS(SELECT 1 FROM ItemLocations x WHERE x.InstanceId=s.InstanceId AND x.Area='Backpack' AND x.Slot=1)
LEFT JOIN ItemLocations sl ON sl.InstanceId=s.InstanceId
LEFT JOIN AssetLeases lease ON lease.CharacterId=c.CharacterId;

UPDATE ItemLocations l JOIN OwnedCombatStarterAudits a ON a.WandInstanceId=l.InstanceId AND a.Status='Applied'
  SET l.Area=NULL,l.CombatLocationKind=3,l.ParentInstanceId=NULL,l.Slot=0;
UPDATE ItemLocations l JOIN OwnedCombatStarterAudits a ON a.SpellInstanceId=l.InstanceId AND a.Status='Applied'
  SET l.Area=NULL,l.CombatLocationKind=2,l.ParentInstanceId=a.WandInstanceId,l.Slot=0;
UPDATE Items i JOIN OwnedCombatStarterAudits a ON a.Status='Applied' AND (a.WandInstanceId=i.InstanceId OR a.SpellInstanceId=i.InstanceId)
  SET i.Revision=i.Revision+1,i.RemainingCharges=IF(a.SpellInstanceId=i.InstanceId,30,0),i.HasUnlimitedCharges=FALSE;
INSERT INTO CharacterCombatLoadouts(CharacterId,SelectedWandInstanceId)
  SELECT CharacterId,WandInstanceId FROM OwnedCombatStarterAudits WHERE Status='Applied';
UPDATE Characters c JOIN OwnedCombatStarterAudits a ON a.CharacterId=c.CharacterId AND a.Status='Applied' SET c.Version=c.Version+1;
COMMIT;
