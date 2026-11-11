# 四端 Fire 可见时间差：只读源链审计

审计日期：2026-11-01。任务触发：用户约 03:47 观察到四端火球命中地面后 Fire 有时显示时刻不同。四端已关闭，本次未重开任何 Client/DS，未操作 Editor、服务、前台、协议生成或 Git，未读取凭据；未改 Runtime、测试、场景、Profile 或任何参数。本文是 staging 报告，不是修复完成或延迟验收。

## 1. 当前可以得出的结论

**现有证据不足以把视觉差归因到网络、Renderer 或低内存中的单一因素。** 源码明确存在“DS 接触 → 材料 Tick → 复制捕获 → 有界发送/Client inbox → 完整安装 → 本地 Fire Seed → GPU Draw”的多段时间线。正式 P7 使用的是 `GpuFireParcelRenderer`，其视觉时钟、概率采样、相机筛选和种子预算能独立造成“Store 已有 Fire，但本帧还没有可见火苗”；同一份 Store 不承诺四端同一渲染帧发射相同 Parcel。

当前问题应拆成两个可测量的问题：

1. 同一份已提交 Chunk 版本，在各端什么时候完整安装？
2. 同一端安装以后，什么时候第一次用该版本生成目标 Fire 的 Seed，随后什么时候在 CPU 提交对应绘制？

只改变协议优先级、页预算、Fire Profile、后台帧率或机器负载再观察“看起来好了”，无法回答延迟究竟在哪一段。本报告只提出观测边界，不建议用这些参数变化替代归因。

## 2. 已核对的源链与精确位置

下文路径相对外层 `F:/Develop Project/RPG/`，行号按本次只读工作区。

