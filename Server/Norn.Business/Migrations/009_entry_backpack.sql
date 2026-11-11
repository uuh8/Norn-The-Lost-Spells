-- 只新增可信的入场保底来源，不回填包、不改变注册资产、不转换旧位置。
-- 一角色最多一份尚存的保底身份，防并发/重试重复领取；当前没有卸包/回收入口。
-- Owner复合外键同时绑定原物品与原角色，不能把保底包的Items.Owner改给队友。
-- 不用ContentId或客户端字段判断免费身份，普通同款包不会因此被绑定。
CREATE TABLE EntryBackpackGrants (
  CharacterId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  InstanceId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  GrantedRunId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  GrantedAtUtc DATETIME(6) NOT NULL,
  UNIQUE KEY UQ_EntryBackpackGrants_Item (InstanceId),
  CONSTRAINT FK_EntryBackpackGrants_OwnedItem FOREIGN KEY (InstanceId,CharacterId) REFERENCES Items(InstanceId,OwnerCharacterId),
  CONSTRAINT FK_EntryBackpackGrants_Run FOREIGN KEY (GrantedRunId) REFERENCES Runs(RunId)
) ENGINE=InnoDB;
