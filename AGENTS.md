# Norn: The Lost Spells — Monorepo Agent 总入口

适用于在仓库根目录打开项目的 Cursor、Codex 等 coding Agent。最后核对：2026-10-31。
**本文件的通用规则适用于整个仓库；以下所有路径均相对外层 Git 根目录。** Unity 专项细则见 [RPG/AGENTS.md](RPG/AGENTS.md)。

## 1. 先理解整个项目

Norn 是结合搜打撤、Noita-inspired 材料世界与法术编程的 Unity RPG，已有单机内容，正在扩展为 **2–4 人 Dedicated Server 合作副本**。ElementWorld 有真实材料数量、反应和玩法后果，不只是 VFX；首版不做 MMO 持续大世界。

这不是只有 Unity 客户端的仓库。三个同级目录共同组成系统：

| 目录 / 项目 | 运行形态与责任 | 不应混淆的边界 |
|---|---|---|
| `RPG/` | Unity 工程，同源构建 **Client 和 Unity Dedicated Server**；含角色、战斗、法术、库存、材料世界与联机装配 | DS 的 Unity Gameplay 源码在这里，不在 ASP.NET Core 目录 |
| `Server/Norn.Business/` | ASP.NET Core 业务服；账户、Session、持久资产、匹配、房间、实例租约和 JoinTicket；访问 MySQL | 不运行 Unity Scene、Physics 或副本 Gameplay Tick |
| `Server/Norn.Business.Tests/` | xUnit / HTTP / 真实 MySQL 业务验证 | 业务测试不代替 Unity 或双 Client 体验验收 |
| `Shared/Protocol/` | netstandard2.1 纯 C# 合同库，同时作为 Unity 本地 UPM 源码包；Protobuf Schema/Generated、业务 DTO 与纯规则 | 不是服务器进程，不监听端口，不反向依赖 UnityEngine 或 ASP.NET Core |

```text
Unity Client × 2–4 ── HTTP/JSON ──> ASP.NET Core 业务服 ──> MySQL
       │                               │
       │ KCP/UDP + Protobuf             │ 启动/接管实例、Ready/Heartbeat/租约
       ▼                               ▼
       Unity Dedicated Server（每房间一个进程，运行权威 Gameplay）

Client、DS、业务服共同编译 Shared/Protocol 的同一份合同源码。
```

本机开发业务 API 使用 loopback HTTP；远端要求 HTTPS。客户端上报意图、做 Prediction 与表现；DS 裁决移动/AI/伤害/资源；业务服掌管长期资产与实例管理。**运行中 DS 的资源状态不等于已经可靠结算入库。**

## 2. 新 Agent 的最小阅读顺序

1. 读本文件，检查外层 Git 状态，保留所有不属于本任务的未提交/未跟踪内容。
2. 按任务读取下面的专项入口；任务涉及 Unity 时，**改动前必须读 `RPG/AGENTS.md`**。
3. 对照对应计划的最新接续段、当前源码/资源和验收证据；不要仅凭 README 的历史阶段判断当前功能。
4. 开工说明“已完成什么、本轮具体做什么”；结束报告修改、实际验证、失败/未测范围和下一步。

