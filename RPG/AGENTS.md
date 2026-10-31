# Norn: The Lost Spells — Agent 项目指南

适用于 Cursor、Codex 等 coding Agent。最后核对：2026-10-31。
本文件补充内层 Unity 工程（Client + Dedicated Server）的专项细则；全仓库总览、业务服/Shared 入口与通用规则见 [外层 AGENTS.md](../AGENTS.md)。下文未特别说明的路径均相对本文件所在的 `RPG/`；不要将这些相对路径用于外层根目录。

## 1. 先读这里：项目与接续位置

- **项目定位**：Unity RPG，结合搜打撤、Noita-inspired 材料世界与法术编程；正在扩展为 **2–4 人 Dedicated Server 合作副本**，不扩展为 MMO 持续大世界。ElementWorld 是有数量、反应和玩法后果的材料世界，不是局部 VFX。
- **现有单机**：Hub/Home 整备、P7 普通副本、P8 Boss、库存/拾取/投放、法杖编辑与研究、真实 Mana/次数/冷却、角色与敌人战斗均有实现。不要按旧文档重新实现 Mana，或把玩家法术编程退回普通技能表。
- **现有联机**：N0–N3 本机阶段已验收；账户、线上资产、匹配、独立 DS、JoinTicket、重连、权威移动/Prediction/Replica 已存在。N4 已有权威战斗、正式 P7/P8 装配、共用 UI 和线上库存适配。
- **N4 验收边界**：代码检查和本机交付已完成，完整 P7 清场、Boss 后期/击杀及最新双 Native Client 主观手感仍待确认；`N4Complete=false`、`FormalOnlineExperienceAccepted=false`、`ReleaseReady=false`。
- **当前主线 N5**：用户已明确授权共享材料世界开发，无需再次等待“是否进入 N3/N5”的旧阶段许可；此授权不代表 N4 正式体验通过。最近有完成证据的小步是 **N5-1C 逻辑占据规范化**，整个 `N5Complete=false`。
- **未验收的工作区代码**：已有 `WorldReplicationCommitOwner`、Payload codec/assembler/installer、`WorldReplicaStore`、Interest 和 PeerState 等后续源码。它们不能被当作不存在而重建，也不能只凭文件存在称为完成。最新保存的 `n5/replication/integration-red.json` 为 63 passed / 9 failed，未见该目录最终 acceptance；接续时先核对源码、失败项与最新测试。
- **后续范围**：材料复制完整接线、冻结 Baseline/Delta、四人 Interest 与原生端性能仍需验证；N6 可靠持久结算、后续远端恢复与安全 Gate 不能视为已完成。

### 最小阅读顺序

1. 阅读本文件，检查外层 Git 工作区状态；保留已有未提交/未跟踪内容。
2. 读 [项目架构与目录导览](Assets/_Project/Docs/项目架构与目录导览.md)，定位任务涉及的入口。
3. 读对应阶段计划、学习文档的最新接续段和 `acceptance.json`，再读当前源码/资源；不必一次读完全部历史文档。
4. 核对实际 `.asmdef`、场景/Prefab/SO 和调用链后再改动。不确定时明确缺少的文件或信息，向用户确认，禁止猜测项目事实。
5. 开工先说明“已完成什么、本轮具体做什么”；结束说明“改了什么、实际验证、失败/未测范围、下一步”。

**事实判定**：当前源码/序列化资源说明实现，实际测试/Profiler/验收记录说明验证范围；计划描述目标。旧 README、旧 Task 中“下一步/尚未实现”可能过时。发现矛盾应指出并核对，不能拿历史通过覆盖当前失败，或把历史失败直接断言为当前仍失败。

### 当前文档与证据入口

