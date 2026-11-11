-- 账号整理及私人据点地面；只新增事实表与兼容状态/位置分支，不回填、不改写001–012。
-- DDL有隐式提交，先在隔离库验证；产生退休/地面/Completed事实后不可删表假装回滚。
CREATE TABLE AccountItemRetirements (
  InstanceId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  OwnerCharacterId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  CommandId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  ContentId VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  Revision INT UNSIGNED NOT NULL,
  Reason VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  SnapshotJson JSON NOT NULL,
  RecordedAtUtc DATETIME(6) NOT NULL,
  INDEX IX_AccountRetirements_Owner (OwnerCharacterId),
  CONSTRAINT FK_AccountRetirement_Item FOREIGN KEY (InstanceId,OwnerCharacterId) REFERENCES Items(InstanceId,OwnerCharacterId),
  CONSTRAINT FK_AccountRetirement_Command FOREIGN KEY (OwnerCharacterId,CommandId) REFERENCES InventoryCommands(CharacterId,CommandId),
  CONSTRAINT CK_AccountRetirement_State CHECK (Revision>0 AND Reason='Merged')
) ENGINE=InnoDB;

CREATE TABLE HubWorldItems (
  InstanceId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  OwnerCharacterId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  WorldOwnerId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  SceneKey VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  WorldSceneKey VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  PoseJson JSON NOT NULL,
  DropCommandId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  RecordedAtUtc DATETIME(6) NOT NULL,
  UNIQUE KEY UQ_HubWorldItems_Spawn (WorldOwnerId),
  INDEX IX_HubWorldItems_OwnerScene (OwnerCharacterId,WorldSceneKey),
  CONSTRAINT FK_HubWorldItems_Item FOREIGN KEY (InstanceId,OwnerCharacterId) REFERENCES Items(InstanceId,OwnerCharacterId),
  CONSTRAINT FK_HubWorldItems_Command FOREIGN KEY (OwnerCharacterId,DropCommandId) REFERENCES InventoryCommands(CharacterId,CommandId),
  CONSTRAINT CK_HubWorldItems_Scene CHECK (SceneKey='hub.map1' AND WorldSceneKey='hub.map1'),
  CONSTRAINT CK_HubWorldItems_Pose CHECK (OCTET_LENGTH(PoseJson)>2 AND OCTET_LENGTH(PoseJson)<=4096)
) ENGINE=InnoDB;

-- 保留012全部位置分支。地面的内部Slot只解决既有唯一键，不是UI格/新增交互容量。
ALTER TABLE ItemLocations DROP CHECK CK_ItemLocations_OwnedKind,
  ADD CONSTRAINT CK_ItemLocations_OwnedKind CHECK (
    (Area IS NOT NULL
      AND ((Area='Backpack' AND Slot>=0 AND Slot<8) OR (Area='Warehouse' AND Slot>=0 AND Slot<16))
      AND CombatLocationKind IS NULL AND ParentInstanceId IS NULL AND GridX=0 AND GridY=0 AND QuarterTurns=0)
    OR (Area IS NULL AND CombatLocationKind IS NOT NULL AND
      ((CombatLocationKind=1 AND ParentInstanceId IS NOT NULL AND ParentInstanceId<>InstanceId
         AND Slot=-1 AND GridX>=0 AND GridY>=0 AND QuarterTurns>=0 AND QuarterTurns<4)
       OR (CombatLocationKind=2 AND ParentInstanceId IS NOT NULL AND ParentInstanceId<>InstanceId
         AND Slot>=0 AND Slot<32 AND GridX=0 AND GridY=0 AND QuarterTurns=0)
       OR (CombatLocationKind=3 AND ParentInstanceId IS NULL AND Slot>=0 AND Slot<3 AND GridX=0 AND GridY=0 AND QuarterTurns=0)
       OR (CombatLocationKind=4 AND ParentInstanceId IS NULL AND Slot=0 AND GridX=0 AND GridY=0 AND QuarterTurns=0)
       OR (CombatLocationKind=5 AND ParentInstanceId IS NULL AND Slot>=0 AND GridX=0 AND GridY=0 AND QuarterTurns=0)
       OR (CombatLocationKind=7 AND ParentInstanceId IS NULL AND Slot>=0 AND Slot<12 AND GridX=0 AND GridY=0 AND QuarterTurns=0)
       OR (CombatLocationKind=8 AND ParentInstanceId IS NULL AND Slot>=0 AND Slot<7 AND GridX=0 AND GridY=0 AND QuarterTurns=0)))
  );

ALTER TABLE Runs DROP CHECK Runs_state_n2,
  ADD CONSTRAINT Runs_state_complete CHECK (State IN ('Starting','Running','Failed','Completed'));

-- 003的State CHECK为匿名，按现有006同等严格元数据识别，绝不猜平台大小写或误删UDP范围约束。
SET @norn_instance_state_check = (
  SELECT tc.CONSTRAINT_NAME FROM information_schema.TABLE_CONSTRAINTS tc
  JOIN information_schema.CHECK_CONSTRAINTS cc
    ON cc.CONSTRAINT_SCHEMA=tc.CONSTRAINT_SCHEMA AND CAST(cc.CONSTRAINT_NAME AS BINARY)=CAST(tc.CONSTRAINT_NAME AS BINARY)
  WHERE tc.CONSTRAINT_SCHEMA=DATABASE() AND LOWER(tc.TABLE_NAME)=LOWER('ServerInstances') AND tc.CONSTRAINT_TYPE='CHECK'
    AND REGEXP_REPLACE(REGEXP_REPLACE(LOWER(REPLACE(cc.CHECK_CLAUSE,CHAR(92),'')), '_[[:alnum:]]+(?='')', ''), '[[:space:]`()]+', '')
      = 'statein''launching'',''ready'',''failed'''
);
SET @norn_instance_state_sql=CONCAT('ALTER TABLE `ServerInstances` DROP CHECK `',REPLACE(@norn_instance_state_check,'`','``'),
  '`, ADD CONSTRAINT `ServerInstances_state_complete` CHECK (State IN (''Launching'',''Ready'',''Failed'',''Completed''))');
PREPARE norn_instance_state_stmt FROM @norn_instance_state_sql;
EXECUTE norn_instance_state_stmt;
DEALLOCATE PREPARE norn_instance_state_stmt;
SET @norn_instance_state_check=NULL;
SET @norn_instance_state_sql=NULL;
