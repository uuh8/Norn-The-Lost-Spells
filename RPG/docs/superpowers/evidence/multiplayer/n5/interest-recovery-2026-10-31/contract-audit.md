# N5 空间 Interest / 退出 / 重入合同只读核查

核查日期：2026-10-31。仓库：`F:/Develop Project/RPG`。本报告只读核查源码、计划和既存证据；未运行 Git、Unity、服务、测试或构建，未改仓库文件。下文行号对应本次读取的文件。

## 结论

已批准的是四名有效 Actor 的真实需求并集、Authority 独立推进、每连接筛选发送、离开释放客户端缓存、重入当前 Baseline，以及现有世界/连接/Chunk 版本栅栏。没有在 N5 计划或总联机计划找到明确的网络 Interest 半径、空间形状、Chunk 入选/距离排序、迟滞或死亡后的可见性合同。

现有 `WorldInterestCoordinator` 的半开 AABB、Profile 的 10/3 Chunk 和 4m/2m、测试 Bounds size=4 都是已有实现/默认值/夹具，不能直接升级为用户批准的空间产品规则。正式通道仍按全量已提交 Key 轮转；未调用该 Interest 类型和 eviction 接口。

## 1. 已确认合同：准确原文与位置

所有路径相对外层根目录。

| 合同 | 原文与位置 |
|---|---|
| 授权范围 | `RPG/docs/superpowers/plans/2026-10-29 N5共享材料世界与分块恢复实施计划.md:3`：「本阶段实现DS唯一CPU材料权威、客户端只读显示、四人活动域与Interest、冻结Baseline/Delta恢复，以及材料生成/收取的数量事务。」 |
| 粗粒度优先级 | 总联机计划 `RPG/docs/superpowers/plans/2026-10-20 Norn多人联机系统总体执行方案.md:257`：「预算紧张时优先保证角色操作、战斗关键反馈和附近材料，允许远处材料稍晚更新。」同段禁止漏量、停必要权威反应或丢可靠业务。N5计划`:19`重申这是用户2026-10-29确认约束。 |
| 公平性和版本依赖 | 总计划`:260`：「持续缺额时需老化/公平性和明确Resync策略，不能永久饿死附近材料。」`:261`：「按各连接Interest筛选，不为四个Client逐帧各扫世界。」Delta合并须保持BaseRevision/事务语义，日志缺口重新Baseline。未规定具体排序公式或权重。 |
| 四人并集和 Authority 边界 | 总计划`:272`：「不能只跟随队长、单个 Camera，或用四人平均坐标代替并集；两名玩家分开后各自区域仍要推进。」以及「Interest 决定向某客户端发送哪些区域，不应让无人可见但尚在反应的世界停止 Gameplay。」 |
| 有效位置与活动源 | N5计划`:70`：「先保证4个有效Actor位置的真实并集，随后未结束Projectile/Fire/反应；无人看见不等于可停止Gameplay。容量不足显式处理，不能仅计RejectedCount就静默漏反应。」 |
| 离开和重入 | N5计划`:71`：「每连接Interest只控制发送，不控制唯一Authority；离开区域释放客户端缓存，再进入请求当前Baseline。」`:72`要求分离玩家、火源满载、重连/死亡/断线、负坐标和容量压力回归。 |
| 世界/连接隔离 | N5计划`:23`：「重连ConnectionEpoch由传输层另审，不重置世界。」`:63`：「State隔离WorldEpoch/ChunkKey/Generation/SnapshotId/ConnectionEpoch；任意高ID不能抢占合法待收Snapshot。」`:114`：「重连同一世界不换WorldEpoch，世界身份冲突不能热覆盖；不新增DS崩溃恢复或业务重启规则。」 |
| 原子安装后ACK | N5计划`:62`：「页缺失/冲突/Checksum或数量非法时保留旧数据/旧Revision；完整验证后原子安装才ACK。Delta只接受BaseRevision等于已安装值；缺版本有界Resync。」 |
| 当前明确分段 | N5计划`:138`：「同世界更高连接代次取消旧候选并保留已安装版本。」`:142`将动态四人Interest选择/缓存释放列为后段；`:146`确认本段仅有界全量Chunk的Native P7非零基线，`N5Complete=false`。 |

## 2. 当前源码已有什么，不能将其误当批准空间规则