| 用途 | 路径 |
|---|---|
| 架构与源码导航 | `Assets/_Project/Docs/项目架构与目录导览.md` |
| 人工 IDE/Editor 操作、基础原理 | `Assets/_Project/Docs/2026-10-21 双端开发基础指南（是什么、为什么、怎么操作）.md` |
| 联机 N0–N4 实现、取舍、Debug 与验收 | `Assets/_Project/Docs/2026-10-20 联机系统开发解析（更新中）.md` |
| 联机总计划与阶段顺序 | `docs/superpowers/plans/2026-10-20 Norn多人联机系统总体执行方案.md` |
| N5 当前计划 | `docs/superpowers/plans/2026-10-29 N5共享材料世界与分块恢复实施计划.md` |
| N5 学习与接续 | `Assets/_Project/Docs/2026-10-29 共享材料世界N5开发解析（更新中）.md` |
| 玩家法术编程 V2 | `Assets/_Project/Docs/2026-10-18 法术编程V2开发解析（更新中）.md` |
| N4 最近交付 | `docs/superpowers/evidence/multiplayer/n4/final-audit/acceptance.json` |
| N5 最近已验收基础 | `docs/superpowers/evidence/multiplayer/n5/logical-occupancy/acceptance.json` |
| N5 后续开发失败记录 | `docs/superpowers/evidence/multiplayer/n5/replication/` |

## 2. 沟通、学习与授权规则

- 默认简体中文；保留 `Rasterization`、`GC Alloc`、`Prediction` 等英文专业术语。用户是北京交通大学通信工程硕士（2026 届），求职方向 Unity/C#/Graphics 客户端开发。
- 解释“是什么、为什么、怎么做”和 trade-off；发现错误前提直接纠正并给出原理/证据。设计优先考虑玩家体验，尤其是背包、物品密集 UI 与跨图流程。
- 非平凡功能通常遵循：讨论设计 → **一份中文详细实施计划** → 用户确认 → 分阶段执行。跨模块/架构变更必须先对齐；已有明确授权的计划继续执行，不重复索要同一阶段许可。
- 计划保存在 `docs/superpowers/plans/`，包括理由、替代方案、架构、精确文件、测试、Editor 设置、验收和回滚/Debug。默认不另建 spec，除非用户要求。
- 当前项目在原工作区直接修改，不主动创建 worktree。用户允许按需使用 SubAgents；明确文件所有权，Unity Editor、测试、构建、协议生成、服务与 Git 操作由主 Agent 集中协调。
- 阶段完成后先核对实际证据与 Editor Gate。操作困难或反复死亡的可见战斗可留作明确人工项，给出操作与预期，不无限重试；确定的编译/逻辑错误仍须修复。
- 人工教学通过 Unity Editor、Unity Hub、Visual Studio 的具体点击与配置讲解。仓库脚本可用于 Agent 自动化，但除非用户要求，不把终端配方当学习流程。
- 不自动提交/推送；需要提交时使用 Conventional Commits。不要清理、回滚或覆盖不属于本任务的 dirty files。

## 3. Monorepo、环境与目录

```text
外层 RPG/                       ← 唯一 Git 根；Cursor 建议打开这里
├── AGENTS.md                   ← Monorepo 入口，要求读取 RPG/AGENTS.md
├── RPG/                        ← Unity Hub 应打开这里；本文件所在位置
│   ├── Assets/_Project/
│   ├── Packages/
│   ├── ProjectSettings/
│   ├── Tools/Networking/
│   └── docs/superpowers/
├── Server/
│   ├── Norn.Online.sln
│   ├── Norn.Business/           ← ASP.NET Core 业务服
│   └── Norn.Business.Tests/     ← xUnit / 真实 MySQL 业务验证
└── Shared/Protocol/            ← 纯 C# 合同 + 本地 UPM 包
    ├── Protos/                 ← .proto 单一来源
    └── Runtime/Generated/      ← 唯一协议生成输出
```

