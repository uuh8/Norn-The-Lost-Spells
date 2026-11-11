# N5 扩展恢复合同：只读审计

只读审计，2026-10-31。脚本路径相对 `RPG/Assets/_Project/Scripts/`。

## 最小可行路径与真实入口

复用 `ElementField/Diagnostics/OnlineMaterialReplicationProbe.cs:24–76` 显式DS夹具；真实写入用 `ElementWorldRuntime.cs:224–234.TryEnqueueWrite`，由 `Multiplayer/Movement/OnlineWorldBootstrap.cs:518–522` 正式步后捕获。保持同Scope/World/Connection/订阅。

可优先试Solid Ice：P7在线Profile支持Ice/DriedSlime，0.25m、Chunk8³、每Tick32格、grain2、每格64 GMU。`ElementField/Solids/FixedSolidWorldRuntime.cs:102–112` 提供投放/逐格读取；`271–298.TryRemove` 含数量/拓扑/Collider预检、回滚与空块回收。先快照格列表再移除，避免索引变化；不能伪称玩家收取/资产结算。旧 `ElementFieldRuntime.ClearFieldForDebug` 属另一运行时，不可混用。

P7 Solid开启重力，Actor/地形/液气/Collider容量可能延后Pending。选Interest内非原点、有支撑且远离Actor的区域，实测实际格/Key；坐标与规模尚未知，不能擅改Profile保证夹具成功。

## 128条历史覆盖

`Core/Online/WorldReplicationCommitOwner.cs:156–184`：history为逐变更Chunk的replacement ring；满后覆盖最旧记录。Coordinator `78` 以chunks=128构造history。Peer `WorldReplicationPeerState.cs:47–60`：无pending时按该peer已ACK版本查精确Delta，查不到才冻结当前Base=0 Baseline。

A完成目标ACK且无pending后，在TryFreeze前有界暂停A；真实材料继续提交、B正常。旧Base匹配Delta入ring后，再128条changed记录才保证淘汰（ACK后通常至少129条）。同Key交替真实增减，每次跨正式Capture，避免新增128个永久Key。Tick/Captures/CommitSequence均不等于记录数。

必须自然查Delta失败→当前Baseline，不先RequireBaseline。记录fallback旧ACK基准/Key、lookup=false、当前Header、真实history追加数；CopyChunk会清Owner.LastFailure，事后读它不足为证。已有pending冻结缓冲不受ring影响，仅停ACK不保证触发。

## tombstone、重建与旧代栅栏

Owner `62–79` 因Geometry持续激活原点Key：**清空原点材料不能删除该Key**。非原点需resident/pending全空并跨正式Capture出Remove；立即重投会变普通Update。

`WorldChunkCommitJournal.cs:177–190`：Remove保Generation并Revision+1；随后Create为Generation+1、Revision1、Base0。Native应先证明实际tombstone安装/对应peer ACK和真实Store清空，再同Key真实重投，证明新代Baseline。Assembler `126–143` 拒绝旧代/同代非法复活；旧页在新代安装且无pending后应StaleVersion，pending期间可能先PendingBusy，不能硬断言同一分类。Coordinator `232–241` 及Peer `81–87` 只接受当前pending精确Generation/Revision/Snapshot/Checksum，旧ACK不得清新pending或耗合法预算。

Channel诊断 `WorldReplicationNetworkChannel.cs:372–376` 现用Generation与Revision同时不小于旧值，是已知P2；须按 `(newG>oldG)||(newG==oldG && newR>=oldR)` 判断。正式版本合同已如此，不需新协议。先修诊断并行为回归，再用于Generation重置的Native证明。

## 多页完整安装与可复用测试

Owner `134–150` 编码为11头＋每cell7 words＋每pending18；当前PageWords160。排除Pending后，同Key至少90个真实cell即641 words、5页；可尝试96格，但以实际Source/冻结Header为准。Assembler `160–180` 完整页齐后才Checksum/Payload/实际Installer/版本交换；Peer `65–87` 全页发送后才接受安装ACK，四页不是整块窗口。

250ms采样可能错过半包窗口；应有界暂停A末页前或逐页同步采Store，证明N−1页旧Store/无ACK、末页后完整替换/精确ACK。现有Probe只在Resync后暂停，尚无此门控；诊断不作HOL/性能结论。

可复用测试：`Tests/Core/WorldReplicationCommitOwnerTests.cs` 的 `InvalidCaptureIsAtomicAndHistoryGapRequiresBaseline`、`FrozenCopySurvivesLiveMutationAndRemovalCreatesTombstone`；`Tests/Core/WorldChunkReplicaAssemblerTests.cs` 的 `DeletionRequiresNewGenerationBeforeRecreation`；`Tests/Multiplayer/WorldReplicationChannelTests.cs` 的 `BaselineLongerThanFourPagesContinuesBeforeInstallAck`、`RealReplicaRemainsEmptyUntilCompleteValidatedPayloadAndThenAcknowledges`。未见对应完整Native证据。

## Source与交付边界

`OnlineMaterialWorldBinding.cs:53–57` 固定创建真实Source，属性只读；无诊断替换入口。Coordinator接口不能解释为允许假Source冒充正式材料。`WorldReplicationSource.cs:17–45` 预检/复制真实四域，保留此链。

逐peer/Key保存关联Header、页、真实格/总量、history追加/fallback、tombstone/新代ACK。保留128 Key、P3、资源错误及后续Gate。无权限阻塞；生成规模、Pending归零、Native时序待实测；未运行Editor/测试/服务/构建/生成/Git写。