| 任务 | 优先阅读 |
|---|---|
| 理解整体架构与玩家路径 | [项目架构与目录导览](RPG/Assets/_Project/Docs/项目架构与目录导览.md) |
| Unity 玩法、场景、模块与表现 | [Unity Agent 指南](RPG/AGENTS.md)，再读涉及的 asmdef/脚本/Scene/Prefab/SO |
| 业务启动/认证/API | `Server/Norn.Business/Program.cs`、`Bootstrap/`、`Features/Accounts/` |
| 线上库存与入局资产 | `Server/Norn.Business/Features/Inventory/`、`Features/Runs/RunEntryService.cs`（后者同属 Norn.Business） |
| 匹配、实例、Ticket、接管 | `Server/Norn.Business/Features/Rooms/`，重点 RoomMatchmakingService / RoomInstanceService / RoomLauncher / RoomInstanceSupervisor |
| MySQL、事务与迁移 | `Server/Norn.Business/Infrastructure/`、`Server/Norn.Business/Migrations/`、`Server/Norn.Business.Tests/` |
| 业务 DTO / 纯合同 | `Shared/Protocol/Runtime/Business/` 与两端调用方 |
| 实时消息/协议兼容 | `Shared/Protocol/Protos/`、`Runtime/Generated/`（同属 Shared/Protocol）、`RPG/Tools/Networking/GenerateProtocol.ps1`、Codec/Reset 与两端消费者 |
| 人工安装/IDE/Editor 操作 | [双端开发基础指南](<RPG/Assets/_Project/Docs/2026-10-21 双端开发基础指南（是什么、为什么、怎么操作）.md>) |
| 联机计划/授权与范围 | [联机总体执行方案](<RPG/docs/superpowers/plans/2026-10-20 Norn多人联机系统总体执行方案.md>) |
| 联机逐项实现/Debug/证据 | [联机系统开发解析](<RPG/Assets/_Project/Docs/2026-10-20 联机系统开发解析（更新中）.md>) |
| 当前 N5 材料世界开发 | [N5 实施计划](<RPG/docs/superpowers/plans/2026-10-29 N5共享材料世界与分块恢复实施计划.md>)、[N5 开发解析](<RPG/Assets/_Project/Docs/2026-10-29 共享材料世界N5开发解析（更新中）.md>) |

## 3. 当前实现与验收状态

以下是 2026-10-31 对仓库现有记录的核对，不表示本轮重新运行了功能测试：

- **单机已有**：Hub/Home、P7/P8、三角色控制与战斗、NPC/Boss、真实库存、Mana/次数/冷却、玩家法术程序、研究台与材料玩法；不要按早期文档重复开发这些系统。
- **N0–N3 本机阶段已验收**：工程/传输、账户与线上资产、房间匹配/准备、独立 DS 入场与重连、权威移动/Prediction/Replica/AI 均有实现。不是“目前只到 N2”。
- **N4 代码与本机交付已有**：权威战斗、正式 P7/P8 装配、共享 HUD/背包与线上库存适配；完整清场、Boss 后期/击杀和最新双 Native Client 主观手感仍待确认。`N4Complete=false`、`FormalOnlineExperienceAccepted=false`、`ReleaseReady=false`。见 `RPG/docs/superpowers/evidence/multiplayer/n4/final-audit/acceptance.json`。
- **当前 N5 已获用户授权并在开发中**：已验收基础包括版本栅栏、CPU 权威时钟/采样、元数据 Journal、World 事务隔离、逻辑占据规范化。最近完成证据为 `RPG/docs/superpowers/evidence/multiplayer/n5/logical-occupancy/acceptance.json`，`N5Complete=false`。
- **后续复制代码已经存在但未完成验收**：CommitOwner、Payload codec/assembler/installer、ReplicaStore、Interest、PeerState 等不能当作缺失重建。现有 `RPG/docs/superpowers/evidence/multiplayer/n5/replication/integration-red.json` 保存 63 passed / 9 failed，未见该目录最终 acceptance；接续时先重查源码与测试，不能将保存的失败直接当作当前运行结果。
- **仍未完成的 Gate**：材料复制完整接线/恢复/四人压力与原生端性能、N6 可靠持久结算、后续远端恢复与安全验收。Windows loopback 结果不能证明 Linux/公网/IL2CPP/D3D12；公网加密分配及两条 native Built-in Resource 启动错误仍未解决。

源码/序列化资源决定“实现了什么”，实际测试/Profiler/验收决定“验证了什么”，计划说明“要做什么”。旧 README 与旧 Task 保留历史，不作为最新阶段的唯一依据。用户进入 N5 的授权不等于 N4 体验已通过，也不应再被旧“等待进入 N3/N5”说明阻塞。

## 4. 全仓库通用协作规则