| 阶段 | 源码/资源位置 | 实际行为及边界 |
|---|---|---|
| 权威 Physics 接触 | `RPG/Assets/_Project/Scripts/Combat/Projectiles/ProjectileBase.cs:442`、`:446`、`:469`、`:484` | Replica 在入口返回；DS 读取真实接触点/法线，消耗本次接触后调用 `ResolveImpact`，避免重复命中。Client 显示的飞行火球不是本地材料写入权威。 |
| 命中效果与材料请求是不同消费者 | 同文件 `:504`–`:506`；`Combat/Projectiles/Fireball.cs:19`、`:25`–`:45` | 先执行 Fireball 的爆炸/伤害表现，再发布 `SurfaceImpacted` 与 `Impacted`。爆炸产出通过战斗事实复制；地面材料通过材料链路复制。不能用爆炸出现或本地外推火球碰地的画面替代 DS 接触时间。 |
| 沉积适配 | `ElementField/Runtime/ElementDepositOnImpact.cs:44`–`:51`、`:180`–`:207` | 从 `SurfaceImpacted` 几何构造请求，交给 `ElementRuntimeRegistry`。`TryDeposit` 的成功仅表示接受写入请求，尚未立即修改 Cell。Fire 的初速度处理见 `:110`–`:116`。 |
| 写入接受和路由 | `ElementField/Runtime/ElementWorldRuntime.cs:224`–`:234`；`ElementField/Simulation/ElementWorldWriteRouter.cs:33`–`:36` | 先经过权威/事务/活动域门，再把 ElementCell 请求送入原队列。拒绝、队列未消费和成功应用必须分开观测。 |
| 材料应用与反应 | `ElementWorldRuntime.cs:669`–`:680`、`:690`–`:710`；`ElementField/World/ElementWorldSimulator.cs:111`–`:124` | 一个完整材料 Tick 内递增材料 WorldTick、消费队列、应用写入，再执行真实 Fire 下落/衰减等模拟并提交。Fire 强度可消失/重现，不能把某坐标第一次被截图捕获等同于同一次命中事件。 |
| 权威钟域与捕获 | `ElementWorldRuntime.Authority.cs:45`–`:66`；`Multiplayer/Movement/OnlineWorldBootstrap.cs:517`–`:522` | 外部 Gameplay 30Hz 喂时间，材料累加器仍约 10Hz；每三个 Gameplay Tick 在材料步完成后捕获一次。低负载下也存在离散捕获边界；不能假定每次 Collision 同步出网。 |
| **Header Tick 的语义** | `ElementField/Replication/WorldReplicationSource.cs:45`；`Core/Online/WorldReplicationCommitOwner.cs:112` | Source 用 `_world.WorldTick`，CommitOwner 将它放进 `WorldChunkPayloadHeader.ServerTick`。**该 Header 的 ServerTick 是材料 Tick；不是 CombatFact/移动的 30Hz ServerTick。** 两者需分别记录，不能直接数值相减当秒数。 |
| 有界发送 | `Multiplayer/World/WorldReplicationCoordinator.cs:163`–`:209`；`WorldReplicationNetworkChannel.cs:131`–`:171` | 每 peer 有当前冻结传输与 Key 轮转；每轮至多两页，控制与页共预算，可靠发送。当前 pending、页数、ACK、超时重启等可以让不同连接处于不同传输边界。只知道累计 ACK 不能说明目标版本延迟。 |
| Client 接收与消费 | `WorldReplicationNetworkChannel.cs:83`–`:98`、`:174`–`:187` | 回调先把合法数据复制到有界 inbox；Pump 每次至多消费两页。接收回调与真正安装不是同一时刻。inbox 满会拒绝，需要保留原因与队列深度。 |
| 完整安装与 ACK | `WorldReplicationCoordinator.cs:313`–`:339`；`ElementField/Replication/WorldReplicaStore.cs:91`–`:160` | Assembler 收齐/验证后调用 Store；Store 先校验与构造 Candidate，再在主线程交换 Current/版本并推进本地 PresentationVersion，成功完整安装后才生成 ACK。需在真正交换后记安装事件，不能以收到首页、ACK 被 DS 稀疏报告采到或文件被观察器读到代替。 |
| Renderer 的只读桥 | `ElementWorldRuntime.cs:241`–`:245`、`:290`–`:305`；`ElementWorldRuntime.Replica.cs:68`–`:83` | Replica 的 Legacy 读数直接来自安装后的 Store，GPU Fire 在 Seed 收集时读当前值。没有另一个 Fire gameplay simulation 在 Client 自己生长材料。 |
| **P7 实际 Renderer** | `RPG/Assets/_Project/Prefabs/World/Online/P7World.prefab:13857`–`:13869` | 启用 `Game.Rendering.GpuFireParcelRenderer`，引用 `GpuFireParcelRenderProfile_Default`，并非旧 `ElementWorldFireRenderer`。P8 同样有 GPU 组件（`P8World.prefab:622490`），另存在旧组件，不据此推断全部对象均同时启用。 |
| 本地视觉采样钟 | `Rendering/ElementField/GpuFireParcelRenderer.cs:220`–`:235` | `dt = min(Time.deltaTime, MaximumVisualDeltaTime)`；累加到 SpawnInterval 才采种子，随后直接清零，最多一次，不补视觉债务；同时用这个 dt 推进 GPU Parcel 生命周期。不是网络 Header 直接驱动的一次性即时显示。 |
| 种子筛选/预算 | 同文件 `:366`–`:429` | Camera Frustum/距离筛选；从本地轮转 Key 起点扫描；每次最多 `_seedCpu.Length` 个种子；每格还经过 `ShouldEmitVisual` 概率；本地 `_visualSequence` 每次采样后递增。没有网络同步视觉 sequence。 |
| CPU 上传/Spawn/Draw | 同文件 `:348`–`:362`、`:239`–`:281` | Seed 收集 → GraphicsBuffer.SetData → Spawn Dispatch；LateUpdate 再提交两次 Splat Draw（统一模式）。这些 CPU 调用时间只能界定提交阶段，不能称为 GPU 完成或屏幕呈现时刻。 |

