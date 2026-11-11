-- 原账号商店状态迁移：不导入本地档，不补发金币/物品，报价始终来自受控Manifest。
CREATE TABLE CharacterEconomyProgress (
  CharacterId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  AdventureProgress BIGINT NOT NULL DEFAULT 0,
  CONSTRAINT FK_CharacterEconomyProgress_Character FOREIGN KEY (CharacterId) REFERENCES Characters(CharacterId),
  CONSTRAINT CK_CharacterEconomyProgress_NonNegative CHECK (AdventureProgress>=0)
) ENGINE=InnoDB;
CREATE TABLE CharacterShopStates (
  CharacterId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  ShopId VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  Version BIGINT NOT NULL,
  ConfigHash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  RemainingBudget BIGINT NOT NULL,
  AppliedProgress BIGINT NOT NULL,
  StocksJson JSON NOT NULL,
  PRIMARY KEY (CharacterId,ShopId),
  CONSTRAINT FK_CharacterShopStates_Character FOREIGN KEY (CharacterId) REFERENCES Characters(CharacterId),
  CONSTRAINT CK_CharacterShopStates_Version CHECK (Version>0 AND RemainingBudget>=0 AND AppliedProgress>=0),
  CONSTRAINT CK_CharacterShopStates_Stocks CHECK (OCTET_LENGTH(StocksJson)>2 AND OCTET_LENGTH(StocksJson)<=65536)
) ENGINE=InnoDB;
-- 沿原唯一RunMember最终收据记进度已应用；没有明确成功事实时始终FALSE，普通ESC/死亡不会补货。
ALTER TABLE RunMemberExitReceipts ADD COLUMN ShopProgressApplied BOOLEAN NOT NULL DEFAULT FALSE;
-- 原Merged历史保持；售出/耗尽状态写入同一永久身份账本，保留Items和原完整状态供审计。
ALTER TABLE AccountItemRetirements DROP CHECK CK_AccountRetirement_State,
  ADD CONSTRAINT CK_AccountRetirement_TradeState CHECK (Revision>0 AND Reason IN ('Merged','Sold','Consumed'));
