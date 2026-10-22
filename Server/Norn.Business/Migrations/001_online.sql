-- N1 只建立账号/线上资产；不把单机 JSON 导入，不创建还未实现的房间/结算表。
CREATE TABLE IF NOT EXISTS Accounts (
  AccountId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  Username VARCHAR(24) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  NormalizedUsername VARCHAR(24) CHARACTER SET ascii COLLATE ascii_bin NOT NULL UNIQUE,
  PasswordHash VARCHAR(512) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  CreatedAtUtc DATETIME(6) NOT NULL
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS Characters (
  CharacterId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  AccountId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL UNIQUE,
  Version INT UNSIGNED NOT NULL,
  SafeHub VARCHAR(64) NOT NULL,
  FOREIGN KEY (AccountId) REFERENCES Accounts(AccountId)
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS Wallets (
  CharacterId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  Balance BIGINT NOT NULL DEFAULT 0,
  FOREIGN KEY (CharacterId) REFERENCES Characters(CharacterId),
  CHECK (Balance >= 0)
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS Items (
  InstanceId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  OwnerCharacterId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  ContentId VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  Revision INT UNSIGNED NOT NULL,
  INDEX (OwnerCharacterId),
  UNIQUE (InstanceId, OwnerCharacterId),
  FOREIGN KEY (OwnerCharacterId) REFERENCES Characters(CharacterId)
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS ItemLocations (
  InstanceId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  OwnerCharacterId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  Area VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  Slot INT NOT NULL,
  UNIQUE (OwnerCharacterId, Area, Slot),
  FOREIGN KEY (InstanceId, OwnerCharacterId) REFERENCES Items(InstanceId, OwnerCharacterId),
  CHECK (Area IN ('Backpack','Warehouse')),
  CHECK ((Area='Backpack' AND Slot>=0 AND Slot<8) OR (Area='Warehouse' AND Slot>=0 AND Slot<16))
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS AuthSessions (
  TokenHash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  AccountId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  ExpiresAtUtc DATETIME(6) NOT NULL,
  INDEX (ExpiresAtUtc),
  INDEX (AccountId),
  FOREIGN KEY (AccountId) REFERENCES Accounts(AccountId)
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS InventoryCommands (
  CharacterId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  CommandId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  RequestHash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  ResultJson JSON NOT NULL,
  CommittedAtUtc DATETIME(6) NOT NULL,
  PRIMARY KEY (CharacterId,CommandId),
  FOREIGN KEY (CharacterId) REFERENCES Characters(CharacterId)
) ENGINE=InnoDB;
