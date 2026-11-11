-- N5 固体持有状态：只增加真实数量的存储能力，不导入单机档，不为旧Items回填或发放材料。
-- ContentId的Kind/稳定Key/容量由可信Manifest读取门验证；这里保留精确unsigned GMU与唯一实例FK。
CREATE TABLE ItemMaterialStates (
  InstanceId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  SchemaVersion INT UNSIGNED NOT NULL,
  MaterialKey TINYINT UNSIGNED NOT NULL,
  Quantity BIGINT UNSIGNED NOT NULL,
  CONSTRAINT FK_ItemMaterialStates_Item FOREIGN KEY (InstanceId) REFERENCES Items(InstanceId),
  CONSTRAINT CK_ItemMaterialStates_Schema CHECK (SchemaVersion=1),
  CONSTRAINT CK_ItemMaterialStates_Key CHECK (MaterialKey>=1 AND MaterialKey<=8),
  CONSTRAINT CK_ItemMaterialStates_Quantity CHECK (Quantity>0)
) ENGINE=InnoDB;