- Git 操作属于外层 `F:/Develop Project/RPG/`；不要在组件中初始化嵌套仓库。完整 Clone 并恢复 Git LFS，不能只复制内层 Unity 项目。
- `Packages/manifest.json` 使用 `file:../../Shared/Protocol`，必须保留同级布局。`Shared` 是库，不是 Web Host；DS 是从 Unity 工程构建的产物，源码不在 ASP.NET Core 项目里。
- 固定版本：Unity **6000.3.16f1**、URP **17.3.0**、Input System **1.19.0**、Cinemachine **3.1.6**、Mirror **96.11.3**、Google.Protobuf **3.34.2** / protoc **34.2**。
- 业务 SDK **10.0.402**、.NET / ASP.NET Core **10.0.13**；Shared TargetFramework 为 **netstandard2.1**。本地 SDK 在 `RPG/.tools/dotnet`，使用 `Tools/Networking/Invoke-OnlineDotNet.ps1` 配合 `Server/global.json`，保留系统 SDK。
- 版本/来源/Hash 以 `Tools/Networking/dependency-lock.json`、各项目 `packages.lock.json`、`ProjectSettings/ProjectVersion.txt` 和 `Packages/manifest.json` 为准，不为本轮任务顺便升级。
- 不提交秘密、Token、连接串、SDK、数据库数据、`Library/Temp`、构建缓存及原始大型 Profiler 数据。

| Unity 路径 | 责任 |
|---|---|
| `Assets/_Project/Scripts/<模块>/` | Runtime 源码，按 asmdef 分域；实际文件夹如 Core/Combat/Run，并非 Game.Core 目录名 |
| `Assets/_Project/Editor/` | Editor 工具，程序集统一为 `Game.Project.Editor` |
| `Assets/_Project/Tests/` | 模块 EditMode 与 RunPlayMode 等测试；测试资产在 Fixtures |
| `Assets/_Project/Scenes/` | 正式场景；测试/Review 场景在 Scenes/Tests，不能混入正式 Hub/Home |
| `Assets/_Project/ScriptableObjects/` | 按业务责任组织的 SO 数据 |
| `Assets/_Project/Resources/Online/` | 联机定义与 Content/Gameplay Manifest |
| `Assets/_Project/Resources/Presentation/` | 共用战斗表现资源 |
| `Assets/_Project/Art/` | 美术、Animator、Materials；另有 Prefabs 目录 |
| `Assets/ThirdParty/Networking/Mirror/` | 已锁定且有本地补丁声明的 Mirror 源码 |
| `docs/superpowers/evidence/` | 机器可读验收与失败/诊断证据 |

生产资产按职责归类，不再建阶段/版本目录。法术资源在 `ScriptableObjects/Spell/{Definitions,Items,Wands,Programs,Libraries}`，其他物品在 Items，世界/研究台/副本配置在 World，输入在 Input；Materials 在 Art/Materials。保留 ContentIds（包括 `norn.v2.*`）、序列化字段和场景路径，不把存档 ID 当新目录名。

## 4. 架构与权威边界

数据优先放在 ScriptableObject。每个领域独立 asmdef；下表为第一方依赖，实际变更前核对 `.asmdef`，不能越过权威 Owner 直接写另一模块状态。

| Assembly | 第一方依赖 / 责任 |
|---|---|
| Game.Core | 无上层依赖；基础、合同、事件、只读接口 |
| Game.Materials | 无第一方依赖；材料基础数据 |
| Game.NetworkProtocol | 无第一方依赖；来自 Shared 的纯合同，Google.Protobuf 是第三方 |
| Game.Combat | Core、Materials；伤害、状态、投射物、资源与模拟权限 |
| Game.Skills | Core、Combat；法术数据、编译与纯解释器 |
| Game.Run | Core、Combat、Skills；库存、资源预约、施法事务与局流程 |
| Game.Character | Core、Combat、Skills、Run；角色、AI、交互与 Unity runtime bridge |
| Game.ElementField | Core、Combat、Materials；材料 Store、模拟、真实数量与投放/收取 |
| Game.Rendering | Core、Combat、ElementField（只读）、Materials；表现 |
| Game.UI | Core、Combat、Skills、Run；只读数据与命令请求 |
| Game.Networking | Core、NetworkProtocol；连接/协议适配，无 Gameplay 权威 |
| Game.Multiplayer | Core、NetworkProtocol、Networking、Character、Combat；Authority/Prediction/Replica 装配 |

