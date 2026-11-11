# N5 认证材料通道第一可运行段：独立最终静态审查

审查日期：2026-10-31。范围依据两层 AGENTS、N5 计划最新接续段及 `authenticated-material-channel-2026-10-31/stage-plan.md`。只读检查项目源码、测试源码与已保存证据；未运行 Unity、构建、服务或测试，未修改仓库文件。

## 审查结论

在本段“有界全量已提交 Chunk、复用现有 Schema 与复制组件”的范围内，未发现可确定复现且需阻塞本段后续 Native 验证的缺陷。此结论是静态审查结果，不代表运行验收通过或 N5 完成。

## 已核对的关键边界

- `WorldReplicationNetworkChannel.cs:58–80`：当前真实 NetworkClient 连接、认证标记、身份对象引用、Run/Dungeon、世界握手及消息 epoch/hash/world 均先于有界页队列；跨帧存储复制编码字节，不保留 Codec 或 Mirror 的借用对象。
- `WorldReplicationNetworkChannel.cs:82–89` 与 `WorldReplicationCoordinator.cs:111–129`：服务端先核验当前 Actor 连接，再匹配 pending Key/Generation/Revision/SnapshotId/Checksum，之后才消耗 ACK/Resync 预算。早到 ACK 不释放冻结传输；Resync 关联当前 pending，并回到同 Key 的 Baseline。
- `WorldReplicationCoordinator.cs:85–109`、`WorldReplicationPeerState.cs:47–87`：每个连接独立冻结 payload，分页与超时重传不重新读取 live Source；每轮两页及含 framing 的 token 预算在入 Mirror 前扣除。
- `WorldReplicationCoordinator.cs:140–176`、`WorldChunkReplicaAssembler.cs:85–156`：新 Snapshot 不抢占进行中的候选；CRC、payload 和版本依赖检查通过且真实 Installer 成功后，才发布已安装版本并构造 ACK。失败保留已发布数据。重连 epoch 只取消候选，保留已安装版本。
- `OnlineWorldBootstrap.cs:492–521` 与 `AuthorityTickDriver.cs:38–70`：材料模拟与复制捕获由唯一 Driver 组织，捕获最多 10Hz、独立于客户端数量；材料发送排在既有 combat/progress/movement pump 之后。
- `OnlineWorldBootstrap.cs:257–308,638–658`：换 Manager 退订并清空旧适配器；StopWorld 先停止 Driver/通道和 Coordinator，再 Detach 材料世界。延迟页不能继续使用停止的 Coordinator 安装。
- `OnlineMaterialReplicationProbe.cs:16–37`：仅 Development DS、非 Editor 且显式设置环境路径时创建，一次性走既有 `ElementWriteRequest` 写入 512 GMU；默认不创建、不修改玩家资产。报告 Accepted 仅表示正式入口接受，非安装验收证据。
- asmdef 保持现有 Core/Networking/Multiplayer/ElementField 边界；本段没有新增 Schema 语义。Mirror 的 `Send<T>` 与 Envelope writer 同步序列化/复制发送缓冲，未见共享 `_send` 的跨帧借用问题。

## 证据与限制

直接读取已有 `coordinator-adapter-green-report.json` 与 XML：记录 13 passed / 0 failed，结束于 2026-10-31 11:47:12Z。`stop-red-results.xml` 保留此前 StopWorld 延迟页 12/1 RED；本次没有复跑，不以历史证据声明当前运行通过。对本段五个已跟踪变更执行 `git diff --check`，exit 0，仅出现既有 LF→CRLF 提示。

本次未验证 Native Client/DS 构建、真实 HTTP/KCP、非零材料端到端安装、完整重连/消耗后不补满、四人动态 Interest/evict、压力/HOL、GC 或目标机器性能。两条既有 Built-in Resource 错误仍须按原证据判断影响。上述范围依阶段计划保留，不列为本轮新增功能要求。未遇到仍未解决的审查访问阻塞。

## 审查时源码 SHA-256

```text
0F9665052D276A3638D7A32ACFC015752389024F9DBC635A720601EBCE7FA25D WorldReplicationCoordinator.cs
03B6BA7B0E23B7168F9B843FCF50796710DAEA2B6A6D3D82CB6CE3F83399E59B WorldReplicationNetworkChannel.cs
24D33DA57B46F09F4ECD0F7AF65C606B87A10E55E617FA50CBCCE1CDAEF27802 OnlineWorldBootstrap.cs
2C9072A887D4AF0D545D60761937DE56EE1877E72BA9C4DA7EE7EB880869725C AuthorityTickDriver.cs
1102E0032F8A897626614D0687034EE9273639A1247C47B4FD101F7D5799EAAB OnlineMaterialReplicationProbe.cs
77BE522A43143314CE4CC6CF94070DC95AECFB1088100DADBAE1D529AC57E668 WorldReplicationChannelTests.cs
```
