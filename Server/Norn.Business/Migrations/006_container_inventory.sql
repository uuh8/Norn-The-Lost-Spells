-- 容器首数据切片只追加持久载体，不生成背包/容器，不搬动旧平面资产，也不提前执行N6结算。
-- MySQL DDL会隐式提交；失败后应核对SchemaMigrations和实际约束，不宣称整份迁移可事务回滚。
-- 001第二个匿名CHECK把Area和Slot一起判断：NULL AND FALSE仍是FALSE，真实Slot=-1不能沿用它。
-- CHECK名字大小写敏感，自动名又取自实际表名；从本Schema元数据取精确名字，避免猜Windows/Linux大小写。
-- 若旧约束缺失或不唯一，下面PREPARE/标量查询应显式失败，不跳过、也不移除任何不相干的CHECK。
SET @norn_container_legacy_check = (
  SELECT tc.CONSTRAINT_NAME FROM information_schema.TABLE_CONSTRAINTS tc
  JOIN information_schema.CHECK_CONSTRAINTS cc
    ON cc.CONSTRAINT_SCHEMA=tc.CONSTRAINT_SCHEMA
      AND CAST(cc.CONSTRAINT_NAME AS BINARY)=CAST(tc.CONSTRAINT_NAME AS BINARY)
  WHERE tc.CONSTRAINT_SCHEMA=DATABASE() AND LOWER(tc.TABLE_NAME)=LOWER('ItemLocations')
    AND tc.CONSTRAINT_TYPE='CHECK' AND LOWER(tc.CONSTRAINT_NAME)=CONCAT(LOWER(tc.TABLE_NAME),'_chk_2')
    -- 先去元数据对引号的反斜杠转义，再去字符集前缀/空白/括号/反引号；实际失败前缀已确认含_ascii\'...\'。
    -- 原列、常量和操作符顺序必须完整一致，不通过扩大匹配范围来跳过迁移失败。
    -- 不仅按匿名序号删约束；表被人工改过、表达式不符时保留原约束并使迁移显式失败。
    AND REGEXP_REPLACE(REGEXP_REPLACE(LOWER(REPLACE(cc.CHECK_CLAUSE,CHAR(92),'')), '_[[:alnum:]]+(?='')', ''), '[[:space:]`()]+', '')
      = 'area=''backpack''andslot>=0andslot<8orarea=''warehouse''andslot>=0andslot<16'
);
SET @norn_container_drop_legacy_sql = CONCAT(
  'ALTER TABLE `ItemLocations` DROP CHECK `', REPLACE(@norn_container_legacy_check,'`','``'), '`'
);
PREPARE norn_container_drop_legacy_stmt FROM @norn_container_drop_legacy_sql;
EXECUTE norn_container_drop_legacy_stmt;
DEALLOCATE PREPARE norn_container_drop_legacy_stmt;
SET @norn_container_legacy_check = NULL;
SET @norn_container_drop_legacy_sql = NULL;

ALTER TABLE ItemLocations
  ADD COLUMN GridX INT NOT NULL DEFAULT 0,
  ADD COLUMN GridY INT NOT NULL DEFAULT 0,
  ADD COLUMN QuarterTurns TINYINT UNSIGNED NOT NULL DEFAULT 0,
  DROP CHECK CK_ItemLocations_OwnedKind,
  DROP INDEX UQ_ItemLocations_OwnedScope,
  -- 网格实例都使用Slot=-1，不能再以同Parent/Slot唯一键误拒多个包内物品。
  -- NULL只解除真实网格的槽号唯一性；根装备和杖程序仍由原位置编号/父链/Slot约束。
  ADD COLUMN UniqueSlot INT GENERATED ALWAYS AS
    (CASE WHEN Area IS NULL AND CombatLocationKind=1 THEN NULL ELSE Slot END) STORED,
  ADD UNIQUE KEY UQ_ItemLocations_OwnedScope (OwnerCharacterId,LocationKind,ParentScope,UniqueSlot),
  ADD CONSTRAINT CK_ItemLocations_OwnedKind CHECK (
    (Area IS NOT NULL
      AND ((Area='Backpack' AND Slot>=0 AND Slot<8) OR (Area='Warehouse' AND Slot>=0 AND Slot<16))
      AND CombatLocationKind IS NULL AND ParentInstanceId IS NULL
      AND GridX=0 AND GridY=0 AND QuarterTurns=0)
    OR (Area IS NULL AND CombatLocationKind IS NOT NULL AND
      ((CombatLocationKind=1 AND ParentInstanceId IS NOT NULL AND ParentInstanceId<>InstanceId
         AND Slot=-1 AND GridX>=0 AND GridY>=0 AND QuarterTurns>=0 AND QuarterTurns<4)
       OR (CombatLocationKind=2 AND ParentInstanceId IS NOT NULL AND ParentInstanceId<>InstanceId
         AND Slot>=0 AND Slot<32 AND GridX=0 AND GridY=0 AND QuarterTurns=0)
       OR (CombatLocationKind=3 AND ParentInstanceId IS NULL AND Slot>=0 AND Slot<3
         AND GridX=0 AND GridY=0 AND QuarterTurns=0)
       OR (CombatLocationKind=4 AND ParentInstanceId IS NULL AND Slot=0
         AND GridX=0 AND GridY=0 AND QuarterTurns=0)
       OR (CombatLocationKind=8 AND ParentInstanceId IS NULL AND Slot>=0 AND Slot<7
         AND GridX=0 AND GridY=0 AND QuarterTurns=0)))
  );

-- 父外键FK_ItemLocations_OwnedParent沿用005的(ParentInstanceId,OwnerCharacterId)，
-- 保证真实父子属于同一持久Owner。父的Kind、是否已装备和真实形状重叠由业务完整候选验证。
-- 001第一个匿名Area枚举CHECK保持；被替换的旧Area/Slot边界已明确并入上面的命名CHECK。
-- 旧Backpack仍仅表示legacy平面槽位，本迁移没有为它制造真实背包或二维位置。

CREATE TABLE ItemContainerStates (
  InstanceId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  SchemaVersion INT UNSIGNED NOT NULL,
  CONSTRAINT FK_ItemContainerStates_Item FOREIGN KEY (InstanceId) REFERENCES Items(InstanceId),
  CONSTRAINT CK_ItemContainerStates_Schema CHECK (SchemaVersion=1)
) ENGINE=InnoDB;

-- State行存在而没有子行才是“明确空容器”；无State是缺失数据，不能被读取层补默认或赠材料。
-- 这里只约束中立数值与外键；非容器夹带State、容量/许可/mask等仍必须完整读出后在业务门拒绝。
CREATE TABLE ItemContainerComponents (
  InstanceId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  MaterialKey TINYINT UNSIGNED NOT NULL,
  Quantity BIGINT UNSIGNED NOT NULL,
  PRIMARY KEY (InstanceId,MaterialKey),
  CONSTRAINT FK_ItemContainerComponents_State FOREIGN KEY (InstanceId) REFERENCES ItemContainerStates(InstanceId),
  CONSTRAINT CK_ItemContainerComponents_Key CHECK (MaterialKey>=1 AND MaterialKey<=8),
  CONSTRAINT CK_ItemContainerComponents_Quantity CHECK (Quantity>0)
) ENGINE=InnoDB;