- 共 **12 个第一方 Runtime Assembly**，其中 NetworkProtocol 位于 Shared，另有 Editor/Tests Assembly。不要沿用“9 个模块”的历史描述。
- 新代码进入依赖足够的最低层。Core 不引用上层；Combat 不反向引用 Skills/Character/ElementField；Gameplay 不引用 UI/Rendering；UI/Rendering 不引用 Character。
- 跨模块全局通知使用 Core 的 `EventBus<T>`；已允许的依赖通过合同/只读服务调用。事件必须为 `struct + IGameEvent`。
- `OnEnable` 订阅、`OnDisable` 退订，保证退出/重入清理。持久服务与场景订阅并存时，不可随意对全局事件 `Clear()` 误删仍有效的订阅。
- ASP.NET Core 业务服负责账户、持久资产、匹配、实例与 Ticket，访问 MySQL；DS 负责 Unity Gameplay/Physics；Client 负责输入、Prediction 与表现，Replica 无伤害/扣资源/AI 写权。
- `ExecutionMode` 与生命周期须明确区分 SinglePlayer/Authority/Replica。Frozen 停止玩法推进，不积累恢复后的追赶债务；Prediction 重放不能重复扣费、发包或创建真实输出。

## 5. 从玩家操作定位源码

| 玩家/系统链路 | 优先入口（相对 Scripts/） |
|---|---|
| 主菜单登录 → 本次 Profile 成功 → Hub | `Multiplayer/Accounts/OnlineAccountBootstrap.cs`、`UI/Online/AccountMenuController.cs` |
| Hub 副本门 F → 单机/匹配选择 | `Run/Runtime/DungeonEntryChoiceGate.cs`、`Multiplayer/Rooms/OnlineRoomClient.cs` |
| 业务房间 → DS Ready → 一次性 Ticket | `../Server/Norn.Business/Features/Rooms/`（相对 Unity 根）、`Multiplayer/Runtime/RoomInstanceBootstrap.cs` |
| 权威移动、预测、插值与 Tick | `Multiplayer/Movement/OnlineWorldBootstrap.cs`、`AuthorityTickDriver.cs`（同目录） |
| 联机施法与资源事务 | `Character/Spells/OnlineCastDriver.cs`、`Run/Spells/OnlineOwnedWorkspaceFactory.cs`、`Multiplayer/Combat/OnlineCombatWorld.cs` |
| 正式 P7/P8 在线内容 | `Character/World/OnlineDungeonRuntime.cs`、`OnlineDungeonDefinition.cs`（同目录）；配置在 ScriptableObjects/World/Online |
| 单机库存/物品唯一归属 | `Run/Runtime/SinglePlayerInventorySession.cs`、`ItemOwnershipStore`；跨图由 `HubPracticeSession` 等原会话链路管理 |
| 统一战斗 UI | `UI/Combat/CombatPresentationHost.cs`、`CombatPresentationScope.cs`、`CombatPresentationContext.cs`（同目录） |
| 原背包共用视图与数据源 | `UI/Inventory/PracticeInventoryWorkspaceBinding.cs`、`Run/Inventory/IInventoryPresentationSource.cs`、`OnlineInventoryPresentationSource.cs`（后两者同目录） |
| 材料权威与采样 | `ElementField/Runtime/ElementWorldRuntime*.cs`、`ElementField/Replication/WorldReplicationSource.cs` |
| N5 当前基础与开发中复制 | `Core/Online/WorldReplicaRevisionState.cs`、`WorldChunkCommitJournal.cs`、`WorldMaterialOccupancySnapshot.cs`、`WorldReplicationCommitOwner.cs`（同目录）；以及 ElementField/Replication、Multiplayer/World |

### 角色与战斗约束

