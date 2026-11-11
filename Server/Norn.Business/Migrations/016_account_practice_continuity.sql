-- 个人Home和平连续性：仅可信实例的最终事务写入，不导入旧本地档、不增加物品/金币。
CREATE TABLE AccountPracticeContinuity (
  CharacterId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  SessionScopeHash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  SourceRunId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  PayloadJson LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
  PayloadHash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  RecordedAtUtc DATETIME(6) NOT NULL,
  CONSTRAINT FK_AccountPracticeContinuity_Character FOREIGN KEY (CharacterId) REFERENCES Characters(CharacterId),
  CONSTRAINT FK_AccountPracticeContinuity_Run FOREIGN KEY (SourceRunId) REFERENCES Runs(RunId),
  CONSTRAINT CK_AccountPracticeContinuity_Payload CHECK (JSON_VALID(PayloadJson) AND OCTET_LENGTH(PayloadJson)>2 AND OCTET_LENGTH(PayloadJson)<=33554432)
) ENGINE=InnoDB;
-- WorldOwnerId保留原32hex载体；场景由原Pose键区分。013及原hub.map1历史保持，不搬动旧资产。
ALTER TABLE HubWorldItems DROP CHECK CK_HubWorldItems_Scene,
  ADD CONSTRAINT CK_HubWorldItems_PeacefulScene CHECK (SceneKey IN ('hub.map1','home.map1') AND WorldSceneKey=SceneKey);
