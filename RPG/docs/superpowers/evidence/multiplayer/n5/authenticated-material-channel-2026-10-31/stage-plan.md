### 2026-10-31 认证材料通道第一可运行段

授权：N5 计划第3行及 N4 acceptance 的 N5ExplicitlyAuthorizedByUser；N5-2 冻结快照/原子安装、N5-4 五种 WorldPage/身份校验/预算、N5-5 正式 P7/P8 接线。现有 WorldPage/Envelope/Codec/Reset、CommitOwner、PeerState、Assembler、ReplicaStore 均复用；应用版本仍3，不新增协议语义。

本轮依次完成：

1. 在现有 Multiplayer/World 增加计划中的 Coordinator。唯一全局捕获点位于 Authority 材料步结束后，最多10Hz；每个认证席位只持有一组冻结传输，不逐 Client 重捕获 live Source。接入现有 Baseline/完整 replacement Delta/tombstone。
2. 在同目录增加薄 NetworkChannel。实际当前认证对象/connectionId/ConnectionEpoch/Run/Dungeon 先校验，再进有界收件/控制预算。发送在既有战斗、进度、移动消息之后；每轮2页、900B Protobuf、保守计入 Mirror framing、18KB/s 初值。客户端4页收件箱、每轮处理2页；完整校验并由真实 Installer 原子安装后才ACK。现有超时重传和关联当前 pending 的受限 Resync 保留旧数据/版本。
3. 以首个合法运动输入的 SimulationReady 作为材料发送屏障，避免世界握手同批到达但 Replica 尚未装配；同世界更高连接代次取消旧候选并保留已安装版本。Detach/换Manager 清理旧 handler/候选，Coordinator 随 World Owner 生存。
4. 行为测试先验证冻结、乱序、缺页、不提前ACK、实际Replica数量、错误身份/旧ACK、缺修订、缓存容量与重连；先看真实RED，再最小实现和同范围回归。独立检查真实Mirror身份门禁及正式Bootstrap接线。
5. 按既有流程构建匹配Native Client/DS，真实HTTP/KCP/正式World路径采集逐Chunk Key/Generation/Revision/Checksum/WordCount，至少包含一份非零材料数据。需要种子时仅显式Development诊断调用既有写入口，不添加玩家资产/生产默认材料或改场景。DS既有Built-in Resource错误保留并判断影响。

边界：本轮先有界全量已提交Chunk发送，动态四人Interest选择/缓存释放、压力调参与完整恢复/HOL验收后续分段；不据此宣称完整通道或N5完成。捕获/提交失败显式锁存复制故障并保留旧副本，不能冒充权威回滚。没有源码新模块目录、跨层依赖、掉落/死亡/撤离/结算规则变更。同进程自动重连、消耗后不补满、完整玩家路径及性能继续列未测。
