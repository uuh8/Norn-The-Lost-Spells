# 阶段账本 — plan: RPG/docs/superpowers/plans/2026-10-29 N5共享材料世界与分块恢复实施计划.md

## 2026-10-31：Interest合同复核与退出缓存版本栅栏

### 1. 目标、授权和本段范围

N5目标是让2–4名玩家共享有真实数量/反应的材料世界：DS唯一CPU模拟，Client只读显示，四人活动域与每连接Interest、冻结Baseline/Delta恢复、生成/收取数量事务。它不等于可靠入库结算（N6），也不把当前只读Source当成DS崩溃恢复存档。最终须实际验证P7/P8材料可见/接触/数量一致、四人持续操作与大基线重连/丢包下恢复、控制延迟和有效性能；握手或局部通道通过不足以结束N5。

本轮按既有N5-3与用户接续授权复核真实位置驱动的空间选择。已批准的是每连接Interest只控发送、离开释放客户端缓存、重入获取当前Baseline；总方案确认操作/关键反馈/附近优先。现有WorldInterestCoordinator接收半开AABB，未生成正式范围；P7/P8 Profile的2m块、XZ10/Y3活动窗口是已有配置，不能自行宣布为已批准联机半径。准确原文/行号见本轮contract-audit.md。

### 2. 必须等待的决策及影响

正式范围大小/形状/Chunk判交尚无确认。已提出“复用现有Profile窗口”与“单独设计联机范围”两方案，前者复用内容配置但需压力验证，后者可独立调参但增加配置与验证面；未选默认方案，不改玩家可见/交互规则。

现有WorldPage Kind仅Baseline/Delta/ACK/Resync/权威删除，没有Interest退出/重入标识。SnapshotId取CommitSequence，同版本重入可重复；不能当成Interest轮次，也不能拿权威删除、ConnectionEpoch或WorldEpoch表示空间退出。已询问是否先细化独立生命周期/恢复标识方案，或暂只验证既有组件；尚未实施任何新协议。静态核查还发现：仅Store.evict不会取消Assembler候选、Channel队列或阻止Coordinator接收合法旧Baseline，因此不能声称退出后迟到页已经被完整屏蔽。该通道层风险本轮未运行复现。

依赖以上选择的正式位置→Bounds→发送筛选→退出/重入接线及Native出入区验证暂停。等待不阻塞下面的既有Store合同验证，沉默不视为批准。

### 3. 实际修改、原因和控制流

- `Scripts/ElementField/Replication/WorldReplicaStore.cs`：仅调整同版本内容核对，保留Header/Generation/Revision事实。之前!Interested会跳过旧Header核对，导致释放后接受“相同版本、不同内容但自身CRC合法”的Baseline。现在无论是否感兴趣先核对Checksum/WordCount/Resident；一致的resident重入继续原子重装，已可见或权威tombstone保持原幂等返回。所有拒绝都在复制/版本提交前，不改协议、世界/连接代次或数量事务。
- `Tests/ElementField/WorldReplicaStoreTests.cs`：保留原13项，新增13项真实Store行为。四域覆盖释放后旧Generation/Revision拒绝、同版本冲突内容拒绝、数量/读模型/可见Key释放与重复释放幂等；另测权威删除后不能借Interest复活。断言材料数量、可见块与PresentationVersion，不用mock、反射空API或修改原断言。

生产和测试均复用现有目录，没有新源码目录、asmdef/Shared/资源/场景变更。客户端Replica仍不运行反应或授予资源；本段不接炼金。

### 4. 实际验证与Debug

2026-10-31：先运行同一Store fixture得到22 passed / 4 failed，失败全部是四域同版本冲突内容被接受（Expected false / Actual true）；保存store-red-report.json/XML后才修复。定向GREEN 26/0，最终原同范围26 fixtures **307 passed / 0 failed**。新增13项中4项真实RED→GREEN，其余9项本来通过，用来验证保留边界；不把它们伪称新增功能RED。这不是整项目Run All。

独立sol/xhigh只读审查及源码Hash已归档；结论按final-review.md。12个保护文件、13个正式派生资源/Profile保持起始Hash；测试引起的Physics自动序列化仅在起始Hash与HEAD字节完全匹配时恢复自身副作用。Editor返回原P7_MainMenu，未Play/编译/Dirty，无提交/推送。本轮没有启动Native、业务服或数据库，不关闭任何用户进程。

本段没有新PlayMode/Native/性能结果：上一段294/0、PlayMode2/0和P7双Native非零稳定基线是历史证据；本段Store修复未构建为Native。持续反应/Native增量/压力性能/同进程自动重连/消耗不补满、P8数据及完整玩家路径均继续未测；DS既有两条Built-in Resource启动错误仍未修复，不压日志。

### 5. 下一步与裁定

范围和独立Interest恢复标识确认后，先细化一小段正式位置接线/迟到页隔离，随后RED→GREEN、同范围回归与逐端Native一致性检查；未获确认前不实现。Store.eviction归还数量容量但保留Key/版本槽，不能宣称无限探索可复用128个Key。N5Complete=false。

Ruling：正式范围/协议决策暂停，先完成独立Store栅栏 — 用户明确禁止自行补半径/版本语义，既有合同可独立检验 — 成本是本段未完成真实位置驱动与Native出入区目标。Ruling：相同版本内容核对覆盖Evict状态 — 与既有Assembler同版本一致性合同对齐，不扩大协议 — 成本是非法冲突Baseline更早拒绝，需要按当前Baseline正确恢复。
