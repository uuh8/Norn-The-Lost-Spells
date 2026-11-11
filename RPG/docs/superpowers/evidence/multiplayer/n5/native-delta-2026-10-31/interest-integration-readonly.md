# N5 Interest 正式接线：只读最小实施建议

日期：2026-10-31。仓库：`F:\Develop Project\RPG`。本报告只读准备，由主控在当前 Native Delta 独立验证及 DS 构建结束后的安全阶段边界接续。没有修改仓库、运行 Editor/服务/测试/构建/协议生成，也没有启动或结束进程。

## 1. 结论与已确认范围

最小可验收段应贯通“DS 当前认证 Actor 位置 → 独立 10/3 参考窗口 → 原生 Chunk 判交 → 每连接每 Key 订阅生命周期 → 同一可靠通道 → Client 清理/重新 Baseline → 实际安装 ACK”。不能仅在 `TryPreparePage` 增加空间过滤；那会留下已安装缓存、旧队列、旧候选和旧 ACK。

总计划 `RPG/docs/superpowers/plans/2026-10-31 Norn项目总计划（更新中）.md` 第 16 节与 N5 计划最后“Interest两项决策已确认”段已明确授权：独立联机 42×14×42m 窗口、DS 玩家所在 2m 参考块对齐、相交原生 Chunk 整块发送；每连接每 Chunk 独立订阅代次，退出失效、重入新代及当前 Baseline；数据页不能自激活，旧代页/ACK/Resync 拒绝；必要 Schema/Codec/Reset/生成/兼容门禁及配套构建均在授权内。没有发现本最小段还必须先询问用户的产品决策。

当前源码工作区已有大量主控进行中的修改/新增；本报告不将保存的历史 307/0 或旧失败记录当作本轮运行结果。当前 Native Delta 和 Interest 是两个不同验证窗口，先保存前者，再改协议和接线。

## 2. 已有文件/API 与实际缺口

以下路径相对外层 Git 根；除配置类/资产外，优先修改已有文件，不建立新的系统目录或第二套 Channel。

| 文件 | 当前真实行为 | 最小接续落点 |
|---|---|---|
| `RPG/Assets/_Project/Scripts/Core/Online/WorldInterestCoordinator.cs` | 固定四连接，`TrySetConnection` / `RemoveConnection` / `TryGetInterest`，仅有半开点 `Contains`；未见生产调用 | 保留身份/Bounds 合同，增加半开 Chunk AABB 判交及可单测的参考块对齐计算；不接管模拟 |
| `.../Multiplayer/Movement/OnlineWorldBootstrap.cs` | `InitializeDungeon` 已创建正式 Coordinator；`AfterPhysics` 使用 `Registry[i].Body.position`，唯一捕获边界为材料步后最多 10Hz；`PumpMaterialReplication` 由既有 Driver 调用 | 加载独立联机配置并传入 Coordinator；沿现有 Registry/身份提供 DS 当前权威位置，不能取 Client 自报坐标、预测位置或 Camera |
| `.../Multiplayer/World/WorldReplicationCoordinator.cs` | `BindPeer` / `RemovePeer`，`TryPreparePage` 全量轮转 Owner Key；Client `ReceivePage` 首次见 Key 自行 `TryBindUnusedKey`；`ExpirePending` 生成 Resync | 真实创建 Interest；增加可信位置更新、生命周期控制收发、active+代次门禁、客户端 Key 绑定仅允许 Enter；超时回复关联原订阅代次 |
| `.../Multiplayer/World/WorldReplicationPeerState.cs` | 一份冻结 payload，固定 Key/ACK 版本数组，`TryFreeze` / `TryAcknowledge` / `RequireBaseline`；18KB/s、burst 3600、每轮两页；暂无订阅状态 | 在固定 Key 槽增加 desired/announced/代次状态，冻结时记录 PendingSubscriptionEpoch；退出立即取消该 Key 冻结/重传，重入清本连接 ACK 基础强制当前 Baseline；所有控制复用预算 |
| `.../Multiplayer/World/WorldReplicationNetworkChannel.cs` | 所有 WorldEnvelope 走 `Channels.Reliable`；Client 四个 byte[] 收件槽，回调只入队、Pump 每轮处理两页；身份先验 | 回调同步处理 Enter/Leave，页仍有界延后；按 Key+代次过滤/清除旧页；每次真正处理页还要复查 active+代次；Server 先发生命周期控制再发数据 |
| `.../Core/Online/WorldReplicationCommitOwner.cs` | 类名就是 `WorldReplicationCommitOwner`，没有独立名为 CoreWorldReplicationCommitOwner 的另一实现；唯一全局捕获、永久有界 Key/版本、整块冻结 Baseline/Delta/删除历史已存在 | 不重建 Owner，不按连接重捕获。为判交提供已成功提交的可信 `WorldGridGeometry` 只读访问，或由 Coordinator 在成功捕获/提交后缓存四域几何 |
| `.../Core/Online/WorldChunkReplicaAssembler.cs` | `CancelPending` 不推进版本，但也不清 `Installed` / published；`TryBindUnusedKey` 依赖 `Installed` 保护已建立版本的 Key；同版本一致 Baseline 可重收 | 退出取消候选，并把“可读 payload”与“已建立版本事实”分开；保留 Header/Revision/Key。不要通过 `Installed=false` 重置来复用槽。可新增可信 `ReleaseInterest` / payloadAvailable 标记，`GetWord/TryCopyInstalled` 不再读取已释放内容 |
| `.../Core/Online/WorldReplicaRevisionState.cs` | 仅完整安装后提交；同版本 Baseline 幂等，非法高候选不推进水位 | 保留规则；无需加入订阅代次，订阅是传输生命周期，不是材料版本 |
| `.../Core/Online/WorldChunkPayloadInterestSink.cs` | 已有 `IWorldChunkPayloadInterestSink.TryEvictInterest` | 正式 Client 要求 Installer 同时支持该接口；Multiplayer 只依赖 Core 合同，无需新增 ElementField 引用 |
| `.../ElementField/Replication/WorldReplicaStore.cs` | 实现 Installer+InterestSink；Evict 释放 Count/数量/Pending/可见性及容量、保留 Key/Header/版本；当前已核对同版本内容冲突，合法同版本释放后可以重装 | 正式接线调用，原则上不重写。未安装过的 Key 没有 Store 槽，不能将其 Evict 的 false 当作已安装缓存清理成功；已知已安装缓存清理失败应显式报错 |

