# Fire出现延迟/各端不同：提交、调度、安装与Resync只读分布审计

2026-11-01。用户03:47亲眼观察“四端火球落地后火元素有时出现延迟、各端不同”是需要继续诊断的实际症状。本报告只读沿用 `n5/four-player-native-2026-11-01` 的原始JSONL、三个P7检查点、OS采样及相关源码；未操作前台/服务/构建/测试/源码，未读`.private`正文或认证日志。

**结论：现有证据支持各端独立调度/安装进度和真实恢复请求确实存在；没有足够边界时间与失败原因将用户症状定位到某一层。0.44791GiB是严重资源压力事实，但尚不能认定唯一原因；同样不能据Resync计数宣布协议缺陷。下一步应补最小关联诊断，在低负载2人正式入口中分段验证，先不改速率、容量、渲染参数或协议。**

## 1. 已支持的事实

### 1.1 材料事实已经进入多端Store，但这不是画面出现时间

原trace1967行、SHA-256：`8e06df0ea86b3a11a547e341c37a610545e6ca2aa5990c1db596116870a60cb9`。ABC各有350/330/343份Store报告，并有同一空间Fire格强度变化；D为了短测量没有Store采样。四端材料ACK均为正，带各自实际ConnectionId/ConnectionEpoch/SubscriptionEpoch、Key、Generation/Revision/SnapshotId/Checksum。

下表**仅是每个客户端本地样本的“最后空Store→首次含Fire”时间括号**。不是DS提交→客户端安装的精确延迟，更不是可见火焰延迟。不同进程的Engine时间/QPC/Monotonic未建立同步校准，不能直接相减。RecordedUtc也只标记一次采样，不能充当提交或像素首次出现的事件时间。

| 端 | 最后空Store原行/源RecordedUtc | 首次含Fire原行/源RecordedUtc | Collector首次读到含Fire |
|---|---|---|---|
| A | 461 / 03:36:09.1472522Z | 467 / 03:36:09.4004883Z | 03:36:09.431796Z |
| B | 466 / 03:36:09.2775177Z | 469 / 03:36:09.5285678Z | 03:36:09.589191Z |
| C | 462 / 03:36:09.2233052Z | 470 / 03:36:09.4892729Z | 03:36:09.604838Z |

源报告产生与Collector读文件是两层边界：C源采样早于B，但Collector读到C晚于B，因此文件读取顺序也不能当网络到达或可见顺序。约250ms采样频率本身会形成观测窗口，不能把窗口宽度宣称为同步延迟上限。

初始Store.WorldTick从A61/B120/C91跳到205/207，并不能说明积压了相应数量的网络Tick。`ElementField/Replication/WorldReplicaStore.cs:160`仅将已安装Header.ServerTick取max作为高水位；无变化期间该值不代表不断走动的实时世界时钟，且不是所有Chunk共同安装的全世界边界。

### 1.2 每连接发送与ACK进度确实不同

本Run四个认证连接、Epoch1、WorldEpoch `5194249043625871581`，ConnectionId分别35675379/35675389/35675378/35675388。`load-stop/ds.json`累计ACK为810/772/790/887，最新逐Key ACK条目数32/29/29/32，而Owner当前32个Key。与同一份稀疏DS报告的当前Owner完整stamp精确相同的ACK条目为11/10/12/22；其余不能强凑相等。这证明“采样时各连接已安装边界不完全相同”，不能单独证明异常原因或最终不收敛。

固定预算、Interest、单连接待确认、不同帧/收包/安装/表现节奏都可能令边界不同。现有数据没有各阶段等待时间或每次拒绝原因，尚不能选定其中某个原因。

### 1.3 Resync真实发生，且不是仅在关闭期出现

DS在03:36:33.909采样为0，03:36:48.939为19，03:37:03.973为41，03:37:18.992为60；清理期最终68。第一份19早于D首次报告fault-100（03:36:59.505）。它们不能全部归因D移动故障，也不能全部解释成窗口关闭副作用。

### 1.4 资源压力确实存在，尚无因果分段证明

OS首次<3GiB为03:35:54.302193Z，最低480,940,032B=0.447910309GiB，发生03:35:55.350281Z。它可影响进程/帧调度的可能性不能忽略，但没有与具体Fire Commit/Send/Receive/Install/Render事件关联的缺页、线程等待、CPU/GPU阶段数据，无法据此断言唯一瓶颈。