真实 P7 Profile：`RPG/Assets/_Project/ScriptableObjects/ElementField/Fire/GpuFireParcelRenderProfile_Default.asset:15`–`:35`：8192 Pool、256 seeds/tick、0.08s SpawnInterval、0.0333s MaximumVisualDeltaTime、0.65 SpawnProbability、0.5 MinimumVisualHeat、UnifiedSoftFlames=1、32–56m 淡出。这里使用实际资源值，不使用 C# 默认值 0.22 概率来解释本次场景。

## 3. 候选机制：哪些已由源码支持，哪些尚未归因

### A. 安装后视觉采样相位与低帧率放大：最直接的独立候选

`GpuFireParcelRenderer.cs:227`–`:233` 对视觉 dt 截断后才累加，而且达到阈值就清零。按实际 0.08/0.0333 配置，低于约 30FPS 时至少需要三次 Update 才触发一次 Spawn；若实际 15FPS，则约 0.2s 真实时间才采一次；5FPS 则约 0.6s。该计算只说明机制，不证明本次 ABC 恰有这些 FPS，也不能由 D 的四秒窗口替 ABC 推算帧率。

即使各端帧率均正常，各自启动/采样相位也不同。`FireCellEmissionSampler.cs:19`–`:21` 的概率包含本地 `visualSequence` 与 heat，较小的 Fire 可在首轮不发种子，下轮才发。同 Store 的首次可见时间不必严格一致。应记录“目标 Fire 已读到但概率拒绝”和“目标 seed 已生成”两个状态，不能只读全局 TotalSeeds。

### B. 相机/距离、预算与本地 Key 顺序：有条件的独立候选

`GpuFireParcelRenderer.cs:368`–`:388` 使用各端 Camera.main、兴趣中心和视锥；`MaterialPresentationView.cs:14`–`:34` 只决定显示，镜头外不会产生新种子。`:379`–`:427` 的 256 上限与 Key 轮转会延后尚未扫描的火源。`WorldReplicaStore.cs:246`–`:251` 按本地槽位顺序输出已订阅 Key，槽位由到达顺序建立，不能假定四端枚举完全同序。**只有记录预算确实饱和/目标确实被剔除，才可把它归因为本次差异。**

GPU Pool 和相位延续还意味着“正在看到的火苗”可以来自稍早的一次 Seed，而不是当前截图中的材料量。只比每端全局 Legacy 强度、Pool 使用数或 DrawBounds 不能定位一个目标火源。

### C. 复制/消费积压、重传/Resync：仍需同时保留的候选

每连接一份冻结传输、每轮两页、Client 每 Pump 两页、可靠 ACK/超时等代码事实均存在。四端历史 summary 中出现了不同 DroppedSteps 与 ResyncAccepted，但没有把其原因和目标版本关联起来。它们说明负载/恢复值得测量，不能直接证明是网络延迟或错误恢复，也不能靠“材料通道未 Faulted”排除安装延迟。

### D. 后台、系统负载与内存：条件，不是单因结论

`Multiplayer/Rooms/OnlineRoomClient.cs:67` 在正式匹配入口将 `Application.runInBackground=true`；Mirror Client 入口也设置后台运行。`Core/Settings/UserSettingsService.cs:29`–`:35` 设正常目标 60FPS；当前 QualitySettings 的 VSync 为 0。**不能因为当前 ProjectSettings 的 runInBackground=0 就断言联机后台暂停，也不能把 targetFrameRate=60 当作实际帧率。** GPU Fire 源码中未见 `Application.isFocused` 的直接限帧分支。

历史 summary 确有低可用内存、CPU 压力、累计 DroppedSteps 和停止记录；但没有各目标版本对应时刻的 render/main-thread/GPU/硬缺页证据。可用内存低不足以证明唯一原因。后续只需观察当前 focus、实际 unscaled frame gap、Application.runInBackground、targetFrameRate、VSync、相机活动状态和同段 OS 指标；本轮不修改优先级、后台参数或限帧策略。

### E. 玩家看到的“命中”本身有独立表现时钟

