-- 个人ESC可靠结算；只增量增加载体/约束，不导入本地档、不发放资产、不改旧迁移Hash。
-- DDL隐式提交，本轮仅隔离测试库验证；主库部署须另行批准和备份。
ALTER TABLE RunMembers DROP CHECK RunMembers_state_n2,
  ADD CONSTRAINT RunMembers_state_exit CHECK (State IN ('Reserved','Admitted','RolledBack','Exited'));
-- 有可信Loadout行+NULL表示真实空手；无行仍是旧未知状态，不补默认法杖。
ALTER TABLE CharacterCombatLoadouts MODIFY SelectedWandInstanceId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NULL;
-- 原已批准快捷法术栏是玩家根Location7/12格；保留006其余全部分支，仅补其持久位置。
ALTER TABLE ItemLocations DROP CHECK CK_ItemLocations_OwnedKind,
  ADD CONSTRAINT CK_ItemLocations_OwnedKind CHECK (
    (Area IS NOT NULL
      AND ((Area='Backpack' AND Slot>=0 AND Slot<8) OR (Area='Warehouse' AND Slot>=0 AND Slot<16))
      AND CombatLocationKind IS NULL AND ParentInstanceId IS NULL
      AND GridX=0 AND GridY=0 AND QuarterTurns=0)
    OR (Area IS NULL AND CombatLocationKind IS NOT NULL AND
      ((CombatLocationKind=1 AND ParentInstanceId IS NOT NULL AND ParentInstanceId<>InstanceId
         AND Slot=-1 AND GridX>=0 AND GridY>=0 AND QuarterTurns>=0 AND QuarterTurns<4)
       OR (CombatLocationKind=2 AND ParentInstanceId IS NOT NULL AND ParentInstanceId<>InstanceId
         AND Slot>=0 AND Slot<32 AND GridX=0 AND GridY=0 AND QuarterTurns=0)
       OR (CombatLocationKind=3 AND ParentInstanceId IS NULL AND Slot>=0 AND Slot<3
         AND GridX=0 AND GridY=0 AND QuarterTurns=0)
       OR (CombatLocationKind=4 AND ParentInstanceId IS NULL AND Slot=0
         AND GridX=0 AND GridY=0 AND QuarterTurns=0)
       OR (CombatLocationKind=7 AND ParentInstanceId IS NULL AND Slot>=0 AND Slot<12
         AND GridX=0 AND GridY=0 AND QuarterTurns=0)
       OR (CombatLocationKind=8 AND ParentInstanceId IS NULL AND Slot>=0 AND Slot<7
         AND GridX=0 AND GridY=0 AND QuarterTurns=0)))
  );

CREATE TABLE RunMemberExitReceipts (
  SettlementId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  RunId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  CharacterId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  InstanceId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  RoomEpoch INT UNSIGNED NOT NULL,
  RequestHash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  RequestJson LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
  ResultJson JSON NOT NULL,
  CommittedAtUtc DATETIME(6) NOT NULL,
  UNIQUE KEY UQ_RunMemberExit_Member (RunId,CharacterId),
  CONSTRAINT FK_RunMemberExit_Run FOREIGN KEY (RunId) REFERENCES Runs(RunId),
  CONSTRAINT FK_RunMemberExit_Character FOREIGN KEY (CharacterId) REFERENCES Characters(CharacterId),
  CONSTRAINT FK_RunMemberExit_Instance FOREIGN KEY (InstanceId) REFERENCES ServerInstances(InstanceId),
  CONSTRAINT CK_RunMemberExit_Json CHECK (JSON_VALID(RequestJson) AND OCTET_LENGTH(RequestJson)>0 AND OCTET_LENGTH(RequestJson)<=67108864),
  CONSTRAINT CK_RunMemberExit_Epoch CHECK (RoomEpoch>0)
) ENGINE=InnoDB;

-- 每Run保留同一真实实例的历史，只有Active行互斥；不能让下局覆盖旧局已结算栅栏。
CREATE TABLE RunItemCustody (
  RunId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  InstanceId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  ContentId VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  OriginalOwnerCharacterId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NULL,
  State VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  SettlementId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NULL,
  FinalOwnerCharacterId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NULL,
  Revision INT UNSIGNED NOT NULL,
  ActiveItemId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin GENERATED ALWAYS AS
    (CASE WHEN State='Active' THEN InstanceId ELSE NULL END) STORED,
  PRIMARY KEY (RunId,InstanceId),
  UNIQUE KEY UQ_RunItemCustody_Active (ActiveItemId),
  INDEX IX_RunItemCustody_Item (InstanceId),
  CONSTRAINT FK_RunItemCustody_Run FOREIGN KEY (RunId) REFERENCES Runs(RunId),
  CONSTRAINT FK_RunItemCustody_OriginalOwner FOREIGN KEY (OriginalOwnerCharacterId) REFERENCES Characters(CharacterId),
  CONSTRAINT FK_RunItemCustody_FinalOwner FOREIGN KEY (FinalOwnerCharacterId) REFERENCES Characters(CharacterId),
  CONSTRAINT FK_RunItemCustody_Receipt FOREIGN KEY (SettlementId) REFERENCES RunMemberExitReceipts(SettlementId),
  CONSTRAINT CK_RunItemCustody_State CHECK (State IN ('Active','Settled','Retired') AND Revision>0),
  CONSTRAINT CK_RunItemCustody_Settled CHECK
    ((State='Settled' AND SettlementId IS NOT NULL AND FinalOwnerCharacterId IS NOT NULL)
     OR (State<>'Settled' AND SettlementId IS NULL AND FinalOwnerCharacterId IS NULL))
) ENGINE=InnoDB;

-- Items自身全局ID仍唯一；检查点身份以副本+原ID留历史，允许已持久物品下局沿ID复用。
ALTER TABLE RunAssetCheckpointIdentities DROP PRIMARY KEY,
  ADD PRIMARY KEY (RunId,InstanceId),
  ADD INDEX IX_RunAssetCheckpointIdentity_Item (InstanceId);