- `RPG/Assets/_Project/Scripts/Core/Online/WorldInterestCoordinator.cs:6–8`只承担每连接发送范围，注释将离开缓存释放/重入Baseline留给复制Owner。`:22–31`接收调用方传入的 `Bounds`，不计算半径、中心、Chunk范围或优先级。`:49–55`实现半开 AABB 点包含（min包含、max排除）；没有 Chunk-AABB 相交/中心点选择规则。
- `RPG/Assets/_Project/Tests/Core/WorldInterestCoordinatorTests.cs:20–27`的四个Bounds中心间隔100、size=4是分离/不填缝测试夹具；`:49–53`测试半开边界。它们不是4m网络半径的授权证据。
- `RPG/Assets/_Project/Scripts/ElementField/Data/ElementWorldProfile.cs:21–23`有默认 XZ=10 Chunk、Y=3 Chunk、sleep=2s；`:71–75`默认 Unit=4m、Projectile=2m。这些属于已有Gameplay/Streaming/Activity Profile，未找到将它们绑定到每连接网络可见范围的批准合同。资产还可能覆盖C#默认值，不应据此断言正式配置值。
- `RPG/Assets/_Project/Scripts/ElementField/Runtime/ElementWorldRuntime.cs:383–403`的ActiveWorldBounds注释限定集中Physics broadphase；`ElementWorldRuntime.Activity.cs:152–166`的Camera可见AABB（far至多64m）限定表现culling。不能直接拿它们作网络兴趣区域，或反过来裁掉无人可见但仍需反应的权威材料。
- `RPG/Assets/_Project/Scripts/Multiplayer/World/WorldReplicationCoordinator.cs:11`明确本段全量Chunk；`:85–103`按owner索引轮转，一个pending冻结传输，没有距离排序/Interest筛选。`:49`的18KB/s、burst3600，以及`:15`的128Key/16384words均为当前预算，不代表空间策略批准。
- 对 Scripts/Tests 的限定搜索结果：`WorldInterestCoordinator`只有类型与测试引用；`TryEvictInterest`只有中立接口、Store实现及Store测试引用；正式Coordinator没有接入。

## 3. 位置来源和有效资格

`RPG/Assets/_Project/Scripts/Multiplayer/Movement/OnlineWorldBootstrap.cs:501–510`每次Authority物理后，按四个席位取 `Registry[i]`，仅 `slot.Targetable && slot.Body != null` 时把 `slot.Body.position` 送入 `material.TrySetActor`，否则ClearActor。`:513–517`随后唯一推进材料并最多10Hz捕获。

`RPG/Assets/_Project/Scripts/Multiplayer/Movement/NetworkActorRegistry.cs:27–29`：`Targetable => Connected && SimulationReady && (Health == null || Health.IsAlive)`，Body取该席位Player/Enemy/Boss Transform。`OnlineMaterialWorldBinding.cs:87–89`只把这些值转交Authority World；`ElementWorldRuntime.AuthorityActivity.cs:14–25`固定四槽并检查位置有限、Actor不重复。

这是现有可信DS Actor位置来源，足以复用到后续设计；它当前服务Authority Activity，不能据此推导“死亡客户端必须失去材料可见性”。现有发送端 `WorldReplicationNetworkChannel.cs:108–111`要求当前认证席位+SimulationReady，没有Health可见性裁定。死亡/观战/断线留存范围仍需用户规则，不能机械复用Targetable为网络可见资格。

Authority Activity的`:37–40`明确全部有界resident/pending规则推进；`:50–59`的Unit/Projectile radius只建Exposure等活动查询域。`ElementWorldRuntime.Activity.cs:14–15`Authority液体不按Interest裁剪，`:143–145`Authority Gameplay Chunk恒可处理。

## 4. 退出缓存、重入和旧包隔离的实际能力

### 缓存退出：已实现接口，未正式接线

`RPG/Assets/_Project/Scripts/Core/Online/WorldChunkPayloadInterestSink.cs:4–6`原文：「释放已安装数量/可见性，保留同WorldEpoch内的Generation/Revision栅栏；这不是权威Chunk删除，重入必须重新安装当前Baseline。固定容量工作区可以保留以避免探索时反复分配，但释放后的payload不得继续被读取或渲染。」

`WorldReplicaStore.cs:186–203`的TryEvictInterest清数量/Pending计数和Interested，归还Cell/Pending及domain resident容量，并提升表现脏版本；保留Header、Version、Key和数组。`:240–249`表现枚举/脏版本不再暴露evicted块。`:102`拒绝evicted resident直接收Delta；`:127–128`确保未Interested时同Revision Baseline重新安装，而非只返回幂等成功。

现有 `Tests/ElementField/WorldReplicaStoreTests.cs:153–163`已覆盖安装20→evict变0→Delta拒绝→相同Revision Baseline恢复20，但没有真实Coordinator离开/重入链路。

注意容量边界：Store的 `_indices/_slots` 在`:144`只增加，evict`:191–203`不删除Key/归还metadata槽；Assembler`:53–57`明确版本槽永久属于已安装Key，`:59–66`拒绝已Installed槽换Key。因此当前回收的是逻辑占据容量，不是“128个地址可无限探索复用”的实现。不能为了省槽清掉Generation记忆。

### 连接重连：已有独立版本语义，勿借用为空间进出代次

`Shared/Protocol/Protos/world.proto:15–25`逐字段规定ConnectionEpoch、固定WorldEpoch、SnapshotId、Generation、BaseRevision/Revision；`:18`只有五种Kind：Baseline/Delta/安装ACK/Resync/删除。没有Interest-enter/exit或InterestEpoch。