D已校准240帧短窗只有3.985秒，nearest-rank帧间隔P95/P99约17.7786/18.2089ms，不代表其他三端、整段运行、DS Tick或GPU呈现通过。这个窗口也不是fault-100之后重新取得的窗口。

## 2. 源码中的确定分段路径

以下路径相对 `RPG/Assets/_Project/Scripts/`。

| 阶段 | 确定实现 | 不能据现有数据推导的部分 |
|---|---|---|
| DS材料推进→提交 | `Multiplayer/Movement/OnlineWorldBootstrap.cs:517–522`：30Hz外部步长喂既有10Hz材料累加器，每3个server tick捕获；Coordinator102–110唯一捕获/Owner提交，不逐连接扫描世界 | 原trace没有本次火球命中/写入入队/首次改变Key提交事件的精确关联时间 |
| Commit与冻结身份 | `Core/Online/WorldReplicationCommitOwner.cs:112`用h.WorldTick构造提交Header；176生成Baseline时用当前CommitSequence和_lastTick。页面名为ServerTick的字段不能直接当30Hz移动Tick或墙钟时间 | SnapshotId是版本/冻结关联值，不是UTC时间；同一Key不同冻结可带不同SnapshotId，需同时保留Owner提交与传输身份 |
| 每连接调度 | Coordinator163–209：每连接独立round/token/Key游标，先生命周期，再取当前Key冻结页；PeerState52/76：已有pending时不再冻结另Key，整块发送后等安装ACK；Coordinator85默认18,000B/s、3,600B burst，每轮2条预算项 | 不知道实际何时被token不足、轮次预算、等待ACK或前一Key挡住；不能从配置值直接算出此次可见延迟 |
| 总发送顺序 | `AuthorityTickDriver.cs:67–69`先控制/战斗/移动Pump，再材料Pump；Channel164–167计入编码+72B预留后Reliable Send | 不能由应用优先顺序排除已进入Mirror/KCP后的排队/HOL；原trace没有真实批/Datagram/重传/Socket等待 |
| 发送端重试 | Coordinator166调用`RestartTimedOutTransfer(now,2)`；PeerState100–104每2秒无进展会重发同冻结传输 | 此动作不递增ResyncAccepted；本轮没有传输重试计数/原因事件，不能说确实重试了多少次 |
| Client接收→排队 | `WorldReplicationNetworkChannel.cs:83–98`身份/Codec/订阅预检，4个固定槽，满则Rejected返回；174–187每Pump最多处理2页 | 拒绝计数未被本轮Client报告分项采集；排队满不会在该分支立即发Resync，后续是否变成缺版本/过期缺数据 |
| 组装→实际Store→ACK | `Core/Online/WorldChunkReplicaAssembler.cs:145–180`收齐、Checksum/Payload验证、Installer原子安装、再提交版本。Coordinator337–339随后Kind3 ACK；Channel189–192可靠回发 | ACK证明Installer已成功，不证明渲染已取数或屏幕已有火焰；未采收页/首末页/安装起止和ACK发送时间 |
| Store→可见表现 | P7World实际引用`Game.Rendering.GpuFireParcelRenderer`。其Update220–235按本地spawnAccumulator到SpawnInterval再Collect/Upload；366–401按本地相机可见、预算及概率采Seed，随后GPU Dispatch/Draw | 没有每个安装stamp对应的首次Seed/Upload/Draw或像素出现数据，因此不能将Store首次非空等同画面出现 |

正式P7 GPU Fire Profile现值为spawnInterval0.08、maximumSeedsPerTick256、capacity8192、spawnProbabilityAtFullAmount0.65、minimumVisualHeat0.5；资源为 `ScriptableObjects/ElementField/Fire/GpuFireParcelRenderProfile_Default.asset:15–20`。`FireCellEmissionSampler.cs:16–21`把minimumVisualHeat作为非零Amount的视觉热量插值下界，并非“低于0.5就不显示”的硬阈值。当前审计不据这些配置推断它们就是此次延迟原因，也不建议改值试过。

## 3. Resync60→68的确定代码路径与可定位边界

### 3.1 服务器计数唯一增长路径

`WorldReplicationNetworkChannel.OnServerControl`（100–108）先检查当前真实认证连接/身份对象、Run/Dungeon/World，再Decode Kind3/4，进入 `WorldReplicationCoordinator.ReceiveControl`：

