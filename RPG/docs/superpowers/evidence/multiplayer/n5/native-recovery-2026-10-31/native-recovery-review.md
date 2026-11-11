# N5 Native 恢复只读审查（冻结）

范围：stage-plan.md 和指定六文件；依据两层 AGENTS、RPG workflow、requesting-code-review。以 source-before.json 区分前阶段工作；没有运行测试/构建/服务/Editor/生成，没有修改仓库或 Git。

## 结论

在主控明确限定的同 Generation 两场景内，未发现确定阻塞。发现 1 项 P2，仅影响隔离 probe 跨 Generation 恢复识别与 replay；主控确认本轮不覆盖 Chunk 删除重建，将其留作下一段独立 RED/GREEN 修复。正式 Coordinator/Assembler 的实际安装、ACK 与 Generation 合同不受影响。问题已即时通知主控。

## P2：恢复 ACK 不能识别新 Generation 的低 Revision

位置：Scripts/Multiplayer/World/WorldReplicationNetworkChannel.cs:372–374。

当前要求 `Generation >= original.generation && Revision >= original.revision`。既有 WorldChunkCommitJournal.cs:177–182 在 Chunk 重建时令 Generation+1、Revision=1。因此故障 G1/R3 后，合法当前 Baseline G2/R1 虽已通过正式 ReceiveControl 并真实安装/ACK，RecoveryProbe 仍不设置 recoveryAcknowledged；后续显式 replay 被错误拒绝，无法完成这段恢复证据链。

最小建议：按 Generation 优先比较，即 `gen > oldGen || (gen == oldGen && rev >= oldRev)`，保持现有正式控制接受、同 Route/身份/订阅约束。新增两测试仅覆盖相同 Generation 的恢复，未覆盖此分支。主控明确本轮 Native 两场景保持同 Generation，不宣称 probe 支持跨 Generation；后续修复须独立行为 RED/GREEN，未擅自改源码。

## 已核对

- BeforeEncode 只改借用 message；Codec 编码的 finally 恢复 Base/Word，Owner/Peer 冻数组不被写入。MarkPageSent 仍使用真实编码字节及 framing，成功后才 Send/记录；故障报告统计实际已送页。
- missing-base 对选中 transfer 的初轮实际发送页统一 base-1；checksum 保持原 CRC，仅首页一 Word。关联正式 Resync 接受后 slot0 hold 三秒自动到期；另一 peer 与模拟不暂停。
- 默认未配置 ENV 时不建 RecoveryProbe/缓存、不轮询文件；只在 Native Development DS 实例化。replay 有单次限制，并核对当前身份、World、连接、有效订阅；不改正式 pending/游标。
- Coordinator 先校验真实身份/订阅/pending，再消耗控制预算；ResyncRequested 与 Accepted 分开，ACK 从真实 pending Header 记录到每 peer/每 Key 固定数组。旧控制无 pending 不会重开恢复。
- 新测试约束真实格数量、旧 Header/可读性、Checksum/MissingRevision、当前 Baseline、逐 peer ACK 和较低版本重放拒绝。Client 250ms Store 采样读取实际 Cells/数量；三秒窗口仍需主控实际保存拒绝后恢复前文件，代码本身不是 Native 结果。

## 证据与边界

只读看到落盘 RED 0/2、GREEN 2/0，XML 记录 full 423/0、PlayMode 2/0、Coordinator 31/0；这些是主控已有记录，我未执行或重跑，不代表当前构建/Native已完成。原六文件审查首尾 SHA256 均一致；Channel 冻结 SHA256：D0A9EE7320A3C301A90B5EA51403C15EBC4A8117E8086764F16A6380B4F0A141。

未评定：日志覆盖长提交、丢包/HOL、四人/持续/性能、远端平台、完整玩家路径、N4/N6；stage-plan 已明确留后段。未把前阶段 Interest 差异或保留的 P3/资源错误作为本轮问题。

当前无权限阻塞：首次普通只读进程遇到 split writable roots sandbox 初始化错误，require_escalated 的同类只读检查已成功。报告冻结；若源码改变，本结论仅适用于上述快照。