- 玩家使用 Input System，禁止 legacy `Input`。角色用 `CharacterController`，`Move()` 在 Update 中执行；投射物用 Rigidbody。CameraRoot 在 LateUpdate 旋转，Cinemachine 跟随。
- `PlayerControllerBase` 共享移动/跳跃/冲刺/FSM；Warrior/Archer/Wizard 各自持有攻击语义与 Animator。Warrior/Archer 为未来变形术保留，不能因主角是 Wizard 删弱其 Controller/状态/Prefab。
- 状态为普通 C# 对象，在 Awake 预创建；ChangeState 执行 Exit → 切换 → Enter，Exit 负责释放状态资源。共享攻击入口为 `TryStartAttack/TryStartAirAttack`，保持 Dash → Attack → Jump 优先级。
- buffer-counter 统一管理 Jump/Attack/Dash 与 Coyote Time；输入记录、倒计时、消费不可散落成互相冲突的状态。
- 本地动画驱动攻击窗口读取 normalizedTime/SO；一次触发检查 state hash 与已触发标记。Attack/Dash/Charge 用 CrossFade 进入，状态名 Awake 预 Hash；Animator 仍须配 EXIT transition。联网动作由已有 authority timeline/事实驱动，不能用客户端 Animator 时间授予伤害。
- 伤害统一经过 `IDamageable/HealthComponent/DamagePipeline`；DamageRequest 是值快照。保持队伍过滤、每击去重、同队 Projectile IgnoreCollision 与触发 payload 的生命周期接口，避免新伤害旁路。
- `StatusController` 统一 Burning/Wet/Poisoned/Sticky、DoT 与反应；扩展现有规则，不另建平行状态系统。

### 法术编程与归属

- **玩家正式链路**：Wizard 输入 → PlayerWizardAttackState → Game.Run 的 owned 施法事务 → `SpellProgramCompiler/SpellProgramMachine` → 冻结 graph/commands → SpellCaster → Projectile → Combat。Run 管库存、Mana/次数/版本、资格、Reservation 与逐层提交；纯解释器不授予资源。
- Deck/Hand/Discard、Draw/Copy、有界执行、独立 trigger scope 是玩家程序语义。同 scope 的最终 Modifier 覆盖其中所有输出，包括此前抽出的输出；payload 冻结后在声明生命周期释放，命中时不重读已变 SO。
- **NPC/Boss 独立路径**：`CastEvaluator` + 只读 WandLoadout + SpellCaster.CastWand 仍有效，不创建可编辑玩家会话；维护时保留其预算、触发与 Mana policy。
- 不恢复已退役 RunSpellSession/RunSessionCoordinator、Palette/Frame editor、P7 起始全库或出口自动解锁全法术。研究台只在允许的 Hub/Home 区域编辑真实实例并复用执行图；背包保留紧凑编辑。
- `DungeonFinalRewardPickup` 发有限随机奖励，背包满保留 pending；退出返回 Hub。携带库存离图前保存，失败保留场景和资产。Editor direct-scene preview 是内存会话，不是正式存档。
- 永久法术知识与物品实例区分；涉及新成长规则时沿已确认的目标完成/成功撤离设计，不因拿到一张卡直接改永久解锁。

### 共用表现与生命周期

- 单机和联机复用原 P7/P8 场景内容、Tab 背包、左上道具/快捷栏、准心、状态 Icons、血条/Mana 和操作体验；适配数据源与授权命令，不另做正式在线 HUD。
- 每个可战斗区域通过 **CombatPresentationHost + Scope/Context/配置** 自动接入。共同资源是 `Assets/_Project/Resources/Presentation/CombatHud.prefab`；新增副本配置内容，不逐图开发 HUD。
- 可复用 UI 和账户应用服务可 DontDestroyOnLoad；角色、敌人、玩法权威、Scene/Run 归属不跟着常驻。进入 attach、换源 rebind、退出 detach/清引用，验证重复进入不叠 UI、不残留旧 HP/Status/Boss/输入占用。
- `MovementWorld/OnlineCombatHud` 为历史技术验收设施，不能取代正式内容的完整体验 Gate。UI 请求不确定或源未就绪时，不能用保留的 Hub 本地资产冒充线上数据。

## 6. 联机和材料维护时不可破坏的边界