1. Coordinator238–245核对Authority/活跃slot、ConnectionEpoch、WorldEpoch/GameplayHash、当前Key订阅与SubscriptionEpoch、实际connectionId、存在真实Pending，以及该Pending的Key/Generation/Revision/SnapshotId/Checksum。
2. Kind4在254通过每槽每秒最多4次预算。
3. 255–256把**DS自己的PendingHeader**记录为LastAcceptedResync，记录slot，再`ResyncAccepted++`。
4. 257调用`RequireBaseline`，PeerState106–107清该Key已确认版本并取消同Key pending；258–259把游标指回该Key，下一轮从当前Baseline恢复。

因此60→68严格表示 **8次通过上述当前连接/传输身份和预算核对的客户端Kind4请求被接受**。不是服务器主动扫描到8次内存不足，不是8个UDP丢包，不是8次KCP重传，也不是单凭“请求Kind=4”就提前计数。

| 原trace行 | DS源报告时间 | Accepted | 最近slot / Key / G:R / Snapshot | 此刻Clients |
|---:|---|---:|---|---:|
| 1815 | 03:37:18.9928713Z | 60 | 0 / Legacy(-6,0,-2) / 1:9 / 547 | 4 |
| 1938 | 03:37:25.3825794Z | 65 | 0 / Legacy(-1,0,-3) / 1:33 / 593 | 3 |
| 1952 | 03:37:26.4437203Z | 67 | 2 / Legacy(-2,0,-4) / 3:125 / 602 | 2 |
| 1964 | 03:37:27.6442061Z | 67 | 同上 | 1 |
| 1967 | 03:37:28.8607114Z | 68 | 3 / Legacy(-1,0,-3) / 1:59 / 619 | 0 |

这些是在**同一DS进程时钟**上的稀疏采样括号：18.992→25.382之间累计+5，25.382→26.443之间+2，27.644→28.860之间+1。最后stamp只代表最近一次，不能把+5都分配给slot0、+2都分配给slot2；也不能将报告Clients下降当成每次Resync产生的确切先后或原因。关闭期相关不等于关闭导致。

### 3.2 客户端能生成Kind4的已知分支

Coordinator313–357与Assembler5–6、106–184表明：

- `_needsBaseline`但收到BaseRevision非0的页：Coordinator319直接请求Resync；该分支没有调用ObserveRejection，单看LastAssemblyFailure还可能保留此前原因。
- `TryBegin`失败：Coordinator323–327记录失败；除了PendingBusy，其他原因会请求Resync。可能的枚举包括MissingRevision、StaleVersion、InvalidHeader、Capacity等，具体取决于真实候选与本地已安装状态。
- `TryReceivePage`失败：331–335记录失败后请求Resync；包含超时、冲突页、Checksum、Payload、Installer拒绝等；InstallerFaulted另会锁存Fault。
- 未完成候选过期：342–347请求Resync；当前Assembler超时参数是15000ms（Coordinator95），不能拿发送端2秒重试当这个15秒过期。

**现有报告不足以定位其中哪条触发本轮60/68次。** 本轮Client原始源只有portal/movement/store，没有逐次Resync原因、rejected candidate/installed stamp、ingress-full/Codec拒绝分类、候选首末页/过期时间。DS最后stamp取自服务器Pending，既不是客户端已安装版本，也没有客户端错误枚举。

相关读数接口其实已经存在：Coordinator `ResyncRequested`、`LastAssemblyFailure`、`LastRejectedCandidate`、`LastRejectedInstalled`、`LastRejectedPayloadAvailable`（64–78、373–378）。旧 `RoomAdmissionProbe.cs:47–54`会将它们放入隔离技术探针，但本轮正式菜单四端trace并未采这些字段；不应为了补证据重新启用携带私有准入配置的旧Lab，更不能伪造历史原因。

## 4. 可排除的具体断言，以及不能排除的部分

**由本轮数据可排除：**

- “四端根本没有真实认证/材料安装ACK”：四Actor与各3remote、真实Pending关联ACK均已有原始证明。
- “本轮显式Owner永久Key容量128触线导致材料复制停机”：观察到最多32Key、所有DS样本MaterialFaulted=false/Failure空；不等于排除了客户端页槽、Cell/Payload容量或瞬时拒绝。
- “Resync完全由D的fault-100或完全由关闭窗口造成”：已有19次在fault-100首次可见前、四端仍连接时发生。
- “D的短窗通过就证明整段/四端/DS/GPU都不卡”：短窗范围与采样类型不支持此结论，且全帧有非零GC。

