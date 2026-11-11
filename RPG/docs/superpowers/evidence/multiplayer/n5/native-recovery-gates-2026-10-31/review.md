# N5 Native 剩余恢复 Gate 只读审查（冻结）

依据：两层 AGENTS、RPG workflow、requesting-code-review，以及 native-recovery-gates-2026-10-31/stage-plan.md/source-before.json。范围为委派七文件；前阶段 Interest/Native 改动不重新归为本轮。没有修改仓库/Git，没有运行 Editor、测试、构建、服务或生成。

## 结论

未发现本冻结快照中确定的 P0/P1/P2，也未发现会阻止当前三类 Native 场景的明确源码问题。可以由主控继续实测；这不是 Native Gate 已完成或 N5 完成声明。

## 核对结果

1. **P2 为真实生产报告分支修复。** Channel.cs:420–440 外层仍只在正式 ReceiveControl 成功、当前 Context 和 Route 相符时进入。提取后的函数由该路径实际调用，按 Generation 优先比较；后续旧代或同代低 Revision 不能覆盖已记恢复 Header/时间，不改变 Coordinator/Assembler 正式 ACK 裁决。Tests:896–941 检查真实记录状态、Header 与时间，非空数学 helper。p2-red XML 两例失败均为合法新代 Revision1 的预期 True/实际 False。

2. **历史观测对应真实行为。** CommitOwner.cs:159–164 仅在真实 AppendHistory 增加 HistoryRecordsWritten；空 Capture 不计数。PeerState.cs:50–71 在真实 TryCopyDelta 失败处分支为 HistoryGapBaseline，之后复制当前 Baseline，不调用 RequireBaseline 伪造缺口。Coordinator:52–55 只暴露值。Channel:557–583 在每次新 transfer 记录前逐字段核对当前冻结 Header，再取 FreezeKind；保留每 peer/Key 最新项，最多 2×128，不积累无限日志。

3. **hold 与原发送语义保持。** 初始45秒、历史45秒、末页3秒均有到期自动释放；extended 总截止240秒。末页在编码/Mark前 hold 一次；BeginRound 原2秒同快照重发继续运行，uniquePages 与 sendEvents 分开。所有正常页仍经 Codec、原字节/轮次预算、Mark、Mirror；诊断不写冻结数组或 CRC。历史 hold 在 TryPrepare/TryFreeze 前跳过 A，B继续；主控须按计划先确认目标全 ACK/无 pending。

4. **真实材料 API 已查。** MaterialVolumeCell.Quantity 是 uint；FixedSolid 与 World 使用相同 Origin/CellSize/ChunkSize（MaterialPhases.cs:15–16），其 Source 也导出同一原生几何。夹具经 TryEnqueueWrite 投放 Ice，quantity-1 与 clear 经 FixedSolid.TryRemove 的数量/拓扑/Collider 预检及回滚，不手写 Store/网络版本。clear 先保存值副本再删，避免压缩索引漏格；要求 PendingDepositCount=0，目标排除原点。Bootstrap.cs:518–522 即使 DebugPause 仍每三 Authority tick 正式 Capture，因此暂停中的真实数量变化可进入历史 ring。

5. **跨代与旧页关联。** 缓存的是 initial 同 Key/World/Connection/Subscription 的真实 History 恢复首页；新代 ACK 须经过正式路径且 Generation 严格增加。显式 replay 仅一次，复核当前身份/有效订阅，不推进正式游标、pending 或制造 ACK。Core 完整闭环约束两非原点六页块、真实 ring 覆盖后自然 Baseline、逐 Key ACK、tombstone 后 G+1/R1、旧页/控制不回灌。

6. **默认关闭及边界。** 无 Recovery ENV 不创建 probe/缓存或轮询文件；实际实例化仅 Native Development DS。材料扩展仅既有显式夹具的 recovery-extended 序列。新增正常路径工作为有界标量观测；DS 报告复用稀疏写入。没有新增 Schema、容量淘汰、单机参数或玩家资产规则。

## 证据与限制

只读看到保存的 P2 RED0/2、合同 RED0/2、GREEN4/0，后续 XML 为427/0及 Play2/0；并未执行或重跑这些验证，也未据此宣称当前构建/Native完成。两 Core 用例的合同 Source 使用 Liquid，不能代替 Native Solid 物理夹具；实际两 Key 各≥90 cells、Pending归零、ring追加数、HistoryGapBaseline、旧Store窗口、tombstone/新代及逐端量/ACK仍由主控实测归档。代码自身有目标规模检查和100材料Tick失败边界，不把候选8192GMU配置当作成功。

未评定：四人/持续/性能/HOL、远端、完整键盘路径、P8、N4/N6；计划已留后段。原P3、DS资源错误不归本轮修复。不自行扩展材料记账/跨模块规则。

七文件审查首尾 SHA256 均未变；Channel 冻结 SHA256：D0378DEA7EEE2ECD7EF1AED959BDF6B2F13BA025F662180B43629DE98BCB5283。无当前权限阻塞。源码后续改变则须重新核对相关差异。
