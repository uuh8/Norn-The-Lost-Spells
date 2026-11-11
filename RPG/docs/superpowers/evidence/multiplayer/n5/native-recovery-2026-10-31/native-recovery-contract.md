# N5 Native 故障恢复合同只读核查

核查日：2026-10-31。未改仓库；未运行 Editor、测试、服务、构建、协议生成或 Git 写操作。已读两层 AGENTS、相关计划/解析及下列源码和测试；历史通过数不是本轮验证。

## 依据与准确位置

以下路径以 `RPG/` 为根，脚本以 `Assets/_Project/Scripts/` 为根。

- `docs/superpowers/plans/2026-10-29 N5共享材料世界与分块恢复实施计划.md:61–64` 原文：“页缺失/冲突/Checksum或数量非法时保留旧数据/旧Revision；完整验证后原子安装才ACK。Delta只接受BaseRevision等于已安装值；缺版本有界Resync。”`174` 记录最新 Interest 交付及保留边界。
- `docs/superpowers/plans/2026-10-31 Norn项目总计划（更新中）.md:318–322`：独立窗口/订阅代次已确认，128 Key 有界；`338–344`：v4 Interest 已交付，Native 缺版本/损坏/日志覆盖仍未测。注意这是“项目总计划”，不是 10-20 联机总体方案的第16节。
- `Core/Online/WorldChunkReplicaAssembler.cs:126–143,160–180`：先验版本，完整收齐才查 Checksum，再验 Payload/实际 Installer，最后交换发布并提交版本；失败保留旧数据。`ElementField/Replication/WorldReplicaStore.cs:91–160` 另有数量、几何、容量及版本预检。
- `Multiplayer/World/WorldReplicationCoordinator.cs:209–229,282–305`：当前身份/订阅和真实 pending 关联后才耗控制预算；Resync 调 RequireBaseline 并优先恢复该 Key；安装成功后才回 ACK。
- `Multiplayer/World/WorldReplicationPeerState.cs:47–60,81–96`：安装 ACK 后才记该连接版本；RequireBaseline 清该 Key 版本和 pending。`Core/Online/WorldReplicationCommitOwner.cs:166–184`：当前 Baseline 的 Base=0；Delta 是依赖精确历史 Base 的完整 replacement，缺历史退 Baseline。
- `Networking/Protocol/WorldPageCodec.cs:42–65`：Delta 要求 `Base>0 && Revision>Base`，Words 仅受分页形状约束；其不验材料 Checksum。`Multiplayer/World/WorldReplicationNetworkChannel.cs:89–96,109–149` 是真实身份/编码/发送/回包入口。

## 两场景的最小边界

两次独立 Native 会话，每次 A 注入一次、B 正常。先证明 A 同一 Key 已完整 Baseline 安装且 DS 接受其 ACK，再推进真实材料提交；不变更 Scope、World/Connection/SubscriptionEpoch 或 Generation。注入在 Channel `123` 的 TryPreparePage 后、TryEncode 前，限显式 Development 开关，默认关闭；不改 Owner、PeerState 冻结数组或正式协议。

1. Checksum：只翻选中 Delta 的一个 Words 值，原 Checksum/Header 保持；其余页正常完整到达。必须观察最后一页后 `Checksum` 拒绝、旧真实 Store 与旧 Revision 保持、无该候选 ACK，关联 Resync 被 DS 接受，再以 Base=0 当前 Baseline 安装/ACK恢复。只收到坏首片不能证明完整校验拒绝。
2. Base 缺口：目标真实 Base 必须 >1，改成 Base−1，Revision 仍严格更大且 Kind=2；这是 wire 合法的领域依赖错误。**选中整份 Delta 的所有页统一改 Base**，不能只改首片，否则余页可能建立合法候选并干扰 Baseline。Assembler `140` 预期 MissingRevision；DS 关联字段 `217–218` 不比较 Base，所以原 pending 的 Resync 仍可被接受；不能改 Snapshot/Revision/Checksum 来“帮助”关联。

恢复后的旧页重放最多一次，属于显式网络故障模拟。若恢复 Baseline.Revision 等于旧 Delta.Revision，Assembler `132/140` 判为 **MissingRevision**，不是 StaleVersion；要断言 **StaleVersion**，必须先使恢复版本严格高于故障版本。更大的 SnapshotId 本身无版本意义。旧页会触发旧 Resync；DS 应拒绝其关联且不能清当前 pending。

## 证据、空白与风险

每条稀疏事件保留：场景/序号/UTC与单调时间、Actor/真实 connectionId、Run/Dungeon/WorldEpoch、ConnectionEpoch、Key、SubscriptionEpoch、Generation、SnapshotId、Base/Revision、Checksum、Words/PageCount/PageIndex、注入前后值、encode/decode成功、接收阶段/拒绝原因、安装次数、Resync/ACK接受与拒绝结果。关联键至少为 `(connectionId, ConnectionEpoch, SubscriptionEpoch, Key, Generation, Revision, SnapshotId, Checksum)`。

四检查点：注入前、拒绝后且恢复前、Baseline恢复并 ACK 后、重放后。比较 A 真实 Store 数量/内容摘要和版本；恢复后 A/B 当前块与暂停的 DS 完全相等。每连接 `PeerAcknowledged` 仅累计数，**不能单独证明目标 Baseline ACK**；应保存该连接被接受的准确 pending Header/关联事件及前后计数。现有 LastAcknowledged 为全局最后值，也不足以归因。不要用 DescribeChunks 元数据单独冒充真实材料保旧证据。

现有测试：`Tests/Multiplayer/WorldReplicationChannelTests.cs:239` 只覆盖初始坏 Baseline→空 Store→恢复；`Tests/Core/WorldChunkReplicaAssemblerTests.cs:26` 覆盖已安装后损坏/缺 Base 保旧；尚缺“已安装真实 Store→坏 Delta→当前关联 Resync→Baseline→逐连接 ACK”的完整闭环及上述重放分类回归。此为覆盖空白，未发现两场景必须新增协议的理由。

保留 128 Key 上限、两条 DS Built-in Resource 启动错误、P3 显式诊断非法/未认证请求后 error 锁存。诊断请求应在认证后合法发出；不要用旧失败状态污染本次结果。日志 ring 覆盖、四人持续压力/HOL、有效 Profiler/GC、P8、重连/资产、远端与 N4/N6 均留后续，N5Complete=false。读取进程的默认沙箱启动失败，已获只读提权成功；无剩余权限阻塞。
