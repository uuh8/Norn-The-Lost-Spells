## 2026-10-31：正式材料 World 的第一段装配与认证后身份握手

本段只接入 Bootstrap 激活前配置、Authority/Replica 模式、唯一材料时钟，以及 Frozen/Detach 生命周期。Payload 的发送、ACK、重传、Interest、Assembler/Installer 的正式网络调用仍未接入，不能据此宣布 N5 完成。

### 授权与 WorldEpoch 合同

N5 实施计划第 3 行记录用户明确进入 N5；`n4/final-audit/acceptance.json` 的 `N5ExplicitlyAuthorizedByUser=true` 独立于 N4 体验是否通过。本轮接续还得到用户对来源方案 1 的明确回复“可以”：Unity Dedicated Server 生成世界生命期编号，客户端认证后通过世界握手取得。确认原文与边界保存于 `bootstrap-lifecycle-2026-10-31/worldepoch-decision.json`。

同一运行世界的重连保留 WorldEpoch，ConnectionEpoch 随新连接提高；创建新的 World Owner 才生成新的非零编号。实现没有将业务服重启当成世界重建，也没有补充 DS 崩溃后复用编号、持久恢复或结算语义。这些恢复组合尚未验证。

### 文件职责与调用顺序

- `Networking/Authentication/JoinTicketAuthenticator.cs`：客户端在场景激活前注册世界握手处理器。JoinReply 认证成功后等待 WorldIdentity；超时拒绝入场。服务端只向仍是当前席位且认证身份一致的连接发送。客户端核对实际连接认证状态、身份引用及 Run/Dungeon/RoomEpoch/ConnectionEpoch；重复相同 WorldEpoch 幂等，不接受同世界无声换号。
- `Networking/Protocol/WorldIdentityCodec.cs`、`WorldIdentityEnvelope.cs`：复用现有 Protocol 目录。256 字节硬上限，预检六个必需字段、类型、规范编码及身份；这是认证/重连时的离散握手，不放进每 Tick 热路径。消息池 Reset 清空全部字段和 unknown fields。
- `Shared/Protocol/Protos/world.proto`：追加中立 WorldIdentityMessage；固定生成器更新唯一 Generated 输出。应用协议升为 3，使没有必需握手的 v2 在入口明确被拒绝；Protobuf、Mirror 和 SDK 依赖版本没有变动。
- `Multiplayer/Movement/OnlineWorldBootstrap.cs`：实例化 inactive World 后，先找到唯一 Binding 并配置角色/WorldEpoch、初始挂起，再激活；真实初始化完成且健康后才能 Ready。唯一 Authority AfterPhysics 入口投喂四个原有席位的有效位置及 30Hz 时间，内部材料时钟继续按既定 10Hz 消费。Replica 只配置表现模式。停止时先解绑，再销毁世界。
- `AuthorityTickDriver.cs`：每个 Render Update 先同步材料挂起状态，即使 Frozen 导致零逻辑步也会暂停材料时钟；既有 Clock 清除追赶债务。材料回调可以同步 StopWorld，故 AfterPhysics 及诊断计量都在回调后检查 Owner 仍有效，取消的样本不记为完成。
- `ElementWorldRuntime.cs`：Authority 的初始化不再要求客户端兴趣锚点；它沿用既有 resident/pending/四人活动采样。SinglePlayer 仍要求作者配置的兴趣锚点，没有偷偷回退到玩家或相机。

正式资源由现有 `Export Formal Dungeon Worlds` 导出器生成。P7/P8 World Prefab 及在线 CPU Profile 属于现有 `Prefabs/World/Online`、`ScriptableObjects/World/Online/Materials` 职责，独立 CPU 副本用于保留离线作者配置。没有新增源码模块目录，没有搬迁现有系统。`.meta` 由 Unity 生成，现有资产 GUID 保留。

### 真实失败与验证边界

证据目录：`docs/superpowers/evidence/multiplayer/n5/bootstrap-lifecycle-2026-10-31/`。Authority 初始化先复现 6 通过/2 失败，再通过 11 项回归；握手与 Bootstrap 的真实 RED 为 17 通过/48 失败，Reset 单独 RED 为 0/1，实现后相关 66 项通过。审查补充的同步 StopWorld 边界先得到 1/2 RED，再得到 3/0 GREEN。早先编译受阻的无效 RED 另存，未冒充行为失败。

业务协议回归先得到 99/1：旧测试固定要求 CurrentVersion=2。更新后明确要求 v3、拒绝 v1/v2，保持原 Dungeon 合同断言；100 项业务测试及真实 HTTP/Protobuf smoke 通过。Release 构建为 0 warning/0 error，固定生成器 `-Check` 通过。该测试使用隔离 test Schema；仅启停本轮创建且 PID/启动时间/路径核对一致的私有数据库与临时业务实例。

正式地图 PlayMode 仅说明现有 P7/P8 World 激活、原 NPC/入口/Boss 触发路径；不会替代真实业务认证后两台 Native Client 的共享材料体验。尚未运行新的 Client/DS 构建、真实 KCP 世界握手与重连、四人材料压力、Native 性能/GC、公网/IL2CPP/Linux 或 DS 重启恢复。N4 人工体验缺口、N6 持久结算也没有因此消失。

下一段应对照已批准 N5 合同，将现有 PeerState/Interest/CommitOwner/Codec/Assembler/Installer 接到已认证连接生命周期，明确有限队列、预算、ACK 的原子安装边界与恢复验证。当前交付到上述第一段为止，完整材料通道另行接续。

本段最终回归为 **25 个 EditMode fixtures：280 通过 / 0 失败**，其中原有同范围 20 fixtures 为 **206 / 0**（前阶段为 196 / 0，本段增加十项边界）。正式 P7/P8 PlayMode 首轮为 **0 / 2**，均因 GasFieldVolumeRenderer.Start 的缺少 World/Material 日志失败。新增保存后 Authority 不残留 Renderer 的断言并重新编译后，连续两轮均 **2 / 0**。没有为通过测试忽略错误，也没有修改生产 Rendering；首轮错误来源仍未确定，必须保留这一局限，不能宣称该渲染故障已修复。

导出器 SaveAssets 将本轮 Physics 测试接触过的 DynamicsManager 从旧序列化格式自动重写；先核对 HEAD 字节哈希等于任务起始哈希，再只恢复该项自身副作用。观察到的重写版本及恢复证明另存证据。最终 12 个受保护场景、设置与真实存档逐字节不变，原有 Recovery 文件保留。P8 派生 World 因既有导出器重建对象 ID，文本 diff 很大；这属于生成资源 churn，不能视为手写玩法重构。源 Scene 与现有 `.meta` GUID 保持，正式资源通过导出/兼容校验和上述两轮 PlayMode；需要审查生成产物，仍未做 Native 交付。

最新完整摘要见本目录 `summary.json`；通过与失败 XML/TRX 均保留。当前没有权限或决策阻塞；未解决的首轮 PlayMode 日志来源、真实 KCP/Native 握手与完整材料通道是后续核查范围。本段收束，不继续扩展通道。

最终文档链接检查没有缺失目标，手写源码/协议/脚本/Markdown/JSON 的 `git diff --check` 为 0。包含 Unity 导出 Prefab 的全范围检查返回 1，原因是序列化空值（如 `m_Name: `）自带行尾空格；没有在兼容 Hash 验证后手改生成 YAML。此项作为生成格式局限保留，不报告全范围 diff 检查通过。最后无待执行 Unity 请求，Editor 为 P7_MainMenu、非 Play、非编译、场景干净；本轮临时业务与私有数据库已按身份核对正常停止。
