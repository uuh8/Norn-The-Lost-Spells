
### 容器Owner投影接线细化（2026-11-01，沿用04:28实施授权）

上一数据片615/0、PlayMode11/0只证明入局数据/导入，尚未证明Owner通道或Native容器可玩。本段修复已证实的仅99项/仅杖卡阻断，不引入新的容器操作/资产规则。

1. Producer与只读Source按真实目录导出/安装装备包、包内物、工具、备用杖及其卡和容器组分；资源仍只给三把装备杖，资格按真实Spell计数。坏批次不替换旧Source，停止/重连不制造默认量。
2. 字节预算：正式目录是8×8包、备用杖3×1、程序最多32。旧64KiB不能覆盖所有合法携带。按中立合同4096项、每项保守4608B（含32×128B alwayscast）、资格每项40B、资源/头部额外2048B估算，总19,040,256B，小于20MiB。这是完整快照技术硬预算，不扩大包格、不承诺未来地图/背包容量。实际编码使用CalculateSize，最大Traits与>64KiB用例输出真实字节；不能靠估算声称Native性能通过。
3. KCP可靠消息也有MTU×窗口/255片上限，完整20MiB不直接送Mirror。沿用现有owner-only可靠通道，Envelope拆16KiB页；4Actor固定槽，每Actor仅当前传输+最新待传各一份，按全局每帧最多4页轮转，更新替换待传但完成当前传输，防止连续施法使大包永远不能完成。不改战斗/材料优先级或系统网络参数。
4. 页包含Epoch、SnapshotSequence、总字节/页索引/页数和完整FNV32 checksum；不是密码认证。先验边界，再有界暂存，完整checksum+Proto/身份+Run目录校验后原子安装。部分页/坏高序号不能推进已安装水位；安装确认后才提交Inbox/Assembler序号。旧Epoch/旧页/重复冲突拒绝，退出清理固定槽和缓冲，无应用层无限重试。
5. Proto追加明确容器State/MaterialAmount，Quantity保持uint64；严格wire预检（包括canonical负Slot=-1）和所有新嵌套/unknown fields Reset。v5仍是本地尚未构建的当前兼容版本；必须统一生成/Hash/匹配Client与DS，不混用旧v4。
6. TDD顺序：Producer/Source已实际82/28 RED，Codec33/17 RED，Reset0/3 RED；分别最小GREEN，再做分片/原子安装/停止边界RED→GREEN、相关与N5回归、必要PlayMode和匹配Native验证。实际玩家容器选择/采集/倾倒和共享拾取/丢地仍是后续已批准操作段。

并行独立核查34旧失败：逐项证据分类；16目录错误先按原fixture复跑，已明确旧契约仅最小适配测试，不恢复/改写用户SO或Shader。ExpireDue先只读active=0，再独占窗口连续两轮原断言；未知active不清理，不放宽计数。Noita仅候选，Physics拒绝恢复不重试。