- 主菜单直接 Login/Register，**单机也须登录**；进入 Hub 必须得到本次成功 Profile。Token 仅驻内存，由独立账户根持有，不交 UI Scene 管理。
- 模式选择只在 P7/P8/远征入口 F 交互；返回/撤离门保持原职责。不恢复主菜单 Online Mode 或离线绕过。
- 线上资产与本地档隔离；匹配/取消 HTTP 结果未知时禁止悄悄退回单机。认证后才装配在线世界；返回恢复原 Hub 会话，重连保留 Actor、提高 ConnectionEpoch。
- 2–4 人同 Dungeon 匹配，ReadyRevision/资产版本 → 队长 Start → DS Ready → 一次性 Ticket。进程启动不等于 Ready，传输连接不等于账号认证；迟到包先验当前 connectionId/ConnectionEpoch 再推进 sequence/rate 状态。
- 正式房间用 `RoomProtocolEnvelope`，Mirror ID **41798**。旧 `ProtobufEnvelope` 的 **44385** 与 ObjectDestroyMessage 冲突，仅留 N0 lab；不能 ReplaceHandler 掩盖冲突。
- JoinTicket 与 Instance secret/玩家 Session 分离，数据库只存 Hash。保留既有 migration Hash，尤其 001–003；不随意重写已应用迁移。协议改 `Shared/Protocol/Protos/*.proto` 后生成，禁止手改 Generated 或重复导入协议 DLL。
- DS Ready timeout 45s、heartbeat 15s、freeze 60s、lease 120s，CanSimulate 还须业务 Ready。业务 Host 停止可保留 DS；接管/停止须核对 PID、UTC StartTicks、规范化路径，不按名称批量杀进程。
- MySQL **8.4.13** 使用独立 loopback **13384** 实例，dev/test Schema 与 runtime/migration/control 用户分离；不操作系统已有 MySQL。用 --no-monitor 识别真实 PID，认证 SHUTDOWN，凭据留 ignored .tools。
- NornKcpTransport Raw IO 当前只验证 Windows IPv4 loopback；`DualMode=false` 须在 Awake 前序列化。RoomManager/Transport 的 useGUILayout=false 防止自动布局每帧分配。
- 保留 `NORN_LOCAL_PATCHES.md` 与 dependency-lock 声明的 Mirror 补丁/Hash。普通 Server Build 保留原 Rendering/Resources 输入；诊断隔离菜单不代表正式配置。Build scope 按 GUID/路径与原设置字节恢复资源，不用已销毁的 Unity managed reference 恢复。
- `ElementWorldStore` 的 Fire/Legacy byte 强度不是全部材料数量；GridLiquid 的 Dynamic/Stable/Pending、FixedSolid、Gas 各有独立原生格与 uint 数量。禁止逐细格重复计量粗格，也不能只发送 Fire 或液面当完整世界。
- Authority 复用唯一 CPU 材料时钟，Replica 不执行反应/DoT/收取/投放；headless 保留必要 CPU Collider，不能依赖 GPU 玩法权威。
- WorldEpoch、ConnectionEpoch、Chunk Generation、Revision 各司其职；渲染脏版本/WorldTick 不能代替已提交网络版本。非法高版本候选不推进水位；完整验证和原子安装后才能提交版本/ACK。
- 库存投放/收取覆盖 World 级事务互斥、Stage/Undo/Commit/Cleanup；异常 Faulted 不等于回滚。Stable↔Dynamic 表示交接不一定是数量变化，失败准备也可能改变表示，不可据此制造资源 Delta。
- 已确认性能策略：优先角色操作、关键战斗反馈和附近材料，远处显示可延后但不能漏量/无限积压。复用 Mirror Batching；30Hz 移动、15Hz 战斗快照、60Hz 网络 pump、10Hz 材料职责分开，不强制塞进同一巨包。
- N0/N1/N2 已完成的本机 Gate 不要求用户无故重做。公网加密分配、两条 native Built-in Resource 启动错误仍未解决；Linux/公网/IL2CPP/D3D12 不得由 loopback 结果外推。生产 Rooms/Instances 部署默认关闭，本机开发入口的启用不等于发布许可。

## 7. 编码、注释与性能

