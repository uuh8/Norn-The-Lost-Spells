# 2026-11-01 P7/P8 Native 原始证据独立离线审计

结论：两图的原始连续诊断、实际 OS 输入记录、DS 当前 AuthorityWorkspace 与关闭前最终报告相互支持：A 使用同一 Native PID，在同一 Run/Instance/World/Actor 内以更高 ConnectionEpoch 重连；同一张卡已消耗次数没有补满，重连后再次施法继续扣除；B 独立观察相同余额变化；A 返回原 Hub 时 B 继续处于原副本。实际 ReplicaStore 中 Fire 格值出现、持续变化与衰减也已取得原始数据。本审计没有运行游戏或修改任何仓库证据。

这些证据不证明完整 N5、N4 完整战斗体验、可靠持久结算、四人压力、全帧零 GC、远端平台或发布就绪。

## 审计输入与可追溯性

证据根为 `RPG/docs/superpowers/evidence/multiplayer/n5/native-player-reconnect-2026-10-31/resume-2026-11-01/`，以下路径相对此根。

| 原始连续 trace | 行数 | 读取时间范围（UTC） | SHA-256 |
|---|---:|---|---|
| `p7/input-reconnect-trace.jsonl` | 2114 | 02:46:15.717677–02:49:15.655993 | `19df38b379785e50def271ff8a5ce3e437127f80ec87d25bd1275e4b5705d9f0` |
| `p8/input-reconnect-trace.jsonl` | 1343 | 03:01:44.076526–03:03:40.908037 | `ee5a4eb7bf2115f942e408cbc08d976d0558efc21a2f1560c96c01f4d28cde21` |

逐条核查 `trace-derived/provenance.json`：P7 34 条、P8 30 条带 TraceLine 的派生报告，其 Source 与 Data 均与对应原始行完全相同，完整 trace Hash 也相符。另核查两图 `pre-first-cast` 各 6 条报告，逐值与原始行一致；`native-final-report` 各 6 份 JSON 的实际字节 Hash 全部匹配归档 provenance。本审计没有读取或校验 `.log` 文件内容。

## 身份与未补满的强证据

| 项目 | P7 | P8 |
|---|---|---|
| A / B 实际 OS 输入 PID | `17228` / `27700` | `15008` / `33356` |
| DS PID / ProcessStartTicks | `32828` / `639290978756872050` | `13468` / `639290988230555535` |
| InstanceId | `fabfd95c2afb4fae984f437ea60fb87c` | `9c24c39aef7f4ba99cf4e802c8a49fbe` |
| RunId | `a6e6a332426d49099943f3b2d80ef60d` | `2f1a38cce22149e69b3db0f6625b1a14` |
| WorldEpoch | `9861598653727865480` | `5352075661865659429` |
| A Actor / ConnectionEpoch | `1 / 1 -> 2` | `1 / 1 -> 2` |
| B Actor / ConnectionEpoch | `2 / 1`，持续不变 | `2 / 1`，持续不变 |
| A 卡 InstanceId | `a22c17a8169149908378a490533c7202` | `cf2174cdd5884fdd981738070ae7cfe4` |
| A Wand InstanceId | `da87bde9b1e04ed68c3fa3bd741c0f02` | `cca27d46be054af49c2a40518d397e33` |
| 同卡次数 | `30 -> 27 -> 重连27 -> 25` | `30 -> 28 -> 重连28 -> 26` |

P7 关键原始 trace 行：A movement 68 为第一次真实攻击前 30 次；103 为消耗后 27；1387 为 Actor1/Epoch2、同卡27；2081 为同卡25。B movement 107 已观察到 A 为27，2090 观察到25。A 当前库存来源为 `InstalledClientSource`，而非仅用旧聚合数组证明自身卡身份；B 的 `ActorCharges[0]` 是独立观察，不能冒称 B 安装了 A 的完整私有库存卡快照。

P8 关键原始行：A movement 77 为第一次真实攻击前30；111 为28；160 为 Actor1/Epoch2、同卡28；1289 为26。B movement 107 观察到28，1286 观察到26。整个过程 A 的卡 ID、Wand ID 与 ActorId 均未替换。

