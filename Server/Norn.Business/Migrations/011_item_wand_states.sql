-- 独立随机杖载体：旧物默认模式0，不重Roll、不回填状态、不改变Owner/位置/数量/金币。
-- 模式1是调用方创建新真实roll物品时显式写入的事实；缺状态必须拒绝，不能回退模板。
ALTER TABLE Items
  ADD COLUMN WandStatsMode TINYINT UNSIGNED NOT NULL DEFAULT 0,
  ADD CONSTRAINT CK_Items_WandStatsMode CHECK (WandStatsMode IN (0,1));

CREATE TABLE ItemWandStates (
  InstanceId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  OwnerCharacterId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  SchemaVersion INT UNSIGNED NOT NULL,
  -- 保存完整DTO的round-trip数值文本与有序trait。不用SQL FLOAT显示格式重新生成固定属性。
  PayloadJson LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
  StateHash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  INDEX IX_ItemWandStates_Owner (OwnerCharacterId),
  CONSTRAINT FK_ItemWandStates_OwnedItem FOREIGN KEY (InstanceId,OwnerCharacterId)
    REFERENCES Items(InstanceId,OwnerCharacterId),
  CONSTRAINT CK_ItemWandStates_Schema CHECK (SchemaVersion=1),
  CONSTRAINT CK_ItemWandStates_Json CHECK (JSON_VALID(PayloadJson) AND OCTET_LENGTH(PayloadJson)>0 AND OCTET_LENGTH(PayloadJson)<=4096),
  CONSTRAINT CK_ItemWandStates_Hash CHECK (REGEXP_LIKE(StateHash,'^[0-9a-f]{64}$','c'))
) ENGINE=InnoDB;