`WorldReplicationPeerState.cs:34–37`Reset清旧ACK记忆和pending；`:47–60`重新冻结，若没有该Key已ACK版本则取Baseline；`:75–81`ACK须匹配当前连接代次和真实pending全部字段；`:89–90`RequireBaseline清该Key依赖并取消该Key pending。

`WorldChunkReplicaAssembler.cs:69–82`更高ConnectionEpoch只取消候选，保留已安装Payload/Header/版本；同代重复幂等、0/倒退拒绝。`:95–99`高Snapshot不能抢pending；`:105–119`旧Generation/Revision拒绝，Delta必须精确匹配已安装BaseRevision，删除后的重建须新Generation Baseline。`:138–156`完整校验→真实Installer→版本/数组交换。`WorldReplicaRevisionState.cs:31–45`同世界重连不换WorldEpoch，也不接受旧Baseline倒退。

`WorldReplicationCoordinator.cs:133–138`给所有Assembler传递新ConnectionEpoch；`:179–180`在页入口匹配当前连接代次、应用版本、WorldEpoch及GameplayHash。`WorldReplicationNetworkChannel.cs:58–80`另验证真实当前认证对象引用，并在新身份清旧收件箱；`:84–89`服务端先核当前实际连接再进ACK/Resync。现有Coordinator测试`:209–217`覆盖重连拒旧ACK、保留旧Store直至新Baseline；这不是空间重入测试。

### 空间退出后旧包：尚缺正式语义和接线

仅调用Store.TryEvictInterest不能取消Assembler旧pending、移出Channel排队页，或使Coordinator拒绝不再Interested的Key。Coordinator.ReceivePage `:144–169`没有Interest准入检查，会对同ConnectionEpoch的合法页继续组装；Store允许Baseline重新置Interested。静态推断：离开后仍排队/在途的合法Baseline能够再次安装，从而让已释放块恢复可见；此路径本次未运行，不写成已复现故障。

不能将Kind=5权威删除伪装成Interest退出；不能在同一认证连接走几步就提高ConnectionEpoch；不能将SnapshotId变为客户端自行推进的兴趣水位。`WorldReplicationCommitOwner.cs:163–173`当前Baseline SnapshotId取当前CommitSequence，未变化世界重请求可能仍相同；Assembler`:112`亦明确SnapshotId不是越大越可信的版本水位。

因此空间进出必须明确：谁决定成员集合、何时生效、如何通知另一端、如何取消/屏蔽旧候选及迟到页、重入如何关联当前Baseline。若要新增控制消息/字段/兴趣代次或用未批准方式解释现有字段，需用户确认协议语义。

## 5. 可独立继续的最小验证段与必要决策

**建议最小段：只验证现有eviction/版本合同在真实WorldReplicaStore Installer下的组合边界，暂不接生产空间选择。** 复用 `Tests/Multiplayer/WorldReplicationChannelTests.cs:109–165`已有Source/Codec/Coordinator/真实WorldReplicaStore harness，并参考 `Tests/ElementField/WorldReplicaStoreTests.cs:153`的退出断言。Core `WorldChunkReplicaAssemblerTests.cs:68–115`另有SmallAtomicStore夹具，可复用其安装重入/故障边界思路，但它不是ElementField生产Store。测试显式提供Key与进入/退出事件作为夹具，不选择玩家半径或Priority，不修改世界/连接代次语义。

应验证：evict后所有读视图不暴露旧量；Delta拒绝且旧版本栅栏保留；重新完整当前Baseline后才恢复，允许同Revision；错误Checksum/缺页/旧Generation不能恢复或提前ACK；重复evict/容量归还；新ConnectionEpoch保留既有安装事实并拒旧连接页。补一个同连接退出后旧Baseline仍在途的观察用例，将当前行为与待批准目标清楚分开，不能预设新兴趣协议后直接实现。

**生产空间接线前必须确认的缺口：** 网络Interest的空间范围/形状/Chunk相交定义和边界；附近优先的具体排队算法与公平性策略；死亡/观战/断线客户端可见范围；空间退出/重入双方同步和迟到包屏蔽合同（尤其需要新协议时）。可以提出备选，但不能把当前AABB、旧Profile半径、Camera64m或先写出的算法当作用户已经批准。

## 6. 已存验收事实与本次边界

`RPG/docs/superpowers/evidence/multiplayer/n5/authenticated-material-channel-2026-10-31/summary.json`保存26 fixtures 294/0、PlayMode2/0、匹配Client/DS构建各0 error，以及P7显式Development种子五个材料Tick后暂停检查的双Native非零一致性。其`:150–159`明确不覆盖动态Interest/evict、持续反应、压力、同进程自动重连、P8材料Native路径和性能，`N5Complete=false`。本报告仅读取这些历史记录，没有重跑。

执行环境：默认只读命令首次因Windows受限token沙箱不能处理split writable roots而未启动；随后限定读取的require_escalated获准，审计完成。无待处理审批、无仓库修改、无服务/Editor/Git操作。本报告是唯一新增文件。