`Game.Multiplayer.asmdef` 现有依赖包含 Core/Networking/NetworkProtocol/Character/Combat，不包含 ElementField；通过 `IWorldChunkPayloadInterestSink` 使用现有依赖即可。`IOnlineMaterialWorldBinding.ReplicaInstaller` 返回的正式对象本来就是 `WorldReplicaStore`。

## 3. 配置与空间计算

建议新增配置源码 `RPG/Assets/_Project/Scripts/Multiplayer/World/OnlineWorldReplicationProfile.cs`，资产放既有 `RPG/Assets/_Project/Resources/Online/WorldReplicationProfile.asset`，由 Bootstrap 按既有 `Online/MovementProfile` 风格一次加载。这里属于联机发送配置，不放进单机 `ElementWorldProfile`，不混入 Movement/Combat 的无关字段，也不新建阶段目录。资源 `.meta` 由 Unity 生成。

最小配置只需要网络参考块边长 2m、XZ 半径 10、Y 半径 3；可用 0.25×8 的只读解释表示来源，不必把两个无须独立调节的乘数暴露成重复可冲突参数。42/14/42 由 `(2r+1)*2` 推导，不再存第三份可漂移尺寸。原点沿用当前可信世界 LegacyCell 几何 `Origin`，该值来自 Source/场景；当前 Runtime 本来以材料 Root 的固定位置建立 `_origin`，不应另行硬编码世界零点。

每轴 `q=floor((authorityPosition-origin)/2m)`；min=`origin+(q-radius)*2m`，max=`origin+(q+radius+1)*2m`。因此中心是玩家所在参考块中心，而非玩家当前浮点位置。负坐标必须 floor，不能整数截断。例如原点 0、x=-0.01 时 q=-1，XZ 窗口为 [-22,20)，不是 [-20,22)。有限数、索引溢出和正尺寸都先验证。