战斗事实保存 OutputId/CommandId（`Multiplayer/Combat/OnlineCombatWorld.cs:267`–`:302`）；Replica 实体是只读表现对象（`:548`–`:570`），`Present` 按收到 pose 的本地时间最多外推 0.1s（`:572`–`:597`）。Fireball 爆炸也走 CombatOutput 事实，材料走另一消费链路。用户画面中“火球碰地”与真正 DS Collision 之间的差异必须单独计量，不能把它全部记作材料网络延迟。

这里需要准确区分三条路径，不能笼统称“Replica 沉积被拒绝但本地 OnImpact 仍放特效”：

- 正式 Replica 在 `ProjectileBase.ConfigureExecutionMode:88`–`:98` 关闭 Collider/设刚体 kinematic；`OnCollisionEnter:446` 和 `ResolveImpact:499` 都先拒绝 Replica，`Fireball.OnImpact:21` 也返回。因此正常客户端不是自行碰撞、触发 SurfaceImpacted、被拒绝写 Fire 后又自行调用 Fireball.OnImpact 播爆炸。若未来发现实际本地调用，需额外栈/模式证据，当前不能假定。
- **DS** 的 `ResolveImpact:504` 先执行 Fireball.OnImpact：`:27` 创建爆炸，`:42`–`:45` 发布爆炸子产出事实；然后 `ProjectileBase:505` 才调用 SurfaceImpacted。`ElementDepositOnImpact:189` 并未把 `TryDeposit` 返回值向上转为失败。故即使后续材料请求缺 Sink、权限/事务/容量门拒绝，先前已经创建/发布的爆炸表现仍可能存在。它不能证明沉积被接受，更不能证明已安装材料 Fire。
- **Client 的爆炸 VFX** 可以由上述可靠战斗出生事实独立播放：`OnlineCombatWorld.OnFact:470` → `CreateReplica:548`–`:563` 直接实例化该效果 Prefab；终止事实 `:477`–`:481` 移除相应产出。这个普通表现实例与 `GpuFireParcelRenderer` 的材料 Fire 并非同一个对象/提交点。

以上是明确分层和可检验候选，**不是断言用户把对象看错**。后续观测应分别标记 ParentProjectile/ExplosionOutput/MaterialCell，并用既有 ActorId/CommandId/OutputId 和材料 Header tuple关联；若只拍到“火球/爆炸亮了一下”，应如实记录为战斗表现时间，不能改名为 DS 材料生成或地面 Fire 首次可见时间。

## 4. 当前 Sampler/raw 能证明什么

`OnlineMaterialReplicationProbe.cs:269`–`:286` 每 250ms **计划**采一次当前 Store，再同步序列化和覆盖文件；实际频率受主线程帧率/IO影响。`CellResult`（`:288`）记录 Domain/Storage/Chunk 坐标/Cell 坐标/Material/Quantity。`ReplicaResult`（`:289`）记录 WorldEpoch、全局 WorldTick、域总量、UTC 和进程本地 Engine realtime。

因此它可以证明：某次实际采样时，某 Cell 在当前安装缓存中有某材料与强度；能辅助稳定检查点、坏页拒绝后的保旧比较。它不记录 Collision/Command/Output 身份、ConnectionEpoch、SubscriptionEpoch、SnapshotId、Chunk Generation/Revision/BaseRevision、CRC、目标 Header 材料 Tick、收页/安装/Seed/Draw 的事件时刻。`WorldReplicaStore.cs:160` 的 WorldTick 是所有已装 Chunk Tick 的 max，不能代表这一个 Cell 的提交时间；`GetChunkVersion`（`:254`–`:255`）是本地 PresentationVersion，也不是网络 Generation/Revision。

已读取 `RPG/docs/superpowers/evidence/multiplayer/n5/four-player-native-2026-11-01/summary.json`、`fire-observation-bounds.json` 与 trace 的顶层结构：ABC 有 Store，D 在有效 profile 窗口关闭了 250ms Sampler；不能补称 D 的逐 Cell 时间。历史 ABC 最大采样间隙分别约 0.335/0.540/0.833s，并非每次严格 250ms。旧最大 35.7437s“首次采样差”涉及反应/消失/重现别名，没有同版本关联，不能报道为同一次火球或可见延迟。较小的约数秒例子也受相同归因限制。