每图六组原始 DS/room/instance 报告的 PID、ProcessStartTicks、Instance、Run、World 都不变。DS `ActorInventories` 的 `SourceKind=AuthorityWorkspace`、`ActualSnapshotTickAvailable=true`、ActualSnapshotTick 与 ProbeSampleTick 相等，属于当前权威 Workspace 的实际采样。另只读核对生产代码：`RoomInstanceBootstrap.cs:191` 从存活席位 `slot.Cast.TryCaptureInventory(slot.Epoch, 1ul, world.ServerTick, out currentInventory)` 导出当前值，再映射为诊断，不重新查询 Entry；诊断调用中的固定 sequence=1 不能作为正式网络库存发送序号的证据。

当前值的原始读数：

| 报告 | P7：UTC / Epoch / 次数 / Tick | P8：UTC / Epoch / 次数 / Tick |
|---|---|---|
| `disconnected/ds.json` | 02:47:56.7110838 / 1 / 27 / 5408 | 03:01:54.9368294 / 1 / 28 / 2649 |
| `reconnected/ds.json` | 02:48:12.9304982 / 2 / 27 / 5895 | 03:01:56.8043961 / 2 / 28 / 2705 |
| `returned-hub-buddy-running/ds.json` | 02:49:16.2191173 / 2 / 25 / 7793 | 03:03:38.3373353 / 2 / 26 / 5751 |

重连时 DS `Accepted: 2 -> 3`、连接客户端数 `2 -> 1 -> 2`；A 的 OS 输入 `Reconnect` 按钮在同 PID 执行。生产 `OnlineRoomClient.Connected` 调用 `Connect()`，后者重新 `POST /ticket` 并重新握手。因此“重新申请准入票后以更高 Epoch 接回原 Actor”由实现与运行状态交叉支持，无需读取票据内容，也不能据本审计声称检查了票据明文字节。

输入脚本实际使用 Windows `SendInput`；OS 记录包含原菜单登录、EnterHub、Matchmaking、两端 Ready、A StartRoom、两段攻击、Reconnect、Cancel 返回。登录记录明确 CredentialTextRecorded=false。本审计原始连续 trace 从已入场阶段才开始，因此它不是完整菜单到 Hub 步行/F 的连续录屏。P7 的 F 操作也没有出现在这份 OS 输入 JSONL 内；不能仅用该文件声称逐次重放了全部前置按键。前置操作由主 Agent 的实际交互观察和门/UI结果补充。

## B 观察、实际 Store 与 Fire 强度

原始采样器报告包含实际 `Cells` 数组，每格有 Domain、Storage、坐标、Material、Quantity；非空样本的 Material 全为 `2`，与源码 MaterialId.Fire 对应。这里是 LegacyCell/Fire 强度，不是 Liquid/Solid/Gas 的数量守恒验收。

| 样本 | P7 A / B | P8 A / B |
|---|---:|---:|
| Store 原始采样数 | 691 / 690 | 442 / 427 |
| 含 Fire 格的采样数 | 71 / 72 | 54 / 60 |
| 最大可见 Fire 强度合计 | 854 / 872 | 370 / 541 |
| 最大 CellCount | 9 / 9 | 10 / 15 |

具体可复核的逐格衰减：

- P7 `(X=-21,Y=0,Z=1,Material=2)`：A 原始行85→87，Tick857→860，强度52→46；B 原始行84→86，Tick857→859，54→48。
- P8 `(X=-205,Y=2,Z=231,Material=2)`：A 行97→101，Tick850→852，30→26；B 行96→98，Tick849→851，34→32。

两端报告时间不同，数值有异步推进差异，不能把不同时间/不同安装边界的强度硬凑成相等。用 WorldTick 单一字段匹配也不足以证明所有 chunk 原子处于同一全世界版本；本次只确认两端实际 Store 看到非空格、同一空间格发生随 Tick 的变化，以及独立 B 侧观察存在，不声称完整逐 Tick 一致或全部材料复制已验收。

