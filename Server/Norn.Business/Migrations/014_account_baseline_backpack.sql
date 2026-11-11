-- 账号注册/历史首次整备也需要同一唯一保底身份；NULL明确表示AccountBaseline来源。
-- 历史非NULL的真实RunEntry来源、Owner/实例唯一约束保持，不回填、不新建Run、不补金币。
-- MySQL不接受同一ALTER中DROP/ADD同名外键；按顺序拆开，最终保留原名与原引用。
-- 任何中途失败必须核读列和外键状态，先恢复009原外键，再由迁移器重试；不改迁移登记绕过。
-- DDL有隐式提交；一旦出现NULL来源行，不可直接收紧为NOT NULL冒充无损回滚。
ALTER TABLE EntryBackpackGrants DROP FOREIGN KEY FK_EntryBackpackGrants_Run;
ALTER TABLE EntryBackpackGrants MODIFY COLUMN GrantedRunId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NULL;
ALTER TABLE EntryBackpackGrants ADD CONSTRAINT FK_EntryBackpackGrants_Run FOREIGN KEY (GrantedRunId) REFERENCES Runs(RunId);