旧 `Monotonic=Time.realtimeSinceStartupAsDouble` 各进程以自己的 Engine 启动为起点，禁止跨进程相减。UTC 同机样本能做粗粒度样本窗口比较；本次旧数据没有 QPC/墙钟调整校准，事后检查 UTC−EngineTime offset 范围很小只能限制漂移，不能补造原本不存在的安装或渲染事件。

## 5. 最小追加观测建议（本轮未实现）

### 5.1 先选一个受控目标，不增加网络合同

后续若实施，仅显式 Development 诊断路径启用，固定一个目标 Legacy Key/Cell（必要时最多四个 Key），单次受控射击、短窗口，保留空场景背景 Fire 与已有正常物理。不要给 `.proto`、ElementWriteRequest 或 Gameplay 添诊断字段；不要靠默认赠送材料或暂停物理制造“通过”。先保留同一个版本的完整关联：

`TraceSession + PID/ProcessStart/BuildHash + WorldEpoch + ConnectionId/ConnectionEpoch + SubscriptionEpoch + Domain/ChunkKey + Generation/BaseRevision/Revision + SnapshotId + MaterialWorldTick + CRC/Words/Resident`。

再附目标 Cell/Material/读到的强度，和本地 PresentationVersion、frameCount。若下一版本在第一次 Seed 前已覆盖旧版，必须写 `SupersededBeforeSeed`，不能把后来版本的 Seed 强行算到旧版。

本次 Store 尚无按 Key 读取网络 Header 的公开方法；若需要严格连接 Render 读数，可后续增加一个只读 `TryGetInstalledHeader` 值副本入口，或在安装处维护仅诊断可见的固定 Key→Header 槽。`Game.Rendering` 已允许引用 `Game.ElementField`；不必给 Multiplayer 增加 ElementField 依赖，不用每帧反射或扫描所有 Key。

### 5.2 建议记录的最少事件

| 事件 | 准确钩点 | 目的 |
|---|---|---|
| `Impact/EnqueueAccepted` | `ProjectileBase.ResolveImpact:504`–`:505` 与 `ElementDepositOnImpact:189`/`:207` | 记录真实 DS 几何和成功入队，区分拒绝。复用同物体 `CombatOutputIdentity.ActorId/CommandId`（`CombatOutputIdentity.cs:35`–`:56`）及已有 OnlineCombat OutputId。它们目前没有贯穿到请求/材料 Header，不能假装已经有关联。 |
| `Applied/MaterialCommitted` | `ElementWorldRuntime.cs:698`–`:707` 及完整 Tick 结束后 | 记录请求真正应用的材料 Tick；诊断旁路若追踪队列序号须区分接受/拒绝且有界，不能改请求语义。一次请求可影响多个 Cell/Key，要记录实际受影响目标，不能仅按命中坐标推定。 |
| `Commit/Freeze/Send` | `WorldReplicationCommitOwner.cs:112`；`Coordinator:198`/`:205`；`Channel:165`–`:169` | 关联真实冻结 Header、PageIndex/Count、发送/重发、队列/预算等待。保留材料 Tick 与 Gameplay Tick 两个字段。 |
| `Receive/Inbox/Install` | `Channel:94`–`:98`、`:178`–`:185`；`WorldReplicaStore:152`–`:160` | 拆开传输、Client 主线程排队和原子发布。安装后再记录，包含拒绝/超时原因。 |
| `TargetRead/SeedDecision` | `GpuFireParcelRenderer:381`–`:401`、`:414`–`:423` | 同一个目标版本是否被读到、frustum/distance/cap/probability 哪个门决定本次不出生；首个目标 Seed 的版本和位置。 |
| `Upload/Spawn/DrawSubmitted` | 同文件 `:355`–`:361`、`:278`/`:281` | 记录首个含该目标 Seed 的 Buffer 区间/SpawnSequence、frameCount，以及紧随其后的 CPU Draw 提交。Draw 是整批实例，必须明确只证明该 batch 含有关联 Seed，不证明目标片元通过深度/透明度后实际可见。 |