**仍不能排除且需要分段证据：** DS材料推进/捕获成本、每连接Key/ACK/token等待、Mirror/KCP排队、Client入口丢页/候选失败、渲染采样/概率/可见范围/预算、CPU/GPU/内存引起的调度等待。此处列的是待检验阶段，不是并列认定的根因。

## 5. 必要最小诊断补充：不改协议语义

父线程已允许必要的最小诊断，本审计仍未写源码。建议先复用现有文件/API，显式默认关闭、固定容量事件ring，主线程只写值；窗口结束再批量导出，不能每事件JSON/IO或无界集合污染测量。

**共同关联键：** ProcessId/StartTicks、进程内递增DiagnosticSequence、阶段枚举、本地单调时间戳及frequency/单位、WorldEpoch、Actor/connectionId/ConnectionEpoch、Domain+ChunkKey、SubscriptionEpoch、Generation/BaseRevision/Revision、OwnerCommitSequence、TransferSnapshotId、Checksum/Words/PageIndex/PageCount。Owner提交身份与后续Baseline冻结身份分别保存；不把SnapshotId当时间。

**最小事件点：**

1. DS：真实Fire写入被应用/首次非零Key提交；记录该提交本地时间、材料WorldTick、30HzServerTick及现有可取得的Cast/Projectile关联ID（没有则明确缺失）。
2. DS每连接：选中Key/FreezeKind、实际准备/发送首末页、等待类型与持续时间（token/轮次/已有pending/等待ACK）、2秒restart次数；记录queue/bytes、token余量和准备但未发原因。
3. Client：页进入/因Codec身份订阅/4槽满而拒绝的分类；候选Begin、首末页、安装成功或失败；**每一次Kind4**连同触发分支枚举、原AssemblyFailure、本地installed/candidate stamp、payloadAvailable/needsBaseline、inbox深度；Kind3实际发出。
4. DS：每次Kind3/4接受或拒绝的分类及完整关联stamp，不再只保留全局最后一次；保持现有身份/速率校验顺序和预算。
5. Client表现：对同一个被跟踪Fire Key/Cell记录首次实际Store非零、首次被GPU Fire消费者选为Seed/上传、首次提交Draw的本地frame与关联PresentationVersion。Seed/Draw仍不保证屏幕像素可见，需要定向镜头/人工观察标记补足，不能把“提交Draw”写成“玩家已经看到”。

**时间处理：** 先算同进程分段时长（DS提交→排入Transport，Client收页→安装→Seed/Draw，DS发末页→收到该ACK）。跨进程单调时钟不直接相减；用中央采集器的请求/回执锚点记录offset上下界/采集延迟，未经校准仅报告采样括号。精确网络单向耗时仍可能缺失，必须写出不确定度。

诊断字段先不进入world.proto/正式消息，不扩128Key、不修改18,000B/s/burst/页预算、不改渲染概率或玩法，不用提高预算掩盖根因。若现有信息已能证明具体层再做单一修复及针对性RED/GREEN，当前阶段没有这个证据。

## 6. 低负载1–2端对照步骤建议

1. 保持已验证正式入口的人数规则；若正式房间要求2人，就使用2个Native而不把最少人数改成1。所谓“1端对照”可仅采/操作A，B空闲留作准入与对照，随后在同配置下比较双端。
2. 先无fault、无恢复/写入注入，A/B在同一真实可达区域内并确保目标Key均在Interest与相机可见域，记录原Build/画质/参数；不传送或直接改DS位置。只做少量可逐次对应的真实火球，逐次记录当前卡量与输入关联，避免额外未记录输入。
3. 每次追踪一组Key/Cell的完整提交→两端发送→安装/Resync→Seed/Draw事件；先把延迟定位成“送出前”“收到后安装前”或“Store已有而表现未取数”。只在同一进程内计算精确耗时，跨进程仍按校准界限报告。
4. 在不改游戏参数的前提下比较资源充足时与仅增加第2个主动客户端时的事件分布；不恢复四窗口来强行复现。OS可用内存守卫应在下一输入/扩充前实际阻断，保留触线到停止时间，避免只后台记录、较晚才清理。
5. 保留已有默认关闭的movement fault-100作为后续独立运动对照；它不应混入最初Fire定位基线，也不能替代全通道Datagram故障。发现实际丢页/等待/恢复的阶段证据后，再决定下一种受控故障，不以修改协议或放宽容量作诊断起点。

最终状态：用户可见问题尚未找到根因；分段证据缺口已明确，最小诊断设计可在现有N5授权与父线程许可内提出具体改动。当前没有待权限事项，也没有对任何运行中实例采取动作。
