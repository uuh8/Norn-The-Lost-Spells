# Native 玩家与同 PID 重连诊断：五文件只读审查

日期：2026-10-31。目标阶段：`n5/native-player-reconnect-2026-10-31`。

结论：本次读取的五文件诊断接线中，未发现确定的 P0/P1/P2 问题，也没有需要阻止根线程继续回归或匹配构建的问题。本报告只说明静态审查所见，不表示真实 OS 输入、同 PID 重连、N5 或性能验收通过。

## 范围与基线

仅审查以下五文件的当前诊断增加及其必要只读调用链：

- `RPG/Assets/_Project/Scripts/Character/Spells/OnlineCastDriver.cs`
- `RPG/Assets/_Project/Scripts/Multiplayer/Diagnostics/OnlineMovementAcceptanceProbe.cs`
- `RPG/Assets/_Project/Scripts/Multiplayer/Runtime/RoomInstanceBootstrap.cs`
- `RPG/Assets/_Project/Scripts/Run/Diagnostics/PortalRoomAcceptanceProbe.cs`
- `RPG/Assets/_Project/Tests/Character/OnlineCastDriverTests.cs`

读取了本阶段 `stage-scope.md`、玩家/重连合同及 `source-before.json`。五文件当前 SHA256 均与阶段起始清单不同。起始清单只含 Path/Hash，没有旧源码内容；遵守本轮不运行 Git 的要求，因此没有逐行取得旧源码 diff。审查集中于根线程指定的新诊断 DTO、复制/映射方法、Probe 新读取点和两个测试，不能用 Hash 比较证明五文件中不存在其他文本变化。

本审查没有修改仓库，没有运行 Editor、服务、测试、生成、构建或 Git。唯一写入是本报告。只读 shell 经 `require_escalated` 审批后成功，没有遗留权限阻塞。

## 主要核对结果

