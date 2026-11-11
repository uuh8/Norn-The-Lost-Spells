# N5空间Interest与订阅代次证据

## 2026-10-31：正式空间Interest与订阅代次第一可验收段

### 1. 结果与批准依据

用户在项目总计划第16节明确批准：独立网络2m参考块、XZ10/Y3半径，42×14×42m块对齐窗口；相交原生Chunk整块同步；每连接每Chunk订阅代次，退出失效、重入新代＋当前Baseline，页不能自激活，旧代数据/控制拒绝。N5计划最后具体边界据此实施，无新增玩法、资产、死亡/撤离/结算规则。本段已接正式位置与网络链，不代表整个N5完成。

真实账户/Ready/Ticket/KCP/正式P7内容，匹配Windows x64 Development Mono Client与真正Dedicated Server，各0 build error。使用显式隔离DS诊断，经既有RestoreMovementSnapshot移动真实认证Actor A，正式Pump读取Registry位置，自行计算Enter/Leave；未直接伪造订阅或材料状态。它验证正式身份→DS位置→发送/清理/恢复，**不等于键盘输入或完整Hub玩家路径**。B保持原位，DS全局材料保持真实权威。

| 检查点 | A位置 | 材料Tick | DS块数 | A缓存块 | B缓存块 | DS每连接安装ACK A/B |
|---|---|---:|---:|---:|---:|---|
| 1 | Home | 5 | 4 | 4 | 4 | 4/4 |
| 2 | away | 5 | 4 | 0 | 4 | 4/4 |
| 3 | home | 5 | 4 | 4 | 4 | 8/4 |
| 4 | away | 10 | 5 | 0 | 5 | 8/12 |
| 5 | home | 10 | 5 | 5 | 5 | 13/12 |

A第一次重入同版本也实际重新Baseline安装，既有块订阅1→2；第二次确认A缓存已空后才触发材料写入，运行Tick5→10再暂停，B收到变化，A在区外无新安装/ACK。A再次返回以Baseline恢复当前5块，旧Key订阅到3、新Key到2，BaseRevision均0；每端当前Key/WorldEpoch/Generation/Revision/Checksum/Words/Resident与DS一致。每连接安装ACK分别确认，不再借聚合ACK归因。最终暂停用于精确比较，不能称无限连续反应或压力测试。

### 2. 文件职责、生命周期与目录

- Core `WorldInterestCoordinator`补参考块floor/半开AABB判交，负坐标、粗原生块部分相交、非法浮点/尺寸/溢出均拒绝。`WorldChunkReplicaAssembler`新增PayloadAvailable/TryReleaseInterest，关闭读取/取消候选而保留Installed、Key、Header及版本；不靠清版本重绑槽。原ConnectionEpoch交接仍保留已安装世界，只取消旧候选。
- Multiplayer/World `OnlineWorldReplicationProfile`独立SO与Resources/Online资产，由Unity创建meta，初值2/10/3。Bootstrap一次加载/验证，NetworkChannel从当前认证Registry Body取DS位置；Source/Commit只全局捕获一次，成功提交后才缓存可信几何，不逐Client扫描live世界。没有修改单机活动参数或DS范围外模拟文件。
- Coordinator/PeerState在4×128固定Key状态中压缩desired/announced生命周期，先旧Leave再新Enter再冻结页，控制与页同受2条/轮、字节及突发预算；不建立无限事件队列，入KCP旧页不能超车撤回。旧代ACK/Resync先于预算/pending修改拒绝。Key和版本不淘汰，超容量/代次耗尽显式失败，不能称无限探索。
- Client可信生命周期在主线程回调同步生效；Leave释放实际InterestSink/Assembler候选与可读payload，定向压缩4槽旧页，Pump二次验证代次。高代页本身无权激活Key；重复Enter不清合法半包；重入首份要求Baseline。更高连接代次保留原已安装世界，等待新DS生命周期核对；初始范围外Leave可清理重连保留的历史Key，未知Key的Leave不分配槽。
- Shared `world.proto`追加fixed64 subscription_epoch=21、kind6/7；生命周期材料字段为0，不复用SnapshotId/Generation/Revision。应用协议v4，v3明确拒绝；Codec/Reset含unknown与工厂同时维护，Generated只由固定工具生成，第三方版本和Shared netstandard2.1不变。
- 现有OnlineCombatTools/OnlineEditorRequests新增独立Profile创建入口并把资产纳入GameplayHash，不重导World/NPC/场景。RoomInstanceBootstrap仅显式Development DS环境变量启用1Hz本地诊断位移/每连接ACK读数；默认无轮询，不作为性能计量。源文件都在既有Core/Networking/Multiplayer分类，没有新模块目录或反向依赖。