原生 Chunk 使用自身域的几何：edge=`geometry.CellSize*geometry.ChunkSize`，min=`geometry.Origin + key.xyz*edge`，max=min+edge。每轴严格满足 `chunk.min < interest.max && chunk.max > interest.min` 才相交。不能仅测试 Chunk 中心，也不能给液体套 2m 原生尺寸；粗液体 Chunk 有自己的 CellSize。边界恰好接触不发送，部分相交整块发送；半开约定与现有 Core `Contains` 一致。

Owner 当前 `_geometries/_hasGeometry` 是 capture scratch，在提交前会被写入。若增加 `TryGetCommittedGeometry`，必须在 TryCaptureCommit 成功发布时才推进专用 committed 几何缓存，失败后不能暴露候选几何。等价的较小实现是 Coordinator 成功 Capture+Commit 后复制源四域几何到自己的固定缓存；不要读取 live Runtime 或额外按 Client Capture。已删除 Key 判交仍需该域最近可信几何。

每次权威网络 Pump 先校验真实当前连接/Actor/ConnectionEpoch/SimulationReady，再用 Registry Body 的当前位置更新 Bounds 并刷新 desired subscription。沿用当前材料发送资格，不改成 Targetable/Health 门禁来暗中新定义死亡后可见规则。位置没有跨参考块时也须处理 Owner 新提交/新 Key；可按“参考块地址或 CommitSequence 改变”触发，或有界扫描最多 128 Key。Frozen 只保留原时钟/模拟规则，不从 Interest 反推模拟活动。

只对 Owner 已提交且已知的有界 Key 判交，不按 21×7×21 再乘四域枚举/预留所有空 Chunk。没有材料的未知空间无需发送；后续新 Key 出现在窗口内时再 Enter。权威 tombstone 与 Interest Leave 保持不同职责。

## 4. 最小 Wire 方案

优先扩展已有 `WorldPageMessage` / `WorldEnvelope`，避免新增可靠流或重复 Envelope：

```proto
// 在既有 world.proto 追加，字段中文注释必须保持分号后同行。
fixed64 subscription_epoch = 21; // 当前ConnectionEpoch与ChunkKey内的非零订阅代次；退出失效，重入换代。
// kind 保留1..5原语义，追加6=InterestEnter、7=InterestLeave。
```

这是建议的具体实现选择，不是已有源码。应用协议从当前 v3 升至 v4：旧 Client 即使忽略新字段，也不具备“页不能激活”与清理语义，必须在业务/准入/实时门禁拒绝。不要兼容接收 subscription_epoch=0 的旧页。依赖包版本、Mirror ID 和 Shared target framework 均不用变。

所有 1..5 页/ACK/Resync/权威删除都必须带当前非零 subscription_epoch。Enter/Leave 只使用公共身份、Key、订阅代次；SnapshotId/Generation/BaseRevision/Revision/页索引/页数/Tick/Checksum/word 相关字段规范化为 0、Words 为空。Codec 分 kind 分支校验，不能为了套用旧校验而伪造非零材料版本。生命周期不授予材料事实，无需在 Enter 复制一份 Header 或用 SnapshotId 冒充订阅代次。