两图所有六份 `sampling.json` 均是 `Paused=false`、`ContinuousSimulation=true`、`DiagnosticsOnly=true`；此标记与采样中不断推进的 Tick、强度衰减相互支持。本次不是早期 debug 暂停定格比较。

## A 返回原 Hub，B 保留副本

- P7 的 trace 在 Cancel 完成前稍早结束，不能从 `trace-derived/returned-hub-buddy-running/a-portal.json` 直接宣布已回 Hub。独立读取 `p7/native-final-report/a-portal.json`，源时间 02:51:05.8278379Z，Scene 为 SinglePlayer_Hub，HubLoaded/HubActive=true、ActorId/ConnectionEpoch=0；a-movement 同时 Ready=false、DungeonId为空。B 最终 02:51:05.9591450Z 仍 Connected/Actor2/Epoch1，movement Ready=true、DungeonId=p7、ServerTick11085、观察 A 次数25。DS 早在02:49:16已保留同一 Run、Clients1并继续模拟。
- P8 trace 自身行1326 已显示 03:03:38.7214534Z 返回原 Hub、HubActive=true。关闭前 a-portal/a-movement 再次确认退出。B 最终03:03:41.3978487Z仍 Connected/Actor2/Epoch1，Ready=true、DungeonId=p8、ServerTick5843、观察 A 次数26。
- A 的旧 store JSON 在退出后没有被删除，仍保存离开前最后一份原 World 数据。它是历史采样文件，不能据此认定 A 回 Hub 后仍绑定旧 Replica；退出应以 portal/movement 生命周期状态为准。

## 必须保留的取证缺陷与范围限制

1. 原 Checkpoint helper 的 PowerShell 逗号数组与未加括号拼接优先级错误，导致 role 文件只叫 `a`/`b` 且相互覆盖。原坏目录与 `checkpoint-copy-defect.json` 已保留；缺失的原 role checkpoint 文件确实没有恢复。后续 trace-derived 是可定位原始连续记录的派生副本，不是原独立 checkpoint。
2. P8 原 baseline 早于 trace 开始，无法恢复其缺失 role 快照。`pre-first-cast` 用事件定义：首个真实 OS Attack 之前的实际原报告，不能改称原 baseline checkpoint。审计发现 P8 首次选择的 a-movement 行88源时间比 OS StartedUtc 晚0.5µs后，主 Agent 已改选更早的原始30次行77；本审计重新逐值核对其与原行相等，原 trace 没有改变。
3. DS 报告并非每个 checkpoint 同刻刷新：两图 `consumed/ds.json` 都仍报告旧的30次；P7 `post-reconnect-cast/ds.json`只到26，P8对应DS报告仍早于第二段施法为28。权威最终25/26来自较后的真实 `returned-hub-buddy-running/ds.json`，不能将前者标签当即时数据。
4. P8 trace 夹有一条旧 P7 DS source。身份核查只接受与本图 baseline InstanceId 匹配的 DS source，未混用旧进程记录。
5. 两图客户端诊断 Calibrated/TickCounterValid 等无有效校准，不能将其中 GC 峰值0解释为性能通过。这里没有 Profiler/四人/远端/完整Boss或清场验收，也未审计持久结算。
6. 同 Native PID 由实际 OS 输入记录在攻击前后/重连按钮前后相同支持；DS 则另有稳定 ProcessStartTicks。连续 trace 的 role 名本身不是客户端 OS 进程身份字段，不能脱离输入/运行记录独立使用。

## 自动审批拒绝与安全替代

一次只读命令原拟读取 Native `.log` 并输出包含 `JoinTicket/Ticket` 的行；自动审批在进程创建前拒绝，理由是可能提取会话凭据且身份/余额核对不需要这些内容，命令未执行。该日志动作已停止，没有重试、改路径或间接获取敏感日志。后续只读取 DS/room/portal/movement/store/provenance 结构化报告及生产代码，以 Accepted2→3、Epoch1→2和实际 Reconnect OS 按钮完成安全交叉核查。当前审计无待权限事项。