### 3. 真实验证与Debug历史

首批测试因项目NUnit不支持Is.AnyOf而编译Blocked，未运行用例；等价修正后149项得到49/100真实RED。最小实现后149/0；补快速未发送跨界压缩与ulong.MaxValue退出/重入两个已有边界回归，Coordinator29/0，最终27 fixtures **421/0**，正式P7/P8 PlayMode **2/0**。不是整项目Run All。原fixture断言目的保留，页工厂按新合同先投递实际Enter，未知字段样本21改为真正未知22，不留零代兼容旁路。

业务首轮42/58失败均因缺少既有N1私有配置路径；保留原log/TRX，提供NORN_N1_CONFIG与正式Manifest路径并使用受guard保护的norn_online_test后 **100/0**。真实HTTP v4往返、v1/v2/v3拒绝和超长拒绝通过；固定生成一致性8文件通过，Shared/业务Release由基础流程实际构建。

v4 Client构建 2026-10-31T14:08:41.2077645Z，DS构建 2026-10-31T14:12:52.0480515Z。工具环境短暂断连导致等待辅助进程结束，Unity构建实际完成；恢复后核对最新成功报告、原辅助进程已无、Native会话不存在、自有数据库PID/启动时间/路径匹配，才归档既有结果并启动一次Native，未重建或重复提交。见connection-recovery.json。

独立sol/xhigh审查无确定P0/P1/P2。保留P3：显式诊断收到非法/未认证请求后error锁存，后续合法home/away不能自行恢复；正常先认证再合法命令场景与默认玩法不受影响，本段不扩大修复。inbox环形压缩未单独做压力专项，Native未注入恶意旧页或故障网络。

### 4. 保护、操作与后续

12个保护文件、13个既有派生资源/Profile Hash保持；只恢复与前段已证自动序列化副作用逐字节相同且起始Hash匹配HEAD的Physics格式变化，不覆盖其他改动。新独立Profile的cs/asset和Unity生成meta齐全。仅清理本任务Native/业务进程及认证关闭自有MySQL，用户Editor回原P7_MainMenu、非Play/编译/Dirty，Recovery保留，无提交/推送。具体Native脚本记录本轮实际路径，重放须使用新私有/证据目录，不能覆盖历史或复用已清短期凭据。

Unity Editor学习入口：Tools→Norn→Online→Create World Replication Profile，Inspector看Resources/Online/WorldReplicationProfile；更新范围后按既有流程刷新Hash并配套构建。正常玩家不启本地诊断环境变量。本轮没有人工目视战斗/渲染手感、键盘完整路径、四人压力/有效Profiler/GC、同进程自动重连、消耗不补满或P8 Native材料验收；Native缺版本/日志覆盖/损坏恢复继续后段。DS既有两条Built-in Resource启动错误仍在。

Ruling：沿已有WorldEnvelope追加订阅字段/控制种类并升v4 — 批准方案内最小兼容接线 — 成本是必须匹配两端与业务，拒绝旧端。Ruling：固定128 Key保留版本、超限显式失败 — 不引入未批准淘汰 — 成本是地图规模仍有硬上限。Ruling：用显式快照位移验证真实DS位置链路 — 不改变正常输入/资产 — 成本是完整键盘玩家路径未测。Ruling：诊断error锁存P3暂缓 — 正常隔离场景不触发 — 成本是错误请求后的夹具恢复仍有缺陷。

本段到此收束，无权限或新决策阻塞；下一段优先既有合同内Native缺版本/日志覆盖/损坏页恢复，再按Gate处理持续/四人/性能。N5Complete=false，N4/N6和远端平台Gate不变。