Leave 携带“被关闭的那一代”，本身不另增代。进入初始代为 1，重新进入严格换新代；在范围内移动、重传和 Resync 不改代。生命周期重复相同消息幂等，不能清掉新候选或重启 TTL。非重复状态转换遵守 Unseen→Enter(g)→Leave(g)→Enter(g'>g)；旧代 Enter/Leave 同样拒绝，不能让迟到 Enter 复活已关闭代。高代页本身一律无权开订阅。

`WorldChunkPayloadHeader` / Owner 的 Checksum/Generation/Revision 不必追加订阅字段；在 Coordinator/Peer 固定槽关联 transport 代次即可。Assembler 每次 TryBegin/Receive 前由 Coordinator 验证 active+exact epoch，转换时同步取消 pending；`_incoming` 的超时 Resync 必须另记所关联代次，不应读取当前新代后替旧候选发控制。

新 fixed64 字段在 tag21 占 10B；900B 上限/160 words/固定数组维持不变，但必须重新跑真实最坏大小 CalculateSize/编解码验证，不能将这里的静态字节推算当实测。

## 5. 同一可靠通道的发送顺序与有界控制状态

Server 在现有 Pump 内按如下顺序操作，不另加 Update 发送时钟：

1. 验证当前连接，Bind/更新 Interest。离开在内存中立即标 inactive，取消该 Key 的冻结页/等待安装 ACK/超时重传，先于本轮任何新页准备。
2. 发送待公布的 Leave，再发送 Enter；同 Key 必须是旧 Leave→新 Enter→新代当前 Baseline 首/后续页。只有 Enter 已成功进入 Mirror 的当前代才可发送该 Key 数据。
3. 页仍用已有冻结副本；退出后已经进入 Mirror/KCP 的旧页不能撤回，也不能宣称控制能超车。同一 `Channels.Reliable` 保证它们在 Leave 之前到达；应用层延后收件仍需第 6 节处理。
4. 全部 World 控制和页都计入既有每连接 bytes/burst/每轮预算。最简单是将“每轮两页”落实为每轮最多两条 World 消息，控制优先；不能绕过 token 限制一次发 128 个控制。保留本来位于战斗/进度/移动之后的材料 Pump。

不需要建立无限生命周期事件队列。每个固定 Key 槽保存 desiredActive、currentSubscriptionEpoch、announcedActive、announcedEpoch（以及 ACK 基础与 pending 所属代）。网络送出的旧代尚宣布 active，而位置经历退出/重入时，必须先 Leave(announcedEpoch)，再 Enter(currentEpoch)；从未进入传输的中间代可以不发送，但编号不重用。快速跨界只更新固定 desired 状态，不不断 append 事件。所有“已经宣布”的状态只在消息确实通过编码/预算并入传输后推进。

既有 `TryPreparePage` 每次只检查一个 Owner Key，上层遇 false 就 break，增加过滤后可能长期空转。最小改成每次最多扫描 Owner.ChunkCount 个 Key，找 active/已宣布且确需发送的一块；扫描上界仍 128，单份 frozen 和轮转公平性保留。没有必要在这一段引入第二套优先级调度器。

ACK/Resync：先验实际连接对象/ConnectionEpoch/WorldEpoch/Hash，再验 Key active、exact subscription_epoch、pendingSubscriptionEpoch、原 PendingHeader 完整关联字段，再消耗现有 rate budget。退出后 HasPending 必须已经取消；旧代控制返回拒绝，不触发 RequireBaseline、不移动 cursor、不消耗合法预算。新代第一次 Freeze 必须 current Baseline（BaseRevision=0），即使上次同 Key 版本仍相同；之后才恢复本订阅内 Delta。

## 6. Client 收件、Assembler 与 Store 一致清理

建议生命周期消息在 `OnClientPage` 的已认证主线程回调中同步应用，数据页保留现有四槽延后处理。Mirror 接收回调本来由主线程串行执行；这里不在网络后台线程安装。这样 Enter/Leave 不会因为四个数据槽满而丢失，也不需要新增无界控制队列。

Enter：完整身份/shape 检查 → 只由可信生命周期控制查找或分配永久 Key 槽 → 校验转换/代次 → 初始化 active 订阅并要求 Baseline。重复相同 Enter 只做幂等返回，不 purge 当前合法 pending、不延长超时。数据页 `ReceivePage` 找不到已激活 Key 时直接拒绝，删除当前“页首次见 Key 自绑定”路径；合法高数值也不能激活。

Leave 在同一个同步方法中完成：

1. 先验证 Key 与当前 active 代次，再置 inactive，使任何后续回调/Pump 页立刻失效。
2. 取消该 Key Assembler pending，清 `_incoming` 及其代次/待发超时回复。
3. 从 Channel 四槽 inbox 稳定压缩去掉该 Key 被关闭代的页；保持其他 Key 到达顺序。不得一刀清空其他仍有效 Key 的队列，否则制造额外丢页/恢复。即使物理队列清理漏一条，Pump 在 TryBegin 前的二次 exact-epoch 门禁仍必须拒绝。
4. 对已安装的实际 Store 调用 `IWorldChunkPayloadInterestSink.TryEvictInterest`，释放数量/缓存占用/可见性。未曾安装者只取消候选，不伪造一次权威删除。
5. 取消 Assembler payload 的公开可读资格，保留永久 Key、Header、已安装版本事实和同版本内容核对信息。`DescribeChunks` 只报告 active 且 payload 可用的数据，不能继续把离开后保留的版本事实当可见材料。

这些操作不可在 `_installing` 回调栅栏内重入。正式主线程路径本来是串行；若已知已安装缓存 Evict 被拒绝，显式锁存复制错误，不重新开启订阅冒充成功。保留固定数组不等于保留可见内容，也不要求清零所有 word/cell 后台存储。

重入：新 Enter(g2) → Baseline。Store 现有逻辑允许保留版本相同且 Checksum/WordCount/residency 一致的 Baseline 重装；Assembler 仍保持 Installed 事实时 `VersionAllowed` 同样允许这种 Baseline。不应为重装清 Generation/Revision，不应调用 TryBindUnusedKey 给历史槽换 Key。只接受 BaseRevision=0 的首份新代数据；首份新代 Delta 拒绝并按当前合法请求有界恢复。

旧高版本候选：g1 未完成的高 Revision/SnapshotId 候选在 Leave 时被 CancelPending；g2 的合法较低候选仅与“最后实际安装版本”比较。任何迟到 g1 页在 active+代次门禁就被拒绝，不能 TryBegin、占候选、推进水位、更新 `_incoming` 或回 Resync。这是订阅门禁的核心验收，而不只是 Store 的内容检查。

新 ConnectionEpoch：保留世界/Key/材料版本，取消旧候选、清 inbox，并使所有旧订阅 inactive；已有可见缓存经同一清理入口释放，新连接各 Key 从新的非零订阅代开始 Baseline。旧连接页/ACK/生命周期在 epoch 门禁就拒绝。不要借换 Manager 或重连复用前代 active 状态；也不要用较大 WorldEpoch 热覆盖旧世界。沿用当前断线/Frozen 策略，不增设资产补满或新的 Actor 生死规则。

## 7. 128 Key 与代次耗尽：沿用明确拒绝，不添加淘汰

当前 `WorldReplicationCoordinator.DefaultChunks=128`；CommitOwner `_count`、Client Coordinator `_slots`、Store `_slots/_indices`、Journal 的 Key/版本事实都不是离开就遗忘。Store Evict 归还的是 Cell/Pending/可见驻留额度，永久 Key 槽没有归还，因此不能声称能无限探索。

最小有界方案：每 peer 固定 128 个订阅元数据槽，4 peer 共 512 槽；Client 固定 128 槽。与 Owner 永久 Key 一一对应或在第一次可信 Enter 时永久绑定，之后离开只改 active，不删除 metadata、不复用另一个 Key。沿用预分配数组/线性扫描或构造时容量固定的索引，不热扩容。Peer 现有 Key/ACK 数组可以扩展，不必另建无上限 Dictionary。这样增加的只是几组长度 128 的 flags/ulong 数组；大 payload 缓冲架构不变。

每次更新先预检需要的新槽及所有要增加的代次，再一次发布状态；容量不足不得发半份 Enter 或偷换旧槽。新增 Key 第 129 个，沿既有 Capacity/Fail 路径明确拒绝并记录原因；不给它发送页，不 silent skip 当正常同步。现有 Peer 的 `TryFreeze` 只有 false，不足以区分“没有变化”和“容量耗尽”，应补可检查的 failure reason 交由现有 Coordinator `Fail`/Channel 单次 GameLog 报告，避免无限无声重试。该 Fail 停止复制，不能宣称把权威材料事务回滚，也不要擅自新增整场游戏重置/断线惩罚策略。

代次使用 ulong，初始 1；仅 inactive→active 重入才检查并 +1，进入期间位置更新/重传/ACK 不加。到 ulong.MaxValue 允许关闭当代，下一次重入显式 SubscriptionEpochExhausted，不能回绕为 0/1、偷偷增 WorldEpoch 或忘记 Key。新的 ConnectionEpoch 是独立命名空间，可重新从 1 开始，旧连接消息由已有连接门禁隔离；不能伪造重连来绕过容量。客户端同样拒绝非法/回退代次且不改变旧状态。

若实际 P7/P8 的本局不同 Key 总量超过 128，本段交付可以明确报告“有界容量拒绝，地图规模 Gate 未通过”；提高常量需有内存预算与实测，LRU、遗忘历史版本、无限探索、自动淘汰或何时换世界都不是此次默认实现。这才是可能需要额外用户决策的范围，不能反过来阻塞 128 内的已批准接线。

## 8. 必须同时检查的消费者

实施时主控集中串行生成/Editor/构建，检查清单如下：

- `Shared/Protocol/Protos/world.proto`：字段21、新 kind 注释及生命周期规范；`Runtime/Generated/World.cs` 仅由 `RPG/Tools/Networking/GenerateProtocol.ps1` 生成。
- `Shared/Protocol/Runtime/WorldMessageReset.cs`：subscription_epoch 清 0，Words 和 unknown fields 继续清理；生成类型 Clone/Equals/Merge/CalculateSize 由生成器处理，禁止手改 Generated。
- `Shared/Protocol/Runtime/ProtocolCompatibility.cs`：v4、明确拒绝v3；旧 HTTP/准入门禁更新。WorldIdentityMessage 注释中当前写死“v3”也应更新为现行合同描述，不需要追加另一份身份字段。
- `Networking/Protocol/WorldPageCodec.cs`：Wire 字段上界20→21及 field21 fixed64；严格重复/unknown/错误 wire/零代/逐 kind 字段检查；预热、160 words、900B 和借用对象生命周期维持。
- `Networking/Protocol/WorldEnvelope.cs`：沿用已有正式 WorldEnvelope ID/900B 边界，不新增第二个 Envelope；若 ID 未变仍跑既有生产全部 Mirror 消息碰撞检查。
- `Multiplayer/World/WorldReplicationCoordinator.cs`：Fill、Reply、ReceiveControl、ReceivePage、ExpirePending、SetClientConnectionEpoch、Stop、DescribeChunks 均检查订阅语义；旧代拒绝不生成当前代 Resync。
- `Multiplayer/World/WorldReplicationPeerState.cs`：冻结、重试、ACK、RequireBaseline、Reset 的订阅关联一致；Leave 不留旧 pending。
- `Multiplayer/World/WorldReplicationNetworkChannel.cs`：Server current identity 门禁、ClientContext、BindClient、生命周期同步处理、固定 inbox 压缩、Pump 二次门禁、SendReply、Clear/Disable/换Manager 同步状态。
- `Multiplayer/Movement/OnlineWorldBootstrap.cs` / `AuthorityTickDriver.cs`：配置加载及当前位置入口；保留唯一捕获/模拟时钟、控制消息后的材料 Pump、Frozen/Detach/停止重入保护。
- `Core/Online/WorldChunkReplicaAssembler.cs` / `WorldReplicaRevisionState.cs` / `WorldChunkPayloadInterestSink.cs`、`ElementField/Replication/WorldReplicaStore.cs`：取消候选与可读性/版本事实分离、同版本重装/冲突拒绝、永久Key不复用；不强加网络代次给材料版本。
- `RPG/Assets/_Project/Resources/Online/ContentManifest.json`、`GameplayManifest.json`、`CombatDefinition.asset` 及 `Editor/Networking/ContentManifestExporter.cs`、`OnlineCombatTools.cs` / 现有Hash导出链：ProtocolVersion/内容Hash配套刷新；独立网络Profile应进入已有可信配置指纹/校验，不能只新增Resources资产却完全绕过现有配置核对。不要为了加此小资产无条件重导出全套World/NPC；先核对实际导出器职责和引用。
- `Server/Norn.Business/Content/ContentManifestValidator.cs`、`Bootstrap/FoundationEndpoints.cs`、Accounts/RoomInstanceService及业务测试：Shared引用使新版本自动传播；当前 `Server/Norn.Business.Tests/DungeonIdentityContractTests.cs` 明确断言3，需要改为新合同，并增加v3拒绝/v4接受。HTTP smoke、JoinTicket/Auth、Content/Gameplay Manifest/Ready消费者一起验证；无数据库迁移需求。
- 测试构造的所有 `WorldPageMessage`、诊断/探针和回放消息必须赋订阅代次并先走Enter；不能为保留旧测试便利留下生产“零代页可自激活”的分支。`WorldPageCodecTests.UnknownField` 目前用tag21作为未知字段，升级后应换真正未知字段并另测21错误 wire。
- `RPG/Tools/Networking/Test-OnlineFoundation.ps1` 等脚本中旧版本断言/原始wire样本/HTTP门禁、现有 Native 诊断路径要与v4匹配。保留v3历史结果为历史，不能覆写旧证据。
- 总计划第16节、N5计划/living explainer记录本段实际实现和实测，N5Complete继续false，未测Gate分别保留。

## 9. 可直接实施的最小验收段

先补行为 RED，再实现：

1. Core 空间测试：2m对齐/42×14×42、非零origin、负坐标、边界刚好接触、粗Liquid原生Chunk仅部分相交、四连接分离不合并中间空白、无效坐标不替换旧范围。
2. Coordinator+Peer+真实Store 闭环：无Enter页拒绝；Enter→Baseline→实际数量/ACK；Leave在半包/等ACK/已安装三种状态清理；DS和另一Client数量不变；重入版本未变也合法完整重装；离开期间DS变化后重入当前Baseline；首份Delta不得顶替Baseline。
3. 订阅栅栏：g1高Revision未完成候选→Leave→Enter(g2)→合法较低候选成功；插入旧g1页/ACK/Resync/Enter/Leave均无效，不清g2候选、不消耗合法预算、不产生恢复风暴。重连更高ConnectionEpoch保留材料版本但重建订阅。
4. 真实Channel边界：inbox四页满时Leave仍生效；只清目标Key旧代页、其他Key顺序保留；排队旧页即使未物理清除也在Pump拒绝；快速退出/重入保持同可靠流Leave→Enter→Baseline；控制消息受budget且不无限排队。
5. 容量：128个历史Key可离开/同Key重入；第129个明确拒绝、不复用历史槽、不扩容；订阅代次最大值可退出但不能再次进入、不回绕。原Store同版本冲突内容四域回归保留。
6. 协议：新增字段最坏编码大小、0/旧版本拒绝、Reset/unknown/重复字段/错误wire、各kind shape、旧页不能误判为生命周期；生产Mirror ID集合、生成一致性、Shared/业务Release及相关业务/HTTP门禁。

相关已有 fixture 落点：`Tests/Core/WorldInterestCoordinatorTests.cs`、`WorldChunkReplicaAssemblerTests.cs`、`WorldReplicaRevisionStateTests.cs`、`WorldReplicationCommitOwnerTests.cs`；`Tests/ElementField/WorldReplicaStoreTests.cs`；`Tests/Multiplayer/WorldReplicationChannelTests.cs` 内现有 Peer/Coordinator/真实Channel fixture；`Tests/Networking/WorldPageCodecTests.cs`、`JoinAdmissionTests.cs`；`Tests/RunPlayMode/FormalDungeonWorldPlayModeTests.cs` 和当前同范围26 fixtures。定向通过后跑实际受影响回归；不在本报告运行任何一项。

真正正式接线完成至少需要匹配v4 Native Client/DS及真实 HTTP/KCP 两Client：分开后A失去目标Chunk而B/DS保留，A离开期间变化后重入当前数据并有安装ACK；另做未变化同版本重装。可复用显式Development诊断，不改正式场景/资产规则；记录Key、订阅代次、Gen/Rev、Checksum/WordCount、数量及拒绝/队列计数。纯算法或反射Channel测试不能替代此证据。原Native Delta构建不能当v4构建复用；它的完成证据独立保留。

四人压力、公网/丢包/HOL、持续反应、Native全帧Profiler/GC、同进程自动重连完整玩家路径、P8完整材料体验、N4与N6仍按原Gate分别保留。小段通过不能写N5整体完成。

## 10. 本报告验证与限制

已实际完成：读取两层AGENTS、相关CLAUDE规则、总计划第16节/N5最新确认、上述现有运行时/协议/测试和程序集内容；只读Git状态确认进行中脏文件存在，未改动它们。`rg` 未在PATH，改用限定第一方目录的 PowerShell 文件/文本查询。

工具限制：默认只读命令因Windows restricted-token sandbox无法表达分离写根而未能启动；随后按父任务明确许可，对只读命令使用require_escalated并成功读取。没有发生自动审批拒绝，没有绕过拒绝；没有执行写仓库/协议生成/构建/测试操作。本文件是唯一输出，位于父任务指定可写目录。

剩余：主控收束当前Native Delta/DS构建后，实施上述方案并运行对应验证。当前没有需额外用户决定才能开始的空白；只有出现实际128历史Key规模不足、需要LRU/遗忘/新容量策略或超出现有订阅/协议授权的行为变化时再单独报告。