只记录一个目标 Cell 的“第一次”，可以将帧级事件压到很小；另附每次未出生的原因汇总计数，而非逐格日志。必须允许“目标未收到/未扫描/概率拒绝/已替换/未生成/超时”的明确未测结果。

### 5.3 跨进程计时

同机 Windows 可使用原始 `QueryPerformanceCounter` ticks 统一事件时间。微软明确说明 QPC 可跨进程/线程一致比较、频率在启动后固定；不同线程相差 ±1 tick 的顺序仍有不确定性。[Microsoft QPC 指南](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps)

`Stopwatch.GetTimestamp()` 返回所用计数器原值，具体计数源须区分高分辨率和 fallback。[Stopwatch.GetTimestamp](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.stopwatch.gettimestamp?view=net-10.0) Unity 当前为 Mono，不能仅凭 .NET Core 假设其频率/原点：每个进程记录 `Stopwatch.IsHighResolution/Frequency`，以 `QPC_before → Stopwatch → QPC_after` 成对锚点在测量前后核对；若频率或原点不同，保留原始值并作有误差界的仿射换算，或所有关键事件直接使用 QPC。整型 ticks 先做差再换算 ms；UTC 只作人工定位与墙钟变化核对。不同机器不能直接拿本机 QPC 相减，远端方案不在本轮范围。

### 5.4 默认关闭、有界且不干扰测量

建议复用显式 ENV/诊断开关：默认不分配 ring、不订阅额外回调、不查询逐目标 Header、不读写文件。开启时预分配固定 `struct[]`，例如最多 2048 条目标事件、最多四个 Key、30–60s 固定时长；满后计 dropped 并停止/标记证据不足，不扩容、不覆盖关键首事件。无 LINQ、装箱、闭包、每事件字符串/JSON、同步文件写入或 GPU readback。记录版本 tuple/QPC/枚举原因；测量结束后一次导出。保留测量前后空路径对照，不能把当前 250ms 全 Store 数组+JSON+File.WriteAllText 的开销计入零分配结论。

## 6. CPU 提交和真正可见的交付边界

最小观测可以先把 **安装→目标 Seed→CPU Draw 提交** 测清，明确命名 `DrawSubmitted`。URP 的 `endCameraRendering` 可附带 frame/camera 回调，帮助关联相机阶段，但这只是渲染流程回调，不是该 Cell 的显示证明。[Unity 6000.3 endCameraRendering](https://docs.unity.com/en-us/engine/6000.3/script-reference/unityengine/rendering/renderpipelinemanager/endcamerarendering)

如前面各段均接近而仍有肉眼可见差异，再单独做 GPU/Present/外部帧观测；GPU fence 通过、帧 Present、最终屏幕目标片元可见仍是不同边界，不能互换命名。现有 GpuFire ProfilerMarker 仅覆盖 CPU 收集/上传/Dispatch/Draw API，也不能用 `GC.GetAllocatedBytesForCurrentThread=0` 或 D 的帧间隔统计替代 CPU/GPU 完成时长。

## 7. 给主 Agent 的当前行动边界

- 现有旧 build 的低负载双端短对照可以降低干扰并重新观察，但只能产出相同采样能力内的粗界；它不能凭空补出上述版本关联和 QPC。保留四端已经关闭的状态，不重开四端。
- 如果后续允许补观测，先做固定目标版本的安装/渲染 CPU 提交闭环，再决定是否需要碰网络或视觉实现。当前没有证据支持直接更改协议、发送优先级、页预算、Fire概率、种子预算或后台参数。
- 本报告无待执行的源代码改动、无自动审批拒绝/不确定进程操作。剩余限制是历史证据粒度不足以及未运行带新观测的对照，不能称作根因已确认或 Fire 同步已修复。