- 命名：私有/保护字段 `_camelCase`，公开成员/类型 PascalCase，接口 IXxx；每个脚本声明与程序集匹配的 `Game.*` namespace。
- 日志统一 `Game.Core.GameLog.Info/Warn/Error`，禁止直接 Debug.Log。GameLog 的 Conditional 在 Release 去除调用点及参数计算。
- 客户端、服务端的重要类/方法与实质改动都写足量简体中文学习注释：职责、分层原因、控制流、不变量、生命周期/API 限制、数学/图形概念和 trade-off；不只写缩写或逐行复述赋值。
- `.proto` 定义上方写摘要，按职责紧凑分组；每个字段/枚举值注释放分号后同行，说明业务含义、单位/默认值/兼容约束。生成类型的解释写 Schema 后重新生成。
- Update/FixedUpdate/每帧热路径禁止 managed allocation、LINQ、boxing、closure/delegate 新建和集合扩容；值类型 struct 的 `new` 语法本身不等于堆分配，检查实际存储/boxing。状态、缓冲区和处理委托预创建；离散操作的合理一次性分配与稳态性能分开说明。
- UnityEngine.Object 的销毁状态用 `==/!=` 判断，不能依赖 `??/?.` 的纯 CLR null 检查。
- 重命名/移动 MonoBehaviour 必须同时移动 .cs 和原 .meta，保留 GUID；不删除重建。新资产的 .meta 由 Unity 生成，不手工编造；保留原 Prefab/Scene/SO 引用。
- Graphics/Shader 任务按初学者讲清坐标空间、向量/公式、Shader stage、Material/属性和 Rendering 取舍。外部资源依赖须在计划中提前列用途、格式/通道、质量/尺寸、Import Settings、路径和 fallback。

## 8. 验证、工具与交付

- 优先 `rg/rg --files` 定位；避开 Library/Temp/第三方大目录的无差别扫描。核对已有工具，不假定 Cursor 拥有本机 Codex 的 skills、MCP 或桌面控制能力。
- Cursor/其他 Agent 可运行可用的编译、测试与 Editor 自动化；缺少工具时给出准确人工步骤。**实际运行才报告通过**，静态审查只能报告所见问题与限制，不能称“逻辑正确”代替验证。
- Unity 测试入口 `Window > General > Test Runner`，按任务选 EditMode/PlayMode；业务测试在 Server/Norn.Business.Tests。Tools > Norn > Online/Combat 下已有 Setup/Verify/Build 菜单，先读实现确认其修改范围。
- `Tools/Networking/Test-OnlineFoundation.ps1` 验证业务/协议基础，不能代替 Unity、KCP、真实玩家流程、材料共享或 Zero-GC Gate。测试/构建/协议生成不能与另一 Agent 的共享 Editor 操作并发。
- Gameplay 验证沿完整玩家路径：输入 → 权限 → 资源预约 → 世界结果/Rendering → 场景切换 → 返回/UI；线上还须身份、Frozen、重连与旧包 fencing。
- 修改前后记录相关正式 Scene、Build Scene List、ProjectSettings、Prefab 与真实单机存档保护状态；不为测试覆盖正式资产或全局 Build Scene List，保留失败尝试与诊断证据。
- 当前 Unity 的 `GC.GetAllocatedBytesForCurrentThread` 曾对已知 1KiB 分配仍返回 0，不能作零分配证据。方法范围使用**正校准**当前线程 GC.Alloc event recorder，计量单位是 events；Native 全帧另用有效 ProfilerRecorder/Profiler。方法零事件不等于全帧 0B、无卡顿或战斗性能通过。
- 历史已验收：N4 final-audit 记录 464 EditMode、跨场次 31 个不同 PlayMode、两端构建各 0 Error；N5 logical-occupancy 记录 356 个相关 EditMode / 0 failed，方法正校准 1 event、测量 0 event。**这些是对应历史窗口，不是本轮 Run All，也不覆盖后续未验收改动。**
- 每 Task 更新对应中文 living explainer：数百字概览，Task 内从 1 编号，说明每个新增/修改脚本的职责、架构位置、接入、技术/API、取舍、测试与 Debug；阶段未完成保持“更新中”。基础人工操作留在基础指南，实现历史留在开发解析，互相链接。
- 最终交付列出实际修改文件、验证结果/证据、失败与未测项、需要的 Editor 操作和下一步。不把编译、测试、外观、完整战斗体验、稳态/全帧 GC、远端平台兼容合并成一个“全部完成”。
