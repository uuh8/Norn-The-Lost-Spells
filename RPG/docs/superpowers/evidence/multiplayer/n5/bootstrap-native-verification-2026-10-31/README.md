## 2026-10-31：第一段握手/生命周期的验证闭环

本阶段仅完成上一段的调查、资源语义审计、匹配 Native 构建与真实 KCP 验证，没有接入完整材料数据通道，也没有新增重启/重建/结算语义。两项独立只读核查显式指定 `gpt-6.1-sol / xhigh`；本地没有修改模型配置。

### 首次 Gas 错误的归因与验证流程纠正

首轮 PlayMode 0/2 的高置信度原因是**实际执行了旧测试程序集**：旧源码 SHA 与阶段前清单完全一致，`EnsureEnemy` 在 54 行；首轮日志调用栈正是 54 行。激活前配置 Binding 后应为 62 行，增加无 Renderer 断言后为 66 行；绿色运行均为 66 行，失败后日志才出现该 PlayMode DLL/PDB 的编译/复制。

旧夹具直接激活安全默认 Replica 的派生 World，未调用 ConfigureReplica 建立 readmodel；Gas Renderer 的 source 因而为空。Gas 的材质及 World 引用均存在，生产 Binding、Renderer、导出器源码在该段前后未改变。无需修改生产 Rendering 或忽略 Error。本轮纠正的是验证流程：每次复制源码后明确 `refresh-source`、等待编译结束，并保存测试 DLL/PDB 与源码哈希，再提交测试请求。没有首次 DLL/PDB 原始字节，故保留历史二进制归因的限制；不声称做过生产 Renderer 修复。详见 `gas-playmode-investigation.md/json` 与精简日志。

### 大型 Prefab diff 的实际含义

审计按 GameObject 层级、组件类型/Script GUID 对本地 fileID 归一化，保留全部字段、子对象顺序与组件相对顺序，并以四项合成自检验证不会把真实字段、顺序或悬空引用差异抹掉。

| 资源 | 改号对象 | 新增/移除组件 | 既有真实变化 |
|---|---:|---:|---|
| P7World | 581 | 20/0 | 10 个组件列表追加 |
| P8World | 71323 | 21/0 | 11 个组件列表追加 |
| P8_05 NPC | 6 | 0/0 | 0；563 个对象语义相同 |

41 个新增组件中，2 个是材料 Binding，其他 39 个逐字段对应原 Scene，包括 P8 嵌套火焰 Prefab override，未解释差异为 0。Transform、原组件相对顺序、子对象顺序不变。10 份在线 CPU Profile 逐一对应原 Profile：名称、routing 指向在线 clone，以及 Liquid/FixedSolid 补写与代码初始化值一致的默认字段；routing 原本就是 CPU。167330 处内部引用全部解析，29491 处外部引用涉及 231 个 GUID，已检查范围未发现错误。84 个导入资产的 subasset fileID 未经 importer 逐项验证，仅证明 GUID/文件存在；静态审计不冒充全部渲染验证。三个 Prefab GUID 保留，13 个资源在 Native/测试后哈希不变。见 `prefab-audit.md/json`、`prefab-source-components.json`。

### 构建、真实认证与重连证据

使用既有 `build-room-client` / `build-room-server` 流程，构建 Windows x64 **Development Mono Player** 和真正的 **Dedicated Server**，均 Succeeded/0 error。Player 以 D3D11 运行，DS 为 Null graphics；入口是 RoomLifecycleLab 诊断场景，但装配实际 P7/P8 World/NPC/材料 Profile。本阶段没有构建 MainMenu Client，不据此宣布完整玩家路径或 N4 体验通过。Managed Gameplay/Shared 程序集哈希已保存，不能以 .NET Release 构建代替这两项 Native 构建。

应用版本 3 对应认证后必需 WorldIdentity 握手；v2 只有 Dungeon 身份，不能满足该激活前合同。真实 HTTP 相同有效身份/Manifest 下，v1/v2 均返回 409，v3 返回 200。DS 启动兼容检查、JoinReply、世界身份 Codec、连接认证引用/Run/Dungeon/RoomEpoch/ConnectionEpoch 检查仍沿既有代码，未弱化为只检查非零编号。

在隔离 test Schema，通过公开 Register/Login/Profile、Ready、Start 和一次性 Ticket 接口启动两个正式地图实例。**8 项 Native 合同场景通过**：P7/P8 四次首次入场；同一运行世界三次新 Ticket 重入（P7 ConnectionEpoch 1→2，P8 1→2→3）；一次尚未到期的已消费 Ticket 立即复投被拒绝。每次正向入场均真实认证、WorldReady=true，客户端握手取得编号、实际装配编号与 DS WorldEpoch 完全相同。重连保留 ActorId 与 WorldEpoch。共运行 10 个 Native Player 进程，其中另外两次旧 Ticket 拒绝没有隔离 TTL，不算作一次性合同的独立证据；另用新鲜 Ticket 的报告时间严格早于到期时间来完成该项验证。

这里的重连由新 Native Client 进程携同一账户会话的新 Ticket 重入同一 Run；没有验证同一客户端进程内自动重连，也没有进行消耗资源后验证不补满。不能从这些结果推导死亡、掉落、撤离、结算或服务重启恢复规则。

本轮收尾同范围 25 fixtures 为 **280/0**，正式 P7/P8 PlayMode 为 **2/0**。Native 日志没有 Gas Renderer Error 或 Exception，但每个 DS 仍有两条历史 Built-in Resource 启动错误，已保留，不能写成 Native 无错误。未采集性能/GC，诊断 JSON/IO 也不适用于零分配验收。

### 失败准备、实际修改与收束

测试编排首次依据旧资产布局，错误尝试移动已装备的注册物品，得到 EquippedItemMoveUnsupported；查明当前注册已装备 Wand/嵌套 Spell 后，只修正编排，未改资产规则。私有业务启动时恢复了隔离 test Schema 里四条历史排队实例，它们在新 DS 构建完成前拒绝 LaunchCompatibilityMismatch，已单列而未算作正式 Native 结果。未清空数据库或修改真实玩家资产。

生产源码只增加两个现有诊断报告的只读 WorldEpoch/WorldReady 字段：`Multiplayer/Diagnostics/RoomAdmissionProbe.cs` 与 `Multiplayer/Runtime/RoomInstanceBootstrap.cs`。没有新增源码目录、修改协议语义或 Gameplay；复用现有诊断层。构建/回归产生的 DynamicsManager 格式自动重写，在起始哈希核对完全一致后仅恢复自身副作用。最终 12 个保护文件不变，用户 Recovery 内容保留。Native/业务/MySQL 只清理本轮身份核对一致的进程，保留原 Unity 会话，不提交/推送。

目前没有权限或待用户决策阻塞。剩余风险是既有 DS Built-in Resource 错误、上述未测范围与尚未接线的材料数据通道。本阶段到此收束；下一段在已有 N5 授权内接续 PeerState/Interest/CommitOwner/Codec/Assembler/Installer 的认证材料路径，仍须按既有预算和完整验证/原子安装后 ACK 合同推进，遇到新语义另行确认。N5Complete=false。
