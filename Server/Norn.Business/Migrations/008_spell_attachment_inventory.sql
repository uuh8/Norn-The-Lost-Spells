-- N5 法术卡的真实供料设置；只增加持久字段，不为旧Items回填，不发材料/容器，不承担N6回写。
-- InstanceId拥有设置，按Items外键维持唯一持有；ContainerId仅保留来源身份，故意没有来源FK。
-- 已失效/移出闭包的来源设置必须保留，实际扣料另由DS按当时的装备背包资格裁决。
CREATE TABLE ItemSpellAttachmentStates (
  InstanceId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  SchemaVersion INT UNSIGNED NOT NULL,
  ContainerId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  Dose BIGINT UNSIGNED NOT NULL,
  Enabled BOOLEAN NOT NULL,
  CONSTRAINT FK_ItemSpellAttachmentStates_Item FOREIGN KEY (InstanceId) REFERENCES Items(InstanceId),
  CONSTRAINT CK_ItemSpellAttachmentStates_Schema CHECK (SchemaVersion=1),
  CONSTRAINT CK_ItemSpellAttachmentStates_Source CHECK (REGEXP_LIKE(ContainerId,'^[0-9a-f]{32}$','c')),
  CONSTRAINT CK_ItemSpellAttachmentStates_Dose CHECK (Dose>0),
  CONSTRAINT CK_ItemSpellAttachmentStates_Enabled CHECK (Enabled IN (0,1))
) ENGINE=InnoDB;
