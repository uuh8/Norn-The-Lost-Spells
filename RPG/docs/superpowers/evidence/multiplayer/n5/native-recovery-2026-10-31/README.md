# N5 v4 Native故障恢复证据

## 2026-10-31：v4 Native故障恢复最小两场景

### 1. 本段结果与合同

依据N5计划N5-2～N5-6和2026-10-31用户接续授权：坏数据保留旧数量/旧Revision，完整校验并原子安装后才ACK；缺Base有界Resync，控制关联当前真实pending。沿用已批准v4/独立Interest，不增协议、玩法、资产、地图重建语义或跨模块架构。此次两个全新P7隔离房间均经真实账户/Ready/Ticket/KCP和正式材料路径，A单次注入、B正常。**仅同Generation、暂停检查点，不是N5完成。**

| 场景 | 故障Base→Revision | 首次拒绝 | 当前Baseline Base→Revision | 重放拒绝 | A/B累计ACK前→后 |
|---|---|---|---|---|---|
| checksum | 3→4 | Checksum | 0→9 | StaleVersion | 4/4→6/12 |
| missing-base | 2→4 | MissingRevision | 0→9 | StaleVersion | 4/4→6/12 |

每次初始材料Tick5、恢复后Tick10；A/B真实Store液体数量512→1024。故障候选Revision4，拒绝时仍保留已安装Revision3和真实旧格；关联Resync后取当前Baseline Revision9。恢复/旧页重放后全部当前Key的WorldEpoch/Generation/Revision/CRC/Words/Resident与DS一致，A/B实际Current格逐项相同。表中累计数仅辅助，**准确归因来自DS每连接每Key接受的pending Header**：含真实ConnectionId/Epoch、SubscriptionEpoch、Key、Generation、Snapshot/Base/Revision、Tick、Checksum/Words，与Client安装Header逐字段核对。

`preserved-fresh-*`由独立只读观察器保存，Client和实际Store采样时间均晚于关联Resync、且恢复Baseline计数尚未增加；目标真实格与注入前一致，不能用旧采样或Header假装保旧。恢复后单次旧故障页被StaleVersion拒绝，实际格与已安装版本不回灌；产生的旧Resync未增加DS接受次数或ACK，未重开恢复。

### 2. 实现、目录与生命周期

- `Multiplayer/World/WorldReplicationCoordinator.cs`只加真实事件观测：Resync产生/接受、Baseline/Delta安装、拒绝原因/候选/保留Header、每连接4×128固定ACK记录和只读当前订阅检查。原身份/pending/预算/版本裁决不改；新连接重置对应观测槽。128 Key仍硬上限，不引淘汰或无限日志。稀疏Describe才分配值副本。
- 同目录`WorldReplicationNetworkChannel.cs`复用正式TryPrepare→Codec→Mark→Mirror。仅Native Development DS且`NORN_N5_RECOVERY_PROBE`显式绝对路径时创建诊断；默认不轮询文件/分配诊断缓存。checksum只翻首页一个Word且保留原CRC；missing-base对选中快照所有实际发送页统一Base−1。借用message在finally立即恢复，Owner/Peer冻结数组不写，成功Send后才计发送。只对当前认证slot0；正式Resync被接受后有界暂停发页3秒，B/模拟继续；恢复ACK后显式单次replay还须当前同连接/世界/订阅。无自动循环注入或无限重试。
- `Multiplayer/Diagnostics/RoomAdmissionProbe.cs`及`Multiplayer/Runtime/RoomInstanceBootstrap.cs`只扩既有稀疏报告，不新增玩家入口。`ElementField/Diagnostics/OnlineMaterialReplicationProbe.cs`复用现有文件，Client仅`NORN_N5_REPLICA_PROBE`显式开启时250ms采样真实Store.Current格/总量；不把ElementField引入Multiplayer asmdef。JSON/采样分配不参加Zero-GC结论。原DS正式ElementWriteRequest两次512与各五真实Tick检查点复用。
- `Tests/Multiplayer/WorldReplicationChannelTests.cs`新增两个真实Owner/Codec/Assembler/Store闭环，原29项Coordinator案例和断言逐字保留。校验坏Delta保旧、多页完整CRC拒绝、wire合法缺Base、Owner继续提交后当前Baseline、精确Resync与逐peer ACK、过期页/控制不制造恢复债务。无新源目录/Shared Schema/meta或资源导出。

