# 阶段账本 — plan: RPG/docs/superpowers/plans/2026-10-29 N5共享材料世界与分块恢复实施计划.md

## 2026-10-31：现有全量通道Native增量最小闭环

### 1. 授权、范围与实际变化

用户授权在Interest确认等待期间继续既有N5-2～N5-6独立恢复验证。本段选择真实Native Delta，不将缺版本/日志覆盖/损坏页故障注入一并扩展。随后用户明确批准独立10/3发送窗口、相交原生Chunk整块同步及每连接每Chunk订阅代次；已先记录总计划第16节，未打断当前验证，不将确认写成实现。下一段按该方案接续。

复用既有目录与组件：WorldReplicationCoordinator仅在DescribeChunks/Stamp追加真实Header的BaseRevision、SnapshotId、ServerTick；既有WorldReplicationChannelTests增加实际Owner/Codec/Assembler/Store闭环，断言真实Delta数量及这些身份。OnlineMaterialReplicationProbe仅在显式Development DS路径加第二环境开关，第一阶段正式写入后五Tick暂停；外部私有一次性请求在双Client已安装后触发第二次正式写入、恢复唯一材料时钟，再经至少五Tick暂停。正常运行未指定路径时无Probe，未启第二开关时保持原单次检查点，不直接写Store/版本/玩家资产，不改场景或协议。

### 2. 实际测试与Native证据

新增用例先14/1 RED（缺实际BaseRevision字段），最小补充后15/0 GREEN；原同范围26 fixtures **308 passed / 0 failed**。不是整项目Run All。最终Windows x64 Development Mono Client与真正Dedicated Server均Succeeded、0 error；经现有RoomLifecycleLab诊断入口装配正式P7，真实账户/Ready/Ticket/KCP，两端均为本次匹配构建，DLL Hash另存。

同一WorldEpoch 6869361087090086539，两端Actor/ConnectionEpoch均保持。检查点1材料Tick 5，检查点2从Tick 5恢复至Tick 10；前后DS Chunk数 4→5，已有块中 1 块的Revision与Checksum发生变化。两Client均实际安装至少一份BaseRevision>0的Delta，实际Delta的BaseRevision/SnapshotId/ServerTick也与DS提交Header相等；全部当前Chunk的WorldEpoch/Generation/Revision/Checksum/Words/Resident与DS精确一致。页数 8→22，DS聚合安装ACK 8→22。

ACK证据只证明DS聚合接受数增加，不声称每个连接最终Delta ACK已逐一确认。独立审查还指出最初判定未精确比对新增SnapshotId/ServerTick，已仅在诊断Python中补上实际Delta字段比较并实际执行，无需重建。审查时保护文件Physics有差异，构建结束后仅在起始Hash与HEAD字节吻合条件下恢复本轮自动序列化副作用。

### 3. 局限、清理与下一步

两个稳定检查点之间确实恢复材料时钟并产生变化；最终暂停用于精确比较，不能称无限连续反应、压力或性能验收。没有运行本段新PlayMode、Native缺版本/历史覆盖/损坏页恢复、同进程自动重连、消耗后不补满、P8材料数据或完整Hub路径。DS既有两条Built-in Resource错误保留，未因此宣称修复或发布就绪。

12个保护文件、13个派生资源/Profile保持；本轮Native已退出或按自有PID/启动时间/路径清理，业务已清理、私有MySQL已认证关闭。一次早期进程记录Path为空触发清理guard，已停止前两个自有进程后中止，未误停该Client；只读核实其准确启动Tick与实际预期路径，随后该诊断Client到自身deadline退出。确认其已退出后才继续业务/数据库清理，保留cleanup-path-race.json。用户Editor与无关Recovery保留。无提交/推送、无新源目录、无Shared/版本变更。本段到此收束，已确认Interest方案下一段从有界配置、订阅控制与旧代屏障分段接续；N5Complete=false。

Ruling：优先最小Native Delta，故障注入后段继续 — 用户允许选择最小独立范围 — 成本是缺版本/覆盖/损坏恢复的Native Gate仍未完成。Ruling：ACK声明仅限聚合增加 — 现有诊断不能逐端归因 — 成本是逐连接最终确认仍未测。Ruling：显式QA五Tick运行夹在两个暂停点之间 — 可以精确比较同一提交状态 — 成本是不代表无暂停连续负载。
