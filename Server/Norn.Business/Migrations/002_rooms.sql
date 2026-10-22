-- N2增量迁移：不改001的Hash，不移动/复制已有物品；状态变化与审计记录由短Transaction提交。
CREATE TABLE IF NOT EXISTS RoomMutationGate (
  GateId INT PRIMARY KEY, CHECK (GateId=1)
) ENGINE=InnoDB;
INSERT IGNORE INTO RoomMutationGate VALUES (1);

CREATE TABLE IF NOT EXISTS Parties (
  RoomId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  DungeonId VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  LeaderCharacterId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  State VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  ReadyRevision INT UNSIGNED NOT NULL,
  RoomEpoch INT UNSIGNED NOT NULL DEFAULT 0,
  ActiveRunId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NULL,
  CreatedAtUtc DATETIME(6) NOT NULL,
  FOREIGN KEY (LeaderCharacterId) REFERENCES Characters(CharacterId),
  CHECK (State IN ('Open','Starting','Closed'))
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS PartyMembers (
  CharacterId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  RoomId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  Seat INT NOT NULL,
  IsReady BOOLEAN NOT NULL DEFAULT FALSE,
  ReadyInventoryVersion INT UNSIGNED NOT NULL DEFAULT 0,
  UNIQUE (RoomId,Seat),
  FOREIGN KEY (CharacterId) REFERENCES Characters(CharacterId),
  FOREIGN KEY (RoomId) REFERENCES Parties(RoomId),
  CHECK (Seat>=0 AND Seat<4)
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS Runs (
  RunId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  RoomId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  RoomEpoch INT UNSIGNED NOT NULL,
  StartRevision INT UNSIGNED NOT NULL,
  State VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  FailureCode VARCHAR(40) CHARACTER SET ascii COLLATE ascii_bin NULL,
  CreatedAtUtc DATETIME(6) NOT NULL,
  UNIQUE (RoomId,RoomEpoch),
  FOREIGN KEY (RoomId) REFERENCES Parties(RoomId),
  CHECK (State IN ('Starting','Failed'))
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS RunMembers (
  RunId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  CharacterId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  InventoryVersion INT UNSIGNED NOT NULL,
  EntrySnapshot JSON NOT NULL,
  State VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  PRIMARY KEY (RunId,CharacterId),
  FOREIGN KEY (RunId) REFERENCES Runs(RunId),
  FOREIGN KEY (CharacterId) REFERENCES Characters(CharacterId),
  CHECK (State IN ('Reserved','RolledBack'))
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS AssetLeases (
  CharacterId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  RunId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  RoomEpoch INT UNSIGNED NOT NULL,
  FOREIGN KEY (CharacterId) REFERENCES Characters(CharacterId),
  FOREIGN KEY (RunId) REFERENCES Runs(RunId)
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS RunLifecycleEvents (
  EventId BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  RunId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  RoomEpoch INT UNSIGNED NOT NULL,
  EventCode VARCHAR(40) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  RecordedAtUtc DATETIME(6) NOT NULL,
  FOREIGN KEY (RunId) REFERENCES Runs(RunId)
) ENGINE=InnoDB;