### 3. 实际验证与失败证据

新增2项先0/2 RED（缺BaselineInstalled观测属性），补最小实现后2/0 GREEN；Coordinator31/0，最终同范围27fixtures **423/0**，正式P7/P8 PlayMode **2/0**。不是整项目Run All，也不是Native完整玩家验收。

业务首次 **99/1**，`OldServerEpoch_CannotRenewOrAdmit_AndLeaseExpiryReleasesAssets`原断言Expected1/Actual2。只读Test Schema查到前段自有Interest实例`94a4ccb6c04740569ebcf5c3447b44dd`与新测试实例都被既有全局ExpireDueAsync处理为LeaseExpired；测试后活动实例0。未手动清库、未改断言/业务语义，按相同流程重跑 **100/0**，真实HTTP v4接受/v1～v3拒绝及超长拒绝，固定8个生成文件一致性通过。保存initial TRX/log、SQL结果和分析；初始root option-file认证失败没有执行SQL，随后使用已验证Test账号完成只读核查并删除本次临时凭据文件。

匹配Windows x64 Development Mono Client构建 `2026-10-31T14:59:54.1286896Z`、真正Dedicated Server构建 `2026-10-31T15:03:52.1940201Z`，各0 error；不是业务Release冒充Unity Native。Hash刷新曾等待Shader编译，读取窗口/IO确认有效后继续，未重启Editor。两Native场景目标都是43 words的**单页**Delta，多页Native故障仍未测；当前多页拒绝只来自行为测试。两个DS各有既有两条Built-in Resource Error，完整日志与行号保留；本轮未阻断认证/恢复，但未修复，不作为图形正确性背书。

### 4. 审查、保护与接续

独立sol/xhigh只读审查未发现阻塞本轮同Generation场景的问题。新增诊断P2：recovery ACK识别用Generation>=旧值且Revision>=旧值，Chunk合法重建为更高Generation、Revision重置1时可能漏认；**仅探针，正式Coordinator/Assembler未受影响**。本段不扩跨Generation场景，后续依已有字典序版本合同做独立行为RED/GREEN最小修复。既有home/away错误锁存P3保留，本轮无位移请求所以不触发。相关审查见native-recovery-review.md。

并行编写时Channel不完整hook曾先落仓；自动审批因主控已暂停仓库写入拒绝撤回。主控新增“仅撤回自有不完整patch”明确授权后唯一重试成功，Hash精确回到本阶段起始值，再跑观测GREEN；完整暂存由主控统一落仓后才编译/回归/构建。没有绕过拒绝或在不确定源码上声明通过；现无权限/待决策阻塞。

另外保护检查发现ProjectSettings.asset仅runInBackground由0变1，写入时间与本任务DS构建完成相差不足2秒；只还原该字段后字节精确匹配阶段起始Hash与HEAD，保存projectsettings-background-observed.diff和projectsettings-background-restoration.json。未回滚其他设置。

12保护文件、13旧正式资源及已有网络Profile/Meta共15资产Hash保持；仅在当前Physics字节精确等于已证自动序列化副作用且起始Hash匹配HEAD时恢复本任务格式变化。用户Editor保留并回P7_MainMenu、非Play/Compiling/Dirty。仅按PID/启动时间/路径结束本任务两组Native/业务，认证关闭本任务13384 MySQL；保留无关Recovery及工作区，未提交推送。

后续先处理诊断P2并核查128条变更记录覆盖后的实际Peer Baseline回退；Native日志ring覆盖、四人/持续反应/HOL/有效Profiler与GC、完整键盘路径、同进程自动重连/消耗不补满、P8 Native恢复和N4/N6/远端Gate继续未完。Ring按变更记录数覆盖，不能把128 Tick或128 Key等同无限探索。炼金仅已确认计划记录，未实施。

Ruling：先交付同Generation单次故障闭环 — 依赖已有v4恢复合同 — 成本是跨代重建/多页Native/日志覆盖仍需后段。Ruling：默认关闭诊断只改编码借用输入并finally恢复 — 不更改权威材料与正常路径 — 成本是诊断采样/JSON不用于性能。Ruling：固定3秒单连接暂停 — 保存拒绝后新鲜实际Store — 成本是此段为稳定检查点，不能称持续压力。Ruling：测试全局过期多计历史自有实例 — 保留99/1并只读归因后原断言重跑 — 不清空/手改数据。
