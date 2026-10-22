-- 增量扩展状态；不改001/002。DDL隐式提交，失败必须审阅迁移状态后修复。
ALTER TABLE Parties DROP CHECK Parties_chk_1,
  ADD CONSTRAINT Parties_state_n2 CHECK (State IN ('Open','Starting','Running','Closed'));
ALTER TABLE Runs DROP CHECK Runs_chk_1,
  ADD CONSTRAINT Runs_state_n2 CHECK (State IN ('Starting','Running','Failed'));
ALTER TABLE RunMembers DROP CHECK RunMembers_chk_1,
  ADD COLUMN ConnectionEpoch INT UNSIGNED NOT NULL DEFAULT 0,
  ADD CONSTRAINT RunMembers_state_n2 CHECK (State IN ('Reserved','Admitted','RolledBack'));
CREATE TABLE IF NOT EXISTS ServerInstances (
  InstanceId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  RunId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL UNIQUE,
  RoomId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  RoomEpoch INT UNSIGNED NOT NULL,
  State VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  SecretHash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  UdpPort INT NOT NULL,
  ActivePort INT NULL UNIQUE, -- 释放时NULL，历史UdpPort保留，不永久占端口。
  ProcessId INT NULL, ProcessStartTicks BIGINT NULL,
  Executable VARCHAR(1024) NOT NULL DEFAULT '',
  ReadyDeadlineUtc DATETIME(6) NOT NULL,
  LeaseExpiresUtc DATETIME(6) NULL,
  LastHeartbeatUtc DATETIME(6) NULL,
  FailureCode VARCHAR(40) CHARACTER SET ascii COLLATE ascii_bin NULL,
  FOREIGN KEY (RunId) REFERENCES Runs(RunId), FOREIGN KEY (RoomId) REFERENCES Parties(RoomId),
  CHECK (State IN ('Launching','Ready','Failed')), CHECK (UdpPort>=1024 AND UdpPort<=65535)
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS JoinTickets (
  TicketHash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  InstanceId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  CharacterId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  RoomEpoch INT UNSIGNED NOT NULL,
  ProtocolVersion INT UNSIGNED NOT NULL,
  BuildVersion VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  ManifestHash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  ExpiresUtc DATETIME(6) NOT NULL, ConsumedUtc DATETIME(6) NULL,
  INDEX (InstanceId,CharacterId), INDEX (ExpiresUtc),
  FOREIGN KEY (InstanceId) REFERENCES ServerInstances(InstanceId), FOREIGN KEY (CharacterId) REFERENCES Characters(CharacterId)
) ENGINE=InnoDB;