1. **Client 使用实际已安装 Source。** `OnlineCastDriver.cs:146–180` 先拒绝 Authority、非本机 Owner 和 `!ClientInventoryReady`；后者核验 Replica 已有 Snapshot 且其 ConnectionEpoch 等于当前 `_epoch`。因此 SetConnection 换到新 Epoch、尚未安装新库存时，不能把旧 Source 换标签当作新证据。方法从当前 `Source.ReadOnly` / `WandsReadOnly` 现取卡实例、当前 Mana/CD/资源版本，不克隆旧网络 DTO 或读取 EntrySnapshot。较新 ActorState 更新的资源值能被诊断读到。
2. **返回独立值副本。** 卡数据先复制为 InventoryItemSnapshot，再创建新的诊断数组和卡 DTO；没有把可变 Source/Workspace/收到的库存 DTO 暴露给诊断使用方。String 是稳定身份值。WandSlot 才填父杖 ID，SpellStorage 的父杖为空，不把玩家 OwnerId 冒充杖实例。Client 明确 `ActualSnapshotTickAvailable=false`，ProbeSampleTick 没有冒充整份库存的原始事实 Tick。
3. **DS 导出当前 Workspace，不推进发送序列。** `RoomInstanceBootstrap.cs:176–192` 在 `NORN_N5_PLAYER_RESOURCE_PROBE == "1"` 且 Development DS 编译边界内启用库存枚举；按 Registry 当前槽 Epoch 调用已有 `TryCaptureInventory`。该方法再验证 CastDriver 的当前 `_epoch`。读取的 `OnlineOwnedWorkspace.TryCapturePresentation` 使用当前实例/资源；参数 `sequence=1` 只写入返回快照，没有递增正式网络通道计数。映射后的 Authority 诊断不将这个1报告为真实网络安装/发送序号。没有施法、扣次、Tick、Entry 重新发放或协议修改。
4. **DS 映射与身份字段。** `OnlineCastDriver.cs:41–82` 对内部刚导出的快照作基本边界检查，复制卡实例与所选杖当前资源；Actor/Epoch/版本来自源快照。Observation Tick 与源导出的 Snapshot Tick 分列。映射不持有 DTO 数组/卡对象引用，也不修改源。Workspace 现有导出会执行其既有 `_wands.Synchronize()` 缓存同步；没有发现新增诊断推进资源时间或发送序列的调用。
5. **默认关闭与热路径。** Client 两个 Probe 仍只由显式 Development/Editor 参数创建。新增卡枚举、Hub 查找、数组与 JSON IO 位于既有1Hz报告分支；Movement 的 `_measuring`、Portal 的 `_measuring || _externalSampling` 均在新增采集前早退。普通 DS 的 `_playerResourceProbe` 默认 false，不创建库存数组且条件短路不调用 Cast 导出；其既有 state 报告继续工作。新增 API 留在正式程序集内，但没有接入生产 Update/Tick 的自动采样调用。
6. **Hub 导航只读。** `PortalRoomAcceptanceProbe.cs:122–156` 读取实际已加载 Hub Scene、原 Session 的 ActorTransform、Portal 位姿/可见性与当前 Camera.main；没有移动角色、开门或创建 Camera。Hub 未加载、Session/Actor/Camera 缺失均有明确可用性字段；无 Actor 的距离为-1。原有 position-portal 命令仍属于既有显式诊断，不是本轮新增真实行走证据。
7. **空对象与生命周期。** 新 Probe 读取先检查 World/Registry、Cast、Session/Actor/Camera/Portal 的 Unity 存活状态；不存在继续使用旧诊断 Source 的持久字段。Client Source 销毁后 Replica 的既有清理会置空，下一次诊断 Ready 检查拒绝。远端 Cast 非本机 Owner 在复制前返回，因此 Client 不伪造其他玩家的完整库存。报告中断线期间可能保留旧 Epoch 的已安装读数，判定程序须看其内嵌 ConnectionEpoch，不能单凭顶层观察时刻当作重连完成。
8. **凭据。** 新 DTO 只含 Actor、连接代次、物品/模板/父杖实例 ID、资源与版本；没有复制 Token、Ticket、实例 Secret、密码或连接串。DS State 仍不序列化启动 Secret，导航只含场景/位姿。

## 测试证据与限制

两个新增用例使用实际生产 Catalog、Owned 事务、真实施法扣次、当前库存导出及 Client Replica 安装：

- `ClientInventoryDiagnosticReadsCurrentSourceCopiesValuesAndRejectsOldEpoch`：实际消耗后安装库存，再施加较新的资源事实，断言诊断 Mana=17、同卡29次、实例/父杖/模板；修改返回 DTO 后再读仍为29；换 Epoch 后拒绝旧 Source；Authority 不能误用 Client 导出。
- `AuthorityInventoryDiagnosticMapsConsumedInstanceValuesWithoutChangingWorkspace`：实际施法扣次后映射，核对观察/源 Tick、Epoch、同卡29次；修改 DTO 不改变再次映射结果，映射前后 Mana/次数不变。

只读取得本阶段 `green-report.json`：2026-10-31 17:04:34 UTC 保存 **2 passed / 0 failed**，这是根线程实际运行后归档的结果，本审查未运行测试。更大范围回归及最终匹配构建由根线程继续。

尚未由这两个测试直接覆盖：Probe 默认开关/测量暂停的运行时断言、Hub 不在/失活/返回的报告序列、SpellStorage 父杖空值及多杖/多卡边界、DS 正式通道发送序列前后专项断言。此处有静态依据，但不能据此声称这些场景实测通过。非法诊断 Config/文件 IO 失败、旧的显式 Interest 诊断 error 锁存等既有行为未在本轮扩修，也不作为本轮新增问题。

真实 OS 键鼠、前台 PID 核验、主菜单/Profile/Hub/F/原房间 UI、消耗后同 PID 新 Ticket 重连、同实例次数继续扣、P7/P8 与另一 Client 持续运行仍需 Native 证据。新诊断读数不能替代这些行为，也不构成零分配、完整 N5 或发布验收。