- 默认简体中文，保留英文技术术语。用户求职方向 Unity/C#/Graphics，解释职责、原理、控制流和 trade-off，而不只给代码；错误前提直接纠正，缺少关键文件/信息时明确指出并确认，不猜测事实。
- 非平凡功能先讨论，写**一份中文详细实施计划**并确认；跨模块/架构变更必须先对齐。已有授权计划继续执行，不重复询问同一许可。计划统一放 `RPG/docs/superpowers/plans/`，默认不另建 spec。
- 当前直接在原工作区修改，不主动创建 worktree。允许按需使用 SubAgents并明确文件所有权；Editor、服务、测试、构建、协议生成和 Git 由主 Agent 集中协调。
- 新增脚本和实质改动，**客户端与服务端都写足量简体中文学习注释**：职责、分层原因、关键流程、不变量、生命周期/API 约束和设计取舍；不机械复述赋值。
- Unity 使用 `Game.*` namespace 与 GameLog；业务服沿现有 `Norn.Business.*` namespace、DI 与 ILogger 习惯，不把 UnityEngine/EventBus/GameLog 引入后端或 Shared。
- 人工学习流程通过 Unity Hub、Unity Editor、Visual Studio 的具体界面/配置讲解；自动化可使用现有脚本，但不默认要求用户照抄终端命令。
- 每 Task 更新对应中文 living explainer，说明文件职责、架构接入、实现理由、实际测试与 Debug；阶段未完保留“更新中”。基础操作写基础指南，实现历史写开发解析。
- 不自动提交/推送；需要提交时采用 Conventional Commits。禁止回滚无关改动、清空工作区或按进程名批量结束 Unity/dotnet/MySQL。

## 5. 必须保留的系统边界

- 主菜单 Login/Register，**单机也须登录**；进入 Hub 需本次成功 Profile，Token 只驻内存。模式选择在 Hub 副本门 F 交互，不能恢复主菜单 Online Mode 或离线绕过。
- 本地存档与线上资产隔离；未知 HTTP 结果不静默回退单机。返回恢复原 Hub，会话重连使用更高 ConnectionEpoch，不能补满已经消耗的资产。
- 传输连接不等于业务认证；DS 进程启动不等于 Ready。JoinTicket 一次性消费；玩家 Session、Instance secret、连接 Epoch 各有职责，不能互相替代。
- Gameplay 权威在 DS，长期持久化权威在业务服/MySQL；Client/Replica 不授予伤害、资源或结算。Frozen/租约过期须阻止模拟，恢复不得积累追赶债务。
- 单机/联机复用原 P7/P8 内容和同一套 Tab 背包、道具栏、准心、状态、HP/Mana。战斗区由统一 CombatPresentationHost + Scope/Context 装配，不逐地图另做 HUD。
- 共用 UI/应用服务可 DontDestroyOnLoad，场景 Actor/敌人/权威不常驻；跨图必须绑定、解绑、清理旧数据源。细节与 asmdef 依赖表见 Unity 指南。
- Shared 只放中立合同/Schema/纯规则；禁止反向引用业务实现、数据库或 UnityEngine。修改合同必须同时检查业务服、Client、DS、Codec、消息池 Reset、兼容版本与测试。
- `.proto` 是消息单一来源，唯一生成输出为 `Shared/Protocol/Runtime/Generated/`；禁止手改生成 C# 或另导入协议 DLL。`ProtocolMessageReset.cs` 等池化 Reset 必须覆盖新字段和 unknown fields。`.proto` 每字段/枚举值中文注释放分号后同行。
- 正式实时房间用 RoomProtocolEnvelope（Mirror ID 41798）；旧 ProtobufEnvelope 的 44385 与 Mirror 内置消息冲突，仅供 N0 lab，不能 ReplaceHandler 掩盖冲突。
- 线上资产事务保留版本、租约、Reservation、幂等和失败边界；已应用数据库迁移不得重写，尤其保留 001–003 Hash。源码中存在 Runs/资产快照不能被解释为 N6 可靠结算已完成。
- 材料世界保留 Fire/Legacy 强度与 Liquid/Solid/Gas 原生格数量区别；逻辑占据变化、Storage 表示交接、Chunk 网络 Revision 和渲染脏版本不能混用。完整验证/原子安装后才推进复制版本并 ACK。

## 6. 环境、布局与敏感内容

