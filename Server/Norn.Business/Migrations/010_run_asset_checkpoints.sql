-- 独立Run资产检查点Journal：只建新表，不回填或修改Items/Wallets/RunMembers/AssetLeases。
-- 现阶段保存不等于退出结算；后续接管原ID必须有明确持久交接，不能靠旧EntrySnapshot重授。
CREATE TABLE RunAssetCheckpointReceipts (
  RunId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  CheckpointId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  InstanceId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  RoomEpoch INT UNSIGNED NOT NULL,
  Sequence BIGINT UNSIGNED NOT NULL,
  PayloadHash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  RecordedAtUtc DATETIME(6) NOT NULL,
  PRIMARY KEY (RunId,CheckpointId),
  UNIQUE KEY UQ_RunAssetCheckpointReceipt_Sequence (RunId,Sequence),
  CONSTRAINT FK_RunAssetCheckpointReceipt_Run FOREIGN KEY (RunId) REFERENCES Runs(RunId),
  CONSTRAINT FK_RunAssetCheckpointReceipt_Instance FOREIGN KEY (InstanceId) REFERENCES ServerInstances(InstanceId),
  CONSTRAINT CK_RunAssetCheckpointReceipt_Sequence CHECK (Sequence>0)
) ENGINE=InnoDB;

CREATE TABLE RunAssetCheckpointHeads (
  RunId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  InstanceId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  RoomEpoch INT UNSIGNED NOT NULL,
  Sequence BIGINT UNSIGNED NOT NULL,
  CheckpointId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  PayloadHash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  PayloadJson JSON NOT NULL,
  PayloadBytes INT UNSIGNED NOT NULL,
  UpdatedAtUtc DATETIME(6) NOT NULL,
  CONSTRAINT FK_RunAssetCheckpointHead_Receipt FOREIGN KEY (RunId,CheckpointId)
    REFERENCES RunAssetCheckpointReceipts(RunId,CheckpointId),
  CONSTRAINT FK_RunAssetCheckpointHead_Instance FOREIGN KEY (InstanceId) REFERENCES ServerInstances(InstanceId),
  CONSTRAINT CK_RunAssetCheckpointHead_Sequence CHECK (Sequence>0),
  CONSTRAINT CK_RunAssetCheckpointHead_Bytes CHECK (PayloadBytes>0 AND PayloadBytes<=67108864)
) ENGINE=InnoDB;

-- 这是持久身份Journal，不是给客户端发物品的表。墓碑保留后，Items中已经删除的旧ID也不能伪装新物。
-- 单Run已登记身份总数另由Gate内32768硬预算限制；到上限显式拒绝，不能删墓碑腾空间。
CREATE TABLE RunAssetCheckpointIdentities (
  InstanceId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  RunId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  ContentId VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  Revision INT UNSIGNED NOT NULL,
  IsRetired BOOLEAN NOT NULL,
  OwnerPlayerId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NULL,
  LastSequence BIGINT UNSIGNED NOT NULL,
  INDEX IX_RunAssetCheckpointIdentity_Run (RunId),
  CONSTRAINT FK_RunAssetCheckpointIdentity_Run FOREIGN KEY (RunId) REFERENCES Runs(RunId),
  CONSTRAINT FK_RunAssetCheckpointIdentity_Owner FOREIGN KEY (OwnerPlayerId) REFERENCES Characters(CharacterId),
  CONSTRAINT CK_RunAssetCheckpointIdentity_State CHECK (IsRetired IN (0,1) AND Revision>0 AND LastSequence>0)
) ENGINE=InnoDB;
