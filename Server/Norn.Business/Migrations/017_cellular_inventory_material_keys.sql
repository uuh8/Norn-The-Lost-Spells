-- 账户库存的可携带材料沿用 Shared 的稳定编号。沙(11)、油(12)可随同一瓶入库；
-- 天然岩石(9)、泥土(10)属于地图，不能经库存表获得。保留原表的主键、外键与数量约束。
-- 每个 ALTER 原子替换同名 CHECK，迁移中断后重新执行也不会重复创建约束。
ALTER TABLE ItemContainerComponents
  DROP CHECK CK_ItemContainerComponents_Key,
  ADD CONSTRAINT CK_ItemContainerComponents_Key CHECK (MaterialKey IN (1,2,3,4,5,6,7,8,11,12));

ALTER TABLE ItemMaterialStates
  DROP CHECK CK_ItemMaterialStates_Key,
  ADD CONSTRAINT CK_ItemMaterialStates_Key CHECK (MaterialKey IN (1,2,3,4,5,6,7,8,11,12));