- 唯一 Git 根是当前外层目录 `F:/Develop Project/RPG/`；不要在 RPG/Server/Shared 初始化嵌套 Git。Clone 整仓库并恢复 Git LFS，三组件作为同一版本维护。
- Cursor 打开外层根；Unity Hub 添加内层 `RPG/`；Visual Studio 打开 `Server/Norn.Online.sln`。
- Unity 的 `RPG/Packages/manifest.json` 使用 `file:../../Shared/Protocol`；业务服 ProjectReference 指向同一 Shared 源码。保留兄弟目录布局。
- 固定：Unity **6000.3.16f1** / URP **17.3.0**；Mirror **96.11.3**；Google.Protobuf **3.34.2** / protoc **34.2**；SDK **10.0.402**、.NET/ASP.NET Core **10.0.13**；Shared **netstandard2.1**，不能随业务服改成 net10.0。
- SDK 在 `RPG/.tools/dotnet`；自动化使用 `RPG/Tools/Networking/Invoke-OnlineDotNet.ps1` 配合 `Server/global.json`，保留系统 SDK。版本、下载来源及 Hash 查 `RPG/Tools/Networking/dependency-lock.json` 和各 `packages.lock.json`。
- MySQL **8.4.13** 使用私有 loopback **13384** 实例，dev/test Schema、runtime/migration/control 用户分离；不操作系统已有 MySQL。启停核对真实 PID/启动时间/路径，认证 SHUTDOWN，禁止批量结束进程。
- 不提交秘密、Token、密码/连接串、本地 SDK、数据库数据、Library/Temp/obj/bin、构建缓存或大型原始 Profiler 数据。第三方合法运行依赖与 License 按现有仓库规则保留。
- Unity `.cs`/资产与 `.meta` 共同维护；移动/重命名一起移动保留 GUID，新 .meta 由 Unity 生成。保留 ContentIds、序列化字段、场景路径及已有本地存档。
- 测试/构建不能覆盖正式 Scene、全局 Build Scene List 或遗留错误证据；服务进程只操作当前任务明确拥有的实例。生产 Rooms/Instances 默认关闭，本机开发入口启用不是发布验收。

## 7. 按修改范围验证和交付

| 修改范围 | 需要核对的证据 |
|---|---|
| 纯文档 | 链接/路径、事实与状态、Git diff；无需为文档启动业务服务或 Unity 测试 |
| ASP.NET Core / MySQL | Norn.Business.Tests 相关 xUnit、HTTP/真实 MySQL 测试；保持测试 Schema 隔离 |
| Shared / Schema | 生成一致性、Shared/业务服构建测试，以及受影响 Unity Codec/Reset/消费者回归 |
| Unity 纯规则/玩法 | 对应 EditMode/PlayMode；Scene/Prefab/SO、完整玩家路径、必要的 Client/DS 构建 |
| 联机体验/性能 | 真实业务入场、身份与 Frozen/重连、双端可见表现、校准有效的 Profiler；不能由纯算法测试替代 |

- `RPG/Tools/Networking/Test-OnlineFoundation.ps1` 覆盖协议生成检查、业务测试及真实 HTTP smoke，不证明 KCP、共享材料、完整 Gameplay 或 Zero-GC。
- 稳态热路径避免 managed allocation、LINQ、boxing、closure/集合扩容，提前创建状态与缓冲区；不要把后端离散 HTTP 请求的一切合理分配误套成 Unity 每帧禁令。
- 当前 Unity 上 GC.GetAllocatedBytesForCurrentThread 曾对已知 1KiB 分配仍返回 0；方法范围用**正校准 GC.Alloc event recorder**，全帧另用有效 ProfilerRecorder/Profiler。方法零 events 不代表 Native 全帧 0B 或无卡顿。
- 仅实际运行才报告编译/测试/性能通过，历史记录注明日期与范围。保留失败和未测项；人工战斗操作困难时给出明确步骤与预期，不无限重试。
- 最终交付包含文件、实际验证与局限、剩余风险/人工项和下一步；不要把代码存在、构建成功、本机功能通过与完整发布验收合成一个“全部完成”。
